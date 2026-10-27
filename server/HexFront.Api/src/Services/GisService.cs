using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HexFront.Core;
using HexFront.Core.GisTools;
using HexFront.Core.Models;
using HexFront.Core.Services;
using HexFront.Core.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HexFront.Api.Services;

/// <summary>
/// 进程内六角格生成调度：能力探测 / 格网方案 / 串行作业队列 / 作业落盘持久化。
///
/// 2026-10-27 起，六角格生成从独立进程 HexFront.GisApi（端口 5180）合并进 HexFront.Api：
/// 不再通过 HTTP 调远程服务，也不再本机跑 tools/gis/hexgen.py，而是直接在本进程调用
/// Core 的 HexGenService.Generate。真实栅格来自 data/terrain/*.ter（TerrainSampler 读取，
/// 含 ESA 土地覆盖），瓦片缺失则回落确定性合成地形。两条路都产出 data/gis/&lt;包名&gt;.json，
/// 生成完成即发布为服务器网络包（data/hexgrids/&lt;id&gt;.zip）。
/// </summary>
public sealed class GisService
{
    /// <summary>作业日志只留最近这么多行，长日志会把轮询响应撑爆。</summary>
    public const int MaxLogLines = 240;
    public const int JobTimeoutMinutes = 20;
    public const int MaxJobsInList = 30;

    /// <summary>数据包落盘/回读契约：与 hexgen.py 及 Core 一致（snake_case + 缩进）。</summary>
    private readonly JsonSerializerOptions _pkgJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>作业记录落盘（不含包体，包体单独存 data/gis）：紧凑，避免大文件。</summary>
    private readonly JsonSerializerOptions _jobJson = new() { WriteIndented = false };

    private readonly ILogger<GisService> _logger;
    private readonly string _gisDir;
    private readonly string _hexGridDir;
    private readonly string _jobsDir;
    private readonly string _terrainDir;
    private readonly string _demMode;
    private readonly TerrainSampler? _sampler;
    private readonly AppDbService _db;

    private readonly object _gate = new();
    private readonly Dictionary<string, GisJob> _jobs = new();
    private readonly Queue<GisJob> _queue = new();
    private GisJob? _running;
    private int _seq;

    private readonly Dictionary<string, JsonObject> _planCache = new();

    public GisService(ILogger<GisService> logger, RoomService rooms, AppDbService db, IConfiguration cfg)
    {
        _logger = logger;
        _db = db;
        _gisDir = rooms.GisDirectory;
        _hexGridDir = rooms.HexGridDirectory;
        Directory.CreateDirectory(_gisDir);

        // 地形目录：GisApi 段（TerrainDir* 特化键优先）→ 回落 server/data/terrain。
        // 与 HexFront.Core.UtilsPath 的口径保持一致：server 目录即仓库根，其下 data/terrain 是默认地形位置。
        var serverRoot = UtilsPath.ServerRoot();
        var opt = cfg.GetSection("GisApi").Get<GisApiOptions>() ?? new GisApiOptions();
        var terrainConfigured = UtilsPath.SelectPlatformPath(opt.TerrainDir, opt.TerrainDirWindows, opt.TerrainDirLinux);
        _terrainDir = UtilsPath.ResolveDir(terrainConfigured, serverRoot, "data/terrain");
        _demMode = string.IsNullOrWhiteSpace(opt.DemMode) ? "auto" : opt.DemMode;

        // 作业落盘：data/gis-api/jobs/<id>.json（包体仍落在 data/gis/<pkg>.json，这里只存作业记录）。
        _jobsDir = Path.Combine(rooms.DataDirectory, "gis-api", "jobs");
        Directory.CreateDirectory(_jobsDir);

        // 真实地形采样器：地形目录存在且含 .ter 瓦片时才建立，否则回落合成地形。
        if (Directory.Exists(_terrainDir))
            _sampler = new TerrainSampler(_terrainDir);

        _logger.LogInformation("GIS 生成配置：terrain_dir={TerrainDir}（dem_mode={DemMode}）, jobs_dir={JobsDir}, 真实瓦片={Real}",
            _terrainDir, _demMode, _jobsDir, _sampler?.Available == true);
        if (_sampler is null)
            _logger.LogWarning("地形目录不存在，将回落合成地形：{Dir}", _terrainDir);
        else if (!_sampler.Available)
            _logger.LogWarning("地形目录已存在但未发现 terrain_manifest.json 或 .ter 瓦片，将回落合成地形：{Dir}。" +
                "请把 GisApi 的 TerrainDir（Linux 写 TerrainDirLinux）指向含 terrain_manifest.json 的目录。", _terrainDir);
        else
            _logger.LogInformation("地形瓦片已加载（真实 DEM），含土地覆盖={HasLandcover}：{Dir}", _sampler.HasLandcover, _terrainDir);

        LoadJobs();
    }

