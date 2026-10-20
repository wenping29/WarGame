using System;
using HexFront.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace HexFront.Api.Services;

/// <summary>
/// 超级管理员守卫：挂到控制器/动作上。未登录 → 401，已登录但角色不是 super_admin → 403。
/// 通过后把用户行放入 HttpContext.Items["hf_user"] 供动作取当前用户 id。
/// </summary>
public sealed class SuperAdminAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext ctx)
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbService>();
        var h = ctx.HttpContext.Request.Headers.Authorization.ToString();
        var token = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
        var u = db.UserByToken(token);
        if (u == null)
        {
            ctx.Result = new UnauthorizedObjectResult(new { error = "未登录或会话已失效" });
            return;
        }
        if (!string.Equals(u["role_code"]?.ToString(), "super_admin", StringComparison.Ordinal))
        {
            ctx.Result = new ObjectResult(new { error = "需要超级管理员权限" }) { StatusCode = 403 };
            return;
        }
        ctx.HttpContext.Items["hf_user"] = u;
    }
}
