using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexFront.Core.GisTools;
using HexFront.Tools.Common;

namespace HexFront.Tools.Commands;

/// <summary>
/// tiff2json 子命令：GeoTIFF -> JSON 栅格（无 GDAL）。
///
/// 用途：把 DEM / 土地覆盖等 GeoTIFF 转成带地理参考的 JSON 二维数组，
/// 便于后续步骤生成「流量 / 通行能力」网格（例如用 --normalize 直接产出 0..1 的通行容量代理）。
///
/// 读取走 GeoBand（与 extract-terrain 同源），按 --scale N 做 N×N 分块均值下采样；
/// 处理时按瓦片高度对齐成条带读取，每个瓦片只解码一次，避免整张大图占内存。
/// </summary>
public static class Tiff2JsonCommand
{
    private const long MaxCells = 60_000_000; // 输出单元格数硬上限（除非 --force）

    public static int Run(string[] argv)
    {
        var a = new Args(argv);
        if (a.Has("h") || a.Has("help"))
        {
            Console.WriteLine("tiff2json — GeoTIFF -> JSON 栅格（无 GDAL）");
            Console.WriteLine("  --in PATH          输入 GeoTIFF（单波段 int16 / uint8）");
            Console.WriteLine("  --out PATH         输出 JSON 路径（缺省输出到 stdout）");
            Console.WriteLine("  --scale N          下采样比例，每个输出像元聚合 N×N 输入像元（默认 1=全分辨率）");
            Console.WriteLine("  --null-nodata      把全为 nodata 的输出像元写成 JSON null");
            Console.WriteLine("  --normalize        额外输出 normalized 字段（0..1 线性归一化），可作通行容量代理");
            Console.WriteLine("  --min F --max F    归一化区间（缺省用观测 min/max）");
            Console.WriteLine("  --crs STR          强制写 crs（如 EPSG:4326），缺省按坐标量级推断");
            Console.WriteLine("  --compact          紧凑输出（不缩进）");
            Console.WriteLine("  --force            超过单元格上限仍强行导出");
            return 0;
        }

        var inPath = a.Get("in");
        if (inPath == null)
        {
            Log.Error("--in 未指定输入 TIFF");
            return 1;
        }
        if (!File.Exists(inPath))
        {
            Log.Error($"文件不存在: {inPath}");
            return 1;
        }

        var outPath = a.Get("out");
        var scale = Math.Max(1, a.Int("scale", 1));
        var nullNoData = a.Bool("null-nodata");
        var normalize = a.Bool("normalize");
        var compact = a.Bool("compact");
        var force = a.Bool("force");
        double minV = 0, maxV = 1;
        var haveMin = a.Has("min") && double.TryParse(a.Get("min"), NumberStyles.Float, CultureInfo.InvariantCulture, out minV);
        var haveMax = a.Has("max") && double.TryParse(a.Get("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out maxV);
        var crsOverride = a.Get("crs");

        using var band = GeoBand.Open(inPath);
        if (band.BitsPerSample is not (8 or 16))
        {
            Log.Error($"仅支持 BitsPerSample ∈ {{8,16}}，当前为 {band.BitsPerSample}");
            return 1;
        }
        var isInt = band.BitsPerSample == 16;

        int W = band.Width, H = band.Height;
        int outW = (W + scale - 1) / scale;
        int outH = (H + scale - 1) / scale;
        long total = (long)outW * outH;
        if (total > MaxCells && !force)
        {
            Log.Error($"输出网格 {outW}x{outH} = {total} 单元格，超过上限 {MaxCells}；用更大 --scale 或 --force 继续");
            return 1;
        }

        double aT = band.Transform[0], cT = band.Transform[1], fT = band.Transform[2];
        double bT = band.Transform[3], dT = band.Transform[4], eT = band.Transform[5];
        band.PixelToGeo(-0.5, -0.5, out double west, out double north);
        band.PixelToGeo(W - 0.5, H - 0.5, out double east, out double south);

        // 聚合：逐条带读取（按瓦片高度对齐，每瓦片仅解码一次）
        var sum = new double[total];
        var count = new int[total];
        int stripH = band.Tiled ? band.TileLength : H;
        double noData = band.HasNoData ? band.NoData : double.NaN;

        for (int yIn = 0; yIn < H; yIn += stripH)
        {
            int hIn = Math.Min(stripH, H - yIn);
            if (isInt)
            {
                var strip = band.ReadWindowInt16(0, yIn, W, hIn);
                for (int rIn = 0; rIn < hIn; rIn++)
                {
                    int rOut = (yIn + rIn) / scale;
                    int baseIn = rIn * W;
                    int baseOut = rOut * outW;
                    for (int cIn = 0; cIn < W; cIn++)
                    {
                        short s = strip[baseIn + cIn];
                        if (band.HasNoData && s == (short)noData) continue;
                        int cOut = cIn / scale;
                        int idx = baseOut + cOut;
                        sum[idx] += s;
                        count[idx]++;
                    }
                }
            }
            else
            {
                var strip = band.ReadWindowUInt8(0, yIn, W, hIn);
                for (int rIn = 0; rIn < hIn; rIn++)
                {
                    int rOut = (yIn + rIn) / scale;
                    int baseIn = rIn * W;
                    int baseOut = rOut * outW;
                    for (int cIn = 0; cIn < W; cIn++)
                    {
                        byte bb = strip[baseIn + cIn];
                        if (band.HasNoData && bb == (byte)noData) continue;
                        int cOut = cIn / scale;
                        int idx = baseOut + cOut;
                        sum[idx] += bb;
                        count[idx]++;
                    }
                }
            }
        }

        // 构建二维 values（row-major），统计有效范围
        var values = new JsonArray();
        double obsMin = double.PositiveInfinity, obsMax = double.NegativeInfinity;
        double sumAll = 0, validCells = 0, nodataCells = 0;
        for (int r = 0; r < outH; r++)
        {
            var row = new JsonArray();
            for (int c = 0; c < outW; c++)
            {
                int idx = r * outW + c;
                if (count[idx] == 0)
                {
                    if (nullNoData) row.Add((JsonNode?)null);
                    else row.Add(isInt ? (JsonNode)(-32768) : (JsonNode)0);
                    nodataCells++;
                    continue;
                }
                double v = sum[idx] / count[idx];
                row.Add(Math.Round(v, 4));
                if (v < obsMin) obsMin = v;
                if (v > obsMax) obsMax = v;
                sumAll += v;
                validCells++;
            }
            values.Add(row);
        }

        double normMin = haveMin ? minV : (validCells > 0 ? obsMin : 0);
        double normMax = haveMax ? maxV : (validCells > 0 ? obsMax : 1);
        double span = normMax - normMin;
        JsonArray? normalized = null;
        if (normalize)
        {
            normalized = new JsonArray();
            for (int r = 0; r < outH; r++)
            {
                var nrow = new JsonArray();
                for (int c = 0; c < outW; c++)
                {
                    int idx = r * outW + c;
                    if (count[idx] == 0)
                    {
                        nrow.Add((JsonNode?)null);
                        continue;
                    }
                    double v = sum[idx] / count[idx];
                    double t = span > 0 ? (v - normMin) / span : 0.0;
                    nrow.Add(Math.Round(t, 4));
                }
                normalized.Add(nrow);
            }
        }

        string crs = crsOverride ?? InferCrs(west, south, east, north);
        double resX = Math.Abs(cT) * scale;
        double resY = Math.Abs(eT) * scale;

        var doc = new JsonObject
        {
            ["format"] = "hexfront-tiff-json/1",
            ["source"] = inPath,
            ["crs"] = crs,
            ["band"] = new JsonObject
            {
                ["bitsPerSample"] = band.BitsPerSample,
                ["sampleFormat"] = band.SampleFormat,
                ["dtype"] = isInt ? "int16" : "uint8",
                ["noData"] = band.HasNoData ? JsonValue.Create(band.NoData) : null,
            },
            ["width"] = outW,
            ["height"] = outH,
            ["scale"] = scale,
            ["bbox"] = new JsonObject
            {
                ["west"] = Math.Round(west, 6),
                ["south"] = Math.Round(south, 6),
                ["east"] = Math.Round(east, 6),
                ["north"] = Math.Round(north, 6),
            },
            ["pixelSize"] = new JsonObject
            {
                ["x"] = Math.Round(resX, 9),
                ["y"] = Math.Round(resY, 9),
            },
            ["stats"] = new JsonObject
            {
                ["min"] = double.IsFinite(obsMin) ? Math.Round(obsMin, 4) : (JsonNode?)null,
                ["max"] = double.IsFinite(obsMax) ? Math.Round(obsMax, 4) : (JsonNode?)null,
                ["mean"] = validCells > 0 ? Math.Round(sumAll / validCells, 4) : (JsonNode?)null,
                ["validCells"] = (long)validCells,
                ["nodataCells"] = (long)nodataCells,
            },
            ["values"] = values,
        };
        if (normalized != null) doc["normalized"] = normalized;

        var opts = new JsonSerializerOptions { WriteIndented = !compact };
        string json = doc.ToJsonString(opts);

        if (outPath != null)
        {
            File.WriteAllText(outPath, json);
            Log.Info($"已写出 JSON -> {outPath} ({outW}x{outH}, crs={crs}, {(normalize ? "含 normalized" : "仅 values")})");
        }
        else
        {
            Console.WriteLine(json);
        }
        return 0;
    }

    private static string InferCrs(double w, double s, double e, double n) =>
        (Math.Abs(w) > 180 || Math.Abs(e) > 180 || Math.Abs(s) > 90 || Math.Abs(n) > 90)
            ? "EPSG:3857"
            : "EPSG:4326";
}
