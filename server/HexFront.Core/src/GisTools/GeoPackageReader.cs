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

    public GpkgLayer ReadLayer(string name)
    {
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
        using (var c = _conn.CreateCommand())
        {
            c.CommandText = $"PRAGMA table_info('{name}')";
            using var rdr = c.ExecuteReader();
            while (rdr.Read())
            {
                var col = rdr.GetString(1);
                if (col != geomCol) fields.Add(col);
            }
        }

        var features = new List<GpkgFeature>();
        using var cmd = _conn.CreateCommand();
        var cols = string.Join(",", fields.Select(f => $"\"{f}\""));
        cmd.CommandText = fields.Count > 0 ? $"SELECT \"{geomCol}\", {cols} FROM \"{name}\"" : $"SELECT \"{geomCol}\" FROM \"{name}\"";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var geom = ParseGeometry(r.GetFieldValue<byte[]>(0));
            if (geom == null) continue;
            var attrs = new Dictionary<string, object?>();
            for (int i = 0; i < fields.Count; i++)
                attrs[fields[i]] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
            features.Add(new GpkgFeature { Geometry = geom, Attributes = attrs });
        }

        return new GpkgLayer { Name = name, GeometryColumn = geomCol, SrsId = srs, Fields = fields, Features = features };
    }

    /// <summary>解析 GPKG 二进制（magic GPKG + flags + srs_id + 可选 envelope）+ 标准 WKB。</summary>
    private static NtsGeometry? ParseGeometry(byte[] blob)
    {
        if (blob == null || blob.Length < 8) return null;
        if (blob[0] != 0x47 || blob[1] != 0x50 || blob[2] != 0x4B || blob[3] != 0x47)
            return null; // 不是 GPKG 二进制
        byte flags = blob[4];
        int envCode = (flags >> 1) & 0x7;
        int envBytes = envCode switch { 0 => 0, 1 => 32, 2 => 48, 3 => 48, 4 => 64, _ => 0 };
        int wkbStart = 8 + envBytes;
        if (wkbStart >= blob.Length) return null;
        var wkb = new byte[blob.Length - wkbStart];
        Array.Copy(blob, wkbStart, wkb, 0, wkb.Length);
        try { return new WKBReader().Read(wkb); }
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
