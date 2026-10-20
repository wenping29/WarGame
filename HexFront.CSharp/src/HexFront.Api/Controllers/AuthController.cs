using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HexFront.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>认证：注册 / 登录 / 登出 / 会话信息。登录、登出、失败均记登录日志。</summary>
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbService _db;

    public AuthController(AppDbService db) => _db = db;

    public sealed record LoginBody(string username, string password);
    public sealed record RegisterBody(string username, string password, string? display_name);

    [HttpPost("api/auth/register")]
    public IActionResult Register(RegisterBody body)
    {
        if (string.IsNullOrWhiteSpace(body.username) || string.IsNullOrWhiteSpace(body.password))
            return BadRequest(new { error = "用户名与密码必填" });
        if (body.username.Length < 2 || body.password.Length < 6)
            return BadRequest(new { error = "用户名 ≥2 字符、密码 ≥6 位" });
        // 注册一律观战角色；角色分配由超级管理员在系统管理页负责
        const string role = "spectator";
        if (!_db.CreateUser(body.username.Trim(), body.password, body.display_name, role))
            return Conflict(new { error = "用户名已存在" });
        _db.LogLogin(body.username.Trim(), null, "register", true, Ip(), Request.Headers.UserAgent.ToString());
        return Ok(new { ok = true, username = body.username.Trim(), role });
    }

    [HttpPost("api/auth/login")]
    public IActionResult Login(LoginBody body)
    {
        var u = _db.VerifyUser(body.username ?? "", body.password ?? "");
        if (u == null)
        {
            _db.LogLogin(body.username, null, "failed", false, Ip(), Request.Headers.UserAgent.ToString(), "用户名或密码错误");
            return Unauthorized(new { error = "用户名或密码错误" });
        }
        var token = _db.CreateSession((int)(long)u["id"]!);
        _db.UpdateLastLogin((int)(long)u["id"]!);
        _db.LogLogin((string)u["username"]!, (int)(long)u["id"]!, "login", true, Ip(), Request.Headers.UserAgent.ToString());
        // VerifyUser 不带菜单；用会话重取带菜单的完整用户
        var full = _db.UserByToken(token) ?? u;
        return Ok(new { token, user = PublicUser(full) });
    }

    [HttpPost("api/auth/logout")]
    public IActionResult Logout()
    {
        var token = BearerToken();
        var u = _db.UserByToken(token);
        if (u != null)
            _db.LogLogin((string)u["username"]!, (int)(long)u["id"]!, "logout", true, Ip(), Request.Headers.UserAgent.ToString());
        if (token != null) _db.DropSession(token);
        return Ok(new { ok = true });
    }

    [HttpGet("api/auth/me")]
    public IActionResult Me()
    {
        var u = _db.UserByToken(BearerToken());
        return u == null ? Unauthorized(new { error = "未登录或会话已失效" }) : Ok(new { user = PublicUser(u) });
    }

    private static Dictionary<string, object?> PublicUser(Dictionary<string, object?> u) => new()
    {
        ["id"] = u["id"], ["username"] = u["username"], ["display_name"] = u["display_name"],
        ["role"] = u["role_code"], ["menus"] = u["menus"], ["last_login_at"] = u["last_login_at"],
    };

    private string? BearerToken()
    {
        var h = Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
    }

    private string? Ip() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
