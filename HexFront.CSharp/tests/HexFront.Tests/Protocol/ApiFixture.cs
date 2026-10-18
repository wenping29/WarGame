using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 把线上那份 HexFront.Api 当独立进程起来（真 Kestrel、真 /ws、真 Program 装配），协议回归对着它打。
/// 不在测试进程里 new WebApplication：那样入口程序集变成 test host，MVC 扫不到 Api 的控制器，
/// 而重搭一遍管道测的就不是线上那份装配了。
/// 存档目录指到临时目录：既不把 data/saves 塞满测试垃圾，也让「落盘三件套」这类断言能读到真文件。
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _hostLog = new();
    private Process? _host;

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; private set; } = "";

    /// <summary>ws:// 版基址，客户端拿它拼 /ws?room=…&amp;role=…。</summary>
    public string WsBaseUrl => "ws" + BaseUrl["http".Length..] + "/ws";

    public string SaveDirectory { get; private set; } = "";

    public async Task InitializeAsync()
    {
        SaveDirectory = Path.Combine(Path.GetTempPath(), "hf-ws-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(SaveDirectory);

        var apiDll = ApiAssemblyPath();
        BaseUrl = $"http://127.0.0.1:{FreeTcpPort()}";

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
        start.ArgumentList.Add(BaseUrl);
        start.Environment["HF_SAVE_DIR"] = SaveDirectory;

        _host = Process.Start(start) ?? throw new InvalidOperationException("起不了 HexFront.Api 进程");
        _host.OutputDataReceived += (_, e) => Append(e.Data);
        _host.ErrorDataReceived += (_, e) => Append(e.Data);
        _host.BeginOutputReadLine();
        _host.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        string? lastStatus = null;
        string? lastError = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (_host.HasExited)
                throw new InvalidOperationException($"HexFront.Api 进程提前退出（exit {ExitCode()}）：\n{Tail()}");
            try
            {
                using var res = await Http.GetAsync($"{BaseUrl}/api/rooms");
                if (res.IsSuccessStatusCode) return;
                lastStatus = $"{(int)res.StatusCode} {res.StatusCode}";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(150);
        }

        await StopHostAsync();
        throw new TimeoutException($"Kestrel 在 40s 内没有就绪（状态 {lastStatus ?? "-"}，错误 {lastError ?? "-"}）\n{Tail()}");
    }

    public async Task DisposeAsync()
    {
        await StopHostAsync();
        try { Directory.Delete(SaveDirectory, recursive: true); } catch (IOException) { /* 临时目录留着也无害 */ }
    }

    private async Task StopHostAsync()
    {
        if (_host is null) return;
        try
        {
            if (!_host.HasExited)
            {
                _host.Kill(entireProcessTree: true);
                await _host.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // 已经退出
        }
        _host.Dispose();
        _host = null;
    }

    private int ExitCode()
    {
        try { return _host!.ExitCode; }
        catch (Exception) { return -1; }
    }

    private void Append(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _hostLog.Enqueue(line);
        while (_hostLog.Count > 400 && _hostLog.TryDequeue(out _)) { }
    }

    private string Tail() => string.Join("\n", _hostLog.ToArray().TakeLast(40));

    /// <summary>Api 的独立输出目录（有 runtimeconfig/deps，才能 dotnet 直接跑 dll）；测试输出目录里的副本没有这些。</summary>
    private static string ApiAssemblyPath()
    {
        // 测试输出 tests/HexFront.Tests/bin/Debug/net10.0 上溯 5 层到 HexFront.CSharp
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dll = Path.Combine(root, "src", "HexFront.Api", "bin", "Debug", "net10.0", "HexFront.Api.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("找不到 HexFront.Api 的输出 dll，先 dotnet build src/HexFront.Api", dll);
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

    public Task<JsonObject> GetJsonAsync(string path) =>
        SendJsonAsync(new HttpRequestMessage(HttpMethod.Get, BaseUrl + path));

    public Task<JsonObject> PostJsonAsync(string path, string json) =>
        SendJsonAsync(new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    private async Task<JsonObject> SendJsonAsync(HttpRequestMessage request)
    {
        using var res = await Http.SendAsync(request);
        var text = await res.Content.ReadAsStringAsync();
        // /api/validate 与 /api/instances 用 HTTP 400 + JSON 体回错误，这里不能按状态码抛
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>POST /api/instances 建房；ai_blue=true 与 Dart 冒烟同形（蓝方交副官代管，便于单端驱动）。</summary>
    public async Task<JsonObject> CreateInstanceAsync(string name = "协议冒烟", int seed = 555,
        bool aiBlue = true, string overridesJson = "{}")
    {
        var body = new JsonObject
        {
            ["scenario"] = "rhine_corridor",
            ["name"] = name,
            ["seed"] = seed,
            ["ai_blue"] = aiBlue,
            ["overrides"] = JsonNode.Parse(overridesJson)!.DeepClone(),
        };
        return await PostJsonAsync("/api/instances", body.ToJsonString());
    }

    /// <summary>房间码必须唯一：同一集合里几十个用例共用一个主机进程里的房间表。</summary>
    public async Task<string> CreateRoomCodeAsync(string? name = null, int seed = 555, bool aiBlue = true)
    {
        var created = await CreateInstanceAsync(name ?? "协议冒烟", seed, aiBlue);
        var code = created["code"]?.GetValue<string>();
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("建房失败：" + created.ToJsonString());
        return code;
    }

    public string InstanceIdOf(JsonObject created) => created["instance"]?.GetValue<string>() ?? "";

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>所有协议用例共用一台主机：房间、连接、存档目录都在同一个进程里，才测得到「顶号」「释放」这类真实交互。</summary>
[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
}
