using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace HexFront.Core.GisTools;

/// <summary>
/// 托管 COG（Cloud-Optimized GeoTIFF）概览读取器（无 GDAL 依赖），供 fetch-landcover
/// 通过 HTTP Range 读取 ESA WorldCover 的远程 3°x3° COG 内部概览并抽稀。
///
/// 能力（仅覆盖本工具链需要的最小子集）：
///   · 枚举主 IFD 与其 SubIFD 概览（按 GDAL COG 的链表结构遍历）。
///   · 解析单波段、BitsPerSample∈{8,16}、Compression∈{1,8}、Predictor∈{1,2}、瓦片布局。
///   · 地理参考：优先 ModelTransformationTag(34264)，否则 tiepoint+scale(33922/33550)。
///   · 把指定概览整带解码为 byte[]（uint8）/ short[]（int16），调用方再做最近邻抽稀。
/// 随机读通过 <see cref="IRandomReader"/> 抽象，使其既可读本地文件也能读远程 RangeFile。
/// </summary>
public sealed class CogReader : IDisposable
{
    private readonly IRandomReader _r;
    private readonly bool _le;
    private readonly List<Ifd> _ifds = new();

    private CogReader(IRandomReader r, bool le)
    {
        _r = r; _le = le;
    }

    public static CogReader Open(IRandomReader r)
    {
        var head = r.Read(0, 8);
        if (head.Length < 8) throw new InvalidDataException("文件过短，不是 TIFF");
        bool le = head[0] == 0x49 && head[1] == 0x49; // "II"
        if (!le && !(head[0] == 0x4D && head[1] == 0x4D)) throw new InvalidDataException("不是 TIFF 文件");
        var magic = ReadU16(head, 2, le);
        if (magic is not (42 or 43)) throw new InvalidDataException($"不支持的 TIFF 版本：{magic}");
        long firstIfd = magic == 43 ? (long)ReadU64(head, 4, le) : ReadU32(head, 4, le);

        var reader = new CogReader(r, le);
        var queue = new Queue<long>();
        queue.Enqueue(firstIfd);
        var visited = new HashSet<long>();
        while (queue.Count > 0)
        {
            var off = queue.Dequeue();
            if (off == 0 || visited.Contains(off)) continue;
            visited.Add(off);
            var ifd = reader.ParseIfd(off);
            reader._ifds.Add(ifd);
            foreach (var sub in ifd.SubIfds) queue.Enqueue(sub);
        }
        if (reader._ifds.Count == 0) throw new InvalidDataException("未解析到任何 IFD");
        return reader;
    }

    /// <summary>IFD 数量：索引 0 为主影像，1..N 为概览（通常按 decimation 递增、即尺寸递减）。</summary>
    public int IfdCount => _ifds.Count;

    public Ifd MainIfd => _ifds[0];

    /// <summary>全部 IFD（含主影像与概览），供调用方读取概览的地理范围/分辨率。</summary>
    public IReadOnlyList<Ifd> Ifds => _ifds;

    /// <summary>概览（排除主影像）中，选宽度≥targetWidth 且最接近 target 的一个；无则取最大概览。</summary>
    public int PickOverview(int targetWidth)
    {
        int best = 0;
        for (int i = 1; i < _ifds.Count; i++)
        {
            if (_ifds[i].Width >= targetWidth)
            {
                // 取第一个满足条件的（列表按尺寸递减，即最高分辨率概览）
                if (best == 0 || _ifds[i].Width < _ifds[best].Width) best = i;
            }
        }
        if (best == 0)
        {
            // 退而求其次：取尺寸最大的概览
            best = 0;
            for (int i = 1; i < _ifds.Count; i++)
                if (_ifds[i].Width > _ifds[best].Width) best = i;
        }
        return best;
    }

    /// <summary>把指定 IFD 的单波段解码为 byte[]（uint8，行优先）。</summary>
    public byte[] ReadBandByte(int ifdIndex)
    {
        var ifd = _ifds[ifdIndex];
        if (ifd.Bits != 8) throw new InvalidOperationException($"IFD{ifdIndex} 不是 uint8 波段");
        return (byte[])DecodeBand(ifd);
    }

    public short[] ReadBandInt16(int ifdIndex)
    {
        var ifd = _ifds[ifdIndex];
        if (ifd.Bits != 16) throw new InvalidOperationException($"IFD{ifdIndex} 不是 int16 波段");
        return (short[])DecodeBand(ifd);
    }

    // ---------------- IFD 解析 ----------------

