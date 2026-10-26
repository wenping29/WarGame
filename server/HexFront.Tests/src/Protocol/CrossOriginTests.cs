using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 方案 B（宝塔双站分离）跨域链路验证。
///
/// 背景：前端由宝塔「静态网站」托管，后端由宝塔「.NET 网站」托管，两者分属不同域名，
/// 于是浏览器从页面直接打后端域名 → REST 走 CORS 预检、WebSocket 跨域握手。
/// 这套断言盯的正是双站部署最容易踩的两处：
///   1) CORS 方法表漏动词（前端有 PUT/DELETE），预检被拒 → 副本编辑类功能 403
///   2) WebSocket 握手被 Origin 卡住或路由没命中
///
/// 做法：真起一个 HexFront.Api 进程（真 Kestrel、真 Program 装配），带 HF_ALLOWED_ORIGINS
/// 指向模拟的「前端站」来源，然后按浏览器的方式打预检与握手。
/// </summary>
public sealed class CrossOriginTests : IAsyncLifetime
{
    /// <summary>模拟前端静态站的 origin（与宝塔上 HF_ALLOWED_ORIGINS 的取值同构）。</summary>
    private const string FrontOrigin = "https://game.example.com";

    /// <summary>不在白名单里的第三方来源。</summary>
    private const string ForeignOrigin = "https://evil.example.org";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private Process? _host;
    private string _baseUrl = "";
    private string _saveDir = "";

