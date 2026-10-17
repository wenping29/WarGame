using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Api.Services;
using HexFront.Core.Models;
using HexFront.Core.Services;
using static HexFront.Core.Services.ScenarioService;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>
/// Session/lobby REST endpoints matching the web/js/api.js contract:
/// scenarios(meta)/rooms/room/{code}/frames/{code}/validate/instances/saves/load.
/// </summary>
[ApiController]
[Route("api")]
public class GameController : ControllerBase
{
    private readonly RoomService _rooms;
    private readonly ILogger<GameController> _logger;

    private static readonly JsonSerializerOptions LibJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public GameController(RoomService rooms, ILogger<GameController> logger)
    {
        _rooms = rooms;
        _logger = logger;
    }

    // ==================== GET /api/scenarios (meta aggregate) ====================

    [HttpGet("scenarios")]
    public Dictionary<string, object?> Scenarios()
    {
        return new Dictionary<string, object?>
        {
            ["scenarios"] = ListScenarioSummaries(),
            ["gis"] = ListGisSummaries(),
            ["saves"] = ListSaveSummaries(),
            ["weather"] = Dump(LibraryService.WEATHER_JSON.Values),
            ["unit_types"] = Dump(LibraryService.UNIT_TYPES.Values),
            ["terrains"] = Dump(LibraryService.STANDARD_TERRAIN.Values),
            ["rules_default"] = DumpOne(LibraryService.DEFAULT_RULES),
            ["tiles"] = new Dictionary<string, object?>
            {
                ["provider"] = "tianditu",
                ["tk"] = TiandituTk(),
            },
        };
    }

    // ==================== Rooms ====================

    [HttpGet("rooms")]
    public Dictionary<string, object?> Rooms() => new()
    {
        ["rooms"] = _rooms.ListRooms(),
    };

    [HttpGet("room/{code}")]
    public IActionResult Room(string code)
    {
        var info = _rooms.PublicInfo(code);
        return info == null ? NotFoundDict("房间不存在") : Ok(info);
    }

    [HttpGet("frames/{code}")]
    public IActionResult Frames(string code)
    {
        var frames = _rooms.Frames(code);
        if (frames == null) return NotFoundDict("房间不存在（重启后请用存档回放）");
        var info = _rooms.PublicInfo(code);
        return Ok(new Dictionary<string, object?>
        {
            ["code"] = info != null ? info["code"] : code.ToUpperInvariant(),
            ["frames"] = frames,
        });
    }

    // ==================== POST /api/validate ====================

    [HttpPost("validate")]
    public IActionResult Validate(Dictionary<string, JsonElement> body)
    {
        var scenarioId = Str(body, "scenario");
        var cfg = string.IsNullOrEmpty(scenarioId) ? null : LoadMergedConfig(scenarioId, BodyOverrides(body));
        if (cfg == null)
        {
            return Ok(new Dictionary<string, object?>
            {
                ["errors"] = new List<string> { "剧本不存在" },
                ["preview"] = new Dictionary<string, object?> { ["sides"] = new Dictionary<string, object?>() },
                ["type_points"] = new Dictionary<string, object?>(),
            });
        }

        var errs = ValidateScenario(cfg);
        return Ok(new Dictionary<string, object?>
        {
            ["errors"] = errs,
            ["preview"] = OobPreview(cfg),
            ["type_points"] = new Dictionary<string, object?>(), // unit_type_defs overrides not yet supported
        });
    }

    // ==================== POST /api/instances ====================

    [HttpPost("instances")]
    public IActionResult CreateInstance(Dictionary<string, JsonElement> body)
    {
        var scenarioId = Str(body, "scenario");
        if (string.IsNullOrEmpty(scenarioId))
            return BadRequestDict("缺少 scenario", null);

        var path = FindScenarioPath(scenarioId);
        if (path == null)
            return NotFoundDict($"剧本不存在: {scenarioId}");

        ScenarioConfig? cfg;
        try
        {
            cfg = LoadMergedConfigJson(System.IO.File.ReadAllText(path), BodyOverrides(body));
        }
        catch (Exception ex)
        {
            return BadRequestDict($"剧本解析失败: {ex.Message}", null);
        }
        if (cfg == null) return BadRequestDict($"剧本不存在: {scenarioId}", null);

        var errs = ValidateScenario(cfg);
        if (errs.Count > 0 && !Bool(body, "force", false))
            return BadRequestDict("剧本/覆盖校验未通过", errs);

        // 不传 seed 就用当前秒数：同 seed + 同指令序列必然同结果，复现一局只要抄下这里回给的 seed
        var seed = UInt(body, "seed", (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & uint.MaxValue));
        GameState g;
        try
        {
            g = ScenarioService.CreateGameState(cfg, _rooms.GisDirectory, seed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "instance creation failed for {Scenario}", scenarioId);
            return StatusCode(500, new Dictionary<string, object?> { ["error"] = "创建副本失败: " + ex.Message });
        }

        if (body.TryGetValue("fog", out var fogEl) && fogEl.ValueKind == JsonValueKind.False)
            g.Flags["fog"] = false;

        var name = Str(body, "name");
        var code = _rooms.CreateRoomFromState(g, name, Bool(body, "ai_red"), Bool(body, "ai_blue"));

        try
        {
            StoreService.WriteInit(g, name, Str(body, "description"), _rooms.SaveDirectory,
                BodyOverrides(body));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "initial save failed for room {Code}", code);
        }