    public string GisDirectory => _gisDir;

    // ==================== 能力探测 ====================

    /// <summary>
    /// 进程内 GIS 工具链可用性：真实 .ter 瓦片是否就绪、DemMode 是否强制真实。
    /// 返回前端既有字段；为兼容 HexGenPanel 的 capsNotes（远程分支），把地形实况挂在 remote.dem 下。
    /// </summary>
    public Task<JsonObject> CapabilitiesAsync(bool refresh = false) => Task.FromResult(LocalCaps());

    private JsonObject LocalCaps()
    {
        var real = _sampler?.Available == true;
        var useReal = _demMode == "real" || (_demMode == "auto" && real);
        return new JsonObject
        {
            ["available"] = JsonValue.Create(true),
            ["engine"] = JsonValue.Create(useReal ? "local-terrain" : "synthetic"),
            ["dem_mode"] = JsonValue.Create(_demMode),
            ["dem_effective_real"] = JsonValue.Create(useReal),
            ["dem_present"] = JsonValue.Create(real),
            ["terrain_dir"] = JsonValue.Create(_terrainDir),
            ["real_tiles_has_landcover"] = JsonValue.Create(_sampler?.HasLandcover ?? false),
            ["gis_dir"] = JsonValue.Create(_gisDir),
            ["remote"] = new JsonObject
            {
                ["base_url"] = "in-process",
                ["service"] = "HexFront.Api",
                ["version"] = "1.0.0",
                ["dem"] = new JsonObject
                {
                    ["mode"] = _demMode,
                    ["effective_real"] = useReal,
                    ["real_tiles_available"] = real,
                    ["real_tiles_has_landcover"] = _sampler?.HasLandcover ?? false,
                    ["terrain_dir"] = _terrainDir,
                },
            },
        };
    }

    // ==================== 格网方案 ====================

    /// <summary>方案计算（进程内，同一几何）：校验后交 GisPlanService，同一 (bbox, cell) 命中缓存直接回。</summary>
    public Task<JsonObject> PlanAsync(double[] bbox, double? cellKm)
    {
        var key = string.Join(",", bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) +
                  ":" + (cellKm?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
        lock (_gate) if (_planCache.TryGetValue(key, out var hit)) return Task.FromResult(hit);
        JsonObject plan;
        try
        {
            ValidateBbox(bbox);
            plan = GisPlanService.PlanPayload(bbox, cellKm);
        }
        catch (GisPlanException e)
        {
            plan = new JsonObject { ["ok"] = JsonValue.Create(false), ["error"] = JsonValue.Create(e.Message) };
        }
        lock (_gate)
        {
            if (_planCache.Count > 64) _planCache.Clear();
            _planCache[key] = plan;
        }
        return Task.FromResult(plan);
    }

    // ==================== 作业 ====================

    public Task<GisJob> StartJobAsync(GisJobSpec spec)
    {
        // 用同一份几何预估格数：客户端算错也不要把服务端拖死
        var (gw, gh) = GisPlanService.GridSize(spec.Bbox.ToArray(), spec.CellKm);
        var cells = gw * gh;
        if (cells > GisPlanService.MaxCells)
            throw new GisPlanException($"该范围会有 {cells} 格，超过上限 {GisPlanService.MaxCells}；请扩大对边距或缩小范围框");

        GisJob job;
        lock (_gate)
        {
            var id = "G" + (++_seq).ToString("D4", CultureInfo.InvariantCulture);
            job = new GisJob
            {
                Id = id,
                Spec = spec,
                PkgName = UniquePkgName(spec.Name),
                Stage = "排队中",
            };
            _jobs[id] = job;
            _queue.Enqueue(job);
            PersistJob(job);
            DrainLocked();
        }
        var real = _demMode == "real" || (_demMode == "auto" && _sampler?.Available == true);
        var useReal = real && !spec.Offline && spec.Dem is not ("synthetic" or "off");
        Log(job, useReal
            ? "引擎：本地 .ter 瓦片（真实 DEM" + (_sampler?.HasLandcover == true ? " + ESA 土地覆盖" : "") + "）"
            : "引擎：合成地形（无真实瓦片 / 已指定 synthetic / 离线模式）");
        return Task.FromResult(job);
    }

    public GisJob? Job(string id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var j) ? j : null;
    }

