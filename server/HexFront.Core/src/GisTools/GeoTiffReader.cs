using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace HexFront.Core.GisTools;

/// <summary>
/// 托管 GeoTIFF 读取器（无 GDAL 依赖），与 tools/gis/extract_terrain.py 的 GeoBand 同一套约定：
/// 解析地理参考标签（ModelTransformationTag 34264 / ModelTiepointTag 33922 + ModelPixelScaleTag 33550）、
/// 按窗口（或瓦片）读取单波段像素，对「瓦片 + DEFLATE(压缩 8) + predictor 2」做手写解码。
///
/// 仅支持本工具链实际用到的子集：单波段、BitsPerSample ∈ {8,16}、SampleFormat ∈ {1,2}、
/// Compression ∈ {1(无), 8(DEFLATE)}、Predictor ∈ {1,2}、瓦片或条带布局。
/// 地理参考：优先 ModelTransformationTag，否则 tiepoint+scale。
/// </summary>
public sealed class GeoBand : IDisposable
{
    private readonly FileStream _fs;
    private readonly BinaryReader _br;
    private readonly bool _le; // little endian

    public int Width { get; }
    public int Height { get; }
    public int BitsPerSample { get; }
    public int SampleFormat { get; }   // 1=uint, 2=int, 3=float
    public bool HasNoData { get; }
    public double NoData { get; }
    public int Compression { get; }
    public int Predictor { get; }
    public bool Tiled { get; }
    public int TileWidth { get; }
    public int TileLength { get; }
    public int RowsPerStrip { get; }

    /// <summary>地理仿射 [a, c, f, b, d, e]：
    /// X = a + col*c + row*f ；Y = b + col*d + row*e（北向上，e 为负）。</summary>
    public double[] Transform { get; }

    private readonly long[] _offsets;
    private readonly long[] _byteCounts;

    private GeoBand(FileStream fs, BinaryReader br, bool le, int width, int height,
        int bits, int sampleFormat, bool hasNoData, double noData, int compression, int predictor,
        bool tiled, int tileWidth, int tileLength, int rowsPerStrip,
        double[] transform, long[] offsets, long[] byteCounts)
    {
        _fs = fs; _br = br; _le = le;
        Width = width; Height = height; BitsPerSample = bits; SampleFormat = sampleFormat;
        HasNoData = hasNoData; NoData = noData; Compression = compression; Predictor = predictor;
        Tiled = tiled; TileWidth = tileWidth; TileLength = tileLength; RowsPerStrip = rowsPerStrip;
        Transform = transform; _offsets = offsets; _byteCounts = byteCounts;
    }

