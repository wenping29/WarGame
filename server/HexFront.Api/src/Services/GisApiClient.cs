using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HexFront.Core.Models;

namespace HexFront.Api.Services;

/// <summary>
/// 独立生成服务 HexFront.GisApi 的 HTTP 客户端。
///
/// 主后端不再自己跑 hexgen.py / 进程内生成，而是把「生成、进度查询、结果下载」三件事交给
/// http://&lt;GisApi&gt;/api/...（默认 127.0.0.1:5180）：
///   POST /api/plan        → 格网方案
///   POST /api/generate    → 提交生成作业（异步，返回作业 id）
///   GET  /api/jobs/{id}   → 作业状态 + 进度 + 日志
///   GET  /api/jobs/{id}/download → 下载生成好的六角格数据包（HexPackage JSON）
/// 远端不可达时所有方法返回 null，调用方（GisService）自动回落到本机引擎。
/// </summary>
public sealed class GisApiClient
{
    /// <summary>一次 HTTP 调用的上限：包体下载可能几 MB，但也不该无限等。</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>探测失败后多久内不再重试（避免每个请求都被连接超时拖住）。</summary>
    private static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly ILogger<GisApiClient> _logger;
    private DateTime _downUntil = DateTime.MinValue;
    private string? _downReason;

    public GisApiClient(IConfiguration cfg, ILogger<GisApiClient> logger)
    {
        _logger = logger;
        var section = cfg.GetSection("GisApi");
        BaseUrl = (Environment.GetEnvironmentVariable("HF_GIS_API_URL")
                   ?? section["BaseUrl"]
                   ?? "http://127.0.0.1:5180").Trim().TrimEnd('/');
        Enabled = !string.Equals(section["Enabled"], "false", StringComparison.OrdinalIgnoreCase)
                  && !string.Equals(Environment.GetEnvironmentVariable("HF_GIS_API_ENABLED"), "0", StringComparison.Ordinal);
        _http = new HttpClient { Timeout = CallTimeout };
    }

    /// <summary>生成服务基址（如 http://127.0.0.1:5180）。</summary>
    public string BaseUrl { get; }

    /// <summary>是否启用远程生成（配置 GisApi:Enabled=false 或 HF_GIS_API_ENABLED=0 可关闭，走本机引擎）。</summary>
    public bool Enabled { get; }

    /// <summary>最近一次探测失败的原因；正常可用时为 null。</summary>
    public string? LastError => DateTime.Now < _downUntil ? _downReason : null;

    // ==================== 能力 / 方案 ====================

    /// <summary>GET /api/capabilities —— 远程引擎与数据可用性；不可达返回 null。</summary>
    public async Task<JsonObject?> CapabilitiesAsync(CancellationToken ct = default)
    {
        var node = await GetJsonAsync("/api/capabilities", ct);
        return node as JsonObject;
    }

    /// <summary>POST /api/plan —— 与本机 GisPlanService 同一份几何，远端算完直接回。</summary>
    public async Task<JsonObject?> PlanAsync(double[] bbox, double? cellKm, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["bbox"] = new JsonArray(bbox.Select(v => JsonValue.Create(v)).ToArray()),
            ["cell_km"] = cellKm == null ? null : JsonValue.Create(cellKm.Value),
        };
        var node = await PostJsonAsync("/api/plan", body, ct);
        return node as JsonObject;
    }

    // ==================== 生成作业 ====================

    /// <summary>
    /// POST /api/generate —— 提交生成作业。
    /// 返回 {"id","status","pkg"}；不可达 / 参数被拒返回 null（参数错误会带 error）。
    /// </summary>
    public async Task<JsonObject?> SubmitAsync(GisJobSpec spec, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["bbox"] = new JsonArray(spec.Bbox.Select(v => JsonValue.Create(v)).ToArray()),
            ["cell_km"] = JsonValue.Create(spec.CellKm),
            ["name"] = JsonValue.Create(spec.Name),
            ["dem"] = JsonValue.Create(spec.Dem),
            ["landuse"] = JsonValue.Create(spec.Landuse),
            ["roads"] = JsonValue.Create(spec.Roads),
            ["water"] = JsonValue.Create(spec.Water),
            ["network"] = JsonValue.Create(spec.Network),
            ["features"] = JsonValue.Create(spec.Features),
            ["seed"] = JsonValue.Create(spec.Seed),
            ["offline"] = JsonValue.Create(spec.Offline),
        };
        return (await PostJsonAsync("/api/generate", body, ct)) as JsonObject;
    }

    /// <summary>GET /api/jobs/{id} —— 作业状态 / 进度 / 日志 / 元信息。</summary>
    public async Task<JsonObject?> JobAsync(string remoteId, CancellationToken ct = default)
    {
        var node = await GetJsonAsync("/api/jobs/" + Uri.EscapeDataString(remoteId), ct);
        return node as JsonObject;
    }

    /// <summary>GET /api/jobs/{id}/download —— 下载生成好的 HexPackage JSON 字节；未就绪返回 null。</summary>
    public async Task<byte[]?> DownloadJobAsync(string remoteId, CancellationToken ct = default)
    {
        if (!Enabled || !AllowCall()) return null;
        try
        {
            using var resp = await _http.GetAsync(BaseUrl + "/api/jobs/" + Uri.EscapeDataString(remoteId) + "/download",
                                                  HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            MarkUp();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            MarkDown(ex.Message);
            return null;
        }
    }

    /// <summary>GET /health —— 连通性探测。</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
        => await GetJsonAsync("/health", ct) != null;

    // ==================== 内部 ====================

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (!Enabled || !AllowCall()) return null;
        try
        {
            using var resp = await _http.GetAsync(BaseUrl + path, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                MarkDown($"HTTP {(int)resp.StatusCode} {path}");
                return null;
            }
            MarkUp();
            var text = await resp.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            MarkDown(ex.Message);
            return null;
        }
    }

    private async Task<JsonNode?> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        if (!Enabled || !AllowCall()) return null;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(BaseUrl + path, content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 参数类错误会把原因带在 body 里，交给上层直接报给用户
                var err = TryError(text) ?? $"HTTP {(int)resp.StatusCode}";
                if ((int)resp.StatusCode >= 500) MarkDown(err); else MarkUp();
                _logger.LogWarning("GisApi {Path} 返回 {Code}：{Err}", path, (int)resp.StatusCode, err);
                return null;
            }
            MarkUp();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            MarkDown(ex.Message);
            return null;
        }
    }

    private static string? TryError(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject o && o["error"] is JsonValue v
                && v.GetValueKind() == JsonValueKind.String)
                return v.GetValue<string>();
        }
        catch { /* 非 JSON 响应 */ }
        return null;
    }

    private bool AllowCall() => DateTime.Now >= _downUntil;

    private void MarkUp() => _downUntil = DateTime.MinValue;

    private void MarkDown(string reason)
    {
        _downUntil = DateTime.Now + CoolDown;
        if (_downReason == reason) return;
        _downReason = reason;
        _logger.LogWarning("GisApi 不可达（{BaseUrl}）：{Reason}，{Sec}s 内回落本机引擎",
            BaseUrl, reason, CoolDown.TotalSeconds.ToString(CultureInfo.InvariantCulture));
    }
}
