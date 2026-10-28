using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.IO;

namespace HexFront.Core.GisTools;

/// <summary>
/// 托管 GeoPackage 读取器（无 GDAL 依赖）：通过 Microsoft.Data.Sqlite 读底层 SQLite，
/// 解析 GPKG 几何二进制头 + 标准 WKB，交给 NetTopologySuite 还原为 IGeometry。
/// 用于 rasters.water_by_hex / roads_by_hex / airports_by_hex 的真实矢量叠加。
/// </summary>
public sealed class GeoPackageReader : IDisposable
{
    private readonly SqliteConnection _conn;

    private GeoPackageReader(SqliteConnection conn) => _conn = conn;

    public static GeoPackageReader Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return new GeoPackageReader(conn);
    }

    /// <summary>要素图层名列表（gpkg_contents 中 data_type='features' 的表）。</summary>
    public List<string> Layers()
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM gpkg_contents WHERE data_type='features' ORDER BY table_name";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public GpkgLayer ReadLayer(string name) => ReadLayer(name, null);

    /// <summary>
    /// 读取图层；bbox 非空时走 GPKG 自带的 rtree_&lt;表&gt;_geom 虚表做粗筛。
    ///
    /// 必须支持粗筛：裁剪后的真实路网也有 3.3 万条线要素（全幅 16.5 万），
    /// 而一次地图叠层只关心剧本那 1°×1° 的范围（约 90 条）。全表载入会让每次请求
    /// 都付出一遍全部 WKB 解析 + 托管对象开销，内存直接跟着全球数据走。
    /// 没有 rtree 表（手工导出/旧版写出器）时退回全表，语义不变，只是慢。
    /// </summary>
    public GpkgLayer ReadLayer(string name, Envelope? bbox)
    {
        // 表名下面要拼进 SQL，只接受 gpkg_contents 里真实存在的要素表，杜绝注入与拼错表名
        if (!Layers().Contains(name, StringComparer.Ordinal))
            throw new ArgumentException($"GeoPackage 中没有要素表：{name}");

        // 几何列与 srs
        string geomCol = "geom";
        int srs = 4326;
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = $"SELECT column_name, srs_id FROM gpkg_geometry_columns WHERE table_name=@t";
            c.Parameters.AddWithValue("@t", name);
            using var rdr = c.ExecuteReader();
            if (rdr.Read()) { geomCol = rdr.GetString(0); srs = rdr.GetInt32(1); }
        }

        var fields = new List<string>();
        string pk = "id";
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = $"PRAGMA table_info('{name}')";
            using var rdr = c.ExecuteReader();
            while (rdr.Read())
            {
                var col = rdr.GetString(1);
                if (col != geomCol) fields.Add(col);
                if (rdr.GetInt32(5) == 1) pk = col; // GPKG 标准主键一般叫 id，但写出器不保证
            }
        }

        var useRtree = bbox != null && RtreeExists(name, geomCol);
        var features = new List<GpkgFeature>();
        using var cmd = _conn.CreateCommand();
        // 列名一律带表限定符：属性表里常有名为 id 的列，与 rtree 虚表的 id 撞名后 SQL 直接 ambiguous
        var selection = $"g.\"{geomCol}\"" + string.Concat(fields.Select(f => $",g.\"{f}\""));
        if (useRtree)
        {
            cmd.CommandText =
                $"SELECT {selection} FROM \"{name}\" AS g" +
                $" JOIN \"rtree_{name}_{geomCol}\" AS r ON g.\"{pk}\" = r.id" +
                " WHERE r.minx <= @e AND r.maxx >= @w AND r.miny <= @n AND r.maxy >= @s";
            cmd.Parameters.AddWithValue("@w", bbox!.MinX);
            cmd.Parameters.AddWithValue("@s", bbox.MinY);
            cmd.Parameters.AddWithValue("@e", bbox.MaxX);
            cmd.Parameters.AddWithValue("@n", bbox.MaxY);
        }
        else
        {
            cmd.CommandText = $"SELECT {selection} FROM \"{name}\" AS g";
        }

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var geom = ParseGeometry(r.GetFieldValue<byte[]>(0));
            if (geom == null) continue;
            if (bbox != null && !useRtree && !geom.EnvelopeInternal.Intersects(bbox)) continue;
            var attrs = new Dictionary<string, object?>();
            for (int i = 0; i < fields.Count; i++)
                attrs[fields[i]] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
            features.Add(new GpkgFeature { Geometry = geom, Attributes = attrs });
        }

        return new GpkgLayer { Name = name, GeometryColumn = geomCol, SrsId = srs, Fields = fields, Features = features };
    }

    private bool RtreeExists(string name, string geomCol)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        c.Parameters.AddWithValue("$n", $"rtree_{name}_{geomCol}");
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// 解析 GeoPackage 几何二进制头 + 标准 WKB。
    ///
    /// 头部有两种真实存在的写法：规范正式的 8 字节「'G','P' + 版本 + 标志 + srs_id(int32)」
    /// ——GDAL/QGIS 就写这种——以及早期草案的 10 字节「'G','P','K','G' + 版本 + 标志 + srs_id」。
    /// 只认后者的话，GDAL 生成的库会被整片当成"非 GPKG"静默丢光：本仓库的 gpkg 全部出自 GDAL，
    /// 所以 water/roads/airports 叠加一直是 0 要素而没有任何报错。
    ///
    /// envelope 指示符 5–7 各家实现长度不一致，不能只信表；所以算出候选起点后还要看 WKB
    /// 首字节（字节序 0/1 + 合法几何类型）是否对得上，对不上就退回逐字节扫描。
    /// </summary>
    private static readonly int[] EnvelopeBytes = { 0, 32, 48, 48, 64, 16, 24, 32 };

    private static NtsGeometry? ParseGeometry(byte[] blob)
    {
        if (blob == null || blob.Length < 12) return null;

        int headerLen;
        byte flags;
        if (blob[0] == 0x47 && blob[1] == 0x50 && blob[2] == 0x4B && blob[3] == 0x47)
        {
            headerLen = 10;   // 草案：GPKG + version + flags + srs_id
            flags = blob[5];
        }
        else if (blob[0] == 0x47 && blob[1] == 0x50)
        {
            headerLen = 8;    // 正式：'G' 'P' + version + flags + srs_id
            flags = blob[3];
        }
        else return null;

        int wkbStart = headerLen + EnvelopeBytes[(flags >> 1) & 0x7];
        var geom = ReadWkb(blob, wkbStart);
        if (geom != null) return geom;

        // 旧版写出器把 10 字节草案头截成了 8 字节，WKB 比 headerLen 更早，所以扫描下界取 8。
        for (int i = 8; i + 4 < blob.Length; i++)
        {
            geom = ReadWkb(blob, i);
            if (geom != null) return geom;
        }
        return null;
    }

    private static NtsGeometry? ReadWkb(byte[] blob, int start)
    {
        if (start < 0 || start + 5 > blob.Length) return null;
        byte order = blob[start];
        if (order != 0x00 && order != 0x01) return null;
        int type = order == 0x01
            ? blob[start + 1] | (blob[start + 2] << 8) | (blob[start + 3] << 16) | (blob[start + 4] << 24)
            : blob[start + 4] | (blob[start + 3] << 8) | (blob[start + 2] << 16) | (blob[start + 1] << 24);
        // WKB 基本类型 1–7（含 Z/M/ZM 的 1x/2x/3x 千位偏移，取模后归一到基本类型）
        if (type <= 0 || type % 1000 is < 1 or > 7) return null;
        try
        {
            var wkb = new byte[blob.Length - start];
            Array.Copy(blob, start, wkb, 0, wkb.Length);
            return new WKBReader().Read(wkb);
        }
        catch { return null; }
    }

    public void Dispose() => _conn.Dispose();
}

public sealed class GpkgFeature
{
    public NtsGeometry Geometry { get; init; } = null!;
    public Dictionary<string, object?> Attributes { get; init; } = new();
}

public sealed class GpkgLayer
{
    public string Name { get; init; } = "";
    public string GeometryColumn { get; init; } = "geom";
    public int SrsId { get; init; }
    public List<string> Fields { get; init; } = new();
    public List<GpkgFeature> Features { get; init; } = new();
}