    public static GeoBand Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        try
        {
            var br = new BinaryReader(fs);
            var bom = br.ReadUInt16();
            bool le = bom switch
            {
                0x4949 => true,   // "II"
                0x4D4D => false,  // "MM"
                _ => throw new InvalidDataException($"不是 TIFF 文件：{path}")
            };
            var magic = ReadUInt16(br, le);
            if (magic is not (42 or 43))
                throw new InvalidDataException($"不支持的 TIFF 版本：{magic}");
            // 仅读第一个 IFD（单波段），足以覆盖本工具链。
            long ifdOffset = magic == 43 ? (long)ReadUInt64(br, le) : ReadUInt32(br, le);

            var tags = new Dictionary<int, (int type, long count, long valueOffset)>();
            fs.Seek(ifdOffset, SeekOrigin.Begin);
            int n = ReadUInt16(br, le);
            for (var i = 0; i < n; i++)
            {
                var tag = ReadUInt16(br, le);
                var type = ReadUInt16(br, le);
                var count = ReadUInt32(br, le);
                var vo = fs.Position;
                var valueOffset = ReadUInt32(br, le); // 4 字节，可能是值或偏移
                tags[tag] = (type, count, valueOffset);
                fs.Seek(vo + 4, SeekOrigin.Begin);
            }

            int width = (int)ReadTagScalar(tags, 256, br, le);
            int height = (int)ReadTagScalar(tags, 257, br, le);
            int bits = tags.TryGetValue(258, out var b) ? (int)ReadArrayScalar(tags, 258, br, le, 0) : 8;
            int sampleFormat = tags.TryGetValue(339, out _) ? (int)ReadArrayScalar(tags, 339, br, le, 0) : 1;
            int compression = tags.TryGetValue(259, out _) ? (int)ReadTagScalar(tags, 259, br, le) : 1;
            int predictor = tags.TryGetValue(317, out _) ? (int)ReadTagScalar(tags, 317, br, le) : 1;

            bool tiled = tags.ContainsKey(322) && tags.ContainsKey(323);
            int tileWidth = tiled ? (int)ReadTagScalar(tags, 322, br, le) : 0;
            int tileLength = tiled ? (int)ReadTagScalar(tags, 323, br, le) : 0;
            int rowsPerStrip = tags.ContainsKey(278) ? (int)ReadTagScalar(tags, 278, br, le) : height;

            (long[] offs, long[] counts) = tiled
                ? (ReadTagOffsets(tags, 324, br, le), ReadTagOffsets(tags, 325, br, le))
                : (ReadTagOffsets(tags, 273, br, le), ReadTagOffsets(tags, 279, br, le));

            bool hasNoData = false; double noData = 0;
            if (tags.TryGetValue(42113, out var nd)) // GDAL_NODATA
            {
                var bytes = ReadValueBytes(nd, br, le);
                var s = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
                if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v))
                { noData = v; hasNoData = true; }
            }

            double[] transform = ParseGeoTransform(tags, br, le, width, height);

            return new GeoBand(fs, br, le, width, height, bits, sampleFormat, hasNoData, noData,
                compression, predictor, tiled, tileWidth, tileLength, rowsPerStrip, transform, offs, counts);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    // ---- 地理参考解析 ----
    private static double[] ParseGeoTransform(Dictionary<int, (int type, long count, long valueOffset)> tags,
        BinaryReader br, bool le, int width, int height)
    {
        // 默认单位仿射（避免无 GIS 标签时炸）
        var def = new double[] { 0, 1, 0, 0, 0, -1 };
        if (tags.TryGetValue(34264, out var tr) && tr.count >= 16)
        {
            var m = ReadDoubles(br, tr.valueOffset, 16, le);
            // GeoTIFF 4x4 模型变换（行主序）：
            //   X = m0*col + m1*row + m3 ; Y = m4*col + m5*row + m7
            // 这里归一成 [constX, colX, rowX, constY, colY, rowY]。
            return new[] { m[3], m[0], m[1], m[7], m[4], m[5] };
        }
        if (tags.TryGetValue(33922, out var tp) && tags.TryGetValue(33550, out var ps))
        {
            var t = ReadDoubles(br, tp.valueOffset, (int)tp.count, le);
            var s = ReadDoubles(br, ps.valueOffset, (int)ps.count, le);
            double i = t[0], j = t[1], X0 = t[3], Y0 = t[4], sx = s[0], sy = s[1];
            // X = X0 - i*sx + col*sx ; Y = Y0 + j*sy - row*sy
            return new[] { X0 - i * sx, sx, 0.0, Y0 + j * sy, 0.0, -sy };
        }
        return def;
    }

    // ---- 像素地理换算（与 Python geo_to_colrow 同一套逆变换） ----
    public void PixelToGeo(double col, double row, out double x, out double y)
    {
        x = Transform[0] + col * Transform[1] + row * Transform[2];
        y = Transform[3] + col * Transform[4] + row * Transform[5];
    }

    public (double col, double row) GeoToColRow(double x, double y)
    {
        // 解 [c f; d e]·[col;row] = [x-a; y-b]
        double c = Transform[1], f = Transform[2], d = Transform[4], e = Transform[5];
        double det = c * e - f * d;
        if (Math.Abs(det) < 1e-15) throw new InvalidOperationException("地理仿射不可逆");
        double col = (e * (x - Transform[0]) - f * (y - Transform[3])) / det;
        double row = (-d * (x - Transform[0]) + c * (y - Transform[3])) / det;
        return (col, row);
    }

    // ---- 解码单块（瓦片或条带）为原生类型一维数组，长度 = blockH*blockW ----
    private Array DecodeBlock(int index, int blockW, int blockH)
    {
        _fs.Seek(_offsets[index], SeekOrigin.Begin);
        var raw = new byte[_byteCounts[index]];
        _fs.ReadExactly(raw, 0, raw.Length);

        byte[] inflated;
        if (Compression == 1)
        {
            inflated = raw;
        }
        else
        {
            // 压缩 8 = DEFLATE：多数实现为原始 DEFLATE(RFC1951)，但部分管线用 zlib(RFC1950) 包裹。
            // 以首字节 0x78 判别 zlib，否则按原始 DEFLATE 处理。
            if (raw.Length > 1 && raw[0] == 0x78 && (raw[1] == 0x9C || raw[1] == 0xDA || raw[1] == 0x01))
            {
                using var ms = new MemoryStream(raw);
                using var zs = new ZLibStream(ms, CompressionMode.Decompress);
                inflated = ReadAll(zs);
            }
            else
            {
                using var ms = new MemoryStream(raw);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                inflated = ReadAll(ds);
            }
        }

        var samples = blockW * blockH;
        if (BitsPerSample == 16)
        {
            var arr = new short[samples];
            Buffer.BlockCopy(inflated, 0, arr, 0, Math.Min(samples * 2, inflated.Length));
            if (Predictor == 2)
                ApplyHorizontalDiffInt16(arr, blockW);
            return arr;
        }
        else if (BitsPerSample == 8)
        {
            var arr = new byte[samples];
            Buffer.BlockCopy(inflated, 0, arr, 0, Math.Min(samples, inflated.Length));
            if (Predictor == 2)
                ApplyHorizontalDiffByte(arr, blockW);
            return arr;
        }
        else
            throw new NotSupportedException($"BitsPerSample={BitsPerSample} 暂不支持");
    }

    private static void ApplyHorizontalDiffInt16(short[] arr, int w)
    {
        for (var r = 0; r < arr.Length / w; r++)
        {
            var baseIdx = r * w;
            for (var c = 1; c < w; c++)
            {
                int prev = arr[baseIdx + c - 1];
                int cur = arr[baseIdx + c];
                int v = prev + cur;
                if (v > short.MaxValue) v -= 65536;
                else if (v < short.MinValue) v += 65536;
                arr[baseIdx + c] = (short)v;
            }
        }
    }

    private static void ApplyHorizontalDiffByte(byte[] arr, int w)
    {
        for (var r = 0; r < arr.Length / w; r++)
        {
            var baseIdx = r * w;
            for (var c = 1; c < w; c++)
                arr[baseIdx + c] = (byte)((arr[baseIdx + c - 1] + arr[baseIdx + c]) & 0xFF);
        }
    }

    // ---- 窗口读取：整数（DEM，int16） ----
    public short[] ReadWindowInt16(int col0, int row0, int cols, int rows)
    {
        if (BitsPerSample != 16 || SampleFormat != 2)
            throw new InvalidOperationException("ReadWindowInt16 要求 int16 波段");
        return (short[])ReadWindow(col0, row0, cols, rows);
    }

    // ---- 窗口读取：无符号字节（土地覆盖，uint8） ----
    public byte[] ReadWindowUInt8(int col0, int row0, int cols, int rows)
    {
        if (BitsPerSample != 8)
            throw new InvalidOperationException("ReadWindowUInt8 要求 uint8 波段");
        return (byte[])ReadWindow(col0, row0, cols, rows);
    }

    private Array ReadWindow(int col0, int row0, int cols, int rows)
    {
        col0 = Math.Max(0, Math.Min(Width, col0));
        row0 = Math.Max(0, Math.Min(Height, row0));
        cols = Math.Max(0, Math.Min(cols, Width - col0));
        rows = Math.Max(0, Math.Min(rows, Height - row0));
        if (cols <= 0 || rows <= 0)
            return BitsPerSample == 16 ? new short[0] : new byte[0];

        if (Tiled)
        {
            int ntx = (Width + TileWidth - 1) / TileWidth;
            int c0 = col0 / TileWidth, c1 = (col0 + cols - 1) / TileWidth;
            int r0 = row0 / TileLength, r1 = (row0 + rows - 1) / TileLength;
            Array outArr = BitsPerSample == 16 ? new short[rows * cols] : new byte[rows * cols];
            for (var ty = r0; ty <= r1; ty++)
            {
                for (var tx = c0; tx <= c1; tx++)
                {
                    int idx = ty * ntx + tx;
                    var block = DecodeBlock(idx, TileWidth, TileLength);
                    int sr0 = ty * TileLength, sc0 = tx * TileWidth;
                    int dr0 = Math.Max(sr0, row0) - row0;
                    int dr1 = Math.Min(sr0 + TileLength, row0 + rows) - row0;
                    int dc0 = Math.Max(sc0, col0) - col0;
                    int dc1 = Math.Min(sc0 + TileWidth, col0 + cols) - col0;
                    int bsr0 = Math.Max(sr0, row0), bsc0 = Math.Max(sc0, col0);
                    for (var dr = dr0; dr < dr1; dr++)
                        for (var dc = dc0; dc < dc1; dc++)
                        {
                            int src = (bsr0 + dr - sr0) * TileWidth + (bsc0 + dc - sc0);
                            int dst = dr * cols + dc;
                            if (BitsPerSample == 16) ((short[])outArr)[dst] = ((short[])block)[src];
                            else ((byte[])outArr)[dst] = ((byte[])block)[src];
                        }
                }
            }
            return outArr;
        }
        else
        {
            // 条带布局：读整张（数据源一般是瓦片；这里仅作兜底，适用于较小的测试文件）
            var full = DecodeBlock(0, Width, Height);
            Array outArr = BitsPerSample == 16 ? new short[rows * cols] : new byte[rows * cols];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                {
                    int src = (row0 + r) * Width + (col0 + c);
                    int dst = r * cols + c;
                    if (BitsPerSample == 16) ((short[])outArr)[dst] = ((short[])full)[src];
                    else ((byte[])outArr)[dst] = ((byte[])full)[src];
                }
            return outArr;
        }
    }

    // ==================== TIFF 基础读写 ====================
    private static ushort ReadUInt16(BinaryReader br, bool le) => le ? br.ReadUInt16() : Reverse(br.ReadUInt16());
    private static uint ReadUInt32(BinaryReader br, bool le) => le ? br.ReadUInt32() : Reverse(br.ReadUInt32());
    private static ulong ReadUInt64(BinaryReader br, bool le) => le ? br.ReadUInt64() : Reverse(br.ReadUInt64());
    private static ushort Reverse(ushort v) => (ushort)((v >> 8) | (v << 8));
    private static uint Reverse(uint v) => ((v >> 24) & 0xFF) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
    private static ulong Reverse(ulong v)
    {
        var b = BitConverter.GetBytes(v);
        Array.Reverse(b);
        return BitConverter.ToUInt64(b);
    }

    private static long ReadTagScalar(Dictionary<int, (int type, long count, long valueOffset)> tags, int tag, BinaryReader br, bool le)
        => ReadArrayScalar(tags, tag, br, le, 0);

    private static long ReadArrayScalar(Dictionary<int, (int type, long count, long valueOffset)> tags, int tag, BinaryReader br, bool le, int i)
    {
        var t = tags[tag];
        var buf = ReadValueBytes(t, br, le); // 恒为小端字节序
        int size = TypeSize(t.type);
        long off = (long)i * size;
        return t.type switch
        {
            1 or 2 or 6 => buf[off],
            3 or 8 => BitConverter.ToUInt16(buf, (int)off),
            4 or 9 or 11 => BitConverter.ToUInt32(buf, (int)off),
            5 or 10 or 12 => (long)BitConverter.ToDouble(buf, (int)off),
            _ => BitConverter.ToUInt32(buf, (int)off),
        };
    }

    /// <summary>TIFF 标准：当 count*typeSize ≤ 4 时，值内联在 4 字节字段里；否则 4 字节存放文件偏移。
    /// 返回的值字节恒为小端序（无论源文件端序）。</summary>
    private static byte[] ReadValueBytes((int type, long count, long valueOffset) t, BinaryReader br, bool le)
    {
        int size = TypeSize(t.type);
        long total = t.count * size;
        byte[] val = new byte[total > 0 ? total : 1];
        if (total <= 4)
        {
            // 内联：4 字节字段 raw 即原始文件字节（端序还原后取前 total 字节）
            uint v = (uint)t.valueOffset;
            byte[] raw = le
                ? new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }
                : new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            int n = (int)Math.Min(total, 4);
            Array.Copy(raw, val, n);
        }
        else
        {
            br.BaseStream.Seek(t.valueOffset, SeekOrigin.Begin);
            br.BaseStream.ReadExactly(val, 0, (int)total);
        }
        if (!le && total > 0) ReverseBuf(val, size);
        return val;
    }

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 1,
    };

    private static byte[] ReverseBuf(byte[] buf, int off, int size)
    {
        byte[] r = new byte[size];
        for (int k = 0; k < size; k++) r[k] = buf[off + size - 1 - k];
        return r;
    }

    private static void ReverseBuf(byte[] buf, int size)
    {
        for (int i = 0; i + size <= buf.Length; i += size)
        {
            for (int k = 0; k < size / 2; k++)
            {
                (buf[i + k], buf[i + size - 1 - k]) = (buf[i + size - 1 - k], buf[i + k]);
            }
        }
    }

    private static double ReverseD(double v)
    {
        var b = BitConverter.GetBytes(v); Array.Reverse(b); return BitConverter.ToDouble(b);
    }

    private static long[] ReadTagOffsets(Dictionary<int, (int type, long count, long valueOffset)> tags, int tag, BinaryReader br, bool le)
    {
        var t = tags[tag];
        if (t.count <= 0) return Array.Empty<long>();
        var buf = ReadValueBytes(t, br, le); // 恒为小端序
        int size = TypeSize(t.type);
        var result = new long[t.count];
        for (var i = 0; i < t.count; i++)
        {
            long off = (long)i * size;
            result[i] = t.type switch
            {
                3 or 8 => BitConverter.ToUInt16(buf, (int)off),
                _ => BitConverter.ToUInt32(buf, (int)off),
            };
        }
        return result;
    }

    private static double[] ReadDoubles(BinaryReader br, long offset, int count, bool le)
    {
        br.BaseStream.Seek(offset, SeekOrigin.Begin);
        var outp = new double[count];
        for (var i = 0; i < count; i++)
        {
            var v = br.ReadDouble();
            outp[i] = le ? v : ReverseD(v);
        }
        return outp;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _br?.Dispose();
        _fs?.Dispose();
    }
}
