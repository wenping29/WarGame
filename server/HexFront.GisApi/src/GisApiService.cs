using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core.GisTools;
using HexFront.Core.Models;
using HexFront.Core.Services;
using HexFront.Core.Validation;
using Microsoft.Extensions.Configuration;

namespace HexFront.GisApi;

/// <summary>
/// 六角格生成 Web API 的核心服务：单线程后台作业队列 + 独立存储（jobs/ 与 grids/）。
/// 不直接写入游戏后端的 data/hexgrids，生成的 HexPackage 落在自己的 grids/ 目录，便于独立管理。
/// 生成调用 Core 的 HexGenService.Generate（传入真实 .ter TerrainSampler 即 GIS 数据驱动）。
/// </summary>
public sealed class GisApiService
{
    private readonly string _dataDir;
    private readonly string _terrainDir;
    private readonly string _demMode;
    private readonly string _jobsDir;
    private readonly string _gridsDir;
    private readonly TerrainSampler? _sampler;

    private readonly object _gate = new();
    private readonly Dictionary<string, GisJob> _jobs = new();
    private readonly Queue<GisJob> _queue = new();
    private GisJob? _running;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    /// <summary>HexPackage 落盘/回读契约：与 hexgen.py 及主后端 data/gis/*.json 一致（snake_case + 缩进）。</summary>
    private readonly JsonSerializerOptions _pkgJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public GisApiService(IConfiguration cfg)
    {
        var opt = cfg.GetSection("GisApi").Get<GisApiOptions>() ?? new GisApiOptions();
        var repo = RepoLocator.ResolveRepoRoot();
        _dataDir = ResolveDir(opt.DataDir, repo, "data/gis-api");
        _terrainDir = ResolveDir(opt.TerrainDir, repo, "data/terrain");
        _demMode = string.IsNullOrWhiteSpace(opt.DemMode) ? "auto" : opt.DemMode;
        _jobsDir = Path.Combine(_dataDir, "jobs");
        _gridsDir = Path.Combine(_dataDir, "grids");
        Directory.CreateDirectory(_jobsDir);
        Directory.CreateDirectory(_gridsDir);

        if (Directory.Exists(_terrainDir))
            _sampler = new TerrainSampler(_terrainDir);

        LoadJobs();
        StartWorker();
    }

    // ==================== 能力查询 ====================

    public JsonObject Capabilities()
    {
        var real = _sampler?.Available == true;
        var useReal = _demMode == "real" || (_demMode == "auto" && real);
        return new JsonObject
        {
            ["service"] = "HexFront.GisApi",
            ["version"] = "1.0.0",
            ["dem"] = new JsonObject
            {
                ["mode"] = _demMode,
                ["effective_real"] = useReal,
                ["real_tiles_available"] = real,
                ["real_tiles_has_landcover"] = _sampler?.HasLandcover ?? false,
                ["terrain_dir"] = _terrainDir,
            },
            ["storage"] = new JsonObject
            {
                ["data_dir"] = _dataDir,
                ["jobs_dir"] = _jobsDir,
                ["grids_dir"] = _gridsDir,
                ["grid_count"] = Directory.Exists(_gridsDir) ? Directory.GetFiles(_gridsDir, "*.json").Length : 0,
            },
            ["endpoints"] = new JsonArray(
                "GET /health", "GET /api/capabilities", "POST /api/plan",
                "POST /api/generate", "GET /api/jobs", "GET /api/jobs/{id}",
                "DELETE /api/jobs/{id}", "GET /api/jobs/{id}/download",
                "GET /api/grids", "GET /api/grids/{id}",
                "GET /api/grids/{id}/download", "DELETE /api/grids/{id}"),
        };
    }

    // ==================== 方案 / 入参 ====================

    public JsonObject Plan(double[] bbox, double? cellKm)
    {
        ValidateBbox(bbox);
        return GisPlanService.PlanPayload(bbox, cellKm);
    }

