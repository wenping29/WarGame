using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>
/// 系统管理端点（仅超级管理员）：用户/角色/菜单 CRUD、登录/查询/错误日志、
/// 用户反馈、剧本目录、房间、已存副本。
/// </summary>
[ApiController]
[SuperAdmin]
public class AdminController : ControllerBase
{
    private readonly AppDbService _db;

    public AdminController(AppDbService db) => _db = db;

    /// <summary>守卫通过后由 SuperAdminAttribute 放入的当前用户。</summary>
    private Dictionary<string, object?> CurrentUser =>
        (Dictionary<string, object?>)(HttpContext.Items["hf_user"] ?? new Dictionary<string, object?>());

    private int CurrentUserId => Convert.ToInt32(CurrentUser["id"] ?? 0);

    // ==================== 用户 ====================

    [HttpGet("api/admin/users")]
    public IActionResult Users() => Ok(new
    {
        users = _db.QueryListPublic("SELECT id, username, display_name, role_code, status, created_at, last_login_at FROM users ORDER BY id"),
    });

    public sealed record CreateUserBody(string username, string password, string? display_name, string? role);

    [HttpPost("api/admin/users")]
    public IActionResult CreateUser(CreateUserBody body)
    {
        if (string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
            return BadRequest(new { error = "用户名与密码必填" });
        var role = string.IsNullOrEmpty(body.role) ? "spectator" : body.role;
        if (_db.QueryRowPublic("SELECT code FROM roles WHERE code=$c", ("$c", role)) == null)
            return BadRequest(new { error = $"角色不存在: {role}" });
        if (!_db.CreateUser(body.username.Trim(), body.password, body.display_name, role))
            return Conflict(new { error = "用户名已存在" });
        return Ok(new { ok = true });
    }

    public sealed record UpdateUserBody(string? display_name, string? role, string? status, string? password);

    [HttpPut("api/admin/users/{id:int}")]
    public IActionResult UpdateUser(int id, UpdateUserBody body)
    {
        if (_db.UserById(id) == null) return NotFound(new { error = "用户不存在" });
        if (body.role != null && _db.QueryRowPublic("SELECT code FROM roles WHERE code=$c", ("$c", body.role)) == null)
            return BadRequest(new { error = $"角色不存在: {body.role}" });
        if (body.status is not (null or "active" or "disabled"))
            return BadRequest(new { error = "status 须为 active|disabled" });
        if (body.password != null && body.password.Length < 6)
            return BadRequest(new { error = "密码 ≥6 位" });
        // 最后一个超级管理员不可降级/禁用自己（避免把自己锁死在管理界面外）
        if (CurrentUserIdIsLastSuperAdmin(id) &&
            (body.role is not (null or "super_admin") || body.status is "disabled"))
            return BadRequest(new { error = "不能停用或降级最后一个超级管理员" });
        if (!_db.UpdateUser(id, body.display_name, body.role, body.status, body.password))
            return NotFound(new { error = "用户不存在" });
        return Ok(new { ok = true });
    }

    [HttpPost("api/admin/users/{id:int}/status")]
    public IActionResult SetUserStatus(int id, [FromBody] Dictionary<string, string> body)
    {
        var status = body.TryGetValue("status", out var s) ? s : "";
        if (status is not ("active" or "disabled")) return BadRequest(new { error = "status 须为 active|disabled" });
        if (status == "disabled" && CurrentUserIdIsLastSuperAdmin(id))
            return BadRequest(new { error = "不能停用最后一个超级管理员" });
        if (!_db.SetUserStatus(id, status)) return NotFound(new { error = "用户不存在" });
        return Ok(new { ok = true });
    }

    [HttpDelete("api/admin/users/{id:int}")]
    public IActionResult DeleteUser(int id)
    {
        if (id == CurrentUserId) return BadRequest(new { error = "不能删除当前登录账号" });
        if (CurrentUserIdIsLastSuperAdmin(id)) return BadRequest(new { error = "不能删除最后一个超级管理员" });
        if (!_db.DeleteUser(id)) return NotFound(new { error = "用户不存在" });
        return Ok(new { ok = true });
    }

    /// <summary>目标用户是否为“唯一 active 的超级管理员”。</summary>
    private bool CurrentUserIdIsLastSuperAdmin(int targetId)
    {
        var u = _db.UserById(targetId);
        if (u == null || (string)u["role_code"]! != "super_admin" || (string)u["status"]! != "active") return false;
        return _db.CountUsersInRole("super_admin") <= 1;
    }

    // ==================== 角色 ====================

    [HttpGet("api/admin/roles")]
    public IActionResult Roles() => Ok(new
    {
        roles = _db.QueryListPublic(
            "SELECT r.*, (SELECT COUNT(*) FROM users u WHERE u.role_code = r.code) AS user_count " +
            "FROM roles r ORDER BY r.id"),
    });

    public sealed record CreateRoleBody(string code, string name, string? description);

    [HttpPost("api/admin/roles")]
    public IActionResult CreateRole(CreateRoleBody body)
    {
        if (string.IsNullOrWhiteSpace(body.code) || string.IsNullOrWhiteSpace(body.name))
            return BadRequest(new { error = "code 与 name 必填" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(body.code, "^[a-z][a-z0-9_]{1,31}$"))
            return BadRequest(new { error = "code 须为小写字母开头、2-32 位小写字母/数字/下划线" });
        if (!_db.CreateRole(body.code.Trim(), body.name.Trim(), body.description))
            return Conflict(new { error = "角色 code 已存在" });
        return Ok(new { ok = true });
    }

    public sealed record UpdateRoleBody(string name, string? description);

    [HttpPut("api/admin/roles/{code}")]
    public IActionResult UpdateRole(string code, UpdateRoleBody body)
    {
        if (string.IsNullOrWhiteSpace(body.name)) return BadRequest(new { error = "name 必填" });
        if (!_db.UpdateRole(code, body.name.Trim(), body.description)) return NotFound(new { error = "角色不存在" });
        return Ok(new { ok = true });
    }

    [HttpDelete("api/admin/roles/{code}")]
    public IActionResult DeleteRole(string code)
    {
        if (code == "super_admin") return BadRequest(new { error = "超级管理员角色不可删除" });
        if (_db.CountUsersInRole(code) > 0)
            return Conflict(new { error = "该角色仍有用户引用，请先转移用户" });
        if (!_db.DeleteRole(code)) return NotFound(new { error = "角色不存在" });
        return Ok(new { ok = true });
    }

    public sealed record SetRoleMenusBody(List<string> menus);

    [HttpPut("api/admin/roles/{code}/menus")]
    public IActionResult SetRoleMenus(string code, SetRoleMenusBody body)
    {
        if (_db.QueryRowPublic("SELECT code FROM roles WHERE code=$c", ("$c", code)) == null)
            return NotFound(new { error = "角色不存在" });
        if (code == "super_admin" && body.menus.All(m => m != "system"))
            return BadRequest(new { error = "超级管理员必须保留系统管理菜单" });
        var valid = _db.QueryListPublic("SELECT code FROM menus").Select(r => (string)r["code"]!).ToHashSet();
        var unknown = body.menus.Where(m => !valid.Contains(m)).ToList();
        if (unknown.Count > 0) return BadRequest(new { error = $"未知菜单: {string.Join(", ", unknown)}" });
        _db.SetRoleMenus(code, body.menus.Distinct());
        return Ok(new { ok = true });
    }

    // ==================== 菜单 ====================

    [HttpGet("api/admin/menus")]
    public IActionResult Menus() => Ok(new
    {
        menus = _db.QueryListPublic("SELECT * FROM menus ORDER BY sort"),
        role_menus = _db.QueryListPublic("SELECT * FROM role_menus"),
    });

    public sealed record CreateMenuBody(string code, string name, string? path, int sort, bool visible);

    [HttpPost("api/admin/menus")]
    public IActionResult CreateMenu(CreateMenuBody body)
    {
        if (string.IsNullOrWhiteSpace(body.code) || string.IsNullOrWhiteSpace(body.name))
            return BadRequest(new { error = "code 与 name 必填" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(body.code, "^[a-z][a-z0-9_]{1,31}$"))
            return BadRequest(new { error = "code 须为小写字母开头、2-32 位小写字母/数字/下划线" });
        if (!_db.CreateMenu(body.code.Trim(), body.name.Trim(), body.path, body.sort, body.visible))
            return Conflict(new { error = "菜单 code 已存在" });
        return Ok(new { ok = true });
    }

    public sealed record UpdateMenuBody(string name, string? path, int sort, bool visible);

    [HttpPut("api/admin/menus/{id:int}")]
    public IActionResult UpdateMenu(int id, UpdateMenuBody body)
    {
        if (string.IsNullOrWhiteSpace(body.name)) return BadRequest(new { error = "name 必填" });
        if (!_db.UpdateMenu(id, body.name.Trim(), body.path, body.sort, body.visible))
            return NotFound(new { error = "菜单不存在" });
        return Ok(new { ok = true });
    }

    [HttpDelete("api/admin/menus/{id:int}")]
    public IActionResult DeleteMenu(int id)
    {
        var m = _db.QueryRowPublic("SELECT code FROM menus WHERE id=$i", ("$i", id));
        if (m == null) return NotFound(new { error = "菜单不存在" });
        if ((string)m["code"]! == "system") return BadRequest(new { error = "系统管理菜单不可删除" });
        return _db.DeleteMenu(id) ? Ok(new { ok = true }) : NotFound(new { error = "菜单不存在" });
    }

    // ==================== 日志 ====================

    [HttpGet("api/admin/logs/login")]
    public IActionResult LoginLogs([FromQuery] int limit = 100, [FromQuery] string? username = null)
    {
        var where = username != null ? "WHERE username=$u" : "";
        return Ok(new { logs = _db.QueryListPublic($"SELECT * FROM login_logs {where} ORDER BY id DESC LIMIT $lim", ("$u", username), ("$lim", Clamp(limit))) });
    }

    [HttpGet("api/admin/logs/query")]
    public IActionResult QueryLogs([FromQuery] int limit = 200, [FromQuery] string? path = null)
    {
        var where = path != null ? "WHERE path LIKE $p" : "";
        return Ok(new { logs = _db.QueryListPublic($"SELECT * FROM query_logs {where} ORDER BY id DESC LIMIT $lim", ("$p", "%" + path + "%"), ("$lim", Clamp(limit))) });
    }

    [HttpGet("api/admin/logs/error")]
    public IActionResult ErrorLogs([FromQuery] int limit = 100) =>
        Ok(new { logs = _db.QueryListPublic("SELECT * FROM error_logs ORDER BY id DESC LIMIT $lim", ("$lim", Clamp(limit))) });

    // ==================== 用户反馈 ====================

    [HttpGet("api/admin/feedback")]
    public IActionResult Feedback([FromQuery] string? scenario = null, [FromQuery] string? status = null, [FromQuery] int limit = 200) =>
        Ok(new { feedback = _db.ListFeedback(scenario, status, Clamp(limit)) });

    [HttpPost("api/admin/feedback/{id:long}/status")]
    public IActionResult FeedbackStatus(long id, [FromBody] Dictionary<string, string> body)
    {
        var status = body.TryGetValue("status", out var s) ? s : "";
        if (status is not ("open" or "resolved")) return BadRequest(new { error = "status 须为 open|resolved" });
        return _db.ResolveFeedback(id, status) ? Ok(new { ok = true }) : NotFound(new { error = "反馈不存在" });
    }

    // ==================== 剧本 / 房间 / 副本 ====================

    [HttpGet("api/admin/scenarios")]
    public IActionResult Scenarios([FromQuery] int limit = 500) =>
        Ok(new { scenarios = _db.ListScenarios(Clamp(limit)) });

    [HttpGet("api/admin/rooms")]
    public IActionResult Rooms([FromQuery] int limit = 200) =>
        Ok(new { rooms = _db.ListRooms(Clamp(limit)) });

    [HttpGet("api/admin/saves")]
    public IActionResult Saves([FromQuery] int limit = 200) =>
        Ok(new { saves = _db.ListSaves(Clamp(limit)) });

    private static int Clamp(int v) => Math.Clamp(v, 1, 1000);
}
