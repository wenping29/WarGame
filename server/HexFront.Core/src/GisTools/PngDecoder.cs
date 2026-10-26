using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace HexFront.Core.GisTools;

/// <summary>
/// 极简 PNG 解码器（无外部依赖）：仅支持 8-bit、无隔行、颜色类型 2(RGB)/6(RGBA)，
/// 处理 5 种行滤波器。供 fetch-dem 把 terrarium PNG 高程瓦片还原为 RGB 字节
/// （ elevation = R*256 + G + B/256 − 32768 ）。
/// </summary>
public sealed class PngDecoder
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Channels { get; private set; } // 3 或 4
    public byte[] Data { get; private set; } = Array.Empty<byte>(); // 行优先 RGB(A)

    public static PngDecoder Decode(byte[] png)
    {
        var d = new PngDecoder();
        d.Parse(png);
        return d;
    }

    private void Parse(byte[] png)
    {
        if (png[0] != 0x89 || png[1] != 0x50) throw new InvalidDataException("不是 PNG");
        int pos = 8;
        byte bitDepth = 0, colorType = 0;
        MemoryStream? idat = null;
        while (pos < png.Length)
        {
            int len = ReadInt(png, pos); pos += 4;
            string type = Encoding.ASCII.GetString(png, pos, 4);
            int dataStart = pos + 4;
            if (type == "IHDR")
            {
                Width = ReadInt(png, dataStart);
                Height = ReadInt(png, dataStart + 4);
                bitDepth = png[dataStart + 8];
                colorType = png[dataStart + 9];
                Channels = colorType switch { 2 => 3, 6 => 4, _ => throw new NotSupportedException($"颜色类型 {colorType} 不支持") };
                if (bitDepth != 8) throw new NotSupportedException("仅支持 8-bit");
            }
            else if (type == "IDAT")
            {
                idat ??= new MemoryStream();
                idat.Write(png, dataStart, len);
            }
            else if (type == "IEND") break;
            pos = dataStart + len + 4; // 跳过 CRC
        }

        idat!.Seek(0, SeekOrigin.Begin);
        using var zs = new DeflateStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        zs.CopyTo(raw);
        var scan = raw.ToArray(); // 每行 = 1 过滤字节 + Width*Channels 字节
        int stride = Width * Channels;
        Data = new byte[Height * stride];
        var prev = new byte[stride];
        int sp = 0;
        for (int y = 0; y < Height; y++)
        {
            int filter = scan[sp++];
            var cur = new byte[stride];
            for (int x = 0; x < stride; x++)
            {
                byte rawByte = scan[sp++];
                byte a = x >= Channels ? cur[x - Channels] : (byte)0;
                byte b = prev[x];
                byte c = x >= Channels ? prev[x - Channels] : (byte)0;
                cur[x] = filter switch
                {
                    0 => rawByte,
                    1 => (byte)(rawByte + a),
                    2 => (byte)(rawByte + b),
                    3 => (byte)(rawByte + (byte)((a + b) / 2)),
                    4 => (byte)(rawByte + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"未知滤波器 {filter}")
                };
            }
            Buffer.BlockCopy(cur, 0, Data, y * stride, stride);
            prev = cur;
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
    }

    private static int ReadInt(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
