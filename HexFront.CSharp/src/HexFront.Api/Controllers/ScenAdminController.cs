using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using HexFront.Api.Services;
using HexFront.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>
/// 剧本管理端点：读全文 / 校验并保存（自动备份）/ 内容错误反馈的提交、列表与删除。
/// 与 GisController 同一口径：snake_case、失败一律 {"error": …}，不走 [ApiController] 模型绑定。
/// </summary>
[Route("api/scenadmin")]
public sealed class ScenAdminController : ControllerBase
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly string[] FeedbackKinds =
        { "部署错误", "史实错误", "数据错误", "地形问题", "天气问题", "其他" };

    private readonly RoomService _rooms;
    private readonly ILogger<ScenAdminController> _logger;
    private readonly AppDbService _db;

    public ScenAdminController(RoomService rooms, ILogger<ScenAdminController> logger, AppDbService db)
    {
        _rooms = rooms;
        _db = db;
        _logger = logger;
    }

    // ==================== GET /api/scenadmin/{id} — 剧本全文 ====================

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(string id)
    {
        var path = FindScenarioPath(id);
        if (path == null) return Error("剧本不存在", 404);

        try
        {
            var text = await System.IO.File.ReadAllTextAsync(path);
            var doc = JsonNode.Parse(text);
            if (doc is null) return Error("剧本 JSON 为空", 400);
            return new JsonResult(new JsonObject
            {
                ["file"] = Path.GetFileName(path),
                ["doc"] = doc,
            });
        }
        catch (JsonException ex)
        {
            return Error("剧本 JSON 解析失败：" + ex.Message, 400);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "read scenario {Id}", id);
            return Error("读取剧本失败", 500);
        }
    }

    // ==================== PUT /api/scenadmin/{id}?validateOnly=1 — 校验/保存 ====================

    /// <summary>保存编辑后的剧本全文；validateOnly=1 只校验不落盘。保存前自动备份原文件。</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Save(string id, bool validateOnly = false)
    {
        var path = FindScenarioPath(id);
        if (path == null) return Error("剧本不存在", 404);

        JsonNode? doc;
        try { doc = await JsonSerializer.DeserializeAsync<JsonNode>(Request.Body); }
        catch (JsonException) { doc = null; }
        if (doc is null) return Error("请求体不是合法 JSON");

        var text = doc.ToJsonString(PrettyJson);
        var errors = ValidateDoc(text);
        var result = new JsonObject
        {
            ["ok"] = errors.Count == 0,
            ["errors"] = new JsonArray(errors.Select(e => JsonValue.Create(e)).ToArray()),
        };
        if (errors.Count > 0 || validateOnly) return new JsonResult(result);

        try
        {
            var backup = BackupOriginal(path);
            await System.IO.File.WriteAllTextAsync(path, text + "\n");
            result["file"] = Path.GetFileName(path);
            result["backup"] = backup is null ? null : Path.GetFileName(backup);
            // 应用库：剧本目录同步（保存后刷新 catalog）
            _db.SyncScenarios(_rooms.ScenarioDirectory);
            result["saved_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _logger.LogInformation("scenario {Id} saved (backup: {Backup})", id, backup);
            return new JsonResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "save scenario {Id}", id);
            return Error("写入剧本文件失败：" + ex.Message, 500);
        }
    }

    private List<string> ValidateDoc(string json)
    {
        try
        {
            var cfg = ScenarioService.LoadFromJson(json);
            return ScenarioService.Validate(cfg);
        }
        catch (Exception ex)
        {
            return new List<string> { "剧本解析失败：" + ex.Message };
        }
    }

    /// <summary>保存前把原文件复制到 data/scenario_backups/，返回备份路径（失败不阻断保存）。</summary>
    private string? BackupOriginal(string path)
    {
        try
        {
            var dir = Path.Combine(_rooms.DataDirectory, "scenario_backups");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dest = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}.{stamp}{Path.GetExtension(path)}");
            System.IO.File.Copy(path, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "backup scenario before save failed");
            return null;
        }
    }

    // ==================== 内容错误反馈（SQLite 应用库） ====================

    /// <summary>GET /api/scenadmin/feedback — 全部反馈（数组，按时间倒序）。</summary>
    [HttpGet("feedback")]
    public IActionResult FeedbackList([FromQuery] string? scenario = null)
    {
        var rows = _db.ListFeedback(scenario);
        var shaped = rows.Select(r => new Dictionary<string, object?>
        {
            ["id"] = r["id"],
            ["scenario"] = r["scenario"],
            ["kind"] = r["kind"],
            ["detail"] = r["detail"],
            ["contact"] = r["contact"],
            ["status"] = r["status"],
            ["username"] = r["username"],
            ["created_at"] = DateTime.TryParse(r["ts"] as string, out var ts)
                ? ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : r["ts"] as string ?? "",
        }).ToList();
        return new JsonResult(new JsonObject { ["feedback"] = new JsonArray(shaped.Select(e => (JsonNode)JsonSerializer.SerializeToNode(e)!).ToArray()) });
    }

    /// <summary>POST /api/scenadmin/feedback — body {scenario, kind, detail, contact?} 提交一条内容错误反馈。</summary>
    [HttpPost("feedback")]
    public async Task<IActionResult> FeedbackAdd()
    {
        JsonNode? body;
        try { body = await JsonSerializer.DeserializeAsync<JsonNode>(Request.Body); }
        catch (JsonException) { body = null; }
        if (body is null) return Error("请求体不是合法 JSON");

        var scenario = body["scenario"]?.GetValue<string>()?.Trim();
        var kind = body["kind"]?.GetValue<string>()?.Trim();
        var detail = body["detail"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(scenario)) return Error("缺少 scenario");
        if (string.IsNullOrEmpty(kind) || !FeedbackKinds.Contains(kind))
            return Error("kind 须为：" + string.Join(" / ", FeedbackKinds));
        if (string.IsNullOrEmpty(detail) || detail.Length < 5) return Error("问题描述至少 5 个字");
        if (detail.Length > 2000) return Error("问题描述过长（≤2000 字）");

        var username = _db.UserByToken(BearerToken())?["username"] as string;
        var id = _db.AddFeedback(scenario, kind, detail,
            body["contact"]?.GetValue<string>()?.Trim() ?? "", username);
        return new JsonResult(new JsonObject
        {
            ["ok"] = true,
            ["entry"] = new JsonObject
            {
                ["id"] = id, ["scenario"] = scenario, ["kind"] = kind, ["detail"] = detail,
                ["contact"] = body["contact"]?.GetValue<string>()?.Trim() ?? "",
                ["status"] = "open",
                ["created_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            },
        });
    }

    /// <summary>DELETE /api/scenadmin/feedback/{fid} — 删除一条反馈（管理侧已处理）。</summary>
    [HttpDelete("feedback/{fid}")]
    public IActionResult FeedbackRemove(string fid)
    {
        if (!long.TryParse(fid, out var id)) return Error("反馈不存在", 404);
        return _db.DeleteFeedback(id) ? new JsonResult(new JsonObject { ["ok"] = true }) : Error("反馈不存在", 404);
    }

    private string? BearerToken()
    {
        var h = Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    // ==================== Helpers ====================

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
            catch { /* skip invalid */ }
        }
        return null;
    }

    private IActionResult Error(string message, int code = 400) =>
        new JsonResult(new JsonObject { ["error"] = JsonValue.Create(message) }) { StatusCode = code };
}
