using System;
using System.IO;

namespace HexFront.GisApi;

/// <summary>
/// 解析数据根目录（其下应有 data/，即HexFront 的 server 目录）。
/// 优先级：环境变量 HF_REPO_ROOT（兼容"解压根"与"直接指 server 目录"两种写法）
/// → 向上找 &lt;dir&gt;/server/data（仓库/发布包标准布局）
/// → 向上找 &lt;dir&gt;/data（自定义布局兜底）。
/// 优先匹配 server/data 而非任意 data/：避免盘符根等无关 data 目录误命中。
/// </summary>
internal static class RepoLocator
{
    public static string ResolveRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("HF_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            if (Directory.Exists(Path.Combine(env, "data"))) return env;                          // env 直接就是 server 目录
            if (Directory.Exists(Path.Combine(env, "server", "data")))                            // env 是解压根
                return Path.Combine(env, "server");
            return env;
        }

        // 首选标准布局：<dir>/server/data ⇒ 根 = <dir>/server
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "server", "data")))
                return Path.Combine(dir, "server");
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        // 兜底：直接含 data/ 的祖先目录
        dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "data")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
