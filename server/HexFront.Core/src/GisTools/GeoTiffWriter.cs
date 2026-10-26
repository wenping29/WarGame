using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HexFront.Core.GisTools;

/// <summary>
/// 极简 GeoTIFF 写出器（无 GDAL 依赖）：单波段、256×256 瓦片、DEFLATE(压缩8) + predictor 2、
/// 写入 ModelTransformationTag(34264) 与 GDAL_NODATA(42113)。供 fetch-dem / fetch-landcover 生成
/// 数据源，随后由 <see cref="GeoBand"/> 读回（extract-terrain / rasters 复用）。
/// 仅支持本工具链实际需要的子集（int16 / uint8）。
/// </summary>
public sealed class GeoTiffWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly BinaryWriter _w;
    private readonly int _tile = 256;
    private readonly int _width, _height;
    private readonly int _bits;
    private readonly bool _signed;
    private readonly double _west, _south, _east, _north;
    private readonly double _noData;
    private readonly List<long> _tileOffsets = new();
    private readonly List<long> _tileBytes = new();
    private readonly List<byte[]> _tiles = new();

    public GeoTiffWriter(string path, int width, int height, double west, double south, double east, double north,
        bool signed = true, double noData = -32768)
    {
        _width = width; _height = height;
        _signed = signed; _bits = signed ? 16 : 8;
        _west = west; _south = south; _east = east; _north = north;
        _noData = noData;
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        _w = new BinaryWriter(_fs);
    }

    /// <summary>写入一个瓦片（传入的 buf 为整张图像的一维样本数组，长度 = width*height）。</summary>
    public void WriteTile(int tx, int ty, Array fullImage)
    {
        int tw = Math.Min(_tile, _width - tx * _tile);
        int th = Math.Min(_tile, _height - ty * _tile);
        if (tw <= 0 || th <= 0) return;

        int ntx = (_width + _tile - 1) / _tile;
        var tile = EncodeTile(fullImage, tx, ty, tw, th);
        _tiles.Add(tile);
        _tileOffsets.Add(0); // 占位，写完 IFD 后回填
        _tileBytes.Add(tile.Length);
    }

    /// <summary>写入一个瓦片（直接传入该瓦片的解码样本，长度 = tw*th，避免持有整张大图）。</summary>
    public void WriteTile(int tx, int ty, short[] tile)
    {
        int tw = Math.Min(_tile, _width - tx * _tile);
        int th = Math.Min(_tile, _height - ty * _tile);
        if (tw <= 0 || th <= 0) return;
        var enc = EncodeTileRaw(tile, tw, th);
        _tiles.Add(enc);
        _tileOffsets.Add(0);
        _tileBytes.Add(enc.Length);
    }

    /// <summary>写入一个瓦片（uint8 波段，直接传入该瓦片的解码样本）。</summary>
    public void WriteTile(int tx, int ty, byte[] tile)
    {
        int tw = Math.Min(_tile, _width - tx * _tile);
        int th = Math.Min(_tile, _height - ty * _tile);
        if (tw <= 0 || th <= 0) return;
        var enc = EncodeTileRaw(tile, tw, th);
        _tiles.Add(enc);
        _tileOffsets.Add(0);
        _tileBytes.Add(enc.Length);
    }

    private byte[] EncodeTile(Array src, int tx, int ty, int tw, int th)
    {
        int n = tw * th;
        byte[] raw = _bits == 16 ? new byte[n * 2] : new byte[n];
        for (int r = 0; r < th; r++)
        {
            int sr = ty * _tile + r;
            for (int c = 0; c < tw; c++)
            {
                int sc = tx * _tile + c;
                int dst = r * tw + c;
                if (_bits == 16)
                {
                    short v = (src is short[] s) ? s[sr * _width + sc] : (short)(src as ushort[])![sr * _width + sc];
                    raw[dst * 2] = (byte)v;
                    raw[dst * 2 + 1] = (byte)(v >> 8);
                }
                else
                {
                    byte v = (src is byte[] b) ? b[sr * _width + sc] : (byte)(src as sbyte[])![sr * _width + sc];
                    raw[dst] = v;
                }
            }
        }
        // predictor 2：每行水平差分（与 GeoBand 解码互逆）
        if (_bits == 16)
        {
            for (int r = 0; r < th; r++)
            {
                int baseIdx = r * tw;
                for (int c = 1; c < tw; c++)
                {
                    int prev = (short)(raw[(baseIdx + c - 1) * 2] | (raw[(baseIdx + c - 1) * 2 + 1] << 8));
                    int cur = (short)(raw[(baseIdx + c) * 2] | (raw[(baseIdx + c) * 2 + 1] << 8));
                    int v = cur - prev;
                    if (v > short.MaxValue) v -= 65536; else if (v < short.MinValue) v += 65536;
                    raw[(baseIdx + c) * 2] = (byte)v;
                    raw[(baseIdx + c) * 2 + 1] = (byte)(v >> 8);
                }
            }
        }
        else
        {
            for (int r = 0; r < th; r++)
            {
                int baseIdx = r * tw;
                for (int c = 1; c < tw; c++)
                    raw[baseIdx + c] = (byte)((raw[baseIdx + c] - raw[baseIdx + c - 1]) & 0xFF);
            }
        }
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            ds.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private byte[] EncodeTileRaw(short[] src, int tw, int th)
    {
        int n = tw * th;
        var raw = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short v = src[i];
            raw[i * 2] = (byte)v;
            raw[i * 2 + 1] = (byte)(v >> 8);
        }
        for (int r = 0; r < th; r++)
        {
            int baseIdx = r * tw;
            for (int c = 1; c < tw; c++)
            {
                int prev = (short)(raw[(baseIdx + c - 1) * 2] | (raw[(baseIdx + c - 1) * 2 + 1] << 8));
                int cur = (short)(raw[(baseIdx + c) * 2] | (raw[(baseIdx + c) * 2 + 1] << 8));
                int v = cur - prev;
                if (v > short.MaxValue) v -= 65536; else if (v < short.MinValue) v += 65536;
                raw[(baseIdx + c) * 2] = (byte)v;
                raw[(baseIdx + c) * 2 + 1] = (byte)(v >> 8);
            }
        }
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            ds.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private byte[] EncodeTileRaw(byte[] src, int tw, int th)
    {
        int n = tw * th;
        var raw = new byte[n];
        Array.Copy(src, raw, n);
        for (int r = 0; r < th; r++)
        {
            int baseIdx = r * tw;
            for (int c = 1; c < tw; c++)
                raw[baseIdx + c] = (byte)((raw[baseIdx + c] - raw[baseIdx + c - 1]) & 0xFF);
        }
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            ds.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    public void Dispose()
    {
        double resLon = (_east - _west) / _width;
        double resLat = (_north - _south) / _height;
        // ModelTransformationTag(34264) 按 GeoTIFF 规范为 4x4 行主序：
        //   | X |   | a b 0 d |   | I |
        //   | Y | = | e f 0 h |   | J |
        //   | Z |   | 0 0 0 0 |   | K |
        //   | 1 |   | 0 0 0 1 |   | 1 |
        // 规范明确 "d 与 h 常用于表示 X/Y 平移"，即平移量位于索引 3 与 7（与 GDAL 一致）。
        // 早先误把原点写在索引 0/4，会导致 GDAL/QGIS 把本文件读成 originX=0、像元宽=经度值。
        double[] tr =
        {
            resLon, 0.0,    0.0, _west,   // 行 0：X = resLon*I + west
            0.0, -resLat,   0.0, _north,  // 行 1：Y = -resLat*J + north
            0.0, 0.0,       0.0, 0.0,     // 行 2
            0.0, 0.0,       0.0, 1.0,     // 行 3
        };

        var nodataStr = Encoding.ASCII.GetBytes(_noData.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");

        int ntx = (_width + _tile - 1) / _tile;
        int nty = (_height + _tile - 1) / _tile;
        int tileCount = ntx * nty;

        int ifdPos = 8;
        int ifdEntries = 13;
        long tagDataPos = ifdPos + 2 + 12 * ifdEntries + 4;
        long toOffset = tagDataPos;
        long tbOffset = toOffset + (long)tileCount * 4;
        long trOffset = Align4(tbOffset + (long)tileCount * 4);
        long ndOffset = Align4(trOffset + 128);
        long tileStart = Align4(ndOffset + nodataStr.Length);

        long cur = tileStart;
        for (int i = 0; i < _tiles.Count; i++)
        {
            _tileOffsets[i] = cur;
            cur += _tiles[i].Length;
            if (cur % 2 != 0) cur++;
        }

        _fs.Seek(0, SeekOrigin.Begin);
        _w.Write((ushort)0x4949);
        _w.Write((ushort)42);
        _w.Write((uint)8);

        _fs.Seek(ifdPos, SeekOrigin.Begin);
        _w.Write((ushort)ifdEntries);
        WriteEntry(256, 4, 1, (uint)_width);
        WriteEntry(257, 4, 1, (uint)_height);
        WriteEntry(258, 3, 1, (ushort)_bits);
        WriteEntry(259, 3, 1, (ushort)8);            // DEFLATE
        WriteEntry(262, 3, 1, (ushort)1);            // BlackIsZero
        WriteEntry(324, 4, (uint)tileCount, (uint)toOffset);   // TileOffsets
        WriteEntry(322, 4, 1, (uint)_tile);          // TileWidth
        WriteEntry(323, 4, 1, (uint)_tile);          // TileLength
        WriteEntry(325, 4, (uint)tileCount, (uint)tbOffset);   // TileByteCounts
        WriteEntry(339, 3, 1, (ushort)(_signed ? 2 : 1));    // SampleFormat
        WriteEntry(317, 3, 1, (ushort)2);            // Predictor 2
        WriteEntry(34264, 12, 16, (uint)trOffset);   // ModelTransformationTag
        WriteEntry(42113, 2, (uint)nodataStr.Length, (uint)ndOffset); // GDAL_NODATA
        _w.Write((uint)0); // next IFD = 0

        _fs.Seek(toOffset, SeekOrigin.Begin);
        foreach (var o in _tileOffsets) _w.Write((uint)o);
        _fs.Seek(tbOffset, SeekOrigin.Begin);
        foreach (var b in _tileBytes) _w.Write((uint)b);
        _fs.Seek(trOffset, SeekOrigin.Begin);
        foreach (var d in tr) _w.Write(d);
        _fs.Seek(ndOffset, SeekOrigin.Begin);
        _w.Write(nodataStr);

        for (int i = 0; i < _tiles.Count; i++)
        {
            _fs.Seek(_tileOffsets[i], SeekOrigin.Begin);
            _w.Write(_tiles[i]);
        }

        _w.Flush();
        _w.Dispose();
        _fs.Dispose();
    }

    private static long Align4(long v) => (v % 4 == 0) ? v : v + (4 - v % 4);

    private void WriteEntry(int tag, int type, uint count, uint valueOrOffset)
    {
        _w.Write((ushort)tag);
        _w.Write((ushort)type);
        _w.Write(count);
        _w.Write(valueOrOffset);
    }
}
