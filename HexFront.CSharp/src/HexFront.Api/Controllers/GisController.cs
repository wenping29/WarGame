using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using HexFront.Api.Services;
using HexFront.Core.Models;
using HexFront.Core.Services;
using HexFront.Core.Validation;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>
/// GIS 端点：能力探测 / 格网方案 / 生成作业 / 数据包清单与校验。
/// 线上字段一律 snake_case（与 tools/gis/hexgen.py 的 ##PLAN##/##CAP## 以及 web、React 向导同一口径），
/// 失败一律 {"error": "…"} —— 所以这里自己读原始 JSON，不走 [ApiController] 的模型绑定与 ProblemDetails。
/// </summary>
[Route("api/gis")]
public sealed class GisController : ControllerBase
{
    private readonly GisService _gis;
    

    public GisController(GisService gis)
    {
        _gis = gis;

    }

    /// <summary>GET /api/gis/capabilities — 本机 GIS 工具链与数据可用性（refresh=1 强制重探）。</summary>
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(bool refresh = false)
        => Json(await _gis.CapabilitiesAsync(refresh));

    /// <summary>GET /api/gis/plan?w&amp;s&amp;e&amp;n&amp;cell_km — 推荐对边距 + 档位阶梯（设计文档 §6.2）。</summary>
    [HttpGet("plan")]
    public IActionResult Plan(string? w = null, string? s = null, string? e = null, string? n = null,
                             string? cell_km = null)
    {
        try
        {
            return PlanResult(GisService.BboxFromQuery(w, s, e, n), GisService.CellKmFromQuery(cell_km));
        }
        catch (GisPlanException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>POST /api/gis/plan — 同一方案的 body 入口（设计文档 §13.1）：body {bbox:[西,南,东,北], cell_km}。</summary>
    [HttpPost("plan")]
    public async Task<IActionResult> PlanPost()
    {
        var body = await ReadBodyAsync();
        if (body?["bbox"] is not JsonArray { Count: 4 } bb) return Error("bbox 需要 [西, 南, 东, 北]");
        var nums = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (bb[i]?.GetValueKind() != JsonValueKind.Number) return Error("bbox 需要四个数值（西 南 东 北）");
            nums[i] = bb[i]!.GetValue<double>();
        }
        try
        {
            return PlanResult(nums, Num(body, "cell_km") ?? Num(body, "cellKm"));
        }
        catch (GisPlanException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>GET /api/gis/jobs — 最近作业（新→旧，最多 30 条）。</summary>
    [HttpGet("jobs")]
    public IActionResult Jobs()
    {
        var arr = new JsonArray();
        foreach (var j in _gis.Jobs()) arr.Add(j);
        return Json(new JsonObject { ["jobs"] = arr });
    }

    /// <summary>POST /api/gis/jobs — 入队生成作业，直接回完整作业对象（前端拿 id 轮询）。</summary>
    [HttpPost("jobs")]
    public async Task<IActionResult> StartJob()
    {
        try
        {
            var job = await _gis.StartJobAsync(GisService.ParseSpec(await ReadBodyAsync()));
            return Json(JobJson.ToNode(job));
        }
        catch (GisPlanException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>POST /api/gis/generate — /jobs 的别名（设计文档 §13.1 的写法）。</summary>
    [HttpPost("generate")]
    public Task<IActionResult> GeneratePost() => StartJob();

    /// <summary>GET /api/gis/jobs/{id} — 轮询作业状态。</summary>
    [HttpGet("jobs/{id}")]
    public IActionResult GetJob(string id)
    {
        var job = _gis.Job(id);
        return job == null
            ? Error("作业不存在（服务端重启后作业记录会清空，数据包仍在）", 404)
            : Json(JobJson.ToNode(job));
    }

    /// <summary>GET /api/gis/packages — data/gis/ 下所有数据包摘要（与 /api/scenarios 的 gis 列表同形）。</summary>
    [HttpGet("packages")]
    public IActionResult Packages()
    {
        var arr = new JsonArray();
        foreach (var (fileName, m) in PackageMetas())
        {
            var width = Int(m, "width");
            var height = Int(m, "height");
            var source = Str(m, "source");
            var cellKm = Dbl(m, "cell_km");
            var scale = Dbl(m, "mercator_scale");
            arr.Add(new JsonObject
            {
                ["name"] = JsonValue.Create(fileName),
                ["label"] = JsonValue.Create(
                    $"({source ?? "?"}) {(cellKm?.ToString(CultureInfo.InvariantCulture) ?? "?")}km/格 · {width}×{height}"),
                ["source"] = JsonValue.Create(source),
                ["cell_km"] = JsonValue.Create(cellKm),
                ["width"] = JsonValue.Create(width),
                ["height"] = JsonValue.Create(height),
                ["cells"] = width is { } w && height is { } h ? JsonValue.Create(w * h) : null,
                ["bbox"] = m["bbox"]?.DeepClone(),
                ["center_lonlat"] = m["center_lonlat"]?.DeepClone(),
                ["mercator_scale"] = JsonValue.Create(scale),
                ["legacy"] = JsonValue.Create(scale == null),   // 缺墨卡托放大 = 旧包（按 1.0 处理）
                ["layers"] = m["layers"]?.DeepClone(),
                ["stats"] = m["stats"]?.DeepClone(),
                ["generated_at"] = JsonValue.Create(Str(m, "generated_at")),
            });
        }
        return Json(new JsonObject { ["packages"] = arr });
    }

    /// <summary>GET /api/gis/packages/{name}/meta — 单个数据包完整元信息。</summary>
    [HttpGet("packages/{name}/meta")]
    public IActionResult PackageMeta(string name)
    {
        var (meta, error, code) = LoadMeta(name);
        return error != null ? Error(error, code) : Json(meta!.DeepClone());
    }

    /// <summary>POST /api/gis/validate — 对已落盘数据包跑 37 项自校验；body {package_name}。</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> ValidatePackage()
    {
        var body = await ReadBodyAsync();
        var name = Str(body, "package_name") ?? Str(body, "packageName");
        if (string.IsNullOrWhiteSpace(name)) return Error("package_name 必填");
        var path = ResolvePackagePath(name!, out var error, out var code);
        if (path == null) return Error(error!, code);
        try
        {
            var pkg = JsonSerializer.Deserialize<HexPackage>(System.IO.File.ReadAllText(path), PackageBind);
            if (pkg == null || pkg.Hexes.Count == 0) return Error("数据包解析失败（0 格）");
            return Json(ReportJson(HexPackageValidator.Validate(pkg), Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            return Error("校验失败：" + ex.Message);
        }
    }

    // ==================== 内部 ====================

    private IActionResult PlanResult(double[] bbox, double? cellKm)
    {
        var plan = _gis.Plan(bbox, cellKm);
        return plan["ok"]?.GetValueKind() == JsonValueKind.True
            ? Json(plan)
            : Error(Str(plan, "error") ?? "方案计算失败");
    }

    private static JsonObject ReportJson(ValidationReport report, string file)
    {
        var results = new JsonArray();
        foreach (var r in report.Results)
        {
            results.Add(new JsonObject
            {
                ["level"] = JsonValue.Create(r.Level),
                ["check"] = JsonValue.Create(r.Check),
                ["detail"] = JsonValue.Create(r.Detail),
            });
        }
        return new JsonObject
        {
            ["file"] = JsonValue.Create(file),
            ["total"] = JsonValue.Create(report.Total),
            ["fails"] = JsonValue.Create(report.Fails),
            ["warns"] = JsonValue.Create(report.Warns),
            ["passes"] = JsonValue.Create(report.Passes),
            ["results"] = results,
        };
    }

    private List<(string FileName, JsonObject Meta)> PackageMetas()
    {
        var dir = _gis.GisDirectory;
        var outList = new List<(string, JsonObject)>();
        if (!Directory.Exists(dir)) return outList;
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(System.IO.File.ReadAllText(file))?["meta"] is JsonObject m)
                    outList.Add((Path.GetFileName(file), m));
            }
            catch
            {
                /* 坏包不进列表 */
            }
        }
        return outList;
    }

    private (JsonObject? Meta, string? Error, int Code) LoadMeta(string name)
    {
        var path = ResolvePackagePath(name, out var error, out var code);
        if (path == null) return (null, error, code);
        try
        {
            var node = JsonNode.Parse(System.IO.File.ReadAllText(path));
            if (node?["meta"] is JsonObject m) return (m, null, 200);
            return (null, $"数据包 '{name}' 缺 meta", 500);
        }
        catch (Exception ex)
        {
            return (null, $"数据包读取失败：{ex.Message}", 500);
        }
    }

    /// <summary>包名可带可不带 .json；只允许安全字符，绝不接受路径分隔符。</summary>
    private string? ResolvePackagePath(string rawName, out string? error, out int code)
    {
        error = null;
        var name = rawName.Trim();
        if (name.Length == 0 || Path.GetFileName(name) != name || name.Contains("..") || name.Contains(':'))
        {
            error = "包名不合法";
            code = 400;
            return null;
        }
        var path = Path.Combine(_gis.GisDirectory, name);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
        if (!System.IO.File.Exists(path))
        {
            error = $"数据包 '{name}' 不存在";
            code = 404;
            return null;
        }
        code = 200;
        return path;
    }

    private async Task<JsonNode?> ReadBodyAsync()
    {
        try { return await JsonSerializer.DeserializeAsync<JsonNode>(Request.Body); }
        catch (JsonException) { return null; }   // 空体/坏体由各自分支报 {"error":…}
    }

    private IActionResult Json(JsonNode node) => new JsonResult(node);

    private IActionResult Error(string message, int code = 400) =>
        new JsonResult(new JsonObject { ["error"] = JsonValue.Create(message) }) { StatusCode = code };

    private static readonly JsonSerializerOptions PackageBind = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static double? Num(JsonNode? node, string key) =>
        node?[key]?.GetValueKind() == JsonValueKind.Number ? node![key]!.GetValue<double>() : null;

    private static string? Str(JsonNode? node, string key) =>
        node?[key]?.GetValueKind() == JsonValueKind.String ? node![key]!.GetValue<string>() : null;

    private static int? Int(JsonObject m, string key) =>
        m[key]?.GetValueKind() == JsonValueKind.Number
            ? Convert.ToInt32(m[key]!.GetValue<double>(), CultureInfo.InvariantCulture) : null;

    private static double? Dbl(JsonObject m, string key) =>
        m[key]?.GetValueKind() == JsonValueKind.Number ? m[key]!.GetValue<double>() : null;
}