    public static GisJobSpec ParseSpec(JsonNode? body)
    {
        if (body is not JsonObject j) throw new GisPlanException("请求体需为 JSON 对象");
        var bb = BboxFromBody(j["bbox"]);
        var cell = NumOrNull(j["cell_km"]);
        if (cell is null or <= 0 or > 30) throw new GisPlanException("对边距需在 0..30 km 之间");
        var dem = Str(j["dem"], "auto");
        if (dem is not ("auto" or "local" or "synthetic" or "open-meteo")) throw new GisPlanException($"未知高程档位 {dem}");
        var lu = Str(j["landuse"], "auto");
        if (lu is not ("auto" or "proxy" or "off")) throw new GisPlanException($"未知土地覆盖档位 {lu}");
        return new GisJobSpec
        {
            Bbox = bb.ToList(),
            CellKm = cell.Value,
            Name = CleanName(Str(j["name"], "")),
            Dem = dem,
            Landuse = lu,
            Roads = NotFalse(j["roads"]),
            Water = NotFalse(j["water"]),
            Network = NotFalse(j["network"]),
            Features = NotFalse(j["features"]),
            Seed = (int)(NumOrNull(j["seed"]) ?? 7),
            Offline = j["offline"]?.GetValueKind() == JsonValueKind.True,
        };
    }

    // ==================== 作业提交 / 查询 ====================

