using HexFront.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<RoomService>();
builder.Services.AddSingleton<GisService>();

// 开发期 React 走 vite 同源代理（/api、/ws），不需要 CORS；
// 只有把 React 构建产物部署到别的源（或直连 :5018）时才用得上，所以是白名单而不是通配。
var allowedOrigins = (Environment.GetEnvironmentVariable("HF_ALLOWED_ORIGINS")
    ?? "http://localhost:5173,http://localhost:5174,http://127.0.0.1:5173,http://127.0.0.1:5174")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).WithMethods("GET", "POST", "OPTIONS").AllowAnyHeader()));

var app = builder.Build();

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
