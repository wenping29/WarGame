using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// 服务器端六角格网络包：每个网格一个 zip（data/hexgrids/&lt;id&gt;.zip，内含 grid.json）。
/// 剧本只存 hexgrid id；开局时由 <see cref="Build"/> 还原 HexGrid + Cells。
/// 几何约定与六角格管理页一致：pointy-top odd-r，对边距 = √3 · 半径，Mercator 米。
/// </summary>
public static class HexGridPackageService
{
    private const string EntryName = "grid.json";
    private const double Sqrt3 = 1.7320508075688772935;
    private const double EarthR = 6378137.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    // ==================== 文档构造 ====================

    public static HexGridDoc CreateDoc(string name, int rows, int cols, double flatToFlat,
        double west, double south, string? defaultTerrain,
        List<string>? holes, Dictionary<string, string>? terrains, string source)
    {
        var now = DateTime.UtcNow.ToString("o");
        return new HexGridDoc
        {
            Id = "hg_" + Guid.NewGuid().ToString("N")[..10],
            Name = string.IsNullOrWhiteSpace(name) ? "网格包" : name.Trim(),
            Rows = rows,
            Cols = cols,
            FlatToFlat = flatToFlat,
            West = west,
            South = south,
            DefaultTerrain = string.IsNullOrWhiteSpace(defaultTerrain) ? "clear" : defaultTerrain.Trim(),
            Holes = holes ?? new List<string>(),
            Terrains = terrains ?? new Dictionary<string, string>(),
            CreatedAt = now,
            UpdatedAt = now,
            Source = source,
        };
    }

    // ==================== zip 存取 ====================

    public static void Save(string dir, HexGridDoc doc)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeId(doc.Id) + ".zip");
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(EntryName, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open());
            w.Write(JsonSerializer.Serialize(doc, JsonOptions));
        }
        File.Move(tmp, path, true);
    }

    public static HexGridDoc? Read(string dir, string id)
    {
        var path = Path.Combine(dir, SafeId(id) + ".zip");
        if (!File.Exists(path)) return null;
        return ReadPath(path);
    }

    public static List<HexGridDoc> ListAll(string dir)
    {
        if (!Directory.Exists(dir)) return new List<HexGridDoc>();
        var list = new List<HexGridDoc>();
        foreach (var zip in Directory.GetFiles(dir, "*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var doc = ReadPath(zip);
            if (doc != null) list.Add(doc);
        }
        return list;
    }

    public static bool Delete(string dir, string id)
    {
        var path = Path.Combine(dir, SafeId(id) + ".zip");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>包是否存在于服务器。</summary>
    public static bool Exists(string dir, string id) =>
        !string.IsNullOrWhiteSpace(id) && File.Exists(Path.Combine(dir, SafeId(id) + ".zip"));

    private static HexGridDoc? ReadPath(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry(EntryName);
            if (entry == null) return null;
            using var r = new StreamReader(entry.Open());
            return JsonSerializer.Deserialize<HexGridDoc>(r.ReadToEnd(), JsonOptions);
        }
        catch (Exception)
        {
            return null; // 坏包直接跳过，列表不因单个损坏文件崩
        }
    }

    /// <summary>id 直接当文件名用，挡住 ../ 与非法字符（id 来自客户端）。</summary>
    private static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..", StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"非法网格包 id: {id}");
        return id;
    }

    // ==================== 几何 → 引擎棋盘 ====================

    /// <summary>把网格包还原成引擎棋盘：HexGrid（Mercator 几何）+ 每格地形（挖除格不入 Cells，等价 void）。</summary>
    public static (HexGrid Grid, Dictionary<string, Cell> Cells) Build(HexGridDoc doc)
    {
        var radius = doc.FlatToFlat / Sqrt3;                       // 外接圆半径（Mercator 米）
        var ox = LngToMx(doc.West) + Sqrt3 / 2.0 * radius;         // 棋盘西缘 → hex(0,0) 中心
        var oy = LatToMy(doc.South) + radius;                      // 棋盘南缘 → hex(0,0) 中心

        var boardW = doc.Cols * doc.FlatToFlat + doc.FlatToFlat / 2.0;
        var boardH = (doc.Rows - 1) * 1.5 * radius + 2.0 * radius;
        var centerLat = MyToLat(oy - boardH / 2.0);
        var scale = 1.0 / Math.Cos(centerLat * Math.PI / 180.0);   // Mercator 米 → 地面米
        var cellGroundKm = doc.FlatToFlat / scale / 1000.0;

        var grid = new HexGrid(ox, oy, radius, doc.Cols, doc.Rows, cellGroundKm, scale);

        var holes = new HashSet<string>(doc.Holes ?? new List<string>());
        var cells = new Dictionary<string, Cell>();
        for (var r = 0; r < doc.Rows; r++)
        {
            for (var q = 0; q < doc.Cols; q++)
            {
                var key = $"{q},{r}";
                if (holes.Contains(key)) continue;
                cells[key] = new Cell
                {
                    Terrain = doc.Terrains != null && doc.Terrains.TryGetValue(key, out var t)
                        ? t : doc.DefaultTerrain,
                };
            }
        }
        return (grid, cells);
    }

    // ==================== Web Mercator 换算（与六角格管理页同一套口径） ====================

    public static double LngToMx(double lng) => lng * Math.PI / 180.0 * EarthR;
    public static double LatToMy(double lat) => Math.Log(Math.Tan(Math.PI / 4.0 + lat * Math.PI / 360.0)) * EarthR;
    public static double MxToLng(double mx) => mx / EarthR * 180.0 / Math.PI;
    public static double MyToLat(double my) => (2.0 * Math.Atan(Math.Exp(my / EarthR)) - Math.PI / 2.0) * 180.0 / Math.PI;

    /// <summary>棋盘外接矩形 [west, south, east, north]（WGS84）。</summary>
    public static double[] BboxOf(HexGridDoc doc)
    {
        var radius = doc.FlatToFlat / Sqrt3;
        var east = MxToLng(LngToMx(doc.West) + Sqrt3 * radius * (doc.Cols + 0.5));
        var north = MyToLat(LatToMy(doc.South) + radius * (1.5 * doc.Rows + 0.5));
        return new[] { doc.West, doc.South, east, north };
    }
}
