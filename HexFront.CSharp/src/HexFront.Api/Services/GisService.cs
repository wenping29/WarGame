using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HexFront.Core.Models;
using HexFront.Core.Services;

namespace HexFront.Api.Services;

/// <summary>
/// GIS 生成作业调度：能力探测 / 格网方案 / 串行作业队列。
/// 真实栅格生成走 tools/gis/hexgen.py（本机 Python + GDAL），无 Python 时降级为原生 C# 合成地形，
/// 两条路都产出 data/gis/&lt;包名&gt;.json，作业日志里如实标注用的是哪条。
/// </summary>
public sealed class GisService
{
    /// <summary>作业日志只留最近这么多行，长日志会把轮询响应撑爆。</summary>
    public const int MaxLogLines = 240;
    public const int JobTimeoutMinutes = 20;
    public const int MaxJobsInList = 30;

    private static readonly JsonSerializerOptions PackageJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ILogger<GisService> _logger;
    private readonly string _repoRoot;
    private readonly string _gisDir;
    private readonly string _hexGridDir;
    private readonly string _scriptPath;
    private readonly AppDbService _db;

    private readonly object _gate = new();
    private readonly Dictionary<string, GisJob> _jobs = new();
    private readonly Queue<GisJob> _queue = new();
    private GisJob? _running;
    private int _seq;

    private readonly Dictionary<string, JsonObject> _planCache = new();
    private JsonObject? _caps;
    private DateTime _capsAt = DateTime.MinValue;
    private Task<JsonObject>? _capsProbe;
    private string? _pythonExe;

