using System;
using System.IO;

namespace HexFront.Tools.Common;

/// <summary>
/// 轻量日志：时间前缀 + 分级；可整体重定向到文件（--log）。输出 UTF-8。
/// </summary>
public static class Log
{
    private static readonly object _lock = new();
    private static TextWriter? _extra;

    public static void SetFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var w = new StreamWriter(path!, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            BufferSize = 1 << 16,
        })
        { AutoFlush = true };
        _extra = w;
    }

    private static void Emit(string tag, string msg)
    {
        var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {tag} {msg}";
        lock (_lock)
        {
            Console.WriteLine(line);
            _extra?.WriteLine(line);
        }
    }

    public static void Info(string msg) => Emit(string.Empty, msg);
    public static void Step(string msg) => Emit("·", msg);
    public static void Warn(string msg) => Emit("!", msg);
    public static void Error(string msg) => Emit("x", msg);
}
