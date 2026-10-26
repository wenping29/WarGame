using System;
using System.IO;
using System.IO.Compression;
using HexFront.Tools.Common;

namespace HexFront.Tools.Commands;

/// <summary>
/// pack-release 子命令：前后端分离产物打成 ZIP64（对应 deploy/tools/pack_release.py）。
/// 用 .NET ZipArchive（自动 ZIP64）替代 Python zipfile；排除项与存储级（已压缩栅格不二次压缩）保持一致。
/// </summary>
public static class PackReleaseCommand
{
    // 目录级排除
    private static readonly HashSet<string> ExcludeDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "__pycache__",
        "saves", "terrain_test",
        "gis_axial_backup", "scenarios_backup_20261019", "scenario_backups",
        ".vs", ".idea", ".vscode",
    };

    // 文件名级排除
    private static readonly HashSet<string> ExcludeFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tianditu.json", ".DS_Store", "Thumbs.db",
    };

    // 后缀级排除
    private static readonly string[] ExcludeSuffixes = { ".pdb", ".xml" };

    // 已压缩栅格：存不压缩
    private static readonly string[] StoreSuffixes =
        { ".tif", ".ovr", ".png", ".jpg", ".jpeg", ".gz", ".zip", ".woff", ".woff2" };

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("pack-release — 前后端分离产物 -> ZIP64");
            Console.WriteLine("  --out PATH          输出 zip（必填）");
            Console.WriteLine("  --root STR          zip 内顶层目录名（必填）");
            Console.WriteLine("  --web DIR           前端 dist 目录 -> <root>/web");
            Console.WriteLine("  --publish DIR       后端 publish 目录 -> <root>/server/HexFront.Api/publish");
            Console.WriteLine("  --db PATH           app.db -> <root>/server/HexFront.Api/app.db");
            Console.WriteLine("  --data DIR          server/data -> <root>/server/data");
            Console.WriteLine("  --deploy DIR        部署物料 -> <root>/deploy");
            return 0;
        }

        var outPath = a.Get("out");
        var root = a.Get("root");
        if (string.IsNullOrEmpty(outPath) || string.IsNullOrEmpty(root))
        {
            Log.Error("--out 与 --root 均为必填");
            return 1;
        }
        if (a.Bool("log")) Log.SetFile(a.Get("log"));

        var counter = (files: 0, bytes: 0L, excluded: 0);
        var t0 = DateTime.UtcNow;
        Log.Info($"打包 {outPath}");

        using (var archive = new ZipArchive(File.Create(outPath), ZipArchiveMode.Create))
        {
            if (a.Get("web") is { } web) { Log.Step("前端静态站 web/"); AddDir(archive, web, $"{root}/web", ref counter); }
            if (a.Get("publish") is { } pub) { Log.Step("后端 publish/ + app.db"); AddDir(archive, pub, $"{root}/server/HexFront.Api/publish", ref counter); }
            if (a.Get("db") is { } db) AddFile(archive, db, $"{root}/server/HexFront.Api/app.db", ref counter);
            if (a.Get("data") is { } data) { Log.Step("数据 server/data/"); AddDir(archive, data, $"{root}/server/data", ref counter); }
            if (a.Get("deploy") is { } dep) { Log.Step("部署物料 deploy/"); AddDir(archive, dep, $"{root}/deploy", ref counter); }
        }

        var size = new FileInfo(outPath).Length;
        Log.Info($"完成：{counter.files} 个文件，原始 {Human(counter.bytes)} -> zip {Human(size)}（排除 {counter.excluded} 项），耗时 {(DateTime.UtcNow - t0).TotalSeconds:F0}s");
        return 0;
    }

    private static void AddDir(ZipArchive zf, string src, string prefix, ref (int files, long bytes, int excluded) c)
    {
        foreach (var dirpath in Directory.EnumerateDirectories(src))
        {
            if (ExcludeDirNames.Contains(Path.GetFileName(dirpath)))
            {
                c.excluded++;
                continue;
            }
            AddDir(zf, dirpath, prefix, ref c);
        }
        foreach (var full in Directory.EnumerateFiles(src))
        {
            var fname = Path.GetFileName(full);
            if (ExcludeFileNames.Contains(fname) || EndsWithAny(fname, ExcludeSuffixes))
            {
                c.excluded++;
                continue;
            }
            var relDir = Path.GetRelativePath(src, Path.GetDirectoryName(full)!).Replace('\\', '/');
            var arc = relDir == "." ? $"{prefix}/{fname}" : $"{prefix}/{relDir}/{fname}";
            AddFile(zf, full, arc, ref c);
        }
    }

    private static void AddFile(ZipArchive zf, string src, string arc, ref (int files, long bytes, int excluded) c)
    {
        if (!File.Exists(src))
        {
            Log.Warn($"跳过不存在的文件: {src}");
            return;
        }
        var level = EndsWithAny(src, StoreSuffixes) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        var entry = zf.CreateEntry(arc.Replace('\\', '/'), level);
        using var os = entry.Open();
        using var fs = File.OpenRead(src);
        fs.CopyTo(os);
        c.files++;
        c.bytes += fs.Length;
    }

    private static bool EndsWithAny(string s, string[] suffixes)
    {
        foreach (var su in suffixes)
            if (s.EndsWith(su, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Human(double n)
    {
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (n < 1024) return $"{n:F1}{unit}";
            n /= 1024;
        }
        return $"{n:F1}TB";
    }
}