    public async Task InitializeAsync()
    {
        _saveDir = Path.Combine(Path.GetTempPath(), "hf-cors-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_saveDir);

        var apiDll = ApiDllPath();
        _baseUrl = $"http://127.0.0.1:{FreeTcpPort()}";

        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.GetDirectoryName(apiDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(apiDll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(_baseUrl);
        // 白名单：只有前端站
        start.Environment["HF_ALLOWED_ORIGINS"] = FrontOrigin;
        // 隔离写入，避免测试往开发库里塞数据
        start.Environment["HF_APP_DB"] = Path.Combine(_saveDir, "app.db");
        start.Environment["HF_SAVE_DIR"] = Path.Combine(_saveDir, "saves");

        _host = Process.Start(start) ?? throw new InvalidOperationException("起不了 HexFront.Api 进程");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (_host.HasExited) throw new InvalidOperationException($"HexFront.Api 提前退出（exit {_host.ExitCode}）");
            try
            {
                using var res = await _http.GetAsync($"{_baseUrl}/api/rooms");
                if (res.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { /* 还没起来 */ }
            catch (TaskCanceledException) { /* 还没起来 */ }
            await Task.Delay(150);
        }
        throw new TimeoutException("Kestrel 在 40s 内没就绪");
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_host is not null && !_host.HasExited)
            {
                _host.Kill(entireProcessTree: true);
                await _host.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { /* 已退出 */ }
        _host?.Dispose();
        _host = null;
        try { if (Directory.Exists(_saveDir)) Directory.Delete(_saveDir, recursive: true); }
        catch (IOException) { /* 临时目录留着也无害 */ }
    }

    /// <summary>浏览器的 CORS 预检：OPTIONS + Origin + Access-Control-Request-Method。</summary>
    private async Task<HttpResponseMessage> PreflightAsync(string origin, string method, string path = "/api/instances")
    {
        var req = new HttpRequestMessage(HttpMethod.Options, _baseUrl + path);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", method);
        req.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task 预检_白名单来源_返回允许来源与动词()
    {
        using var res = await PreflightAsync(FrontOrigin, "POST");

        Assert.True(res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK,
            $"预检应成功，实际 {(int)res.StatusCode} {res.StatusCode}");

        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"),
            "响应缺少 Access-Control-Allow-Origin 头，浏览器会拦掉实际请求");

        Assert.Equal(FrontOrigin, res.Headers.GetValues("Access-Control-Allow-Origin").First());
    }

    [Fact]
    public async Task 预检_动词PUT_被允许()
    {
        // 回归点：前端 api.ts 有 PUT（副本单位编辑）与 DELETE。
        // CORS 方法表漏掉时，浏览器在预检阶段就收到 403，功能整个不可用。
        using var res = await PreflightAsync(FrontOrigin, "PUT");

        Assert.True(res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK,
            $"PUT 预检应成功，实际 {(int)res.StatusCode} {res.StatusCode}");

        Assert.True(res.Headers.Contains("Access-Control-Allow-Methods"), "响应缺少 Access-Control-Allow-Methods 头");
        var allowed = res.Headers.GetValues("Access-Control-Allow-Methods").First();
        Assert.Contains("PUT", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 预检_动词DELETE_被允许()
    {
        using var res = await PreflightAsync(FrontOrigin, "DELETE");

        Assert.True(res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK,
            $"DELETE 预检应成功，实际 {(int)res.StatusCode} {res.StatusCode}");

        var allowed = res.Headers.GetValues("Access-Control-Allow-Methods").First();
        Assert.Contains("DELETE", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 预检_非白名单来源_不返回允许来源头()
    {
        using var res = await PreflightAsync(ForeignOrigin, "POST");

        // 关键：非白名单来源不能拿到 ACAO 头，否则等于放开任意站点跨域读接口
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"),
            "非白名单来源拿到了 Access-Control-Allow-Origin，白名单失效");
    }

    [Fact]
    public async Task 实际请求_白名单来源_带允许来源头()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/api/rooms");
        req.Headers.Add("Origin", FrontOrigin);
        using var res = await _http.SendAsync(req);

        Assert.True(res.IsSuccessStatusCode, $"实际请求应成功，实际 {(int)res.StatusCode}");
        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"),
            "白名单来源的实际请求缺少 Access-Control-Allow-Origin 头");
        Assert.Equal(FrontOrigin, res.Headers.GetValues("Access-Control-Allow-Origin").First());
    }

    [Fact]
    public async Task WebSocket_跨域握手_成功升级且收到首帧()
    {
        // 浏览器从静态站页面直连后端域名的 /ws，握手请求会带 Origin。
        // 后端没做 Origin 校验，握手应当照常升级成 101。
        // 房间码必须真实存在：未知房间码服务端按设计返回 404（RoomService.cs:230），
        // 那是业务校验而不是跨域问题，所以先建房再连。
        var code = await CreateRoomAsync();

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", FrontOrigin);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var uri = new Uri($"{_baseUrl.Replace("http", "ws", StringComparison.Ordinal)}/ws?room={code}&role=spectator");

        try
        {
            await ws.ConnectAsync(uri, cts.Token);
        }
        catch (WebSocketException ex)
        {
            Assert.Fail($"WebSocket 跨域握手失败：{ex.Message}");
        }

        Assert.Equal(WebSocketState.Open, ws.State);

        // 握手成功还不够：得确认帧真的能流回来（nginx 侧漏配 Upgrade 时就卡在这一步）
        // 首帧是视图快照，可能远大于单次接收上限，要按 EndOfMessage 累积成完整帧再解析。
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var received = await ws.ReceiveAsync(new ArraySegment<byte>(buf), recvCts.Token);
            ms.Write(buf, 0, received.Count);
            if (received.EndOfMessage) break;
        }
        var text = Encoding.UTF8.GetString(ms.ToArray());

        Assert.NotNull(JsonNode.Parse(text));
    }

    /// <summary>建一个真实房间，拿房间码（未知房间码握手会被服务端 404，见 RoomService.cs:230）。</summary>
    private async Task<string> CreateRoomAsync()
    {
        var body = new JsonObject
        {
            ["scenario"] = "rhine_corridor",
            ["name"] = "跨域握手测试",
            ["seed"] = 555,
            ["ai_blue"] = true,
            ["overrides"] = JsonNode.Parse("{}")!.DeepClone(),
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync($"{_baseUrl}/api/instances", content);
        var text = await res.Content.ReadAsStringAsync();

        Assert.True(res.IsSuccessStatusCode, $"建房失败 {(int)res.StatusCode}：{text}");

        var created = JsonNode.Parse(text)!.AsObject();
        var code = created["code"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(code), "建房返回里没有房间码：" + text);
        return code!;
    }

    /// <summary>Api 的独立输出目录（测试输出目录里的副本没有 runtimeconfig/deps，跑不起来）。</summary>
    private static string ApiDllPath()
    {
        // 测试输出 tests/HexFront.Tests/bin/Debug/net10.0 上溯 4 层到 server
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var dll = Path.Combine(root, "HexFront.Api", "bin", "Debug", "net10.0", "HexFront.Api.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("找不到 HexFront.Api 的输出 dll，先 dotnet build HexFront.Api", dll);
        return dll;
    }

    /// <summary>用当前运行时同一个 dotnet 宿主，避免 PATH 上装的是另一个 SDK 版本。</summary>
    private static string DotnetHost()
    {
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) && Path.GetFileName(self).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return self;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var candidate = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "dotnet.exe");
        return candidate != null && File.Exists(candidate) ? candidate : "dotnet";
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
