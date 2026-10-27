using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using HexFront.Core;
using HexFront.Core.GisTools;
using HexFront.Tools.Common;

namespace HexFront.Tools.Commands;

/// <summary>
/// extract-terrain 子命令：GeoTIFF -> 紧凑 .ter 瓦片（无 GDAL）。
/// 对应 extract_terrain.py：读 GeoBand 窗口 -> 写 TER1 头 + int16 高程（+ 可选 uint8 土地覆盖）。
/// </summary>
public static class ExtractTerrainCommand
{
    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("extract-terrain — GeoTIFF -> .ter 地形瓦片（无 GDAL）");
            Console.WriteLine("  --dem PATH           DEM GeoTIFF（int16, EPSG:4326，默认 data/gis/dem/global_dem_30s.tif）");
            Console.WriteLine("  --landcover PATH     土地覆盖 GeoTIFF（uint8，可选，默认 data/gis/landcover/global_landcover_0p01.tif）");
            Console.WriteLine("  --mode region|global 区域单文件 / 全球切瓦片（默认 region）");
            Console.WriteLine("  --bbox W S E N       区域窗口（度）");
            Console.WriteLine("  --tile-deg FLOAT     全球瓦片边长（度，默认 10）");
            Console.WriteLine("  --out DIR            输出目录（默认 data/terrain）");
            Console.WriteLine("  --name STR           region 模式文件名（默认 region）");
            return 0;
        }

        var demPath = a.Get("dem") ?? UtilsPath.DataSub("gis/dem/global_dem_30s.tif");
        var lcPath = a.Get("landcover") ?? UtilsPath.DataSub("gis/landcover/global_landcover_0p01.tif");
        var mode = a.Get("mode", "region");
        var outDir = a.Get("out") ?? UtilsPath.DataSub("terrain");
        var name = a.Get("name", "region");
        var tileDeg = a.Dbl("tile-deg", 10.0);

        if (!File.Exists(demPath))
        {
            Log.Error($"DEM 不存在: {demPath}");
            return 1;
        }
        using var dem = GeoBand.Open(demPath);
        GeoBand? lc = File.Exists(lcPath) ? GeoBand.Open(lcPath) : null;
        Log.Info($"DEM {dem.Width}x{dem.Height} nodata={(dem.HasNoData ? dem.NoData : -32768)}");
        if (lc != null) Log.Info($"LC  {lc.Width}x{lc.Height} nodata={(lc.HasNoData ? lc.NoData : 0)}");

        Directory.CreateDirectory(outDir);
        var tiles = new List<JsonObject>();

        if (mode == "region")
        {
            var bb = a.GetList("bbox");
            if (bb.Count != 4)
            {
                Log.Error("--bbox 需要 4 个值：W S E N");
                return 1;
            }
            var ci = CultureInfo.InvariantCulture;
            var w = double.Parse(bb[0], ci); var s = double.Parse(bb[1], ci);
            var e = double.Parse(bb[2], ci); var n = double.Parse(bb[3], ci);
            var rec = MakeTile(dem, lc, w, s, e, n, outDir, name);
            if (rec != null) tiles.Add(rec);
            WriteManifest(outDir, tiles, -1, "region");
            Log.Info($"区域瓦片已生成 -> {Path.Combine(outDir, name + ".ter")}");
        }
        else
        {
            foreach (var (w, s, e, n) in TerrainTileWriter.IterTiles(tileDeg))
            {
                var tname = TerrainTileWriter.TileName(w, n);
                var rec = MakeTile(dem, lc, w, s, e, n, outDir, tname);
                if (rec != null)
                {
                    tiles.Add(rec);
                    Log.Step($"+ {tname} ({rec["cols"]}x{rec["rows"]})");
                }
            }
            WriteManifest(outDir, tiles, tileDeg, "global");
            Log.Info($"全球瓦片已生成：{tiles.Count} 块 -> {outDir}");
        }
        return 0;
    }

    private static JsonObject? MakeTile(GeoBand dem, GeoBand? lc, double w, double s, double e, double n,
        string outDir, string name)
    {
        var (elev, ew, es, ee, en, rows, cols) = ReadWindowI16(dem, w, s, e, n);
        if (elev.Length == 0)
        {
            Log.Warn($"跳过空瓦片 {name}（窗口在数据源范围外）");
            return null;
        }
        var resLon = (ee - ew) / cols;
        var resLat = (en - es) / rows;

        byte[]? lcData = null;
        short lcNoData = 0;
        if (lc != null)
        {
            var (ld, lw, ls, le, ln, lr, lc2) = ReadWindowU8(lc, w, s, e, n);
            if (ld.Length > 0)
            {
                var lcRes = (le - lw) / lc2;
                lcData = TerrainTileWriter.ResampleNearest(ld, lr, lc2, lw, ln, lcRes, cols, rows, ew, en, resLon);
                lcNoData = lc.HasNoData ? (short)lc.NoData : (short)0;
            }
        }

        var path = Path.Combine(outDir, name + ".ter");
        short elevNoData = dem.HasNoData ? (short)dem.NoData : (short)(-32768);
        TerrainTileWriter.Write(path, rows, cols, ew, es, ee, en, resLon, resLat,
            elev, lcData, elevNoData, (byte)lcNoData, 4326);

        return new JsonObject
        {
            ["name"] = name,
            ["file"] = name + ".ter",
            ["west"] = Math.Round(ew, 6),
            ["south"] = Math.Round(es, 6),
            ["east"] = Math.Round(ee, 6),
            ["north"] = Math.Round(en, 6),
            ["cols"] = cols,
            ["rows"] = rows,
            ["resLon"] = resLon,
            ["resLat"] = resLat,
            ["hasLandcover"] = lcData != null,
        };
    }

    private static (short[] data, double w, double s, double e, double n, int rows, int cols)
        ReadWindowI16(GeoBand band, double lonW, double latS, double lonE, double latN)
    {
        var (c0f, r0f) = band.GeoToColRow(lonW, latN); // 西北角
        var (c1f, r1f) = band.GeoToColRow(lonE, latS); // 东南角
        int col0 = (int)Math.Floor(c0f), col1 = (int)Math.Ceiling(c1f);
        int row0 = (int)Math.Floor(r0f), row1 = (int)Math.Ceiling(r1f);
        col0 = Math.Clamp(col0, 0, band.Width); col1 = Math.Clamp(col1, 0, band.Width);
        row0 = Math.Clamp(row0, 0, band.Height); row1 = Math.Clamp(row1, 0, band.Height);
        int cols = col1 - col0, rows = row1 - row0;
        if (cols <= 0 || rows <= 0)
            return (Array.Empty<short>(), 0, 0, 0, 0, 0, 0);
        var arr = band.ReadWindowInt16(col0, row0, cols, rows);
        band.PixelToGeo(col0, row0, out double tw, out double tn);
        band.PixelToGeo(col1, row1, out double te, out double ts);
        return (arr, tw, ts, te, tn, rows, cols);
    }

    private static (byte[] data, double w, double s, double e, double n, int rows, int cols)
        ReadWindowU8(GeoBand band, double lonW, double latS, double lonE, double latN)
    {
        var (c0f, r0f) = band.GeoToColRow(lonW, latN);
        var (c1f, r1f) = band.GeoToColRow(lonE, latS);
        int col0 = (int)Math.Floor(c0f), col1 = (int)Math.Ceiling(c1f);
        int row0 = (int)Math.Floor(r0f), row1 = (int)Math.Ceiling(r1f);
        col0 = Math.Clamp(col0, 0, band.Width); col1 = Math.Clamp(col1, 0, band.Width);
        row0 = Math.Clamp(row0, 0, band.Height); row1 = Math.Clamp(row1, 0, band.Height);
        int cols = col1 - col0, rows = row1 - row0;
        if (cols <= 0 || rows <= 0)
            return (Array.Empty<byte>(), 0, 0, 0, 0, 0, 0);
        var arr = band.ReadWindowUInt8(col0, row0, cols, rows);
        band.PixelToGeo(col0, row0, out double tw, out double tn);
        band.PixelToGeo(col1, row1, out double te, out double ts);
        return (arr, tw, ts, te, tn, rows, cols);
    }

    private static void WriteManifest(string dir, List<JsonObject> tiles, double tileDeg, string mode)
    {
        TerrainTileWriter.WriteManifest(dir, tiles,
            mode == "global" ? tileDeg : -1, mode);
    }
}
