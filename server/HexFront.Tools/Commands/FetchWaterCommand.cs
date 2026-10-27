using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using HexFront.Core;
using HexFront.Core.GisTools;
using HexFront.Tools.Common;
using NetTopologySuite.Geometries;

namespace HexFront.Tools.Commands;

/// <summary>
/// fetch-water 子命令：Natural Earth 10m 水系（河流/湖泊）-> 统一 .gpkg（对应 fetch_global_water.py）。
/// 下载 zip -> 解压 shapefile -> ShapefileReader 解析 -> GeoPackageWriter 写出（保留 DBF 列 + src_layer）；
/// 湖泊三图层合并为 lakes_all。无 GDAL 依赖。
/// </summary>
public static class FetchWaterCommand
{
    private const string Mirror = "https://naturalearth.s3.amazonaws.com/10m_physical/{0}.zip";
    private static readonly (string OutLayer, string Name)[] Layers =
    {
        ("rivers", "ne_10m_rivers_lake_centerlines"),
        ("rivers_scale_rank", "ne_10m_rivers_lake_centerlines_scale_rank"),
        ("lakes", "ne_10m_lakes"),
        ("lakes_europe", "ne_10m_lakes_europe"),
        ("lakes_north_america", "ne_10m_lakes_north_america"),
        ("lakes_historic", "ne_10m_lakes_historic"),
    };
    private static readonly HashSet<string> LakeLayers = new() { "lakes", "lakes_europe", "lakes_north_america" };

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("fetch-water — 全球河流湖泊矢量（Natural Earth 10m -> gpkg）");
            Console.WriteLine("  --root DIR         数据根（默认 <repo>/data/water）");
            Console.WriteLine("  --step all|download|convert|validate  默认 all");
            Console.WriteLine("  --keep-src         保留解压出的 shapefile（默认转换完即删）");
            return 0;
        }
        if (a.Bool("log")) Log.SetFile(a.Get("log"));

        var root = a.Get("root") ?? UtilsPath.DataSub("water");
        Directory.CreateDirectory(root);
        var step = a.Get("step", "all");
        var keepSrc = a.Bool("keep-src");

        var t0 = DateTime.UtcNow;
        Log.Info($"root={root} step={step}");
        if (step is "all" or "download") StepDownload(root);
        if (step is "all" or "convert") StepConvert(root, keepSrc);
        if (step is "all" or "validate") StepValidate(root);
        Log.Info($"结束，耗时 {(DateTime.UtcNow - t0).TotalSeconds:F1}s");
        return 0;
    }

    private static void StepDownload(string root)
    {
        var raw = Path.Combine(root, "raw");
        Directory.CreateDirectory(raw);
        long total = 0;
        foreach (var (_, name) in Layers)
        {
            var url = string.Format(Mirror, name);
            var dest = Path.Combine(raw, name + ".zip");
            total += Downloader.DownloadFileAsync(url, dest).GetAwaiter().GetResult();
        }
        Log.Info($"下载合计 {total / 1048576.0:F2} MB");
    }

    private static void StepConvert(string root, bool keepSrc)
    {
        var outGpkg = Path.Combine(root, "global_water.gpkg");
        if (File.Exists(outGpkg)) File.Delete(outGpkg);
        var srcDir = Path.Combine(root, "src");
        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);

        var lakeParts = new List<(List<string> Fields, List<(Geometry Geom, Dictionary<string, string> Attrs)> Rows)>();

        foreach (var (outLayer, name) in Layers)
        {
            var zip = Path.Combine(root, "raw", name + ".zip");
            var shp = ExtractShp(zip, Path.Combine(srcDir, name));
            var reader = ShapefileReader.Open(Path.ChangeExtension(shp, null)!);
            var fields = new List<string> { "src_layer" };
            fields.AddRange(reader.Fields);
            var geomType = reader.Features.Count > 0
                ? reader.Features[0].Geometry.GeometryType.ToUpperInvariant()
                : "GEOMETRY";

            using (var w = GeoPackageWriter.Create(outGpkg, outLayer, geomType,
                       fields.Select(f => (f, "TEXT"))))
            {
                foreach (var f in reader.Features)
                {
                    var vals = new object?[fields.Count];
                    vals[0] = outLayer;
                    for (int i = 0; i < f.Attributes.Length; i++) vals[i + 1] = f.Attributes[i];
                    w.AddFeature(f.Geometry, vals);
                }
            }
            Log.Step($"{outLayer,-22} {reader.Features.Count,6} 要素  {geomType}");

            if (LakeLayers.Contains(outLayer))
            {
                var dicts = reader.Features.Select(f =>
                {
                    var d = new Dictionary<string, string> { ["src_layer"] = outLayer };
                    for (int i = 0; i < reader.Fields.Count; i++)
                        d[reader.Fields[i]] = i < f.Attributes.Length ? f.Attributes[i] : "";
                    return (f.Geometry, d);
                }).ToList();
                lakeParts.Add((fields, dicts));
            }
        }

        // 合并湖泊三图层 -> lakes_all（列取并集，来源记 src_layer）
        var unionFields = new List<string> { "src_layer" };
        foreach (var p in lakeParts)
            foreach (var fld in p.Fields.Where(f => f != "src_layer"))
                if (!unionFields.Contains(fld)) unionFields.Add(fld);

        using (var w = GeoPackageWriter.Create(outGpkg, "lakes_all", "MULTIPOLYGON",
                   unionFields.Select(f => (f, "TEXT"))))
        {
            foreach (var part in lakeParts)
                foreach (var (geom, d) in part.Rows)
                {
                    var vals = new object?[unionFields.Count];
                    for (int i = 0; i < unionFields.Count; i++)
                        vals[i] = d.TryGetValue(unionFields[i], out var v) ? v : null;
                    w.AddFeature(geom, vals);
                }
        }
        var mergedCount = lakeParts.Sum(p => p.Rows.Count);
        Log.Step($"{"lakes_all",-22} {mergedCount,6} 要素（{string.Join("+", LakeLayers)} 合并）");

        Log.Info($"输出 {outGpkg} ({new FileInfo(outGpkg).Length / 1048576.0:F2} MB)");
        if (!keepSrc)
        {
            try { Directory.Delete(srcDir, true); Log.Step("已删除中间 shapefile 目录 src/（保留 raw/*.zip）"); }
            catch { /* 忽略清理失败 */ }
        }
    }

    private static void StepValidate(string root)
    {
        var gpkg = Path.Combine(root, "global_water.gpkg");
        using var r = GeoPackageReader.Open(gpkg);
        var meta = new Dictionary<string, object?>
        {
            ["generated_at"] = DateTime.UtcNow.ToString("O"),
            ["file"] = gpkg,
            ["size_mb"] = Math.Round(new FileInfo(gpkg).Length / 1048576.0, 2),
            ["source"] = "Natural Earth 10m 水系（naturalearth.s3.amazonaws.com 镜像）",
            ["layers"] = new Dictionary<string, object?>(),
        };
        foreach (var layerName in r.Layers())
        {
            var layer = r.ReadLayer(layerName);
            var geomTypes = layer.Features.Select(f => f.Geometry.GeometryType).Distinct().ToList();
            var (minX, minY, maxX, maxY) = Bounds(layer);
            Log.Step($"{layerName,-22} {layer.Features.Count,8} 要素 {string.Join("/", geomTypes)}");
            ((Dictionary<string, object?>)meta["layers"])[layerName] = new Dictionary<string, object?>
            {
                ["features"] = layer.Features.Count,
                ["geometry"] = string.Join("/", geomTypes),
                ["bounds"] = new[] { Round(minX), Round(minY), Round(maxX), Round(maxY) },
                ["fields"] = layer.Fields,
            };
        }
        var metaPath = Path.Combine(root, "metadata.json");
        File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(meta, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"metadata.json 已写入 {metaPath}");
    }

    private static (double, double, double, double) Bounds(GpkgLayer layer)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var f in layer.Features)
        {
            var e = f.Geometry.EnvelopeInternal;
            if (e == null) continue;
            minX = Math.Min(minX, e.MinX); minY = Math.Min(minY, e.MinY);
            maxX = Math.Max(maxX, e.MaxX); maxY = Math.Max(maxY, e.MaxY);
        }
        return (minX, minY, maxX, maxY);
    }

    private static double Round(double v) => Math.Round(v, 3);

    private static string ExtractShp(string zipPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var za = ZipFile.OpenRead(zipPath);
        foreach (var entry in za.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var dest = Path.Combine(outDir, entry.Name);
            entry.ExtractToFile(dest, true);
        }
        var shp = Directory.EnumerateFiles(outDir, "*.shp", SearchOption.AllDirectories).FirstOrDefault();
        if (shp == null) throw new FileNotFoundException("解压后找不到 .shp", zipPath);
        return shp;
    }
}
