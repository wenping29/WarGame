using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HexFront.Api.Services;
using Microsoft.AspNetCore.Http;

namespace HexFront.Api.Services;

/// <summary>
/// 审计中间件：每个 /api 请求记查询日志（方法/路径/状态/耗时/用户/IP），
/// 异常与 5xx 记错误日志。/ws 跳过（WebSocket 长连接不适用请求语义）。
/// </summary>
public sealed class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AppDbService _db;

    public AuditMiddleware(RequestDelegate next, AppDbService db)
    {
        _next = next;
        _db = db;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api"))
        {
            await _next(ctx);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(ctx);
            var status = ctx.Response.StatusCode;
            _db.LogQuery(ctx.Request.Method, path, status, sw.ElapsedMilliseconds, Username(ctx), Ip(ctx));
            if (status >= 500)
                _db.LogError("request", path, $"HTTP {status}", null);
        }
        catch (Exception ex)
        {
            _db.LogQuery(ctx.Request.Method, path, 500, sw.ElapsedMilliseconds, Username(ctx), Ip(ctx));
            _db.LogError("request", path, ex.Message, ex.ToString());
            throw;
        }
    }

    private static string? Username(HttpContext ctx)
    {
        // 中间件不便整表 join：直接从 Bearer token 反查用户名
        var h = ctx.Request.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return _lookup(ctx, h[7..].Trim());
    }

    // 惰性解析：借用请求服务里的 AppDbService 查 sessions→users（同一单例实例）
    private static string? _lookup(HttpContext ctx, string token)
    {
        var db = ctx.RequestServices.GetService(typeof(AppDbService)) as AppDbService;
        var u = db?.UserByToken(token);
        return u?["username"] as string;
    }

    private static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();
}

public static class AuditMiddlewareExtensions
{
    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app) =>
        app.UseMiddleware<AuditMiddleware>();
}
