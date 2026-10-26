using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HexFront.Core.GisTools;
using Xunit;

namespace HexFront.Tests.Gis;

/// <summary>
/// GeoBand（GeoTIFF 读取）回归测试。
///
/// 锁住两个曾经真实存在、且会影响 extract-terrain / rasters / fetch-* 全部 GIS 链路的解析缺陷：
///   1. TIFF 标量标签（count*类型宽度 ≤ 4）按规范是**内联**在 4 字节字段里的，
///      旧实现一律把它当文件偏移去 Seek，导致宽高/位深/压缩等全部读错。
///   2. ModelTransformationTag(34264) 的平移量在索引 3(X) / 7(Y)（GeoTIFF 2.6 规范明文
///      "d 与 h 常用于表示 X/Y 平移"，与 GDAL 一致），旧实现取的是 m[0]/m[4]，丢掉了原点。
///
/// 覆盖两条地理参考分支：ModelTransformation（自家 GeoTiffWriter 产出）与
/// ModelTiepoint + ModelPixelScale（GDAL/rasterio 产出的真实 DEM、土地覆盖均走这条）。
/// </summary>
public class GeoTiffReaderTests : IDisposable
{
    private readonly List<string> _tmpFiles = new();

    public void Dispose()
    {
        foreach (var p in _tmpFiles)
        {
            try { File.Delete(p); } catch { /* 临时文件清理失败不影响断言 */ }
        }
    }

    private string TempPath(string name)
    {
        var p = Path.Combine(Path.GetTempPath(), $"hft_{Guid.NewGuid():N}_{name}");
        _tmpFiles.Add(p);
        return p;
    }

    // ------------------------------------------------------------------
    // 1) ModelTransformation 分支：走自家 GeoTiffWriter 写、GeoBand 读的往返
    // ------------------------------------------------------------------

    [Fact]
    public void Read_ModelTransformationFile_OriginComesFromMatrixTranslation()
    {
        const int w = 8, h = 6;
        double west = 116.0, south = 39.94, east = 116.08, north = 40.0;

        // 值：data[r][c] = 100 + r*10 + c*3，(0,0) 与 (5,7) 设为 nodata
        var img = new short[w * h];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                img[r * w + c] = (short)(100 + r * 10 + c * 3);
        img[0 * w + 0] = -32768;
        img[5 * w + 7] = -32768;

        var path = TempPath("matrix.tif");
        using (var tw = new GeoTiffWriter(path, w, h, west, south, east, north))
            tw.WriteTile(0, 0, img);

        using var band = GeoBand.Open(path);

        // 标量标签都是内联存放的：读错这里会全盘皆错
        Assert.Equal(w, band.Width);
        Assert.Equal(h, band.Height);
        Assert.Equal(16, band.BitsPerSample);
        Assert.Equal(2, band.SampleFormat);
        Assert.Equal(8, band.Compression);
        Assert.True(band.HasNoData);
        Assert.Equal(-32768, band.NoData);

        // 仿射原点必须来自矩阵的 d/h（索引 3/7），而不是索引 0/4
        band.PixelToGeo(0, 0, out double x0, out double y0);
        Assert.Equal(west, x0, 6);
        Assert.Equal(north, y0, 6);

        band.PixelToGeo(w, h, out double x1, out double y1);
        Assert.Equal(east, x1, 6);
        Assert.Equal(south, y1, 6);

        // 逐窗回读，含 predictor=2 的 int16 还原
        var win = band.ReadWindowInt16(0, 0, w, h);
        Assert.Equal(img.Length, win.Length);
        for (int i = 0; i < img.Length; i++) Assert.Equal(img[i], win[i]);

        // GeoToColRow 与 PixelToGeo 互逆
        band.PixelToGeo(3.0, 2.0, out double gx, out double gy);
        var (col, row) = band.GeoToColRow(gx, gy);
        Assert.Equal(3.0, col, 6);
        Assert.Equal(2.0, row, 6);
    }

    // ------------------------------------------------------------------
    // 2) ModelTiepoint + ModelPixelScale 分支（真实 GDAL 产物的写法）
    // ------------------------------------------------------------------

    [Fact]
    public void Read_TiepointFile_ScalarsInline_AndOriginFromTiepoint()
    {
        const int w = 4, h = 4;
        double west = -180.0, north = 90.0, res = 0.5;

        var data = new byte[w * h];
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                data[r * w + c] = (byte)((r * 4 + c) % 11);

        // nodata 用 2 字节 ASCII "0\0" —— 长度 ≤ 4，按规范内联存放
        var path = TempPath("tiepoint.tif");
        File.WriteAllBytes(path, BuildTiff(
            w, h, bits: 8, sampleFormat: 1,
            ZlibCompress(HorizontalDiffByte(data, w)),
            tiled: false,
            tiepoint: new double[] { 0, 0, 0, west, north, 0 },
            pixelScale: new double[] { res, res, 0 },
            nodataAscii: "0"));

        using var band = GeoBand.Open(path);

        Assert.Equal(w, band.Width);
        Assert.Equal(h, band.Height);
        Assert.Equal(8, band.BitsPerSample);
        Assert.Equal(1, band.SampleFormat);
        Assert.True(band.HasNoData, "内联的 GDAL_NODATA 应被解析出来");
        Assert.Equal(0, band.NoData);

        band.PixelToGeo(0, 0, out double x0, out double y0);
        Assert.Equal(west, x0, 6);
        Assert.Equal(north, y0, 6);

        band.PixelToGeo(w, h, out double x1, out double y1);
        Assert.Equal(west + w * res, x1, 6);
        Assert.Equal(north - h * res, y1, 6);

        var win = band.ReadWindowUInt8(0, 0, w, h);
        Assert.Equal(data, win);
    }

