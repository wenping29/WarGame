using System;
using System.IO;
using HexFront.Core;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// server 根目录解析的回归测试。
///
/// 线上事故（宝塔部署）：HF_REPO_ROOT 指向 /www/wwwroot/wargame/server 时，
/// 早期实现无条件再拼一层 "server"，所有 data 路径整体偏移一层：
///   剧本目录不可读: /www/wwwroot/wargame/server/server/data/scenarios
/// 症状是「剧本列表为空 / 推演读不到剧本」，但启动不报错，很难定位。
///
/// HF_REPO_ROOT 实际有两种合理写法，必须都支持：
///   A) 解压根，其下有 server/    例：/www/wwwroot/wargame
///   B) server 目录本身           例：/www/wwwroot/wargame/server
///
/// 关键不变量：无论哪种写法，结果都不含重复的 server/server，
/// 且 app.db 与 data 必须落在同一棵树上（否则库和剧本数据会分家）。
/// </summary>
public sealed class ServerRootResolutionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "hf-root-" + Guid.NewGuid().ToString("N")[..8]);

    public ServerRootResolutionTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    /// <summary>造出 A 布局：<root>/server/{HexFront.Api,data}</summary>
    private string MakeLayoutA()
    {
        var root = Path.Combine(_tmp, "layoutA");
        Directory.CreateDirectory(Path.Combine(root, "server", "HexFront.Api"));
        Directory.CreateDirectory(Path.Combine(root, "server", "data", "scenarios"));
        return root;
    }

    /// <summary>造出 B 布局：<root>/server 本身就是 server 目录</summary>
    private string MakeLayoutB()
    {
        var root = Path.Combine(_tmp, "layoutB");
        Directory.CreateDirectory(Path.Combine(root, "server", "HexFront.Api"));
        Directory.CreateDirectory(Path.Combine(root, "server", "data", "scenarios"));
        return root;
    }

    [Fact]
    public void HF_REPO_ROOT指向解压根_拼出server子目录()
    {
        var root = MakeLayoutA();

        var resolved = UtilsPath.ResolveServerRoot(root, root);

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "server")), resolved);
    }

    [Fact]
    public void HF_REPO_ROOT直接指向server目录_不得再拼一层server()
    {
        // 这是线上踩到的写法：宝塔里把项目路径填到 server 这一层。
        var serverDir = Path.Combine(MakeLayoutB(), "server");

        var resolved = UtilsPath.ResolveServerRoot(serverDir, serverDir);

        // 必须是 server 目录本身，而不是 server/server
        Assert.Equal(Path.GetFullPath(serverDir), resolved);
        Assert.DoesNotContain(
            Path.Combine("server", "server"),
            resolved.Replace(Path.DirectorySeparatorChar, '/'));
    }

    [Fact]
    public void 解析结果_剧本目录与appdb落在同一棵树()
    {
        // 回归的真正意义：data 与 app.db 不能分家，否则剧本读不到、库却是新的。
        var serverDir = Path.Combine(MakeLayoutB(), "server");

        var resolved = UtilsPath.ResolveServerRoot(serverDir, serverDir);
        var scenarioDir = Path.Combine(resolved, "data", "scenarios");

        Assert.True(Directory.Exists(scenarioDir), $"剧本目录应真实存在：{scenarioDir}");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(serverDir, "HexFront.Api", "app.db")),
            Path.GetFullPath(Path.Combine(resolved, "HexFront.Api", "app.db")));
    }

    [Fact]
    public void HF_REPO_ROOT指向改名目录_直接采用不拼server()
    {
        // 目录被改过名（例如 server -> backend），HF_REPO_ROOT 里直接躺着 HexFront.Api。
        var root = Path.Combine(_tmp, "renamed");
        Directory.CreateDirectory(Path.Combine(root, "HexFront.Api"));
        Directory.CreateDirectory(Path.Combine(root, "data"));

        var resolved = UtilsPath.ResolveServerRoot(root, root);

        Assert.Equal(Path.GetFullPath(root), resolved);
    }

    [Fact]
    public void 未设HF_REPO_ROOT_从程序集位置回溯找server目录()
    {
        // 开发/本机路径：没有环境变量时，靠程序集位置回溯。
        var probe = Path.Combine(_tmp, "probe", "server", "HexFront.Api", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(probe);

        var resolved = UtilsPath.ResolveServerRoot(null, probe);

        Assert.Equal(Path.GetFullPath(Path.Combine(_tmp, "probe", "server")), resolved);
    }

    [Fact]
    public void HF_REPO_ROOT为空串_等同未设置()
    {
        var probe = Path.Combine(_tmp, "probe2", "server", "HexFront.Api");
        Directory.CreateDirectory(probe);

        var resolved = UtilsPath.ResolveServerRoot("   ", probe);

        Assert.Equal(Path.GetFullPath(Path.Combine(_tmp, "probe2", "server")), resolved);
    }
}