    public GisJob Submit(GisJobSpec spec)
    {
        var job = new GisJob
        {
            Id = "gj_" + Guid.NewGuid().ToString("N")[..12],
            Spec = spec,
            PkgName = (string.IsNullOrEmpty(spec.Name) ? "gis" : spec.Name) + "_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = GisJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        lock (_gate)
        {
            _jobs[job.Id] = job;
            _queue.Enqueue(job);
            PersistJob(job);
        }
        return job;
    }

    public GisJob? GetJob(string id)
    {
        lock (_gate)
            return _jobs.TryGetValue(Safe(id), out var j) ? j : null;
    }

    /// <summary>作业的轻量视图（不含完整包体）；用于列表 / 单作业查询。</summary>
    public JsonObject? JobJson(string id)
    {
        var j = GetJob(id);
        return j == null ? null : JobView(j);
    }

    public JsonArray ListJobs()
    {
        lock (_gate)
            return new JsonArray(_jobs.Values
                .OrderByDescending(j => j.CreatedAt)
                .Select(JobView).ToArray());
    }

    public bool DeleteJob(string id)
    {
        id = Safe(id);
        lock (_gate)
        {
            if (!_jobs.Remove(id)) return false;
            var path = Path.Combine(_jobsDir, id + ".json");
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
    }

    /// <summary>作业结果包路径（落在 grids/&lt;pkgName&gt;.json）；未生成或失败返回 null。</summary>
    public string? JobPackagePath(string id)
    {
        var job = GetJob(id);
        if (job == null || string.IsNullOrEmpty(job.PkgName)) return null;
        return GridPath(job.PkgName);
    }

    // ==================== 网格包（独立存储） ====================

    public JsonArray ListGrids()
    {
        var arr = new JsonArray();
        if (!Directory.Exists(_gridsDir)) return arr;
        foreach (var f in Directory.GetFiles(_gridsDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var pkg = JsonSerializer.Deserialize<HexPackage>(File.ReadAllText(f), _pkgJson);
                if (pkg == null) continue;
                arr.Add(new JsonObject
                {
                    ["id"] = Path.GetFileNameWithoutExtension(f),
                    ["name"] = pkg.Meta.Name,
                    ["width"] = pkg.Width,
                    ["height"] = pkg.Height,
                    ["cell_km"] = Math.Round(pkg.Meta.CellGroundM / 1000.0, 4),
                    ["bbox"] = new JsonArray(pkg.Meta.Bbox.Select(v => JsonValue.Create(v)).ToArray()),
                    ["source"] = pkg.Meta.Source,
                    ["hexes"] = pkg.Hexes.Count,
                    ["generated_at"] = pkg.Meta.GeneratedAt,
                });
            }
            catch { /* 坏文件跳过 */ }
        }
        return arr;
    }

    public JsonObject? GetGridMeta(string id)
    {
        var path = GridPath(id);
        if (path == null) return null;
        try
        {
            var pkg = JsonSerializer.Deserialize<HexPackage>(File.ReadAllText(path), _pkgJson);
            if (pkg == null) return null;
            return new JsonObject
            {
                ["id"] = id,
                ["name"] = pkg.Meta.Name,
                ["width"] = pkg.Width,
                ["height"] = pkg.Height,
                ["cell_km"] = Math.Round(pkg.Meta.CellGroundM / 1000.0, 4),
                ["bbox"] = new JsonArray(pkg.Meta.Bbox.Select(v => JsonValue.Create(v)).ToArray()),
                ["source"] = pkg.Meta.Source,
                ["hexes"] = pkg.Hexes.Count,
                ["generated_at"] = pkg.Meta.GeneratedAt,
                ["stats"] = JsonSerializer.SerializeToNode(pkg.Meta.Stats, _json),
            };
        }
        catch { return null; }
    }

    public string? GridPath(string id)
    {
        var p = Path.Combine(_gridsDir, Safe(id) + ".json");
        return File.Exists(p) ? p : null;
    }

    public bool DeleteGrid(string id)
    {
        var p = GridPath(id);
        if (p == null) return false;
        File.Delete(p);
        return true;
    }

    // ==================== 后台作业执行 ====================

    private void StartWorker()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                GisJob? job = null;
                try
                {
                    lock (_gate)
                    {
                        if (_running == null && _queue.Count > 0)
                        {
                            var candidate = _queue.Dequeue();
                            // 已被删除的作业直接跳过
                            if (candidate != null && _jobs.ContainsKey(candidate.Id))
                            {
                                job = candidate;
                                _running = candidate;
                            }
                        }
                    }
                    if (job != null)
                    {
                        RunJob(job);
                        lock (_gate) { if (_running == job) _running = null; }
                        job = null;
                    }
                    else
                    {
                        await Task.Delay(400);
                    }
                }
                catch
                {
                    lock (_gate) { if (_running == job) _running = null; }
                    job = null;
                    await Task.Delay(400);
                }
            }
        });
    }

    private void RunJob(GisJob job)
    {
        job.Status = GisJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        PersistJob(job);
        try
        {
            var real = _demMode == "real" || (_demMode == "auto" && _sampler?.Available == true);
            var useReal = real && !job.Spec.Offline
                && job.Spec.Dem is not ("synthetic" or "off");
            var genSpec = new GenSpec
            {
                Name = job.PkgName,
                Bbox = job.Spec.Bbox.ToArray(),
                CellKm = job.Spec.CellKm,
                Seed = job.Spec.Seed,
                Offline = job.Spec.Offline,
                UseRealDem = useReal,
                Features = job.Spec.Features,
            };

            var pkg = HexGenService.Generate(genSpec, useReal ? _sampler : null);
            var report = HexPackageValidator.Validate(pkg);
            if (report.Fails > 0)
            {
                job.Status = GisJobStatus.Failed;
                job.Error = $"自校验 {report.Fails} 项未通过，未产出数据包";
                job.FinishedAt = DateTime.UtcNow;
                PersistJob(job);
                return;
            }

            var path = Path.Combine(_gridsDir, job.PkgName + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(pkg, _pkgJson));

            var counts = pkg.Meta.Stats.TryGetValue("terrain_counts", out var tc) ? tc : new Dictionary<string, object>();
            job.Meta = new JsonObject
            {
                ["ok"] = JsonValue.Create(true),
                ["file"] = JsonValue.Create(path),
                ["name"] = JsonValue.Create(job.PkgName + ".json"),
                ["source"] = JsonValue.Create(pkg.Meta.Source),
                ["cell_km"] = JsonValue.Create(job.Spec.CellKm),
                ["width"] = JsonValue.Create(pkg.Meta.Width),
                ["height"] = JsonValue.Create(pkg.Meta.Height),
                ["bbox"] = new JsonArray(pkg.Meta.Bbox.Select(v => JsonValue.Create(v)).ToArray()),
                ["fails"] = JsonValue.Create(report.Fails),
                ["warns"] = JsonValue.Create(report.Warns),
                ["terrain_counts"] = JsonSerializer.SerializeToNode(counts, _json),
                ["generated_at"] = JsonValue.Create(pkg.Meta.GeneratedAt),
            };
            job.Package = pkg;
            job.Status = GisJobStatus.Done;
            job.FinishedAt = DateTime.UtcNow;
            PersistJob(job);
        }
        catch (Exception ex)
        {
            job.Status = GisJobStatus.Failed;
            job.Error = ex.Message;
            job.FinishedAt = DateTime.UtcNow;
            PersistJob(job);
        }
    }

    // ==================== 持久化 ====================

    private void PersistJob(GisJob job)
    {
        Directory.CreateDirectory(_jobsDir);
        var pkg = job.Package;
        job.Package = null; // 包体单独存 grids/，作业记录不重复存放大对象
        try
        {
            File.WriteAllText(Path.Combine(_jobsDir, job.Id + ".json"),
                JsonSerializer.Serialize(job, _json));
        }
        finally
        {
            job.Package = pkg;
        }
    }

    private void LoadJobs()
    {
        if (!Directory.Exists(_jobsDir)) return;
        foreach (var f in Directory.GetFiles(_jobsDir, "*.json"))
        {
            try
            {
                var job = JsonSerializer.Deserialize<GisJob>(File.ReadAllText(f), _json);
                if (job == null) continue;
                _jobs[job.Id] = job;
                // 重启后把未完成的作业重新入队重跑
                if (job.Status is GisJobStatus.Queued or GisJobStatus.Running)
                {
                    job.Status = GisJobStatus.Queued;
                    _queue.Enqueue(job);
                    PersistJob(job);
                }
            }
            catch { /* 坏记录忽略 */ }
        }
    }

    // ==================== 视图 / 工具 ====================

    private JsonObject JobView(GisJob job)
    {
        var spec = job.Spec;
        return new JsonObject
        {
            ["id"] = job.Id,
            ["status"] = job.Status.ToString(),
            ["pkg_name"] = job.PkgName,
            ["name"] = spec.Name,
            ["bbox"] = new JsonArray(spec.Bbox.Select(v => JsonValue.Create(v)).ToArray()),
            ["cell_km"] = spec.CellKm,
            ["created_at"] = job.CreatedAt.ToString("o", CultureInfo.InvariantCulture),
            ["started_at"] = job.StartedAt?.ToString("o", CultureInfo.InvariantCulture),
            ["finished_at"] = job.FinishedAt?.ToString("o", CultureInfo.InvariantCulture),
            ["error"] = job.Error == null ? null : JsonValue.Create(job.Error),
            // DeepClone：同一 job.Meta 会被多个请求的视图引用，JsonNode 不允许一个节点挂到多个父节点下
            ["meta"] = job.Meta?.DeepClone(),
        };
    }

    private static string ResolveDir(string? configured, string repo, string fallbackRelative)
    {
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(repo, fallbackRelative)
            : configured!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void ValidateBbox(double[] bb)
    {
        if (bb.Length != 4) throw new GisPlanException("bbox 需要 [西, 南, 东, 北]");
        if (!(bb[0] < bb[2] && bb[1] < bb[3])) throw new GisPlanException("bbox 需满足 西<东 且 南<北");
        if (bb[1] < -84.5 || bb[3] > 84.5) throw new GisPlanException("墨卡托格网在极区无意义，纬度限制在 ±84.5°");
        if (bb[2] - bb[0] > GisPlanService.MaxBboxSpanDeg || bb[3] - bb[1] > GisPlanService.MaxBboxSpanDeg)
            throw new GisPlanException($"范围框单边不得超过 {GisPlanService.MaxBboxSpanDeg}°（先选小一些的战场）");
    }

    private static double[] BboxFromBody(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count != 4) throw new GisPlanException("bbox 需要 [西, 南, 东, 北]");
        var nums = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (arr[i]?.GetValueKind() != JsonValueKind.Number) throw new GisPlanException("bbox 需要 [西, 南, 东, 北]");
            nums[i] = arr[i]!.GetValue<double>();
            if (!double.IsFinite(nums[i])) throw new GisPlanException("bbox 含非法数值");
        }
        ValidateBbox(nums);
        return nums;
    }

    private static string CleanName(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-' ? ch : '_');
        var cleaned = sb.ToString().TrimStart('.').Replace(".json", "");
        if (cleaned.Length > 48) cleaned = cleaned[..48];
        return cleaned.Length == 0 ? "gis" : cleaned;
    }

    private static string Safe(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..", StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new GisPlanException($"非法 id: {id}");
        return id;
    }

    private static double? NumOrNull(JsonNode? n) =>
        n?.GetValueKind() == JsonValueKind.Number ? n!.GetValue<double>() : null;

    private static string Str(JsonNode? n, string def) =>
        n?.GetValueKind() == JsonValueKind.String ? n!.GetValue<string>() : def;

    private static bool NotFalse(JsonNode? n) => n?.GetValueKind() != JsonValueKind.False;
}
