using System;
using System.IO;
using System.Reflection;

namespace HexFront.Core;

/// <summary>
/// 统一路径解析工具（后端 HexFront.Api 与工具端 HexFront.Tools 共用，消除两端各自实现）。
///
/// 所有 data 类路径（scenarios / saves / gis / hexgrids / terrain / app.db）都相对 server 目录。
/// 锚点是 server 根目录（含 HexFront.Api 与 data）：优先取 HF_REPO_ROOT（两种写法），
/// 否则从程序集位置回溯。借助 SelectPlatformPath，同一份 appsettings.json 可在 Windows / Linux
/// 自动取对应盘符 / 路径，无需为不同系统维护两份文件。
///
/// 原先分散在 AppDbService（后端）、RepoLocator（工具端）与 PathResolver 的逻辑，统一收敛到这里。
/// </summary>
public static class UtilsPath
{
    /// <summary>后端 server 目录（含 HexFront.Api 与 data）。优先 HF_REPO_ROOT，否则从程序集位置回溯。</summary>
    public static string ServerRoot() => ResolveServerRoot(
        Environment.GetEnvironmentVariable("HF_REPO_ROOT"),
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? Directory.GetCurrentDirectory());

    /// <summary>
    /// 解析 server 根目录（含 HexFront.Api 与 data）。抽成纯函数以便单测直接验证各种目录布局，
    /// 不必真起进程、也不受并行测试改环境变量的干扰。
    ///
    /// HF_REPO_ROOT 两种写法都支持（写错任一种都会让所有 data 路径整体偏移一层）：
    ///   A) 指向「解压根」——其下有 server/ 子目录。例：/www/wwwroot/wargame
    ///   B) 直接指向「server 目录本身」。例：/www/wwwroot/wargame/server
    /// 早期实现只认 A，B 会拼出 server/server（剧本目录变成 .../server/server/data/scenarios，直接读不到）。
    /// </summary>
    public static string ResolveServerRoot(string? configuredRoot, string probeStartDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var full = Path.GetFullPath(configuredRoot.Trim());

            // 写法 B：目录名本身就是 server，直接用，不再重复拼一层。
            if (string.Equals(Path.GetFileName(full), "server", StringComparison.OrdinalIgnoreCase))
                return full;

            // 写法 A：解压根，其下的 server/ 里含 HexFront.Api。
            var nested = Path.Combine(full, "server");
            if (Directory.Exists(Path.Combine(nested, "HexFront.Api")))
                return Path.GetFullPath(nested);

            // 目录被改过名：HF_REPO_ROOT 指向的目录里直接躺着 HexFront.Api。
            if (Directory.Exists(Path.Combine(full, "HexFront.Api")))
                return full;

            // 都没命中：按标准布局拼 server（保持旧行为，让首次部署时目录能被自动创建出来）。
            return Path.GetFullPath(nested);
        }

        // 开发/本机运行：没有环境变量时，从程序集位置回溯，找名为 server 且含 HexFront.Api 的目录。
        var dir = probeStartDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (string.Equals(Path.GetFileName(dir), "server", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(dir, "HexFront.Api")))
                return Path.GetFullPath(dir);
            dir = Path.GetDirectoryName(dir);
        }
        return Path.GetFullPath(Path.Combine(probeStartDir, "server"));
    }

    /// <summary>数据根目录：HF_DATA_DIR 环境变量优先，否则回落 server/data。</summary>
    public static string DataDir()
    {
        var env = Environment.GetEnvironmentVariable("HF_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return NormalizePath(env.Trim().TrimEnd('\\', '/'));
        return Path.Combine(ServerRoot(), "data");
    }

    /// <summary>数据根目录下的相对子路径（例：DataSub("scenarios") → &lt;data&gt;/scenarios）。</summary>
    public static string DataSub(string relative) => Path.Combine(DataDir(), relative);

    /// <summary>按当前运行系统选取路径键：系统特化键优先，取不到回退通用键。</summary>
    public static string? SelectPlatformPath(string? common, string? windows, string? linux)
    {
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(windows)) return windows;
        if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(linux)) return linux;
        return common;
    }

    /// <summary>
    /// 路径归一：展开环境变量（Windows %VAR% / Linux $VAR / ${VAR}）、展开 ~ 为用户主目录、
    /// 并统一使用 / 分隔符（Windows 与 Linux 的 .NET 均接受，便于同一份配置跨系统通用）。
    /// </summary>
    public static string NormalizePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path).Trim();
        if (path.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rest = path.Substring(1).TrimStart('/');
            path = string.IsNullOrEmpty(rest) ? home : Path.Combine(home, rest);
        }
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// 解析目录：configured 为空 → 回落 repo/fallbackRelative；否则绝对路径直接采用，
    /// 相对路径（按仓库/server 根拼接）。末尾归一并确保目录存在。
    /// </summary>
    public static string ResolveDir(string? configured, string repo, string fallbackRelative)
    {
        string dir;
        if (string.IsNullOrWhiteSpace(configured))
        {
            dir = Path.Combine(repo, fallbackRelative);
        }
        else
        {
            dir = NormalizePath(configured);
            // 非绝对路径按仓库根解析（如 "data"），绝对路径直接采用
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(repo, dir);
        }
        dir = NormalizePath(dir);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
