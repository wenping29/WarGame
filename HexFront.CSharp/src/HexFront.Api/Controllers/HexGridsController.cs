using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HexFront.Api.Services;
using HexFront.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>
/// 服务器六角格网络包管理（data/hexgrids/&lt;id&gt;.zip，内含 grid.json）。
/// 剧本管理页「战场环境」生成网格后剧本只存 hexgrid id；六角格管理页在这里做列表/查看/删除。
/// 删除被剧本引用的包会被 409 拒绝（引用清单随列表返回）。
/// </summary>
[ApiController]
[Route("api/hexgrids")]
public class HexGridsController : ControllerBase
{
    private static readonly string[] TerrainIds = LibraryService.STANDARD_TERRAIN.Keys.ToArray();

    private readonly RoomService _rooms;
    private readonly ILogger<HexGridsController> _logger;
    private readonly AppDbService _db;

    public HexGridsController(RoomService rooms, ILogger<HexGridsController> logger, AppDbService db)
    {
        _rooms = rooms;
        _logger = logger;
        _db = db;
    }

    private string Dir => _rooms.HexGridDirectory;

    private static readonly JsonSerializerOptions DocJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>HexGridDoc 等 POCO 走 snake_case 输出（MVC 默认 camelCase 与线上契约不符，这里手动序列化）。</summary>
    private IActionResult DocOk(Dictionary<string, object?> payload)
    {
        Response.ContentType = "application/json";
        return Content(JsonSerializer.Serialize(payload, DocJson), "application/json");
    }

    // ==================== GET /api/hexgrids — 包清单 ====================

    [HttpGet]
    public IActionResult List()
    {
        var usedBy = ScanScenarioRefs(); // hexgrid id → 引用它的剧本 id
        var grids = HexGridPackageService.ListAll(Dir).Select(d => new Dictionary<string, object?>
        {
            ["id"] = d.Id,
            ["name"] = d.Name,
            ["rows"] = d.Rows,
            ["cols"] = d.Cols,
            ["flat_to_flat"] = d.FlatToFlat,
            ["west"] = d.West,
            ["south"] = d.South,
            ["bbox"] = HexGridPackageService.BboxOf(d),
            ["default_terrain"] = d.DefaultTerrain,
            ["holes_count"] = d.Holes?.Count ?? 0,
            ["terrains_count"] = d.Terrains?.Count ?? 0,
            ["created_at"] = d.CreatedAt,
            ["updated_at"] = d.UpdatedAt,
            ["source"] = d.Source,
            ["used_by"] = usedBy.TryGetValue(d.Id, out var s) ? s : Array.Empty<string>(),
        }).ToList();
        return Ok(new Dictionary<string, object?> { ["grids"] = grids, ["count"] = grids.Count });
    }