        return Ok(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["instance"] = g.InstanceId,
            ["seed"] = g.Seed,
            ["room"] = _rooms.PublicInfo(code),
        });
    }

    // ==================== POST /api/saves/load ====================

    [HttpPost("saves/load")]
    public IActionResult LoadSave(Dictionary<string, JsonElement> body)
    {
        var id = Str(body, "instance");
        if (string.IsNullOrEmpty(id))
            return BadRequestDict("缺少 instance", null);

        StoreService.LoadOutcome loaded;
        try
        {
            // 最新快照 + 重放尾部指令：重放走的是同一个种子随机源，所以续局结果与不中断时一致
            loaded = StoreService.Load(id, _rooms.SaveDirectory);
        }
        catch (ArgumentException ex)
        {
            return BadRequestDict(ex.Message, null);
        }
        catch (Exception)
        {
            return NotFoundDict("存档不存在或损坏");
        }

        var g = loaded.Game;
        if (loaded.FingerprintMismatches.Count > 0)
            _logger.LogWarning("存档 {Instance} 重放指纹不一致：{Detail}", id, string.Join("; ", loaded.FingerprintMismatches));

        var code = _rooms.CreateRoomFromState(g);
        return Ok(new Dictionary<string, object?>
        {
            ["code"] = code,
            ["resumed_turn"] = g.Turn,
            ["snapshot_turn"] = loaded.SnapshotTurn,
            ["replayed_turns"] = loaded.ReplayedTurns,
            ["fingerprint"] = g.Fingerprint(),
            ["replay_warnings"] = loaded.FingerprintMismatches,
            ["room"] = _rooms.PublicInfo(code),
        });
    }

    // ==================== Scenario listing / validation ====================

    private List<Dictionary<string, object?>> ListScenarioSummaries()
    {
        var outList = new List<Dictionary<string, object?>>();
        if (!Directory.Exists(_rooms.ScenarioDirectory)) return outList;

        foreach (var file in Directory.GetFiles(_rooms.ScenarioDirectory, "*.json").OrderBy(f => f))
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                var m = doc.RootElement;
                if (m.ValueKind != JsonValueKind.Object) continue;
                outList.Add(new Dictionary<string, object?>
                {
                    ["file"] = Path.GetFileName(file),
                    ["id"] = PropStr(m, "id"),
                    ["name"] = PropStr(m, "name"),
                    ["era"] = PropStr(m, "era"),
                    ["scale"] = PropStr(m, "scale"),
                    ["description"] = PropStr(m, "description"),
                    ["gis_package"] = PropStr(m, "gis_package"),
                    ["environment"] = m.TryGetProperty("environment", out var env) ? env.Clone() : null,
                    ["max_turns"] = m.TryGetProperty("max_turns", out var mt) ? mt.GetInt32() : (int?)null,
                    ["sides"] = m.TryGetProperty("sides", out var sides) && sides.ValueKind == JsonValueKind.Object
                        ? sides.EnumerateObject().Select(p => p.Name).ToList()
                        : null,
                    ["unit_count"] = OobUnitCount(m),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "skip invalid scenario {File}", file);
            }
        }
        return outList;
    }

    private static int OobUnitCount(JsonElement scenarioRoot)
    {
        if (!scenarioRoot.TryGetProperty("sides", out var sides) || sides.ValueKind != JsonValueKind.Object)
            return 0;
        var count = 0;
        foreach (var side in sides.EnumerateObject())
        {
            if (side.Value.TryGetProperty("oob", out var oob) && oob.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in oob.EnumerateArray())
                {
                    count += e.TryGetProperty("qty", out var qty) && qty.TryGetInt32(out var q) ? q : 1;
                }
            }
        }
        return count;
    }

    private List<Dictionary<string, object?>> ListGisSummaries()
    {
        var outList = new List<Dictionary<string, object?>>();
        if (!Directory.Exists(_rooms.GisDirectory)) return outList;

        foreach (var file in Directory.GetFiles(_rooms.GisDirectory, "*.json").OrderBy(f => f))
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Path.GetFileName(file);
                var w = PropInt(meta, "width");
                var h = PropInt(meta, "height");
                var source = PropStr(meta, "source");
                var cellKm = PropDbl(meta, "cell_km");
                outList.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["label"] = $"({source ?? "?"}) {cellKm?.ToString() ?? "?"}km/格 · {w ?? '?'}×{h ?? '?'}",
                    ["source"] = source,
                    ["cell_km"] = cellKm,
                    ["width"] = w,
                    ["height"] = h,
                    ["cells"] = w != null && h != null ? w * h : null,
                    ["bbox"] = meta.TryGetProperty("bbox", out var bb) ? bb.Clone() : null,
                    ["center_lonlat"] = meta.TryGetProperty("center_lonlat", out var cl) ? cl.Clone() : null,
                    ["mercator_scale"] = PropDbl(meta, "mercator_scale"),
                    ["radius_m"] = PropDbl(meta, "radius_m"),
                    ["mercator_ox"] = PropDbl(meta, "mercator_ox"),
                    ["mercator_oy"] = PropDbl(meta, "mercator_oy"),
                    ["stats"] = meta.TryGetProperty("stats", out var st) ? st.Clone() : null,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "skip invalid gis package {File}", file);
            }
        }
        return outList;
    }

    private List<Dictionary<string, object?>> ListSaveSummaries()
    {
        var outList = new List<Dictionary<string, object?>>();
        foreach (var m in StoreService.ListSaves(_rooms.SaveDirectory))
        {
            outList.Add(new Dictionary<string, object?>
            {
                ["instance"] = m.Instance,
                ["name"] = m.Name,
                ["scenario"] = m.Scenario,
                ["scenario_name"] = m.ScenarioName,
                ["last_turn"] = m.LastTurn,
                ["max_turns"] = m.MaxTurns,
                ["seed"] = m.Seed,
                ["created_at"] = m.CreatedAt.ToString("o"),
                ["updated_at"] = m.UpdatedAt.ToString("o"),
                ["turns_recorded"] = m.TurnsRecorded,
                ["snapshot_turn"] = m.SnapshotTurn,
                ["description"] = m.Description,
            });
        }
        return outList;
    }

    /// <summary>Scenario validity checks ported from Dart Scenario.validate (sides/oob/types/coords/budget/weather).</summary>
    private static List<string> ValidateScenario(ScenarioConfig cfg)
    {
        var errs = new List<string>();
        if (cfg.Sides == null || cfg.Sides.Count == 0)
        {
            errs.Add("缺少 sides 阵营定义");
        }
        else
        {
            foreach (var (side, sc) in cfg.Sides)
            {
                if (sc.Oob.Count == 0)
                {
                    errs.Add($"阵营 {side} 的 oob 为空");
                    continue;
                }
                double used = 0;
                foreach (var e in sc.Oob)
                {
                    if (LibraryService.UNIT_TYPES.TryGetValue(e.Type, out var t))
                        used += t.Points * e.Qty;
                    else
                        errs.Add($"阵营 {side} 引用未知兵种 {e.Type}");
                    if (e.At == null)
                        errs.Add($"阵营 {side} 单位 {e.Type} 缺少坐标 at");
                }
                if (sc.PointsBudget > 0 && used > sc.PointsBudget + 0.001)
                    errs.Add($"阵营 {side} OOB 点数 {used:F1} 超出预算 {sc.PointsBudget:F1}");
            }
        }

        var initial = cfg.Weather?.Initial;
        if (initial != null && !LibraryService.WEATHER_JSON.ContainsKey(initial))
            errs.Add($"未知天气 {initial}");

        foreach (var vc in cfg.VictoryConds)
        {
            if (!ScenarioService.IsVictoryKind(vc.Kind))
                errs.Add($"未知胜利条件种类 {vc.Kind}（可用：annihilate / capture / nodes / survive）");
            if (vc.Kind == "capture" && string.IsNullOrWhiteSpace(vc.Target))
                errs.Add("capture 胜利条件缺少 target 格坐标");
        }

        return errs;
    }

    private static Dictionary<string, object?> OobPreview(ScenarioConfig cfg)
    {
        var sides = new Dictionary<string, object?>();
        if (cfg.Sides != null)
        {
            foreach (var (side, sc) in cfg.Sides)
            {
                double pts = 0;
                var units = 0;
                foreach (var e in sc.Oob)
                {
                    units += e.Qty;
                    if (LibraryService.UNIT_TYPES.TryGetValue(e.Type, out var t))
                        pts += t.Points * e.Qty;
                }
                sides[side] = new Dictionary<string, object?>
                {
                    ["points"] = pts,
                    ["budget"] = sc.PointsBudget,
                    ["units"] = units,
                    ["name"] = sc.Name,
                };
            }
        }
        return new Dictionary<string, object?> { ["sides"] = sides };
    }

    // ==================== Scenario load + overrides ====================

    private string? FindScenarioPath(string idOrFile)
    {
        if (!Directory.Exists(_rooms.ScenarioDirectory)) return null;
        var files = Directory.GetFiles(_rooms.ScenarioDirectory, "*.json");
        foreach (var file in files)
        {
            var fname = Path.GetFileName(file);
            if (fname == idOrFile || fname.Equals(idOrFile + ".json", StringComparison.OrdinalIgnoreCase))
                return file;
        }
        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("id", out var id) && id.GetString() == idOrFile)
                    return file;
            }
            catch { /* skip */ }
        }
        return null;
    }

    private ScenarioConfig? LoadMergedConfig(string scenarioId, JsonElement? overrides)
    {
        var path = FindScenarioPath(scenarioId);
        return path == null ? null : LoadMergedConfigJson(System.IO.File.ReadAllText(path), overrides);
    }

    private static ScenarioConfig? LoadMergedConfigJson(string scenarioJson, JsonElement? overrides)
    {
        var node = JsonNode.Parse(scenarioJson)?.AsObject()
            ?? throw new InvalidOperationException("剧本 JSON 无效");
        if (overrides is { } ov && ov.ValueKind == JsonValueKind.Object)
        {
            var overNode = JsonNode.Parse(ov.GetRawText())?.AsObject();
            if (overNode != null) DeepMergeInto(node, overNode);
        }
        return ScenarioService.LoadFromJson(node.ToJsonString());
    }

    /// <summary>Dart deepMerge semantics: null in overrides means "do not override".</summary>
    private static void DeepMergeInto(JsonObject target, JsonObject over)
    {
        foreach (var (k, v) in over.ToList())
        {
            if (v == null) continue;
            if (v is JsonObject vo && target[k] is JsonObject to)
            {
                DeepMergeInto(to, vo);
            }
            else
            {
                target[k] = v.DeepClone();
            }
        }
    }

    private static JsonElement? BodyOverrides(Dictionary<string, JsonElement> body) =>
        body.TryGetValue("overrides", out var ov) && ov.ValueKind == JsonValueKind.Object ? ov : null;

    // ==================== Small helpers ====================

    private static string? PropStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? PropInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static double? PropDbl(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.TryGetDouble(out var v) ? v : null;

    private static string? Str(Dictionary<string, JsonElement> b, string k) =>
        b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(Dictionary<string, JsonElement> b, string k, bool def = false) =>
        b.TryGetValue(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    /// <summary>无符号整数入参（种子）；负数或超范围当没传，交给默认值。</summary>
    private static uint UInt(Dictionary<string, JsonElement> b, string k, uint def) =>
        b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n) ? n : def;

    private static List<JsonElement> Dump<T>(IEnumerable<T> items) =>
        items.Select(i => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(i, LibJson))).ToList();

    private static JsonElement DumpOne<T>(T item) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(item, LibJson));

    private IActionResult NotFoundDict(string error) => StatusCode(404, new Dictionary<string, object?>
    {
        ["error"] = error,
    });

    private IActionResult BadRequestDict(string error, List<string>? errors)
    {
        var dict = new Dictionary<string, object?> { ["error"] = error };
        if (errors != null) dict["errors"] = errors;
        return StatusCode(400, dict);
    }

    private static string? TiandituTk()
    {
        var env = Environment.GetEnvironmentVariable("TIANDITU_TK")?.Trim();
        if (!string.IsNullOrEmpty(env)) return env;
        try
        {
            var dataDir = Path.GetDirectoryName(RoomServiceStaticPaths.DataDir);
            var file = Path.Combine(dataDir!, "tianditu.json");
            if (System.IO.File.Exists(file))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                return PropStr(doc.RootElement, "tk");
            }
        }
        catch { /* ignore */ }
        return null;
    }
}

/// <summary>Shared data-directory paths derived from the scenario directory.</summary>
internal static class RoomServiceStaticPaths
{
    public static string DataDir => _dataDir ??= Path.GetDirectoryName(DefaultScenarioDir)!;

    private static string? _dataDir;

    private static string DefaultScenarioDir =>
        Environment.GetEnvironmentVariable("HF_SCENARIO_DIR") ??
        Path.Combine(RepoRoot(), "data", "scenarios");

    private static string RepoRoot()
    {
        var bin = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        return string.IsNullOrEmpty(bin)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", "..", ".."));
    }
}
