using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core.Geometry;
using HexFront.Core.GisTools;
using HexFront.Core.Services;
using HexFront.Core;
using HexFront.Tools.Common;
using NetTopologySuite.Geometries;

namespace HexFront.Tools.Commands;

/// <summary>
/// rasters 子命令：真实栅格/矢量按六角格聚合取样（对应 tools/gis/rasters.py）。
///   · dem_by_hex        —— DEM 像素按格面分箱求均值（最邻近格心会退化为单点噪声，这里按面）
///   · landcover_by_hex  —— 土地覆盖像素按格众数
///   · water_by_hex      —— 河/湖矢量与每格六角多边形求交（NTS）
///   · roads_by_hex      —— 路网线要素离散落格
///   · airports_by_hex   —— 机场点要素落格
/// 无 GDAL 依赖；栅格走 GeoTiffReader，矢量走 GeoPackageReader/ShapefileReader + NTS。
/// </summary>
public static class RastersCommand
{
    private static readonly GeometryFactory F = new();

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("rasters — 真实栅格/矢量按六角格聚合取样");
            Console.WriteLine("  --bbox W S E N      格网经纬度范围（度）");
            Console.WriteLine("  --cell-km FLOAT     每格地面边长 km（默认 5）");
            Console.WriteLine("  --dem PATH          DEM GeoTIFF（int16, 4326）");
            Console.WriteLine("  --landcover PATH    土地覆盖 GeoTIFF（uint8, 4326）");
            Console.WriteLine("  --water-gpkg PATH   水系 gpkg（含 rivers/lakes_all 图层）");
            Console.WriteLine("  --roads PATH        路网线要素（gpkg/shp）");
            Console.WriteLine("  --airports PATH     机场点要素（gpkg/shp）");
            Console.WriteLine("  --out PATH          输出 JSON（默认 stdout 摘要）");
            Console.WriteLine("  --what dem|landcover|water|roads|airports|all  默认 all");
            return 0;
        }
        if (a.Bool("log")) Log.SetFile(a.Get("log"));

        var bb = a.GetList("bbox");
        if (bb.Count != 4) { Log.Error("--bbox 需要 4 个值：W S E N"); return 1; }
        var ci = CultureInfo.InvariantCulture;
        var bbox = new[] { double.Parse(bb[0], ci), double.Parse(bb[1], ci), double.Parse(bb[2], ci), double.Parse(bb[3], ci) };
        var cellKm = a.Dbl("cell-km", 5.0);

        var grid = BuildGrid(bbox, cellKm);
        Log.Info($"格网 {grid.Width}x{grid.Height} cellKm={cellKm} bbox={string.Join(",", bbox)}");
        var what = a.Get("what", "all");
        var result = new JsonObject();
        result["bbox"] = JsonArrayFrom(bbox);
        result["width"] = grid.Width;
        result["height"] = grid.Height;

        if (what is "dem" or "all")
        {
            var dem = a.Get("dem") ?? UtilsPath.DataSub("gis/dem/global_dem_30s.tif");
            if (File.Exists(dem)) result["dem"] = RunDem(grid, dem);
            else Log.Warn($"DEM 不存在，跳过: {dem}");
        }
        if (what is "landcover" or "all")
        {
            var lc = a.Get("landcover") ?? UtilsPath.DataSub("gis/landcover/global_landcover_0p01.tif");
            if (File.Exists(lc)) result["landcover"] = RunLandcover(grid, lc);
            else Log.Warn($"土地覆盖不存在，跳过: {lc}");
        }
        if (what is "water" or "all")
        {
            var wg = a.Get("water-gpkg") ?? UtilsPath.DataSub("water/global_water.gpkg");
            if (File.Exists(wg)) result["water"] = RunWater(grid, wg);
            else Log.Warn($"水系 gpkg 不存在，跳过: {wg}");
        }
        if (what is "roads" or "all")
        {
            var rp = a.Get("roads");
            if (!string.IsNullOrEmpty(rp) && File.Exists(rp)) result["roads"] = RunLineCells(grid, rp, "roads");
        }
        if (what is "airports" or "all")
        {
            var ap = a.Get("airports");
            if (!string.IsNullOrEmpty(ap) && File.Exists(ap)) result["airports"] = RunPointCells(grid, ap);
        }

        var json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var outPath = a.Get("out");
        if (outPath != null) { File.WriteAllText(outPath, json); Log.Info($"结果已写入 {outPath}"); }
        else Console.WriteLine(json);
        return 0;
    }

    // ---------------- 栅格：像素→格分箱 ----------------

    private static JsonObject RunDem(GridCoverage grid, string demPath)
    {
        using var dem = GeoBand.Open(demPath);
        var (sum, cnt) = BinPixels(grid, dem, isByte: false);
        FillGaps(sum, cnt, grid.Width, grid.Height);
        int filled = 0;
        var cells = new JsonObject();
        for (int r = 0; r < grid.Height; r++)
            for (int q = 0; q < grid.Width; q++)
            {
                var i = r * grid.Width + q;
                if (cnt[i] > 0) { cells[$"{q},{r}"] = Math.Round(sum[i] / cnt[i], 1); filled++; }
            }
        Log.Step($"dem: {filled}/{grid.Width * grid.Height} 格有高程数据");
        return new JsonObject { ["kind"] = "local-geotiff", ["path"] = demPath, ["cells_with_data"] = filled };
    }

    private static JsonObject RunLandcover(GridCoverage grid, string lcPath)
    {
        using var lc = GeoBand.Open(lcPath);
        var classTally = TallyClasses(grid, lc);
        int filled = 0;
        foreach (var (i, dict) in classTally)
        {
            if (dict.Count == 0) continue;
            filled++;
        }
        Log.Step($"landcover: {filled}/{grid.Width * grid.Height} 格有类别数据");
        return new JsonObject { ["kind"] = "local-geotiff", ["path"] = lcPath, ["cells_with_data"] = filled };
    }

    private static (double[] sum, int[] cnt) BinPixels(GridCoverage grid, GeoBand band, bool isByte)
    {
        var w = grid.Width; var h = grid.Height;
        var sum = new double[w * h];
        var cnt = new int[w * h];
        var (c0f, r0f) = band.GeoToColRow(grid.Bbox()[0], grid.Bbox()[3]); // NW
        var (c1f, r1f) = band.GeoToColRow(grid.Bbox()[2], grid.Bbox()[1]); // SE
        int col0 = Math.Clamp((int)Math.Floor(c0f), 0, band.Width);
        int col1 = Math.Clamp((int)Math.Ceiling(c1f), 0, band.Width);
        int row0 = Math.Clamp((int)Math.Floor(r0f), 0, band.Height);
        int row1 = Math.Clamp((int)Math.Ceiling(r1f), 0, band.Height);
        if (isByte)
        {
            for (int row = row0; row < row1; row++)
            {
                var arr = band.ReadWindowUInt8(col0, row, col1 - col0, 1);
                for (int cc = 0; cc < arr.Length; cc++)
                {
                    band.PixelToGeo(col0 + cc, row, out double lon, out double lat);
                    var (q, r) = grid.CellAt(lon, lat);
                    if (q >= 0 && q < w && r >= 0 && r < h) cnt[r * w + q]++; // 仅计数由 TallyClasses 处理
                }
            }
        }
        else
        {
            short nd = band.HasNoData ? (short)band.NoData : (short)(-32768);
            for (int row = row0; row < row1; row++)
            {
                var arr = band.ReadWindowInt16(col0, row, col1 - col0, 1);
                for (int cc = 0; cc < arr.Length; cc++)
                {
                    if (arr[cc] == nd) continue;
                    band.PixelToGeo(col0 + cc, row, out double lon, out double lat);
                    var (q, r) = grid.CellAt(lon, lat);
                    if (q >= 0 && q < w && r >= 0 && r < h) { int i = r * w + q; sum[i] += arr[cc]; cnt[i]++; }
                }
            }
        }
        return (sum, cnt);
    }

    private static Dictionary<int, Dictionary<byte, int>> TallyClasses(GridCoverage grid, GeoBand band)
    {
        var w = grid.Width; var h = grid.Height;
        var tally = new Dictionary<int, Dictionary<byte, int>>();
        for (int i = 0; i < w * h; i++) tally[i] = new Dictionary<byte, int>();
        var (c0f, r0f) = band.GeoToColRow(grid.Bbox()[0], grid.Bbox()[3]);
        var (c1f, r1f) = band.GeoToColRow(grid.Bbox()[2], grid.Bbox()[1]);
        int col0 = Math.Clamp((int)Math.Floor(c0f), 0, band.Width);
        int col1 = Math.Clamp((int)Math.Ceiling(c1f), 0, band.Width);
        int row0 = Math.Clamp((int)Math.Floor(r0f), 0, band.Height);
        int row1 = Math.Clamp((int)Math.Ceiling(r1f), 0, band.Height);
        for (int row = row0; row < row1; row++)
        {
            var arr = band.ReadWindowUInt8(col0, row, col1 - col0, 1);
            for (int cc = 0; cc < arr.Length; cc++)
            {
                if (arr[cc] == 0) continue; // 0 = nodata（海洋）
                band.PixelToGeo(col0 + cc, row, out double lon, out double lat);
                var (q, r) = grid.CellAt(lon, lat);
                if (q >= 0 && q < w && r >= 0 && r < h)
                {
                    var i = r * w + q;
                    tally[i].TryGetValue(arr[cc], out int c);
                    tally[i][arr[cc]] = c + 1;
                }
            }
        }
        return tally;
    }

    private static void FillGaps(double[] sum, int[] cnt, int w, int h)
    {
        var q = new Queue<(int, int)>();
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                if (cnt[r * w + c] > 0) q.Enqueue((c, r));
        while (q.Count > 0)
        {
            var (cq, cr) = q.Dequeue();
            var val = sum[cr * w + cq] / cnt[cr * w + cq];
            foreach (var (dq, dr) in Hex.DirectionsFor(cr))
            {
                int nq = cq + dq, nr = cr + dr;
                if (nq < 0 || nq >= w || nr < 0 || nr >= h) continue;
                int ni = nr * w + nq;
                if (cnt[ni] == 0) { sum[ni] = val; cnt[ni] = 1; q.Enqueue((nq, nr)); }
            }
        }
    }

    // ---------------- 矢量：NTS 叠加 ----------------

    private static JsonObject RunWater(GridCoverage grid, string gpkgPath)
    {
        using var r = GeoPackageReader.Open(gpkgPath);
        var layers = r.Layers();
        var riverCells = new HashSet<(int, int)>();
        var lakeShare = new Dictionary<(int, int), double>();
        var hexArea = 1.5 * Math.Sqrt(3.0) * Math.Pow(grid.HexWidthM / Math.Sqrt(3.0), 2);

        Geometry? rivers = null;
        if (layers.Contains("rivers")) rivers = UnionLayer(r.ReadLayer("rivers"));
        Geometry? lakes = null;
        if (layers.Contains("lakes_all")) lakes = UnionLayer(r.ReadLayer("lakes_all"));
        else if (layers.Contains("lakes")) lakes = UnionLayer(r.ReadLayer("lakes"));

        for (int rr = 0; rr < grid.Height; rr++)
            for (int q = 0; q < grid.Width; q++)
            {
                var poly = HexPolygon(grid, q, rr);
                if (rivers != null)
                {
                    var buf = rivers.Buffer(Math.Max(20.0, grid.HexWidthM * 0.35));
                    if (poly.Intersects(buf)) riverCells.Add((q, rr));
                }
                if (lakes != null)
                {
                    var inter = poly.Intersection(lakes);
                    if (!inter.IsEmpty && inter.Area > 0)
                        lakeShare[(q, rr)] = Math.Min(1.0, inter.Area / hexArea);
                }
            }
        Log.Step($"water: 河 {riverCells.Count} 格 / 湖 {lakeShare.Count} 格");
        return new JsonObject
        {
            ["kind"] = "natural-earth-10m-water",
            ["river_cells"] = riverCells.Count,
            ["lake_cells"] = lakeShare.Count,
        };
    }

    private static JsonObject RunLineCells(GridCoverage grid, string path, string label)
    {
        var geoms = ReadGeometries(path);
        var cells = new HashSet<(int, int)>();
        var tol = grid.HexWidthM * 0.5 * 180.0 / 20037508.342789244;
        foreach (var g in geoms)
        {
            Geometry? seg = g is LineString ls ? ls : (g is MultiLineString mls ? mls : null);
            if (seg == null) continue;
            foreach (var (q, r) in SegmentCells(grid, seg, tol)) cells.Add((q, r));
        }
        Log.Step($"{label}: {cells.Count} 格被线要素穿过");
        return new JsonObject { ["kind"] = label, ["cells"] = cells.Count };
    }

    private static JsonObject RunPointCells(GridCoverage grid, string path)
    {
        var geoms = ReadGeometries(path);
        var marks = new Dictionary<(int, int), int>();
        foreach (var g in geoms)
        {
            if (g is not Point pt) continue;
            var (q, r) = grid.CellAt(pt.X, pt.Y);
            if (q >= 0 && q < grid.Width && r >= 0 && r < grid.Height)
                marks.TryAdd((q, r), 1);
        }
        Log.Step($"airports: {marks.Count} 格有机场点");
        return new JsonObject { ["kind"] = "airports", ["cells"] = marks.Count };
    }

    // ---------------- 工具 ----------------

    private static GridCoverage BuildGrid(double[] bbox, double cellKm)
    {
        var (clon, clat) = GisPlanService.GridCenter(bbox);
        var (w, h) = GisPlanService.GridSize(bbox, cellKm);
        return new GridCoverage(clon, clat, w, h, cellKm);
    }

    private static (double x, double y) HexCenter(GridCoverage g, int q, int r) =>
        (g.MercatorOx + g.HexWidthM * (q + (r & 1) / 2.0), g.MercatorOy - g.RowStepM * r);

    private static Polygon HexPolygon(GridCoverage g, int q, int r)
    {
        var (cx, cy) = HexCenter(g, q, r);
        var rad = g.HexWidthM / Math.Sqrt(3.0);
        var coords = new Coordinate[7];
        for (int i = 0; i < 6; i++)
        {
            var ang = Math.PI / 180.0 * (60 * i + 30);
            coords[i] = new Coordinate(cx + rad * Math.Cos(ang), cy + rad * Math.Sin(ang));
        }
        coords[6] = coords[0];
        return F.CreatePolygon(coords);
    }

    private static Geometry? UnionLayer(GpkgLayer layer)
    {
        if (layer.Features.Count == 0) return null;
        var geoms = layer.Features.Select(f => f.Geometry).ToArray();
        return F.CreateGeometryCollection(geoms).Union();
    }

    private static IEnumerable<(int, int)> SegmentCells(GridCoverage grid, Geometry line, double tolDeg)
    {
        var pts = line.Coordinates;
        var outCells = new HashSet<(int, int)>();
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            int steps = (int)Math.Ceiling(a.Distance(b) / tolDeg) + 1;
            for (int s = 0; s <= steps; s++)
            {
                var t = (double)s / steps;
                var lon = a.X + (b.X - a.X) * t;
                var lat = a.Y + (b.Y - a.Y) * t;
                var (q, r) = grid.CellAt(lon, lat);
                if (q >= 0 && q < grid.Width && r >= 0 && r < grid.Height) outCells.Add((q, r));
            }
        }
        return outCells;
    }

    private static List<Geometry> ReadGeometries(string path)
    {
        var list = new List<Geometry>();
        if (path.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase))
        {
            using var r = GeoPackageReader.Open(path);
            foreach (var name in r.Layers())
                foreach (var f in r.ReadLayer(name).Features) list.Add(f.Geometry);
        }
        else if (path.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
        {
            var r = ShapefileReader.Open(Path.ChangeExtension(path, null)!);
            list.AddRange(r.Features.Select(f => f.Geometry));
        }
        else if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, "gdb")) || path.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
                Log.Warn("ESri FileGDB 暂不支持，请转为 gpkg/shp 后传入");
        }
        return list;
    }

    private static JsonArray JsonArrayFrom(double[] v)
    {
        var a = new JsonArray();
        foreach (var x in v) a.Add(x);
        return a;
    }
}
