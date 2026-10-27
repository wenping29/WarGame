using System;
using System.IO;

namespace HexFront.Api.Services;

/// <summary>
/// 路径解析工具：与 HexFront.GisApi 的 GisApiService.NormalizePath / SelectPlatformPath /
/// ResolveDir 逻辑保持一致，让同一份 appsettings.json 在 Windows / Linux 上都能正确取到数据根目录。
/// </summary>
internal static class PathResolver
{
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
