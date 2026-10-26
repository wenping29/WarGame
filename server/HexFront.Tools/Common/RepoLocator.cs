using System;
using System.IO;

namespace HexFront.Tools.Common;

/// <summary>
/// 解析「server/」根目录与 data 目录，语义与后端 AppDbService.ResolveServerRoot 保持一致：
///   · 支持 HF_REPO_ROOT（两种写法：指向解压根含 server/，或直接指向 server 目录本身）
///   · 否则从程序所在目录向上查找含 HexFront.Api + data 的目录
/// 工具用到的默认数据源路径（data/gis/dem、data/scenarios 等）都基于此。
/// </summary>
public static class RepoLocator
{
    public static string ServerRoot()
    {
        var env = Environment.GetEnvironmentVariable("HF_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            env = env.Trim().TrimEnd('\\', '/');
            if (LooksLikeServer(env)) return env;
            var srv = Path.Combine(env, "server");
            if (LooksLikeServer(srv)) return srv;
        }

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir) && dir.Length > 3)
        {
            if (LooksLikeServer(Path.Combine(dir, "server")))
                return Path.Combine(dir, "server");
            if (LooksLikeServer(dir)) return dir;
            dir = Path.GetDirectoryName(dir)!;
        }
        return Directory.GetCurrentDirectory();
    }

    public static string DataDir()
    {
        var env = Environment.GetEnvironmentVariable("HF_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim().TrimEnd('\\', '/');
        return Path.Combine(ServerRoot(), "data");
    }

    public static string DataSub(string relative) => Path.Combine(DataDir(), relative);

    private static bool LooksLikeServer(string p)
    {
        if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) return false;
        return Directory.Exists(Path.Combine(p, "HexFront.Api")) &&
               Directory.Exists(Path.Combine(p, "data"));
    }
}