    public List<JsonObject> Jobs()
    {
        List<GisJob> list;
        lock (_gate) list = _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(MaxJobsInList).ToList();
        return list.Select(JobJson.ToNode).ToList();
    }

    private string UniquePkgName(string baseName)
    {
        var pkg = baseName;
        for (var n = 1; File.Exists(Path.Combine(_gisDir, pkg + ".json")); n++)
            pkg = baseName + "_" + n;
        return pkg;
    }

    private void DrainLocked()
    {
        if (_running != null || _queue.Count == 0) return;
        var job = _queue.Dequeue();
        _running = job;
        _ = Task.Run(() =>
        {
            try { RunInProcess(job); }
            catch (Exception e)
            {
                job.Status = GisJobStatus.Failed;
                job.Error = e.Message;
                Log(job, "失败：" + e.Message);
            }
            finally
            {
                job.FinishedAt = DateTime.UtcNow;
                PersistJob(job);
                lock (_gate) { _running = null; DrainLocked(); }
            }
        });
    }

    /// <summary>
    /// 进程内生成：直接调用 Core 的 HexGenService.Generate（真实 .ter 或合成兜底），
    /// 阶段进度回报前端；完成后自校验、落盘 data/gis/&lt;包名&gt;.json，并发布为服务器网络包。
    /// </summary>
    private void RunInProcess(GisJob job)
    {
        void OnProgress(string stage, double p)
        {
            job.Stage = stage;
            job.Progress = Math.Clamp((int)Math.Round(p * 100), 0, 100);
            Log(job, $"[{job.Progress}%] {stage}");
            PersistJob(job);
        }

        job.Status = GisJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.Stage = "开始生成";
        job.Progress = 0;
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

            var pkg = HexGenService.Generate(genSpec, useReal ? _sampler : null, OnProgress);
            Log(job, "生成完成，开始自校验");
            job.Stage = "自校验";
            job.Progress = 95;
            PersistJob(job);
            var report = HexPackageValidator.Validate(pkg);
            if (report.Fails > 0)
            {
                job.Status = GisJobStatus.Failed;
                var failNames = string.Join("；", report.Results
                    .Where(r => r.Level == "FAIL")
                    .Select(r => string.IsNullOrEmpty(r.Detail) ? r.Check : $"{r.Check}（{r.Detail}）"));
                job.Error = $"自校验 {report.Fails} 项未通过，未产出数据包：{failNames}";
                Log(job, "失败：" + job.Error);
                job.Progress = 0;
                return;
            }

            var pkgPath = Path.Combine(_gisDir, job.PkgName + ".json");
            File.WriteAllText(pkgPath, JsonSerializer.Serialize(pkg, _pkgJson));
            Log(job, $"自校验 {report.Total} 项通过（{report.Warns} 项 WARN）→ {Path.GetFileName(pkgPath)}");

            var counts = pkg.Meta.Stats.GetValueOrDefault("terrain_counts");
            job.Meta = new JsonObject
            {
                ["ok"] = JsonValue.Create(true),
                ["file"] = JsonValue.Create(pkgPath),
                ["name"] = JsonValue.Create(job.PkgName + ".json"),
                ["source"] = JsonValue.Create(pkg.Meta.Source),
                ["cell_km"] = JsonValue.Create(job.Spec.CellKm),
                ["width"] = JsonValue.Create(pkg.Meta.Width),
                ["height"] = JsonValue.Create(pkg.Meta.Height),
                ["bbox"] = ArrNum(pkg.Meta.Bbox),
                ["fails"] = JsonValue.Create(report.Fails),
                ["warns"] = JsonValue.Create(report.Warns),
                ["terrain_counts"] = counts == null ? null : JsonSerializer.SerializeToNode(counts, _pkgJson),
                ["generated_at"] = JsonValue.Create(pkg.Meta.GeneratedAt),
                ["errors"] = ArrStr(report.Results.Where(r => r.Level == "FAIL").Select(r => r.Check)),
            };
            job.Status = GisJobStatus.Done;
            job.Stage = "已完成";
            job.Progress = 100;
            Log(job, $"完成：data/gis/{job.PkgName}.json（{pkg.Meta.Width}×{pkg.Meta.Height} 格）");
            PublishToHexGrid(job);   // 副本统一只用服务器网络包：生成完成即存储为 data/hexgrids/<id>.zip
        }
        catch (Exception ex)
        {
            job.Status = GisJobStatus.Failed;
            job.Error = ex.Message;
            job.Stage = "失败";
            Log(job, "失败：" + ex.Message);
        }
    }

    /// <summary>生成完成即发布为服务器网络包（转换 + zip 落盘 + 地图包目录表登记）；失败只记日志不影响数据包本身。</summary>
    private void PublishToHexGrid(GisJob job)
    {
        try
        {
            var pkgPath = Path.Combine(_gisDir, job.PkgName + ".json");
            var pkg = JsonSerializer.Deserialize<HexPackage>(File.ReadAllText(pkgPath), _pkgJson);
            if (pkg == null || pkg.Hexes.Count == 0) throw new InvalidOperationException("数据包无格数据");
            var doc = HexGridPackageService.FromHexPackage(pkg, job.Spec.Name);
            HexGridPackageService.Save(_hexGridDir, doc);
            job.HexGridId = doc.Id;
            _db.UpsertMapPackage(doc.Id, Path.Combine(_hexGridDir, doc.Id + ".zip"), doc.Rows, doc.Cols, doc.FlatToFlat);
            Log(job, $"已存储为服务器网络包：{doc.Id}（{doc.Rows}×{doc.Cols} 格 · 对边距 {doc.FlatToFlat:F0} m）");
        }
        catch (Exception e)
        {
            Log(job, "存储服务器网络包失败（数据包仍在 data/gis，可重新生成）：" + e.Message);
            _logger.LogWarning(e, "hexgrid publish failed for {Pkg}", job.PkgName);
        }
    }

    // ==================== 入参校验 ====================

    /// <summary>线上 spec（snake_case）→ GisJobSpec；错误一律以 GisPlanException 抛出，控制器包成 {"error":…}。</summary>
    public static GisJobSpec ParseSpec(JsonNode? body)
    {
        if (body is not JsonObject j) throw new GisPlanException("请求体需为 JSON 对象");
        var bb = BboxFromBody(j["bbox"]);
        var cell = NumOrNull(j["cell_km"]);
        if (cell is null or <= 0 or > 30) throw new GisPlanException("对边距需在 0..30 km 之间");
        var dem = Str(j["dem"], "auto");
        if (dem is not ("auto" or "local" or "synthetic")) throw new GisPlanException($"未知高程档位 {dem}");
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

    public static double[] BboxFromQuery(string? w, string? s, string? e, string? n)
    {
        var bb = new double[4];
        var raw = new[] { w, s, e, n };
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(raw[i], NumberStyles.Float, CultureInfo.InvariantCulture, out bb[i]))
                throw new GisPlanException("需要 w/s/e/n 四个数值（西 南 东 北）");
            if (!double.IsFinite(bb[i])) throw new GisPlanException("bbox 含非法数值");
        }
        ValidateBbox(bb);
        return bb;
    }

    public static double? CellKmFromQuery(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private static void ValidateBbox(double[] bb)
    {
        if (!(bb[0] < bb[2] && bb[1] < bb[3])) throw new GisPlanException("bbox 需满足 西<东 且 南<北");
        if (bb[1] < -84.5 || bb[3] > 84.5) throw new GisPlanException("墨卡托格网在极区无意义，纬度限制在 ±84.5°");
        if (bb[2] - bb[0] > GisPlanService.MaxBboxSpanDeg || bb[3] - bb[1] > GisPlanService.MaxBboxSpanDeg)
            throw new GisPlanException($"范围框单边不得超过 {GisPlanService.MaxBboxSpanDeg}°（先选小一些的战场）");
    }

    /// <summary>包名只允许安全字符，落在 data/gis/ 里，绝不接受路径分隔符；再打一时间戳避免撞名。</summary>
    private static string CleanName(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-' ? ch : '_');
        var cleaned = sb.ToString().TrimStart('.').Replace(".json", "");
        if (cleaned.Length > 48) cleaned = cleaned[..48];
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return (cleaned.Length == 0 ? "gis" : cleaned) + "_" + stamp;
    }

    private static string Str(JsonNode? n, string def) =>
        n?.GetValueKind() == JsonValueKind.String ? n!.GetValue<string>() : def;

    private static bool NotFalse(JsonNode? n) => n?.GetValueKind() != JsonValueKind.False;

    private static double? NumOrNull(JsonNode? n)
    {
        if (n?.GetValueKind() != JsonValueKind.Number) return null;
        try { return n.GetValue<double>(); } catch { return null; }
    }

    private static JsonArray ArrNum(IEnumerable<double> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Create(v));
        return a;
    }

    private static JsonArray ArrStr(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Create(v));
        return a;
    }

    private static string Num(double v) =>
        v.ToString(v == Math.Round(v) ? "F0" : "R", CultureInfo.InvariantCulture);

    // ==================== 作业持久化 ====================

    private void PersistJob(GisJob job)
    {
        Directory.CreateDirectory(_jobsDir);
        var pkg = job.Package;
        job.Package = null; // 包体单独存 data/gis，作业记录不重复存放大对象
        try
        {
            File.WriteAllText(Path.Combine(_jobsDir, job.Id + ".json"),
                JsonSerializer.Serialize(job, _jobJson));
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
                var job = JsonSerializer.Deserialize<GisJob>(File.ReadAllText(f), _jobJson);
                if (job == null) continue;
                _jobs[job.Id] = job;
                // 重启后把未完成的作业重新入队重跑（数据包若已存在则重跑会覆盖，安全）
                if (job.Status is GisJobStatus.Queued or GisJobStatus.Running)
                {
                    job.Status = GisJobStatus.Queued;
                    job.Stage = "排队中";
                    _queue.Enqueue(job);
                    PersistJob(job);
                }
            }
            catch
            {
                /* 坏记录忽略 */
            }
        }
    }

    // ==================== 日志 ====================

    private void Log(GisJob job, string line)
    {
        lock (job.Log)
        {
            job.Log.Add(line);
            while (job.Log.Count > MaxLogLines) job.Log.RemoveAt(0);
        }
        PersistJob(job);
    }
}

