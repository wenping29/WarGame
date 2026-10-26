using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core.Services;
using HexFront.GisApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<GisApiService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Swagger UI 读取 AddOpenApi()/MapOpenApi() 产出的 OpenAPI 文档
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "HexFront.GisApi v1");
    });
}

// ==================== 健康检查 ====================
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "HexFront.GisApi",
    time = DateTime.UtcNow,
}));

// ==================== 能力查询 ====================
app.MapGet("/api/capabilities", (GisApiService svc) => Results.Ok(svc.Capabilities()));

// ==================== 生成方案（按 bbox + 可选 cellKm 反推行列数） ====================
app.MapPost("/api/plan", (GisApiService svc, PlanRequest req) =>
{
    try
    {
        return Results.Ok(svc.Plan(req.Bbox, req.CellKm));
    }
    catch (GisPlanException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ==================== 提交生成作业（异步） ====================
app.MapPost("/api/generate", (GisApiService svc, JsonObject body) =>
{
    try
    {
        var spec = GisApiService.ParseSpec(body);
        var job = svc.Submit(spec);
        return Results.Accepted($"/api/jobs/{job.Id}", new
        {
            id = job.Id,
            status = job.Status.ToString(),
            pkg = job.PkgName,
        });
    }
    catch (GisPlanException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ==================== 作业管理 ====================
app.MapGet("/api/jobs", (GisApiService svc) => Results.Ok(svc.ListJobs()));

app.MapGet("/api/jobs/{id}", (GisApiService svc, string id) =>
{
    var node = svc.JobJson(id);
    return node == null ? Results.NotFound(new { error = "job not found" }) : Results.Ok(node);
});

app.MapDelete("/api/jobs/{id}", (GisApiService svc, string id) =>
{
    var ok = svc.DeleteJob(id);
    return ok ? Results.Ok(new { id, deleted = true }) : Results.NotFound(new { error = "job not found" });
});

app.MapGet("/api/jobs/{id}/download", (GisApiService svc, string id) =>
{
    var path = svc.JobPackagePath(id);
    return path == null
        ? Results.NotFound(new { error = "job result not available" })
        : Results.File(path, "application/json", Path.GetFileName(path));
});

// ==================== 网格包管理（独立存储） ====================
app.MapGet("/api/grids", (GisApiService svc) => Results.Ok(svc.ListGrids()));

app.MapGet("/api/grids/{id}", (GisApiService svc, string id) =>
{
    var meta = svc.GetGridMeta(id);
    return meta == null ? Results.NotFound(new { error = "grid not found" }) : Results.Ok(meta);
});

app.MapGet("/api/grids/{id}/download", (GisApiService svc, string id) =>
{
    var path = svc.GridPath(id);
    return path == null
        ? Results.NotFound(new { error = "grid not found" })
        : Results.File(path, "application/json", Path.GetFileName(path));
});

app.MapDelete("/api/grids/{id}", (GisApiService svc, string id) =>
{
    var ok = svc.DeleteGrid(id);
    return ok ? Results.Ok(new { id, deleted = true }) : Results.NotFound(new { error = "grid not found" });
});

app.Run();

/// <summary>POST /api/plan 的请求体：bbox=[西,南,东,北]，cellKm 可选（不给则返回档位阶梯）。</summary>
public sealed record PlanRequest(double[] Bbox, double? CellKm);
