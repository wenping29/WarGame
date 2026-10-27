using HexFront.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 监听地址优先级：--urls 命令行参数 > ASPNETCORE_URLS 环境变量 > appsettings.json 的 "Urls" 键。
//
// 为什么要显式接管：框架自带的顺序是反的 —— 配置文件里的 "Urls" 会盖过 ASPNETCORE_URLS。
// 而运维的直觉与用途都要求环境变量优先：
//   · 宝塔 .NET 站点面板的「端口」字段是靠 ASPNETCORE_URLS 传给进程的，让它被配置文件盖掉会让人以为没生效
//   · 部署模板里用 ASPNETCORE_URLS=http://127.0.0.1:5018 避免后端直接暴露公网（被盖掉就会退回 0.0.0.0）
//   · 端口的「默认值」放在 appsettings.json 里，改配置文件即可改端口（不重启服务、只改文件的诉求）
// UseUrls 的优先级高于 urls 配置键、低于命令行参数，正好落在想要的位置。
var urlsFromEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (!string.IsNullOrWhiteSpace(urlsFromEnv))
{
    builder.WebHost.UseUrls(urlsFromEnv);
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<RoomService>();
// 六角格生成已在进程内完成（2026-10-27 合并进 HexFront.Api，不再独立进程 HexFront.GisApi）：
// GisService 直接调 Core 的 HexGenService.Generate（真实 .ter 或合成兜底），提交作业、轮询、落盘、发布网络包。
builder.Services.AddSingleton<GisService>();
builder.Services.AddSingleton<OpsLogService>();
builder.Services.AddSingleton<AppDbService>();

// 开发期 React 走 vite 同源代理（/api、/ws），不需要 CORS；
// 只有把 React 构建产物部署到别的源（或直连 :5018）时才用得上，所以是白名单而不是通配。
// 方法表要覆盖前端实际用到的全部动词：api.ts 里除 GET/POST 外还有 PUT（副本单位编辑）与 DELETE。
// 少一个就会在跨源部署（方案B：静态站 + .NET 站分域名）时卡在 CORS 预检 OPTIONS 上返回 403。
var allowedOrigins = (Environment.GetEnvironmentVariable("HF_ALLOWED_ORIGINS")
    ?? "http://localhost:5173,http://localhost:5174,http://127.0.0.1:5173,http://127.0.0.1:5174")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins)
          .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
          .AllowAnyHeader()));

var app = builder.Build();

// 统一应用库启动初始化：种子数据（角色/菜单/管理员）→ 历史库迁移（ops_log.db、
// scenario_feedback.json）→ 剧本目录/内容同步 → 推演存档内容同步。失败不阻断启动。
var appDb = app.Services.GetRequiredService<AppDbService>();
var roomSvc = app.Services.GetRequiredService<RoomService>();
appDb.Initialize(roomSvc.ScenarioDirectory, roomSvc.SaveDirectory, roomSvc.HexGridDirectory);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Swagger UI reads the OpenAPI document produced by AddOpenApi()/MapOpenApi().
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "HexFront API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseCors();

// 查询日志 / 错误日志中间件：每个 /api 请求一条（含耗时、登录用户），异常与 5xx 另记错误日志
app.UseAuditLog();

// 单进程部署：Dart 版直接 serve ../web，C# 版对应把 React 构建产物放 wwwroot。
// 目录不存在时整段跳过，只提供 API 的部署方式不受影响。
if (Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// 会话传输：原生 /ws JSON 协议（web/js/ws.js 与 React 客户端共用，语义见 doc §12）。
// doc §13.2 原本要求重写为 SignalR Hub，实装与全量验证走的是 /ws，SignalR 版 RoomHub 已删除（偏离记录在 §13.2）。
app.Map("/ws", async (HttpContext context, RoomService rooms) => await rooms.HandleAsync(context));

app.MapControllers();

// React 用的是 path 路由，静态托管下刷新 /room/XXXX/red 必须回落到 index.html
if (Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")))
{
    app.MapFallbackToFile("index.html");
}

app.Run();