/// <summary>作业 → 线上 JSON（字段名与 hexfront/bin/server.dart 的 GisJob.toJson 逐项一致）。</summary>
internal static class JobJson
{
    public static JsonObject ToNode(GisJob job)
    {
        List<string> log;
        lock (job.Log) log = job.Log.ToList();
        var spec = job.Spec;
        return new JsonObject
        {
            ["id"] = JsonValue.Create(job.Id),
            ["status"] = JsonValue.Create(job.Status.ToString().ToLowerInvariant()),
            ["name"] = JsonValue.Create(job.PkgName + ".json"),
            ["spec"] = new JsonObject
            {
                ["bbox"] = ArrNum(spec.Bbox),
                ["cell_km"] = JsonValue.Create(spec.CellKm),
                ["name"] = JsonValue.Create(spec.Name),
                ["dem"] = JsonValue.Create(spec.Dem),
                ["landuse"] = JsonValue.Create(spec.Landuse),
                ["roads"] = JsonValue.Create(spec.Roads),
                ["water"] = JsonValue.Create(spec.Water),
                ["network"] = JsonValue.Create(spec.Network),
                ["features"] = JsonValue.Create(spec.Features),
                ["seed"] = JsonValue.Create(spec.Seed),
                ["offline"] = JsonValue.Create(spec.Offline),
            },
            ["log"] = ArrStr(log),
            ["progress"] = JsonValue.Create(job.Progress),
            ["stage"] = job.Stage == null ? null : JsonValue.Create(job.Stage),
            ["remote_id"] = job.RemoteId == null ? null : JsonValue.Create(job.RemoteId),
            ["error"] = job.Error == null ? null : JsonValue.Create(job.Error),
            ["meta"] = job.Meta?.DeepClone(),
            ["hexgrid_id"] = job.HexGridId == null ? null : JsonValue.Create(job.HexGridId),
            ["created_at"] = JsonValue.Create(job.CreatedAt.ToString("O")),
            ["started_at"] = job.StartedAt == null ? null : JsonValue.Create(job.StartedAt.Value.ToString("O")),
            ["finished_at"] = job.FinishedAt == null ? null : JsonValue.Create(job.FinishedAt.Value.ToString("O")),
        };
    }

    private static JsonArray ArrNum(IEnumerable<double> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Create(v));
        return a;
    }

    private static JsonArray ArrStr(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Create(v));
        return a;
    }
}