    private Ifd ParseIfd(long ifdOffset)
    {
        var head = ReadAt(ifdOffset, 2);
        int n = ReadU16(head, 0, _le);
        var entries = ReadAt(ifdOffset + 2, n * 12);
        var tags = new Dictionary<int, (int type, long count, long valueOffset)>();
        for (int i = 0; i < n; i++)
        {
            int o = i * 12;
            int tag = ReadU16(entries, o, _le);
            int type = ReadU16(entries, o + 2, _le);
            long count = ReadU32(entries, o + 4, _le);
            long valueOffset = ReadU32(entries, o + 8, _le);
            tags[tag] = (type, count, valueOffset);
        }

        int width = (int)ReadScalar(tags, 256);
        int height = (int)ReadScalar(tags, 257);
        int bits = tags.TryGetValue(258, out var b) ? (int)ReadScalar(tags, 258) : 8;
        int compression = tags.TryGetValue(259, out _) ? (int)ReadScalar(tags, 259) : 1;
        int predictor = tags.TryGetValue(317, out _) ? (int)ReadScalar(tags, 317) : 1;
        bool tiled = tags.ContainsKey(322) && tags.ContainsKey(323);
        int tileW = tiled ? (int)ReadScalar(tags, 322) : 0;
        int tileH = tiled ? (int)ReadScalar(tags, 323) : 0;
        var offsets = tiled ? ReadOffsets(tags, 324) : ReadOffsets(tags, 273);
        var byteCounts = tiled ? ReadOffsets(tags, 325) : ReadOffsets(tags, 279);
        var sub = tags.TryGetValue(330, out var s) ? ReadOffsets(tags, 330) : Array.Empty<long>();

        double left = 0, top = 0, resX = 1, resY = -1;
        if (tags.TryGetValue(34264, out var tr) && tr.count >= 16)
        {
            var m = ReadDoubles(tr.valueOffset, 16);
            left = m[0]; top = m[4]; resX = m[1]; resY = m[5];
        }
        else if (tags.TryGetValue(33922, out var tp) && tags.TryGetValue(33550, out var ps))
        {
            var t = ReadDoubles(tp.valueOffset, (int)tp.count);
            var scl = ReadDoubles(ps.valueOffset, (int)ps.count);
            left = t[3]; top = t[4]; resX = scl[0]; resY = -scl[1];
        }

        return new Ifd(ifdOffset, width, height, bits, compression, predictor, tiled, tileW, tileH,
            offsets, byteCounts, sub, left, top, resX, resY);
    }

    private long ReadScalar(Dictionary<int, (int type, long count, long valueOffset)> tags, int tag)
    {
        var t = tags[tag];
        if (t.count <= 4)
        {
            // 内联值：按类型宽度从 valueOffset 起始读取
            var buf = ReadAt(t.valueOffset, 8);
            return t.type switch
            {
                1 or 2 => buf[0],
                3 or 8 => ReadU16(buf, 0, _le),
                4 or 9 or 11 => ReadU32(buf, 0, _le),
                5 or 10 or 12 => (long)ReadF64(buf, 0, _le),
                _ => ReadU32(buf, 0, _le)
            };
        }
        return (long)ReadU32(ReadAt(t.valueOffset, 8), 0, _le); // 外部单值
    }

