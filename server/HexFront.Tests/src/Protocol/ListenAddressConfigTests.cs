using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 监听端口的配置来源验证。
///
/// 需求：后端端口要能在 appsettings.json 里配置（宝塔上改配置文件比改环境变量直观）。
/// 实现用的是配置根级的 "Urls" 键，而不是 Kestrel:Endpoints —— 因为官方文档明确：
/// 「Kestrel endpoint configuration overrides UseUrls, ASPNETCORE_URLS, and --urls」。
/// 若用 Kestrel:Endpoints，部署模板里的 ASPNETCORE_URLS=http://127.0.0.1:5018 会被静默覆盖，
/// 后端会退化成监听 0.0.0.0（宝塔 .NET 站点面板设的端口同样失效）。
///
/// 用根级 Urls 键的优先级链（高 → 低）：
///   1. --urls 命令行参数
///   2. ASPNETCORE_URLS 环境变量
///   3. appsettings.json 的 "Urls" 键   ← 本次要保证这一层真的生效
///   4. Kestrel 默认（http://localhost:5000）
///
/// 这两个用例把第 2、3 层的边界钉死：配置文件的默认值要生效，且环境变量要能覆盖它。
/// </summary>
public sealed class ListenAddressConfigTests
{
    /// <summary>appsettings.json 里配置的默认端口。</summary>
    private const int DefaultPort = 5018;

    private static string ApiDllPath()
    {
        // 测试输出 tests/HexFront.Tests/bin/Debug/net10.0 上溯 4 层到 server
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var dll = Path.Combine(root, "HexFront.Api", "bin", "Debug", "net10.0", "HexFront.Api.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("找不到 HexFront.Api 的输出 dll，先 dotnet build HexFront.Api", dll);
        return dll;
    }

    private static string DotnetHost()
    {
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) && Path.GetFileName(self).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return self;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var candidate = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "dotnet.exe");
        return candidate != null && File.Exists(candidate) ? candidate : "dotnet";
    }

    /// <summary>
    /// 起一个 HexFront.Api 进程，等到 Kestrel 打出 "Now listening on:"，返回那行日志。
    /// 不传 --urls / 不设 ASPNETCORE_URLS，从而让配置文件的 Urls 键生效。
    /// </summary>
    private static async Task<string> StartAndReadListenLineAsync(IDictionary<string, string>? extraEnv)
    {
        var apiDll = ApiDllPath();
        var saveDir = Path.Combine(Path.GetTempPath(), "hf-listen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(saveDir);

        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.GetDirectoryName(apiDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(apiDll);
        // 关键：不传 --urls、不设 ASPNETCORE_URLS，端口只能来自 appsettings.json
        start.Environment["HF_APP_DB"] = Path.Combine(saveDir, "app.db");
        start.Environment["HF_SAVE_DIR"] = Path.Combine(saveDir, "saves");
        // Kestrel 的 "Now listening on:" 是 Information 级；生产配置默认 Warning，这里强制打开
        start.Environment["Logging__LogLevel__Default"] = "Information";
        if (extraEnv != null)
            foreach (var kv in extraEnv) start.Environment[kv.Key] = kv.Value;

        var log = new ConcurrentQueue<string>();
        using var proc = Process.Start(start) ?? throw new InvalidOperationException("起不了 HexFront.Api 进程");
        proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Enqueue(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Enqueue(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        string? listen = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(40))
        {
            listen = log.FirstOrDefault(l => l.Contains("Now listening on:", StringComparison.Ordinal));
            if (listen != null) break;
            if (proc.HasExited)
                break;
            await Task.Delay(100);
        }

        var lines = string.Join("\n", log);
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            await proc.WaitForExitAsync();
        }
        catch (InvalidOperationException) { /* 已退出 */ }
        try { if (Directory.Exists(saveDir)) Directory.Delete(saveDir, recursive: true); } catch (IOException) { }

        if (listen == null)
            throw new InvalidOperationException($"40s 内没等到 Kestrel 监听日志（进程 exit={proc.ExitCode}）：\n{lines}");

        return listen!;
    }

    [Fact]
    public async Task 未设环境变量时_端口取自appsettings的Urls键()
    {
        var listen = await StartAndReadListenLineAsync(null);

        Assert.Contains($":{DefaultPort}", listen);
        // 不能是 Kestrel 的默认端口（那说明配置没读到）
        Assert.DoesNotContain(":5000", listen);
    }

    [Fact]
    public async Task 设了ASPNETCORE_URLS时_环境变量覆盖配置文件的Urls键()
    {
        // 宝塔 .NET 站点面板 / systemd 都靠这个环境变量指定端口，必须能盖过配置文件
        var port = FreeTcpPort();
        var listen = await StartAndReadListenLineAsync(new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
        });

        Assert.Contains($":{port}", listen);
        Assert.DoesNotContain($":{DefaultPort}", listen);
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