    public GisService(ILogger<GisService> logger, RoomService rooms, AppDbService db)
    {
        _logger = logger;
        // GIS 目录只有一份口径：与建格/存档用的 RoomService.GisDirectory 同源
        _gisDir = rooms.GisDirectory;
        _hexGridDir = rooms.HexGridDirectory;
        _db = db;
        _repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(_gisDir)!) ?? Directory.GetCurrentDirectory();
        _scriptPath = Path.Combine(_repoRoot, "tools", "gis", "hexgen.py");
        Directory.CreateDirectory(_gisDir);
    }

    public string GisDirectory => _gisDir;
    public string ScriptPath => _scriptPath;

    // ==================== 能力探测 ====================

    /// <summary>本机 GIS 工具链可用性；5 分钟缓存，refresh=1 强制重探。</summary>
    public async Task<JsonObject> CapabilitiesAsync(bool refresh)
    {
        Task<JsonObject>? inflight;
        lock (_gate)
        {
            if (!refresh && _caps != null && DateTime.Now - _capsAt < TimeSpan.FromMinutes(5)) return _caps;
            inflight = _capsProbe;
            if (inflight == null)
            {
                _capsProbe = null;
                inflight = ProbeAsync();
                _capsProbe = inflight;
            }
        }
        var result = await inflight;
        lock (_gate)
        {
            if (ReferenceEquals(_capsProbe, inflight))
            {
                _caps = result;
                _capsAt = DateTime.Now;
                _capsProbe = null;
            }
        }
        return result;
    }

    private async Task<JsonObject> ProbeAsync()
    {
        var exe = await FindPythonAsync();
        if (exe == null)
            return Unavailable("未找到 python：真实栅格生成需要本机 Python（可用环境变量 HEXGEN_PYTHON 指定）；" +
                               "作业将降级为原生 C# 合成地形");
        if (!File.Exists(_scriptPath))
            return Unavailable($"未找到生成脚本：{_scriptPath}；作业将降级为原生 C# 合成地形");

        try
        {
            var (stdout, _, exitCode) = await RunOnceAsync(exe, new[] { "-X", "utf8", _scriptPath, "--capabilities" },
                                                            TimeSpan.FromMinutes(2));
            if (exitCode != 0) return Unavailable($"能力探测退出码 {exitCode}");
            var line = LastPrefix(stdout, "##CAP## ");
            if (line == null) return Unavailable("能力探测无输出（缺少 ##CAP## 行）");
            return MapCaps(JsonNode.Parse(line)!.AsObject(), exe);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "GIS 能力探测失败");
            return Unavailable($"能力探测失败：{e.Message}");
        }
    }

    private JsonObject MapCaps(JsonObject m, string exe)
    {
        static bool B(JsonObject s, string k) => s[k]?.GetValueKind() == JsonValueKind.True;
        var rasterio = B(m, "rasterio");
        var demPresent = B(m, "dem_present");
        var roadsGdb = B(m, "roads_gdb_present");
        var airports = B(m, "airports_present");
        var vectorOk = B(m, "vector_ok");
        return new JsonObject
        {
            ["available"] = JsonValue.Create(true),
            ["engine"] = JsonValue.Create(rasterio && demPresent ? "python-hexgen" : "synthetic"),
            ["python"] = JsonValue.Create(exe),
            ["python_version"] = m["python"]?.DeepClone(),
            ["rasterio"] = JsonValue.Create(rasterio),
            ["gdal_version"] = m["version"]?.DeepClone(),
            ["reason"] = m["reason"]?.DeepClone(),
            ["script"] = JsonValue.Create(_scriptPath),
            ["gis_dir"] = JsonValue.Create(_gisDir),
            ["dem_tif"] = m["dem_tif"]?.DeepClone(),
            ["dem_present"] = JsonValue.Create(demPresent),
            ["dem_info"] = m["dem_info"]?.DeepClone(),
            ["landcover_online"] = JsonValue.Create(rasterio),
            ["water_gpkg"] = m["water_gpkg"]?.DeepClone(),
            ["water_present"] = JsonValue.Create(B(m, "water_present")),
            ["water_vector_ok"] = JsonValue.Create(B(m, "water_vector_ok")),
            ["water_reason"] = m["water_reason"]?.DeepClone(),
            ["vector_ok"] = JsonValue.Create(vectorOk),
            ["vector_reason"] = m["vector_reason"]?.DeepClone(),
            ["roads_gdb"] = m["roads_gdb"]?.DeepClone(),
            ["roads_gdb_present"] = JsonValue.Create(roadsGdb),
            ["airports_shp"] = m["airports_shp"]?.DeepClone(),
            ["airports_present"] = JsonValue.Create(airports),
            ["network_ok"] = JsonValue.Create(vectorOk && (roadsGdb || airports)),
        };
    }

    private JsonObject Unavailable(string reason) => new()
    {
        ["available"] = JsonValue.Create(false),
        ["engine"] = JsonValue.Create("synthetic"),
        ["python"] = JsonValue.Create((string?)null),
        ["rasterio"] = JsonValue.Create(false),
        ["gdal_version"] = JsonValue.Create((string?)null),
        ["reason"] = JsonValue.Create(reason),
        ["script"] = JsonValue.Create(_scriptPath),
        ["gis_dir"] = JsonValue.Create(_gisDir),
        ["dem_tif"] = JsonValue.Create((string?)null),
        ["dem_present"] = JsonValue.Create(false),
        ["landcover_online"] = JsonValue.Create(false),
        ["water_present"] = JsonValue.Create(File.Exists(Path.Combine(_repoRoot, "data", "water", "global_water.gpkg"))),
        ["water_vector_ok"] = JsonValue.Create(false),
        ["vector_ok"] = JsonValue.Create(false),
        ["roads_gdb"] = JsonValue.Create(Path.Combine(_gisDir, "roads_pkg", "v10", "roads.gdb")),
        ["roads_gdb_present"] = JsonValue.Create(Directory.Exists(Path.Combine(_gisDir, "roads_pkg", "v10", "roads.gdb"))),
        ["airports_shp"] = JsonValue.Create(Path.Combine(_repoRoot, "data", "airports", "ne_10m_airports.shp")),
        ["airports_present"] = JsonValue.Create(File.Exists(Path.Combine(_repoRoot, "data", "airports", "ne_10m_airports.shp"))),
        ["network_ok"] = JsonValue.Create(false),
    };

    private async Task<string?> FindPythonAsync()
    {
        var fromEnv = Environment.GetEnvironmentVariable("HEXGEN_PYTHON")?.Trim();
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        lock (_gate) if (_pythonExe != null) return _pythonExe;
        foreach (var cand in new[] { "python", "py", "python3" })
        {
            try
            {
                var (_, _, code) = await RunOnceAsync(cand, new[] { "--version" }, TimeSpan.FromSeconds(10));
                if (code == 0)
                {
                    lock (_gate) _pythonExe = cand;
                    return cand;
                }
            }
            catch { /* 继续试下一个候选 */ }
        }
        return null;
    }

    // ==================== 格网方案 ====================

    /// <summary>方案计算（进程内，与生成同一份几何）；同一 (bbox, cell) 命中缓存直接回。</summary>
    public JsonObject Plan(double[] bbox, double? cellKm)
    {
        var key = string.Join(",", bbox.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) +
                  ":" + (cellKm?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
        lock (_gate) if (_planCache.TryGetValue(key, out var hit)) return hit;
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
        return plan;
    }

    // ==================== 作业 ====================

    public async Task<GisJob> StartJobAsync(GisJobSpec spec)
    {
        // 用同一份几何预估格数：客户端算错也不要把服务端拖死
        var (gw, gh) = GisPlanService.GridSize(spec.Bbox.ToArray(), spec.CellKm);
        var cells = gw * gh;
        if (cells > GisPlanService.MaxCells)
            throw new GisPlanException($"该范围会有 {cells} 格，超过上限 {GisPlanService.MaxCells}；请扩大对边距或缩小范围框");

        var exe = await FindPythonAsync();
        lock (_gate)
        {
            var id = "G" + (++_seq).ToString("D4", CultureInfo.InvariantCulture);
            var job = new GisJob { Id = id, Spec = spec, PkgName = UniquePkgName(spec.Name) };
            _jobs[id] = job;
            _queue.Enqueue(job);
            DrainLocked(exe);
            return job;
        }
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

    private void DrainLocked(string? exe)
    {
        if (_running != null || _queue.Count == 0) return;
        var job = _queue.Dequeue();
        _running = job;
        _ = Task.Run(async () =>
        {
            try { await ExecuteAsync(job, exe); }
            finally
            {
                lock (_gate) { _running = null; DrainLocked(_pythonExe ?? exe); }
            }
        });
    }

    private async Task ExecuteAsync(GisJob job, string? exe)
    {
        job.Status = GisJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        Log(job, $"作业开始：范围 {string.Join(", ", job.Spec.Bbox.Select(Num))}° · 对边距 {Num(job.Spec.CellKm)} km");
        try
        {
            if (exe != null && File.Exists(_scriptPath))
            {
                Log(job, "引擎：tools/gis/hexgen.py（本地栅格/矢量真实数据）");
                await RunPythonAsync(job, exe);
            }
            else
            {
                Log(job, "引擎：原生 C# 合成地形（未找到 Python 或 hexgen.py ⇒ 高程/覆盖为确定性噪声代理）");
                await Task.Run(() => RunNative(job));
            }
            var pkgPath = Path.Combine(_gisDir, job.PkgName + ".json");
            if (job.Status == GisJobStatus.Done && !File.Exists(pkgPath))
            {
                job.Status = GisJobStatus.Failed;
                job.Error = $"生成器报告成功，但 {pkgPath} 不存在（回读校验失败）";
                Log(job, "失败：" + job.Error);
            }
        }
        catch (Exception e)
        {
            job.Status = GisJobStatus.Failed;
            job.Error = e.Message;
            Log(job, "失败：" + e.Message);
        }
        if (job.Status == GisJobStatus.Done)
        {
            Log(job, $"完成：data/gis/{job.PkgName}.json（{MetaInt(job, "width")}×{MetaInt(job, "height")} 格）");
            PublishToHexGrid(job);   // 副本统一只用服务器网络包：生成完成即存储为 data/hexgrids/<id>.zip
        }
        job.FinishedAt = DateTime.UtcNow;
    }

    /// <summary>生成完成即发布为服务器网络包（转换 + zip 落盘 + 地图包目录表登记）；失败只记日志不影响数据包本身。</summary>
    private void PublishToHexGrid(GisJob job)
    {
        try
        {
            var pkgPath = Path.Combine(_gisDir, job.PkgName + ".json");
            var pkg = JsonSerializer.Deserialize<HexPackage>(File.ReadAllText(pkgPath), PackageJson);
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

    private static int MetaInt(GisJob job, string key) =>
        job.Meta?[key] is { } v && int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private async Task RunPythonAsync(GisJob job, string exe)
    {
        var spec = job.Spec;
        var args = new List<string>
        {
            "-X", "utf8", _scriptPath,
            "--bbox",
        };
        args.AddRange(spec.Bbox.Select(Num));
        args.AddRange(new[]
        {
            "--cell-km", Num(spec.CellKm),
            "--out", $"data/gis/{job.PkgName}.json",
            "--dem", spec.Dem,
            "--landuse", spec.Landuse,
            "--roads", spec.Roads ? "on" : "off",
            "--water", spec.Water ? "auto" : "off",
            "--network", spec.Network ? "auto" : "off",
            "--features", spec.Features ? "on" : "off",
            "--seed", spec.Seed.ToString(CultureInfo.InvariantCulture),
            "--print-meta",
        });
        if (spec.Offline) args.Add("--offline");

        using var proc = Start(exe, args);
        proc.OutputDataReceived += (_, e) =>
        {
            var t = e.Data?.TrimEnd();
            if (string.IsNullOrEmpty(t)) return;
            if (!t.StartsWith("##META## ", StringComparison.Ordinal)) { Log(job, t); return; }
            try { job.Meta = JsonNode.Parse(t.Substring(9))?.AsObject(); }
            catch { /* 元信息坏掉只是显示问题 */ }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            var t = e.Data?.TrimEnd();
            if (!string.IsNullOrEmpty(t)) Log(job, "! " + t);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(JobTimeoutMinutes));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            job.Status = GisJobStatus.Failed;
            job.Error = $"生成超时（{JobTimeoutMinutes} 分钟）已终止";
            Log(job, "失败：" + job.Error);
            return;
        }

        var code = proc.ExitCode;
        var ok = job.Meta?["ok"]?.GetValueKind() == JsonValueKind.True;
        if (code != 0 || !ok)
        {
            job.Status = GisJobStatus.Failed;
            var fails = MetaInt(job, "fails");
            job.Error = fails > 0
                ? $"自校验 {fails} 项未通过，未产出数据包"
                : code == 0 ? "生成器未回报成功（缺 ##META## ok）" : $"生成进程退出码 {code}";
            Log(job, "失败：" + job.Error);
            return;
        }
        job.Status = GisJobStatus.Done;
    }

    private void RunNative(GisJob job)
    {
        var spec = job.Spec;
        var pkg = HexGenService.Generate(new GenSpec
        {
            Name = job.PkgName,
            Bbox = spec.Bbox.ToArray(),
            CellKm = spec.CellKm,
            Seed = spec.Seed,
            Offline = spec.Offline,
            UseRealDem = !spec.Offline && spec.Dem != "off",
            Features = spec.Features,
        });
        var report = HexFront.Core.Validation.HexPackageValidator.Validate(pkg);
        var counts = pkg.Meta.Stats.GetValueOrDefault("terrain_counts");
        job.Meta = new JsonObject
        {
            ["ok"] = JsonValue.Create(report.Fails == 0),
            ["file"] = JsonValue.Create(Path.Combine(_gisDir, job.PkgName + ".json")),
            ["name"] = JsonValue.Create(job.PkgName + ".json"),
            ["source"] = JsonValue.Create(pkg.Meta.Source),
            ["cell_km"] = JsonValue.Create(spec.CellKm),
            ["width"] = JsonValue.Create(pkg.Meta.Width),
            ["height"] = JsonValue.Create(pkg.Meta.Height),
            ["bbox"] = ArrNum(pkg.Meta.Bbox),
            ["fails"] = JsonValue.Create(report.Fails),
            ["warns"] = JsonValue.Create(report.Warns),
            ["layers"] = LayerKinds(pkg.Meta.Layers),
            ["terrain_counts"] = counts == null ? null : JsonSerializer.SerializeToNode(counts, PackageJson),
            ["generated_at"] = JsonValue.Create(pkg.Meta.GeneratedAt),
            ["errors"] = ArrStr(report.Results.Where(r => r.Level == "FAIL").Select(r => r.Check)),
        };
        // 自校验不过就不落盘：与 hexgen.py 一样不产出半成品
        if (report.Fails > 0)
        {
            job.Status = GisJobStatus.Failed;
            job.Error = $"自校验 {report.Fails} 项未通过，未产出数据包";
            Log(job, "失败：" + job.Error);
            return;
        }
        File.WriteAllText(Path.Combine(_gisDir, job.PkgName + ".json"), JsonSerializer.Serialize(pkg, PackageJson));
        Log(job, $"自校验 {report.Total} 项通过（{report.Warns} 项 WARN）");
        job.Status = GisJobStatus.Done;
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

    private static JsonObject LayerKinds(Dictionary<string, object> layers)
    {
        var o = new JsonObject();
        foreach (var (k, v) in layers) o[k] = JsonValue.Create(v?.ToString() ?? "");
        return o;
    }

    private static string Num(double v) =>
        v.ToString(v == Math.Round(v) ? "F0" : "R", CultureInfo.InvariantCulture);

    private Process Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return Process.Start(psi) ?? throw new GisPlanException("无法启动进程 " + exe);
    }

    private async Task<(string stdout, string stderr, int exitCode)> RunOnceAsync(string exe, string[] args, TimeSpan timeout)
    {
        using var proc = Start(exe, args);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new GisPlanException($"{exe} 超时（{timeout.TotalSeconds:F0}s）");
        }
        var so = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        var se = await proc.StandardError.ReadToEndAsync(cts.Token);
        return (so, se, proc.ExitCode);
    }

    private static string? LastPrefix(string text, string prefix)
    {
        string? hit = null;
        foreach (var line in text.Split('\n'))
            if (line.StartsWith(prefix, StringComparison.Ordinal)) hit = line;
        return hit == null ? null : hit.Substring(prefix.Length).Trim();
    }

    private void Log(GisJob job, string line)
    {
        lock (job.Log)
        {
            job.Log.Add(line);
            while (job.Log.Count > MaxLogLines) job.Log.RemoveAt(0);
        }
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