    // ------------------------------------------------------------------
    // 最小 TIFF 构造：只写测试需要的标签，用于覆盖 GeoTiffWriter 不写的分支
    // ------------------------------------------------------------------

    private static byte[] BuildTiff(int w, int h, int bits, int sampleFormat, byte[] strip,
        bool tiled, double[]? tiepoint, double[]? pixelScale, string? nodataAscii)
    {
        var tags = new List<(int Tag, int Type, int Count, byte[] Payload)>
        {
            (256, 4, 1, U32((uint)w)),
            (257, 4, 1, U32((uint)h)),
            (258, 3, 1, U16((ushort)bits)),
            (259, 3, 1, U16(8)),   // Compression = DEFLATE
            (262, 3, 1, U16(1)),   // Photometric = BlackIsZero
            (317, 3, 1, U16(2)),   // Predictor = 2
            (339, 3, 1, U16((ushort)sampleFormat)),
        };
        if (tiled)
        {
            tags.Add((322, 4, 1, U32((uint)w)));   // TileWidth
            tags.Add((323, 4, 1, U32((uint)h)));   // TileLength
            tags.Add((324, 4, 1, U32(0)));         // TileOffsets（稍后回填）
            tags.Add((325, 4, 1, U32((uint)strip.Length)));
        }
        else
        {
            tags.Add((273, 4, 1, U32(0)));         // StripOffsets（稍后回填）
            tags.Add((277, 3, 1, U16(1)));         // SamplesPerPixel
            tags.Add((278, 4, 1, U32((uint)h)));   // RowsPerStrip
            tags.Add((279, 4, 1, U32((uint)strip.Length)));
        }
        if (tiepoint != null) tags.Add((33922, 12, tiepoint.Length, Doubles(tiepoint)));
        if (pixelScale != null) tags.Add((33550, 12, pixelScale.Length, Doubles(pixelScale)));
        if (nodataAscii != null)
        {
            var b = Encoding.ASCII.GetBytes(nodataAscii + "\0");
            tags.Add((42113, 2, b.Length, b));
        }
        tags = tags.OrderBy(t => t.Tag).ToList();

        const int ifdPos = 8;
        long blobStart = ifdPos + 2 + 12L * tags.Count + 4;
        var blobOffset = new Dictionary<int, long>();
        long cur = blobStart;
        foreach (var t in tags)
        {
            if (t.Payload.Length <= 4) continue;
            blobOffset[t.Tag] = cur;
            cur += t.Payload.Length;
            if (cur % 2 != 0) cur++;
        }
        long stripOffset = cur;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, true);
        bw.Write((ushort)0x4949);   // "II" little-endian
        bw.Write((ushort)42);
        bw.Write((uint)ifdPos);

        ms.Seek(ifdPos, SeekOrigin.Begin);
        bw.Write((ushort)tags.Count);
        foreach (var t in tags)
        {
            uint valueField = t.Payload.Length <= 4
                ? InlineValue(t.Payload)
                : (uint)blobOffset[t.Tag];
            // StripOffsets / TileOffsets 的真实偏移在布局算好后才确定
            if (t.Tag is 273 or 324) valueField = (uint)stripOffset;

            bw.Write((ushort)t.Tag);
            bw.Write((ushort)t.Type);
            bw.Write((uint)t.Count);
            bw.Write(valueField);
        }
        bw.Write((uint)0);   // next IFD

        foreach (var t in tags)
        {
            if (t.Payload.Length <= 4) continue;
            ms.Seek(blobOffset[t.Tag], SeekOrigin.Begin);
            bw.Write(t.Payload);
        }
        ms.Seek(stripOffset, SeekOrigin.Begin);
        bw.Write(strip);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>把 ≤4 字节的值按 TIFF 内联规则塞进 4 字节字段（左对齐，小端）。</summary>
    private static uint InlineValue(byte[] payload)
    {
        var buf = new byte[4];
        Array.Copy(payload, buf, payload.Length);
        return BitConverter.ToUInt32(buf, 0);
    }

    private static byte[] U16(ushort v) => BitConverter.GetBytes(v);
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);

    private static byte[] Doubles(double[] v)
    {
        var b = new byte[v.Length * 8];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    private static byte[] HorizontalDiffByte(byte[] data, int w)
    {
        var raw = new byte[data.Length];
        for (int r = 0; r < data.Length / w; r++)
        {
            byte prev = 0;
            for (int c = 0; c < w; c++)
            {
                byte v = data[r * w + c];
                raw[r * w + c] = (byte)(v - prev);
                prev = v;
            }
        }
        return raw;
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }
}
