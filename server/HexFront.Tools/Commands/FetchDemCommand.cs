using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HexFront.Core.GisTools;
using HexFront.Tools.Common;

namespace HexFront.Tools.Commands;

/// <summary>
/// fetch-dem 子命令：下载全球 DEM（AWS terrarium 瓦片 -> EPSG:3857 镶嵌 -> EPSG:4326 栅格）。
/// 对应 tools/gis/fetch_global_dem.py，无 GDAL 依赖：PNG 用 PngDecoder 解码，镶嵌/重采样用
/// GeoTiffWriter/GeoBand 手写为 GeoTIFF。
///   --root DIR         缓存与产物目录（默认 data/dem）
///   --z INT            瓦片金字塔层级（默认 7 ≈ 1.2km）
///   --workers INT      并行下载数（默认 16）
///   --cell-arcsec INT  输出网格分辨率（弧秒，默认 30）
///   --probe INT        只下载前 N 张测吞吐
///   --step all|download|mosaic|resample|validate
/// </summary>
public static class FetchDemCommand
{
    private const string TileUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";
    private const int TilePx = 256;
    private const short Nodata = -32768;
    private const double OriginShift = 20037508.342789244;

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("fetch-dem — 下载全球 DEM（terrarium 瓦片 -> GeoTIFF）");
            Console.WriteLine("  --root DIR         缓存与产物目录（默认 data/dem）");
            Console.WriteLine("  --z INT            瓦片金字塔层级（默认 7 ≈ 1.2km）");
            Console.WriteLine("  --workers INT      并行下载数（默认 16）");
            Console.WriteLine("  --cell-arcsec INT  输出网格分辨率（弧秒，默认 30）");
            Console.WriteLine("  --probe INT        只下载前 N 张测吞吐");
            Console.WriteLine("  --step all|download|mosaic|resample|validate");
            return 0;
        }
        if (a.Bool("log")) Log.SetFile(a.Get("log"));

        var root = a.Get("root") ?? RepoLocator.DataSub("dem");
        Directory.CreateDirectory(root);
        int z = a.Int("z", 7);
        int workers = a.Int("workers", 16);
        int cellArcsec = a.Int("cell-arcsec", 30);
        int probe = a.Int("probe", 0);
        var step = a.Get("step", "all");

        var tilesDir = Path.Combine(root, $"tiles_z{z}");
        var mosaicPath = Path.Combine(root, $"global_dem_z{z}_webmercator.tif");
        var outPath = Path.Combine(root, $"global_dem_{cellArcsec}s.tif");

        var t0 = DateTime.Now;
        if (probe > 0)
        {
            Log.Info($"[探测] 下载前 {probe} 张瓦片测吞吐（z={z}）");
            StepDownload(tilesDir, z, workers, probe).Wait();
            Log.Info($"[探测] 完成，用时 {Elapsed(t0)}");
            return 0;
        }
        if (step is "all" or "download")
            StepDownload(tilesDir, z, workers, 0).Wait();
        if (step is "all" or "mosaic")
            StepMosaic(tilesDir, mosaicPath, z);
        if (step is "all" or "resample")
            StepResample(mosaicPath, outPath, cellArcsec, z);
        if (step is "all" or "validate")
            StepValidate(root, outPath, cellArcsec);
        Log.Info($"[完成] 总耗时 {Elapsed(t0)}");
        return 0;
    }

    private static async Task StepDownload(string tilesDir, int z, int workers, int total)
    {
        int n = 1 << z;
        var jobs = new (int x, int y)[n * n];
        int k = 0;
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) jobs[k++] = (x, y);
        if (total > 0) jobs = jobs.Take(total).ToArray();

        var sem = new SemaphoreSlim(workers);
        int done = 0, missing = 0;
        var t0 = DateTime.Now;
        Log.Info($"[下载] 共 {jobs.Length} 张瓦片，并行 {workers}");
        await Task.WhenAll(jobs.Select(async job =>
        {
            await sem.WaitAsync();
            try
            {
                var (x, y) = job;
                var dest = Path.Combine(tilesDir, $"{x}", $"{x}_{y}.png");
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) { Interlocked.Increment(ref done); return; }
                var url = TileUrl.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
                var bytes = await Downloader.GetBytesAsync(url);
                if (bytes.Length == 0) { Interlocked.Increment(ref missing); Interlocked.Increment(ref done); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await File.WriteAllBytesAsync(dest, bytes);
                Interlocked.Increment(ref done);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref missing);
                Interlocked.Increment(ref done);
                Log.Warn($"[下载] 瓦片失败：{ex.Message}");
            }
            finally { sem.Release(); }
        }));
        Log.Info($"[下载] 完成 {done} 张（缺失/失败 {missing}），用时 {Elapsed(t0)}");
    }

    private static void StepMosaic(string tilesDir, string outPath, int z)
    {
        if (File.Exists(outPath)) { Log.Info($"[镶嵌] 已存在，跳过 → {outPath}"); return; }
        int n = 1 << z;
        int size = n * TilePx;
        var t0 = DateTime.Now;
        int filled = 0, skipped = 0;
        using (var w = new GeoTiffWriter(outPath, size, size, -OriginShift, -OriginShift, OriginShift, OriginShift,
                   signed: true, noData: Nodata))
        {
            for (int ty = 0; ty < n; ty++)
            {
                for (int tx = 0; tx < n; tx++)
                {
                    var p = Path.Combine(tilesDir, $"{tx}", $"{tx}_{ty}.png");
                    short[] tile;
                    if (File.Exists(p) && new FileInfo(p).Length > 0)
                    {
                        tile = DecodeTerrarium(File.ReadAllBytes(p));
                        filled++;
                    }
                    else
                    {
                        tile = new short[TilePx * TilePx];
                        for (int i = 0; i < tile.Length; i++) tile[i] = Nodata;
                        skipped++;
                    }
                    w.WriteTile(tx, ty, tile);
                }
                if ((ty + 1) % 8 == 0 || ty == n - 1)
                    Log.Info($"[镶嵌] 行 {ty + 1}/{n} 已写 {filled} 块，用时 {Elapsed(t0)}");
            }
        }
        Log.Info($"[镶嵌] 完成：{filled} 块写入，{skipped} 块缺失留空 → {outPath}");
    }

    private static void StepResample(string srcPath, string outPath, int cellArcsec, int z)
    {
        if (File.Exists(outPath)) { Log.Info($"[重采样] 已存在，跳过 → {outPath}"); return; }
        using var src = GeoBand.Open(srcPath);
        int sw = src.Width, sh = src.Height;
        double dx = cellArcsec / 3600.0;
        int outW = (int)Math.Round(360.0 / dx);
        int outH = (int)Math.Round(180.0 / dx);
        Log.Info($"[重采样] 3857 {sw}x{sh} -> 4326 {outW}x{outH}（{cellArcsec}弧秒）");

        var c0 = new int[outW];
        var c1 = new int[outW];
        var fc = new double[outW];
        for (int i = 0; i < outW; i++)
        {
            double lon = i * dx - 180.0;
            double cf = (lon + 180.0) / 360.0 * sw - 0.5;
            c0[i] = Math.Clamp((int)Math.Floor(cf), 0, sw - 1);
            c1[i] = Math.Clamp(c0[i] + 1, 0, sw - 1);
            fc[i] = cf - Math.Floor(cf);
        }

        var t0 = DateTime.Now;
        using (var w = new GeoTiffWriter(outPath, outW, outH, -180, -90, 180, 90, signed: true, noData: Nodata))
        {
            int nty = (outH + 255) / 256;
            int ntx = (outW + 255) / 256;
            short[]? rowA = null, rowB = null;
            int cachedR0 = -999;
            for (int ty = 0; ty < nty; ty++)
            {
                int th = Math.Min(256, outH - ty * 256);
                var chunk = new short[th * outW];
                for (int bi = 0; bi < th; bi++)
                {
                    int j = ty * 256 + bi;
                    double lat = 90.0 - (j + 0.5) * dx;
                    if (!(lat > -85.05 && lat < 85.05)) continue;
                    double mercY = 6378137.0 * Math.Log(Math.Tan(Math.PI / 4 + (lat * Math.PI / 180) / 2));
                    double sy = (OriginShift - mercY) / (2 * OriginShift) * sh - 0.5;
                    int r0 = (int)Math.Floor(sy);
                    double fr = sy - r0;
                    r0 = Math.Clamp(r0, 0, sh - 1);
                    if (r0 != cachedR0)
                    {
                        rowA = src.ReadWindowInt16(0, r0, sw, 1);
                        var rb = src.ReadWindowInt16(0, Math.Min(r0 + 1, sh - 1), sw, 1);
                        rowB = rb.Length == sw ? rb : rowA;
                        cachedR0 = r0;
                    }
                    for (int i = 0; i < outW; i++)
                    {
                        short va = rowA![c0[i]], vb = rowA[c1[i]], vc = rowB![c0[i]], vd = rowB[c1[i]];
                        if (va == Nodata || vb == Nodata || vc == Nodata || vd == Nodata) { chunk[bi * outW + i] = Nodata; continue; }
                        double v = (va * (1 - fc[i]) + vb * fc[i]) * (1 - fr) + (vc * (1 - fc[i]) + vd * fc[i]) * fr;
                        chunk[bi * outW + i] = (short)Math.Clamp(Math.Round(v), -32768, 32767);
                    }
                }
                for (int tx = 0; tx < ntx; tx++)
                {
                    int tw = Math.Min(256, outW - tx * 256);
                    var tile = new short[tw * th];
                    for (int r = 0; r < th; r++)
                        for (int c = 0; c < tw; c++)
                            tile[r * tw + c] = chunk[r * outW + (tx * 256 + c)];
                    w.WriteTile(tx, ty, tile);
                }
                if (ty % 8 == 0 || ty == nty - 1)
                    Log.Info($"[重采样] 输出行 {(ty + 1) * 256}/{outH} 用时 {Elapsed(t0)}");
            }
        }
        Log.Info($"[重采样] 完成 → {outPath}");
    }

    private static void StepValidate(string root, string outPath, int cellArcsec)
    {
        using var d = GeoBand.Open(outPath);
        double? vmin = null, vmax = null; double vsum = 0; long vn = 0, vbad = 0, vpos = 0;
        int sh = d.Height;
        for (int j0 = 0; j0 < sh; j0 += 512)
        {
            int j1 = Math.Min(j0 + 512, sh);
            var band = d.ReadWindowInt16(0, j0, d.Width, j1 - j0);
            for (int i = 0; i < band.Length; i++)
            {
                short v = band[i];
                if (v == Nodata) { vbad++; continue; }
                vmin = vmin.HasValue ? Math.Min(vmin.Value, v) : v;
                vmax = vmax.HasValue ? Math.Max(vmax.Value, v) : v;
                vsum += v; vn++; if (v > 0) vpos++;
            }
            Log.Info($"[校验] 行 {j1}/{sh} 已统计");
        }
        if (vn == 0) throw new InvalidOperationException("[校验] 输出栅格全是无数据，检查上游瓦片");
        var stats = new JsonObject
        {
            ["file"] = Path.GetFullPath(outPath),
            ["width"] = d.Width,
            ["height"] = d.Height,
            ["cell_arcsec"] = 3600.0 * 180.0 / d.Height,
            ["crs"] = "EPSG:4326",
            ["valid_pixels"] = vn,
            ["nodata_pixels"] = vbad,
            ["min_m"] = vmin,
            ["max_m"] = vmax,
            ["mean_m"] = Math.Round(vsum / vn, 2),
            ["above_sea_pct"] = Math.Round(100.0 * vpos / vn, 2),
        };
        Log.Info($"[校验] 有效像素 {vn} / 无数据 {vbad}，高程范围 {vmin} ~ {vmax} m，均值 {stats["mean_m"]} m，陆地占比 {stats["above_sea_pct"]}%");
        MergeMeta(root, "global_dem", stats);
    }

    private static short[] DecodeTerrarium(byte[] png)
    {
        var img = PngDecoder.Decode(png);
        int ch = img.Channels;
        var tile = new short[TilePx * TilePx];
        for (int i = 0; i < TilePx * TilePx; i++)
        {
            int r = img.Data[i * ch], g = img.Data[i * ch + 1], b = img.Data[i * ch + 2];
            double e = r * 256.0 + g + b / 256.0 - 32768.0;
            tile[i] = (short)Math.Clamp(Math.Round(e), -32768, 32767);
        }
        return tile;
    }

    private static void MergeMeta(string root, string key, JsonObject stats)
    {
        var mp = Path.Combine(root, "metadata.json");
        JsonObject meta = new();
        if (File.Exists(mp))
        {
            try { meta = JsonNode.Parse(File.ReadAllText(mp)) as JsonObject ?? new JsonObject(); }
            catch { meta = new JsonObject(); }
        }
        meta[key] = stats;
        File.WriteAllText(mp, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"[校验] 元数据写入 → {mp}");
    }

    private static string Elapsed(DateTime t0) => (DateTime.Now - t0).ToString(@"hh\:mm\:ss");
}