    // ==================== GET /api/hexgrids/{id} — 包全文 ====================

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        try
        {
            var doc = HexGridPackageService.Read(Dir, id);
            if (doc == null) return NotFound(new { error = $"六角格包不存在: {id}" });
            return DocOk(new Dictionary<string, object?>
            {
                ["grid"] = doc,
                ["bbox"] = HexGridPackageService.BboxOf(doc),
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ==================== POST /api/hexgrids — 生成并入库 ====================

    [HttpPost]
    public IActionResult Create([FromBody] Dictionary<string, JsonElement> body)
    {
        var rows = Int(body, "rows") ?? -1;
        var cols = Int(body, "cols") ?? -1;
        var ftf = Dbl(body, "flat_to_flat");
        var west = Dbl(body, "west");
        var south = Dbl(body, "south");

        if (rows is < 1 or > 200) return BadRequest(new { error = "rows 需在 1–200 之间" });
        if (cols is < 1 or > 200) return BadRequest(new { error = "cols 需在 1–200 之间" });
        if (ftf < 10 || ftf > 1_000_000) return BadRequest(new { error = "flat_to_flat 需在 10–1000000 米之间" });
        if (!IsFinite(west) || !IsFinite(south)) return BadRequest(new { error = "west/south 锚点无效" });

        var terrain = Str(body, "default_terrain");
        if (!string.IsNullOrEmpty(terrain) && !TerrainIds.Contains(terrain))
            return BadRequest(new { error = $"未知地形 {terrain}" });

        // 可选挖除/涂装（剧本管理页首版只传几何，这两个字段留给六角格管理页扩展）
        var holes = new List<string>();
        if (body.TryGetValue("holes", out var holesEl) && holesEl.ValueKind == JsonValueKind.Array)
            foreach (var h in holesEl.EnumerateArray())
                if (h.ValueKind == JsonValueKind.String) holes.Add(h.GetString()!);
        var terrains = new Dictionary<string, string>();
        if (body.TryGetValue("terrains", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
            foreach (var p in tEl.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) terrains[p.Name] = p.Value.GetString()!;

        var doc = HexGridPackageService.CreateDoc(Str(body, "name") ?? "", rows, cols, ftf, west, south,
            terrain, holes, terrains, Str(body, "source") is { Length: > 0 } s ? s : "api");
        try
        {
            HexGridPackageService.Save(Dir, doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "hexgrid package save failed");
            return StatusCode(500, new { error = "网格包保存失败: " + ex.Message });
        }

        // 地图包目录表同步登记（id ↔ 剧本 hexgrid / scenarios.map_id）
        _db.UpsertMapPackage(doc.Id, Path.Combine(Dir, doc.Id + ".zip"), doc.Rows, doc.Cols, doc.FlatToFlat);

        _logger.LogInformation("hexgrid {Id} created ({Rows}x{Cols} @ {Ftf}m)", doc.Id, rows, cols, ftf);
        return DocOk(new Dictionary<string, object?>
        {
            ["grid"] = doc,
            ["bbox"] = HexGridPackageService.BboxOf(doc),
        });
    }

    // ==================== DELETE /api/hexgrids/{id} — 删包（被剧本引用时拒绝） ====================

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var refs = ScanScenarioRefs().TryGetValue(id, out var s) ? s : Array.Empty<string>();
        if (refs.Length > 0)
            return StatusCode(409, new
            {
                error = $"网格包正被剧本引用：{string.Join("、", refs)}，先在剧本里解除绑定再删除",
                used_by = refs,
            });

        try
        {
            if (!HexGridPackageService.Delete(Dir, id))
                return NotFound(new { error = $"六角格包不存在: {id}" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        _db.DeleteMapPackage(id); // 地图包目录表同步移除
        _logger.LogInformation("hexgrid {Id} deleted", id);
        return Ok(new { id, deleted = true });
    }

    // ==================== 内部 ====================

    /// <summary>扫剧本目录：hexgrid id → 引用它的剧本 id 列表（坏文件跳过）。</summary>
    private Dictionary<string, string[]> ScanScenarioRefs()
    {
        var map = new Dictionary<string, List<string>>();
        var dir = _rooms.ScenarioDirectory;
        if (!Directory.Exists(dir)) return new Dictionary<string, string[]>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(f));
                if (!doc.RootElement.TryGetProperty("hexgrid", out var el)
                    || el.ValueKind != JsonValueKind.String) continue;
                var gid = el.GetString();
                if (string.IsNullOrEmpty(gid)) continue;
                var sid = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()! : Path.GetFileNameWithoutExtension(f);
                if (!map.TryGetValue(gid, out var list)) map[gid] = list = new List<string>();
                list.Add(sid);
            }
            catch
            {
                // 剧本解析失败不打断清单
            }
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static string? Str(Dictionary<string, JsonElement> b, string k) =>
        b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(Dictionary<string, JsonElement> b, string k) =>
        b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double Dbl(Dictionary<string, JsonElement> b, string k) =>
        b.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}
