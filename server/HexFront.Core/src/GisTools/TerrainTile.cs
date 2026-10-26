using System;
using System.IO;

namespace HexFront.Core.GisTools;

/// <summary>
/// 读取 extract_terrain.py / ExtractTerrainCommand 生成的 .ter 地形瓦片（无 GDAL 依赖）。
/// 小端固定布局：80 字节头 + int16 高程数组（row0=north）+ 可选 uint8 土地覆盖数组。
/// 坐标换算与生成端一致：col = floor((lon - west) / resLon)，row = floor((north - lat) / resLat)。
/// 与 HexFront.Api.Services.TerrainTile 同格式，但位于 Core 以便生成引擎与新 Web API 共用。
/// </summary>
public sealed class TerrainTile
{
    public int Version { get; }
    public int Crs { get; }
    public double West { get; }
    public double South { get; }
    public double East { get; }
    public double North { get; }
    public int Cols { get; }
    public int Rows { get; }
    public double ResLon { get; }
    public double ResLat { get; }
    public bool HasLandcover { get; }
    public short ElevNoData { get; }
    public byte LcNoData { get; }

    private readonly short[] _elev;
    private readonly byte[]? _lc;

    public TerrainTile(int version, int crs, double west, double south, double east, double north,
        int cols, int rows, double resLon, double resLat, bool hasLandcover,
        short elevNoData, byte lcNoData, short[] elev, byte[]? lc)
    {
        Version = version;
        Crs = crs;
        West = west;
        South = south;
        East = east;
        North = north;
        Cols = cols;
        Rows = rows;
        ResLon = resLon;
        ResLat = resLat;
        HasLandcover = hasLandcover;
        ElevNoData = elevNoData;
        LcNoData = lcNoData;
        _elev = elev;
        _lc = lc;
    }

    public static TerrainTile Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Load(fs);
    }

    public static TerrainTile Load(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: false);
        var magic = r.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'T' || magic[1] != (byte)'E'
            || magic[2] != (byte)'R' || magic[3] != (byte)'1')
            throw new InvalidDataException($"不是有效的 .ter 文件（magic 不匹配）");

        int version = r.ReadInt32();
        int crs = r.ReadInt32();
        double west = r.ReadDouble();
        double south = r.ReadDouble();
        double east = r.ReadDouble();
        double north = r.ReadDouble();
        int cols = r.ReadInt32();
        int rows = r.ReadInt32();
        double resLon = r.ReadDouble();
        double resLat = r.ReadDouble();
        int hasLc = r.ReadInt32();
        short elevNoData = r.ReadInt16();
        byte lcNoData = r.ReadByte();
        r.ReadBytes(5); // padding 到 80 字节头

        int n = cols * rows;
        var buf = r.ReadBytes(n * 2);
        if (buf.Length < n * 2)
            throw new InvalidDataException(".ter 高程数据不完整");
        var elev = new short[n];
        Buffer.BlockCopy(buf, 0, elev, 0, n * 2);

        byte[]? lc = null;
        if (hasLc == 1)
        {
            lc = r.ReadBytes(n);
            if (lc.Length < n)
                throw new InvalidDataException(".ter 土地覆盖数据不完整");
        }

        return new TerrainTile(version, crs, west, south, east, north, cols, rows,
            resLon, resLat, hasLc == 1, elevNoData, lcNoData, elev, lc);
    }

    public bool Contains(double lat, double lon) =>
        lon >= West && lon <= East && lat >= South && lat <= North;

    /// <summary>经度 → 列号（闭区间，东边界点归属最后一列，需钳制浮点越界）。</summary>
    public int ColAt(double lon) =>
        Math.Clamp((int)Math.Floor((lon - West) / ResLon), 0, Cols - 1);

    /// <summary>纬度 → 行号（闭区间，北边界点归属第一行，需钳制）。</summary>
    public int RowAt(double lat) =>
        Math.Clamp((int)Math.Floor((North - lat) / ResLat), 0, Rows - 1);

    public short ElevationAt(double lat, double lon)
    {
        var col = ColAt(lon);
        var row = RowAt(lat);
        return _elev[col + row * Cols];
    }

    public byte LandcoverAt(double lat, double lon)
    {
        if (_lc == null) return LcNoData;
        var col = ColAt(lon);
        var row = RowAt(lat);
        return _lc[col + row * Cols];
    }

    /// <summary>按经纬度取样；坐标落在瓦片外时返回 false（调用方应降级合成 DEM）。</summary>
    public bool TrySample(double lat, double lon, out short elevation, out byte landcover)
    {
        if (!Contains(lat, lon))
        {
            elevation = ElevNoData;
            landcover = LcNoData;
            return false;
        }
        elevation = ElevationAt(lat, lon);
        landcover = LandcoverAt(lat, lon);
        return true;
    }
}