    private long[] ReadOffsets(Dictionary<int, (int type, long count, long valueOffset)> tags, int tag)
    {
        if (!tags.TryGetValue(tag, out var t)) return Array.Empty<long>();
        if (t.count <= 0) return Array.Empty<long>();
        int tsize = t.type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            _ => 4
        };
        var buf = ReadAt(t.valueOffset, checked((int)(t.count * tsize)));
        var outp = new long[t.count];
        for (long i = 0; i < t.count; i++)
        {
            outp[i] = t.type switch
            {
                3 or 8 => ReadU16(buf, (int)(i * tsize), _le),
                _ => ReadU32(buf, (int)(i * tsize), _le)
            };
        }
        return outp;
    }

    private double[] ReadDoubles(long offset, int count)
    {
        var buf = ReadAt(offset, count * 8);
        var outp = new double[count];
        for (int i = 0; i < count; i++) outp[i] = ReadF64(buf, i * 8, _le);
        return outp;
    }

    // ---------------- 波段解码 ----------------

    private Array DecodeBand(Ifd ifd)
    {
        if (!ifd.Tiled) throw new NotSupportedException("仅支持瓦片布局的 COG");
        Array band = ifd.Bits == 16 ? new short[ifd.Width * ifd.Height] : new byte[ifd.Width * ifd.Height];
        int ntx = (ifd.Width + ifd.TileW - 1) / ifd.TileW;
        for (int ty = 0; ty < (ifd.Height + ifd.TileH - 1) / ifd.TileH; ty++)
        {
            for (int tx = 0; tx < ntx; tx++)
            {
                int idx = ty * ntx + tx;
                if (idx >= ifd.Offsets.Length) continue;
                long off = ifd.Offsets[idx];
                long bc = idx < ifd.ByteCounts.Length ? ifd.ByteCounts[idx] : 0;
                if (off <= 0 || bc <= 0) continue;
                var raw = _r.Read(off, (int)bc);
                var decoded = Inflate(raw, ifd.Bits, ifd.Predictor, ifd.TileW);
                int sr0 = ty * ifd.TileH, sc0 = tx * ifd.TileW;
                int tw = Math.Min(ifd.TileW, ifd.Width - sc0);
                int th = Math.Min(ifd.TileH, ifd.Height - sr0);
                if (ifd.Bits == 16)
                {
                    var src = (short[])decoded;
                    var dst = (short[])band;
                    for (int r = 0; r < th; r++)
                        for (int c = 0; c < tw; c++)
                            dst[(sr0 + r) * ifd.Width + (sc0 + c)] = src[r * tw + c];
                }
                else
                {
                    var src = (byte[])decoded;
                    var dst = (byte[])band;
                    for (int r = 0; r < th; r++)
                        for (int c = 0; c < tw; c++)
                            dst[(sr0 + r) * ifd.Width + (sc0 + c)] = src[r * tw + c];
                }
            }
        }
        return band;
    }

    private Array Inflate(byte[] raw, int bits, int predictor, int stride)
    {
        byte[] inflated;
        if (raw.Length > 1 && raw[0] == 0x78 && (raw[1] == 0x9C || raw[1] == 0xDA || raw[1] == 0x01))
        {
            using var ms = new MemoryStream(raw);
            using var zs = new ZLibStream(ms, CompressionMode.Decompress);
            inflated = ReadAll(zs);
        }
        else if (bits == 16)
        {
            using var ms = new MemoryStream(raw);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            inflated = ReadAll(ds);
        }
        else
        {
            using var ms = new MemoryStream(raw);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            inflated = ReadAll(ds);
        }

        int n = inflated.Length / (bits == 16 ? 2 : 1);
        if (predictor == 2)
        {
            if (bits == 16)
            {
                var arr = new short[n];
                Buffer.BlockCopy(inflated, 0, arr, 0, Math.Min(n * 2, inflated.Length));
                ApplyDiffInt16(arr, stride);
                return arr;
            }
            else
            {
                var arr = new byte[n];
                Buffer.BlockCopy(inflated, 0, arr, 0, Math.Min(n, inflated.Length));
                ApplyDiffByte(arr, stride);
                return arr;
            }
        }
        if (bits == 16)
        {
            var arr = new short[n];
            Buffer.BlockCopy(inflated, 0, arr, 0, Math.Min(n * 2, inflated.Length));
            return arr;
        }
        var b = new byte[n];
        Buffer.BlockCopy(inflated, 0, b, 0, Math.Min(n, inflated.Length));
        return b;
    }

    private static void ApplyDiffInt16(short[] arr, int w)
    {
        for (int r = 0; r < arr.Length / w; r++)
        {
            int baseIdx = r * w;
            for (int c = 1; c < w; c++)
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

    private static void ApplyDiffByte(byte[] arr, int w)
    {
        for (int r = 0; r < arr.Length / w; r++)
        {
            int baseIdx = r * w;
            for (int c = 1; c < w; c++)
                arr[baseIdx + c] = (byte)((arr[baseIdx + c - 1] + arr[baseIdx + c]) & 0xFF);
        }
    }

    private byte[] ReadAt(long offset, int length)
    {
        if (length <= 0) return Array.Empty<byte>();
        length = (int)Math.Min(length, _r.Length - offset);
        if (length <= 0) return Array.Empty<byte>();
        return _r.Read(offset, length);
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => _r.Dispose();

    // ---------------- TIFF 基础类型读取 ----------------

    private static ushort ReadU16(byte[] b, int o, bool le) =>
        le ? (ushort)(b[o] | (b[o + 1] << 8)) : (ushort)((b[o] << 8) | b[o + 1]);
    private static uint ReadU32(byte[] b, int o, bool le) =>
        le ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
           : (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
    private static ulong ReadU64(byte[] b, int o, bool le)
    {
        ulong v = 0;
        if (le)
        {
            for (int i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
        }
        else
        {
            for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        }
        return v;
    }
    private static double ReadF64(byte[] b, int o, bool le)
    {
        if (!le) { var c = new byte[8]; for (int i = 0; i < 8; i++) c[i] = b[o + 7 - i]; return BitConverter.ToDouble(c, 0); }
        return BitConverter.ToDouble(b, o);
    }

    /// <summary>单个影像目录（主影像或概览）。</summary>
    public sealed record Ifd(
        long Offset, int Width, int Height, int Bits, int Compression, int Predictor,
        bool Tiled, int TileW, int TileH, long[] Offsets, long[] ByteCounts, long[] SubIfds,
        double Left, double Top, double ResX, double ResY);
}

/// <summary>随机读字节源抽象（本地文件或远程 HTTP Range）。</summary>
public interface IRandomReader : IDisposable
{
    long Length { get; }
    byte[] Read(long offset, int length);
}

/// <summary>本地文件随机读（基于 FileStream）。</summary>
public sealed class FileRandomReader : IRandomReader
{
    private readonly FileStream _fs;
    public FileRandomReader(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        Length = _fs.Length;
    }
    public long Length { get; }
    public byte[] Read(long offset, int length)
    {
        if (offset < 0 || offset >= Length) return Array.Empty<byte>();
        length = (int)Math.Min(length, Length - offset);
        var buf = new byte[length];
        _fs.Seek(offset, SeekOrigin.Begin);
        _fs.ReadExactly(buf, 0, length);
        return buf;
    }
    public void Dispose() => _fs.Dispose();
}
