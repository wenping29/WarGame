using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexFront.Core.GisTools;

/// <summary>
/// 把 GeoTIFF（高程 DEM / 土地覆盖 landcover）写成 C# 端 <see cref="TerrainTile"/>（位于 HexFront.Api）可直接读取的
/// 紧凑 .ter 瓦片。格式与 tools/gis/extract_terrain.py 完全一致（小端、80 字节固定头 + int16 高程 + 可选 uint8 土地覆盖）。
///
/// 与 Python 端相同：高程 nodata = -32768，土地覆盖 nodata = 0。
/// </summary>
public static class TerrainTileWriter
{
    public const string Magic = "TER1";
    public const int Version = 1;
    public const short ElevNoDataDefault = -32768;
    public const byte LcNoDataDefault = 0;

    /// <summary>把一块高程（int16，长度 = rows*cols）+ 可选土地覆盖（uint8）写进 .ter 文件。</summary>
    public static void Write(string path, int rows, int cols, double west, double south, double east, double north,
        double resLon, double resLat, short[] elev, byte[]? landcover,
        short elevNoData = ElevNoDataDefault, byte lcNoData = LcNoDataDefault, int crs = 4326)
    {
        if (elev.Length != rows * cols)
            throw new ArgumentException($"高程数组长度 {elev.Length} 必须等于 rows*cols={rows * cols}", nameof(elev));
        if (landcover != null && landcover.Length != elev.Length)
            throw new ArgumentException("土地覆盖数组长度必须与高程一致", nameof(landcover));

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        w.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
        w.Write(Version);
        w.Write(crs);
        w.Write(west); w.Write(south); w.Write(east); w.Write(north);
        w.Write(cols); w.Write(rows);
        w.Write(resLon); w.Write(resLat);
        w.Write(landcover != null ? 1 : 0);
        w.Write(elevNoData);
        w.Write(lcNoData);
        w.Write(new byte[5]); // padding 到 80 字节头

        // int16 高程，小端
        var ebuf = new byte[elev.Length * 2];
        Buffer.BlockCopy(elev, 0, ebuf, 0, ebuf.Length);
        w.Write(ebuf);
        if (landcover != null)
            w.Write(landcover);
    }

    /// <summary>把分类数据（土地覆盖）按最邻近重采样到目标网格（与 Python resample_nearest 一致）。</summary>
    public static byte[] ResampleNearest(byte[] src, int srcRows, int srcCols, double srcWest, double srcNorth,
        double srcRes, int dstRows, int dstCols, double dstWest, double dstNorth, double dstRes)
    {
        var dst = new byte[dstRows * dstCols];
        for (var r = 0; r < dstRows; r++)
        {
            double lat = dstNorth - (r + 0.5) * dstRes;
            int sr = (int)Math.Clamp(Math.Round((srcNorth - lat) / srcRes - 0.5), 0, srcRows - 1);
            for (var c = 0; c < dstCols; c++)
            {
                double lon = dstWest + (c + 0.5) * dstRes;
                int sc = (int)Math.Clamp(Math.Round((lon - srcWest) / srcRes - 0.5), 0, srcCols - 1);
                dst[r * dstCols + c] = src[sr * srcCols + sc];
            }
        }
        return dst;
    }

    public static short[] ResampleNearest(short[] src, int srcRows, int srcCols, double srcWest, double srcNorth,
        double srcRes, int dstRows, int dstCols, double dstWest, double dstNorth, double dstRes)
    {
        var dst = new short[dstRows * dstCols];
        for (var r = 0; r < dstRows; r++)
        {
            double lat = dstNorth - (r + 0.5) * dstRes;
            int sr = (int)Math.Clamp(Math.Round((srcNorth - lat) / srcRes - 0.5), 0, srcRows - 1);
            for (var c = 0; c < dstCols; c++)
            {
                double lon = dstWest + (c + 0.5) * dstRes;
                int sc = (int)Math.Clamp(Math.Round((lon - srcWest) / srcRes - 0.5), 0, srcCols - 1);
                dst[r * dstCols + c] = src[sr * srcCols + sc];
            }
        }
        return dst;
    }

    /// <summary>瓦片名（与 Python tile_name 一致）：N51E006 形式。</summary>
    public static string TileName(double west, double north)
    {
        string ns = north >= 0 ? "N" : "S";
        string ew = west >= 0 ? "E" : "W";
        return $"{ns}{Math.Abs((int)Math.Round(north)):D2}{ew}{Math.Abs((int)Math.Round(west)):D3}";
    }

    /// <summary>按 tileDeg（可为小数）把全球切成 [west,south,east,north] 瓦片边界序列。</summary>
    public static IEnumerable<(double west, double south, double east, double north)> IterTiles(double tileDeg)
    {
        const double eps = 1e-9;
        double n = 90.0;
        while (n > -90.0 + eps)
        {
            double south = n - tileDeg;
            if (south < -90.0) south = -90.0;
            if (n <= south + eps) { n -= tileDeg; continue; }
            double w = -180.0;
            while (w < 180.0 - eps)
            {
                double east = w + tileDeg;
                if (east > 180.0) east = 180.0;
                if (east <= w + eps) { w += tileDeg; continue; }
                yield return (w, south, east, n);
                w += tileDeg;
            }
            n -= tileDeg;
        }
    }

    /// <summary>写出 terrain_manifest.json（与 Python 同构）。</summary>
    public static void WriteManifest(string dir, List<JsonObject> tiles, double tileDeg, string mode)
    {
        var manifest = new JsonObject
        {
            ["version"] = Version,
            ["crs"] = 4326,
            ["mode"] = mode,
            ["tileDeg"] = tileDeg,
            ["tiles"] = new JsonArray(tiles.ToArray()),
        };
        File.WriteAllText(Path.Combine(dir, "terrain_manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>round 到字符串（避免文化相关的小数分隔符）。</summary>
    public static double R(double v, int digits = 6) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
}
