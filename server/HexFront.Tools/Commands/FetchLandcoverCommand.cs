using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HexFront.Core;
using HexFront.Core.GisTools;
using HexFront.Tools.Common;

namespace HexFront.Tools.Commands;

/// <summary>
/// fetch-landcover 子命令：装配全球土地覆盖（ESA WorldCover 2021 v200）概览。
/// 对应 tools/gis/fetch_global_landcover.py，无 GDAL 依赖：用 HTTP Range 读远程 COG 概览
/// （CogReader），最近邻抽稀到目标经纬度网格，写 uint8 GeoTIFF。
///   --root DIR          缓存与产物目录（默认 data/landcover）
///   --cell-deg FLOAT   目标网格（度），0.01≈1.1km（默认 0.01）
///   --workers INT       并行瓦片数（默认 6）
///   --probe INT         只装配前 N 块瓦片
///   --step all|build|validate
/// </summary>
public static class FetchLandcoverCommand
{
    private const string ListUrl = "https://esa-worldcover.s3.amazonaws.com/?list-type=2&max-keys=1000&prefix={prefix}";
    private const string ObjUrl = "https://esa-worldcover.s3.amazonaws.com/{key}";
    private const string Prefix = "v200/2021/map/";

    private static readonly Dictionary<int, string> ClassNames = new()
    {
        [10] = "Tree cover", [20] = "Shrubland", [30] = "Grassland", [40] = "Cropland",
        [50] = "Built-up", [60] = "Bare/sparse vegetation", [70] = "Snow and ice",
        [80] = "Permanent water bodies", [90] = "Herbaceous wetland", [95] = "Mangroves", [97] = "Moss and lichen",
    };

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("fetch-landcover — 装配全球土地覆盖（ESA WorldCover v200）");
            Console.WriteLine("  --root DIR          缓存与产物目录（默认 data/landcover）");
            Console.WriteLine("  --cell-deg FLOAT   目标网格（度），0.01≈1.1km（默认 0.01）");
            Console.WriteLine("  --workers INT      并行瓦片数（默认 6）");
            Console.WriteLine("  --probe INT        只装配前 N 块瓦片");
            Console.WriteLine("  --step all|build|validate");
            return 0;
        }
        if (a.Bool("log")) Log.SetFile(a.Get("log"));

        var root = a.Get("root") ?? UtilsPath.DataSub("landcover");
        Directory.CreateDirectory(root);
        double cellDeg = a.Dbl("cell-deg", 0.01);
        int workers = a.Int("workers", 6);
        int probe = a.Int("probe", 0);
        var step = a.Get("step", "all");

        int gridW = (int)Math.Round(360.0 / cellDeg);
        int gridH = (int)Math.Round(180.0 / cellDeg);
        var outPath = Path.Combine(root, $"global_landcover_{(cellDeg.ToString("g", CultureInfo.InvariantCulture)).Replace(".", "p")}.tif");
        var statePath = Path.Combine(root, "landcover_progress.json");

        if (step is "all" or "build")
        {
            if (gridW * (long)gridH > 1_200_000_000)
                Log.Warn($"[注意] 目标网格 {gridW}x{gridH} 约 {gridW * (long)gridH / 1e6:F0}MB 内存，建议用更大的 --cell-deg 以降低开销");
            StepBuild(root, outPath, statePath, cellDeg, gridW, gridH, workers, probe).Wait();
        }
        if (probe > 0)
        {
            Log.Info($"[探测] 前 {probe} 块已装配（--probe 模式，不跑 validate）");
            return 0;
        }
        if (step is "all" or "validate")
            StepValidate(root, outPath, cellDeg, gridW, gridH);
        return 0;
    }

    private static async Task StepBuild(string root, string outPath, string statePath, double cellDeg,
        int gridW, int gridH, int workers, int probe)
    {
        Log.Info("[列表] 拉取 ESA WorldCover 瓦片清单 ...");
        var keys = ListTiles();
        Log.Info($"[列表] {Prefix} 共 {keys.Count} 块 3°x3° 瓦片");
        if (probe > 0) keys = keys.Take(probe).ToList();

        var full = new byte[gridW * gridH]; // 0 = nodata（海洋/未覆盖）
        var done = LoadDone(statePath);
        var todo = keys.Where(k => !done.Contains(k)).ToList();

        var t0 = DateTime.Now;
        long nbytes = 0;
        int completed = done.Count;
        var sem = new SemaphoreSlim(workers);
        var lockObj = new object();

        await Task.WhenAll(todo.Select(async key =>
        {
            await sem.WaitAsync();
            try
            {
                var url = ObjUrl.Replace("{key}", key);
                byte[] sub;
                int row0 = 0, col0 = 0, rows = 0, cols = 0;
                try { sub = ReadTile(url, cellDeg, gridW, gridH, out row0, out col0, out rows, out cols); }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is IOException || ex is InvalidDataException)
                {
                    Log.Warn($"[跳过] {key}: {ex.Message}");
                    lock (lockObj) { done.Add(key); SaveDone(statePath, done); }
                    return;
                }
                if (sub == null) { lock (lockObj) { done.Add(key); SaveDone(statePath, done); } return; }
                lock (lockObj)
                {
                    int r1 = Math.Max(0, row0), c1 = Math.Max(0, col0);
                    int r2 = Math.Min(rows, gridH - row0), c2 = Math.Min(cols, gridW - col0);
                    for (int r = r1; r < r2; r++)
                        for (int c = c1; c < c2; c++)
                            full[(row0 + r) * gridW + (col0 + c)] = sub[r * cols + c];
                    nbytes += sub.Length;
                    done.Add(key);
                    completed++;
                    if (completed % 100 == 0 || completed == todo.Count)
                    {
                        var el = (DateTime.Now - t0).TotalSeconds;
                        var rate = completed / el;
                        var eta = (todo.Count - completed) / Math.Max(rate, 1e-6) / 60;
                        Log.Info($"[装配] {completed}/{todo.Count} 累计 {nbytes / 1048576.0:F1} MB 速率{rate:F2}块/秒 预计剩余{eta:F1}分钟");
                        SaveDone(statePath, done);
                    }
                }
            }
            finally { sem.Release(); }
        }));

        SaveDone(statePath, done);
        Log.Info($"[装配] 完成 {done.Count}/{keys.Count} 块，用时 {(DateTime.Now - t0).ToString(@"hh\:mm\:ss")}");

        // 写 GeoTIFF（uint8，nodata=0），按 256 瓦片行主序写出
        var t1 = DateTime.Now;
        int ntx = (gridW + 255) / 256, nty = (gridH + 255) / 256;
        using (var w = new GeoTiffWriter(outPath, gridW, gridH, -180, -90, 180, 90, signed: false, noData: 0))
        {
            for (int ty = 0; ty < nty; ty++)
            {
                int th = Math.Min(256, gridH - ty * 256);
                for (int tx = 0; tx < ntx; tx++)
                {
                    int tw = Math.Min(256, gridW - tx * 256);
                    var tile = new byte[tw * th];
                    for (int r = 0; r < th; r++)
                        for (int c = 0; c < tw; c++)
                            tile[r * tw + c] = full[(ty * 256 + r) * gridW + (tx * 256 + c)];
                    w.WriteTile(tx, ty, tile);
                }
            }
        }
        Log.Info($"[创建] 目标栅格 {gridW}x{gridH}（{cellDeg}°/像素）→ {outPath} 用时 {(DateTime.Now - t1).ToString(@"hh\:mm\:ss")}");
    }

    /// <summary>读一块远程 COG 瓦片，最近邻抽稀到该瓦片 bbox 在全局网格中的子窗口。</summary>
    private static byte[] ReadTile(string url, double cellDeg, int gridW, int gridH,
        out int row0, out int col0, out int rows, out int cols)
    {
        row0 = col0 = rows = cols = 0;
        using var rf = new Downloader.HttpRangeFile(url);
        using var cog = CogReader.Open(new HttpRandomReader(rf));
        int ov = cog.PickOverview((int)Math.Round(3.0 / cellDeg)); // 单块 3° -> 目标列数
        var oi = cog.Ifds[ov];
        int ovW = oi.Width, ovH = oi.Height;
        double resX = Math.Abs(oi.ResX), resY = Math.Abs(oi.ResY);
        double bLeft = oi.Left, bTop = oi.Top;
        col0 = (int)Math.Round((bLeft + 180.0) / cellDeg);
        row0 = (int)Math.Round((90.0 - bTop) / cellDeg);
        cols = (int)Math.Round(ovW * resX / cellDeg);
        rows = (int)Math.Round(ovH * resY / cellDeg);
        var ovBand = cog.ReadBandByte(ov);
        var sub = new byte[rows * cols];
        for (int r = 0; r < rows; r++)
        {
            int gRow = row0 + r;
            if (gRow < 0 || gRow >= gridH) continue;
            double lat = 90.0 - (gRow + 0.5) * cellDeg;
            int ovRow = Math.Clamp((int)Math.Round((bTop - lat) / resY), 0, ovH - 1);
            for (int c = 0; c < cols; c++)
            {
                int gCol = col0 + c;
                if (gCol < 0 || gCol >= gridW) continue;
                double lon = -180.0 + (gCol + 0.5) * cellDeg;
                int ovCol = Math.Clamp((int)Math.Round((lon - bLeft) / resX), 0, ovW - 1);
                sub[r * cols + c] = ovBand[ovRow * ovW + ovCol];
            }
        }
        return sub;
    }

    private static List<string> ListTiles()
    {
        var keys = new List<string>();
        string? token = null;
        var reKey = new Regex(@"<Key>([^<]+)</Key>");
        var reTok = new Regex(@"<NextContinuationToken>([^<]+)</NextContinuationToken>");
        do
        {
            var u = ListUrl.Replace("{prefix}", Prefix);
            if (token != null) u += "&continuation-token=" + Uri.EscapeDataString(token);
            var bytes = Downloader.GetBytesAsync(u).GetAwaiter().GetResult();
            var xml = System.Text.Encoding.UTF8.GetString(bytes);
            foreach (Match m in reKey.Matches(xml))
                if (m.Groups[1].Value.EndsWith("_Map.tif")) keys.Add(m.Groups[1].Value);
            var tm = reTok.Match(xml);
            token = tm.Success ? tm.Groups[1].Value : null;
        } while (token != null);
        return keys;
    }

    private static HashSet<string> LoadDone(string path)
    {
        if (!File.Exists(path)) return new HashSet<string>();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var arr = node?["done"] as JsonArray;
            if (arr == null) return new HashSet<string>();
            var set = new HashSet<string>();
            foreach (var v in arr) if (v != null) set.Add(v.GetValue<string>());
            return set;
        }
        catch { return new HashSet<string>(); }
    }

    private static void SaveDone(string path, HashSet<string> done)
    {
        var obj = new JsonObject { ["done"] = new JsonArray(done.OrderBy(x => x).Select(x => (JsonNode)x).ToArray()) };
        File.WriteAllText(path, obj.ToJsonString());
    }

    private static void StepValidate(string root, string outPath, double cellDeg, int gridW, int gridH)
    {
        using var d = GeoBand.Open(outPath);
        var counts = new long[256];
        int dh = d.Height;
        for (int j0 = 0; j0 < dh; j0 += 512)
        {
            int j1 = Math.Min(j0 + 512, dh);
            var band = d.ReadWindowUInt8(0, j0, d.Width, j1 - j0);
            foreach (var v in band) counts[v]++;
            if ((j0 / 512) % 6 == 0) Log.Info($"[校验] 行 {j1}/{dh}");
        }
        long covered = 0;
        for (int c = 1; c < 256; c++) covered += counts[c];
        Log.Info($"[校验] 分类像素 {covered}，无数据 {counts[0]}");
        var classes = new JsonObject();
        foreach (var kv in ClassNames)
        {
            long px = counts[kv.Key];
            classes[kv.Key.ToString()] = new JsonObject
            {
                ["name"] = kv.Value,
                ["pixels"] = px,
                ["pct_of_classified"] = covered == 0 ? 0 : Math.Round(100.0 * px / covered, 2),
            };
            Log.Info($"   {kv.Key,2} {kv.Value,-24} {px,12} px  {classes[kv.Key.ToString()!]!["pct_of_classified"]}%");
        }
        var meta = new JsonObject();
        var mp = Path.Combine(root, "metadata.json");
        if (File.Exists(mp))
        {
            try { meta = JsonNode.Parse(File.ReadAllText(mp)) as JsonObject ?? new JsonObject(); }
            catch { meta = new JsonObject(); }
        }
        meta["global_landcover"] = new JsonObject
        {
            ["file"] = Path.GetFullPath(outPath),
            ["crs"] = "EPSG:4326",
            ["bounds"] = new JsonArray(-180, -90, 180, 90),
            ["cell_deg"] = cellDeg,
            ["approx_resolution_km"] = Math.Round(111.32 * cellDeg, 2),
            ["width"] = gridW,
            ["height"] = gridH,
            ["nodata"] = 0,
            ["classes"] = classes,
            ["source"] = new JsonObject
            {
                ["name"] = "ESA WorldCover 10m 2021 v200",
                ["prefix"] = Prefix,
                ["license"] = "CC BY 4.0 (open, attribution required)",
                ["note"] = "概览抽稀装配，非原生 10m；原生全量 115.5GB 未下载",
            },
        };
        File.WriteAllText(mp, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"[校验] 元数据写入 → {mp}");
    }

    /// <summary>把 Downloader.HttpRangeFile 适配为 CogReader 需要的随机读接口。</summary>
    private sealed class HttpRandomReader : IRandomReader
    {
        private readonly Downloader.HttpRangeFile _rf;
        public HttpRandomReader(Downloader.HttpRangeFile rf) => _rf = rf;
        public long Length => _rf.Length;
        public byte[] Read(long offset, int length) => _rf.Read(offset, length);
        public void Dispose() => _rf.Dispose();
    }
}
