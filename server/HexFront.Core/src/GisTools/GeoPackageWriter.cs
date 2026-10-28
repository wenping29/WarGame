using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.IO;

namespace HexFront.Core.GisTools;

/// <summary>
/// 托管 GeoPackage 写出器（无 GDAL 依赖）：建最小合规库（gpkg_spatial_ref_sys / gpkg_contents /
/// gpkg_geometry_columns + 要素表），几何以 GPKG 二进制（小端头 + 标准 WKB）写入。
/// 供 fetch-water 把 Natural Earth shapefile 转为统一 .gpkg。
/// </summary>
public sealed class GeoPackageWriter : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _layer;
    private readonly string _geomCol;
    private readonly List<string> _fields;
    private readonly List<string> _fieldTypes;
    private double _minX = double.MaxValue, _minY = double.MaxValue, _maxX = double.MinValue, _maxY = double.MinValue;

    private GeoPackageWriter(SqliteConnection conn, string layer, string geomCol, List<string> fields, List<string> fieldTypes)
    {
        _conn = conn; _layer = layer; _geomCol = geomCol; _fields = fields; _fieldTypes = fieldTypes;
    }

    public static GeoPackageWriter Create(string path, string layer, string geometryType,
        IEnumerable<(string Name, string SqlType)>? attributeFields = null)
    {
        if (File.Exists(path)) File.Delete(path);
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var t = conn.BeginTransaction();
        Exec(conn, "PRAGMA application_id=0x47504B47");
        Exec(conn, "CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT, srs_id INTEGER PRIMARY KEY, organization TEXT, organization_coordsys_id INTEGER, definition TEXT, description TEXT)");
        Exec(conn, "INSERT INTO gpkg_spatial_ref_sys VALUES ('Undefined',0,'none',0,'undefined','')");
        Exec(conn, "INSERT INTO gpkg_spatial_ref_sys VALUES ('WGS 84',4326,'EPSG',4326,'GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]','')");
        Exec(conn, "CREATE TABLE gpkg_contents (table_name TEXT PRIMARY KEY, data_type TEXT, identifier TEXT, description TEXT, last_change DATETIME DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER)");
        Exec(conn, "CREATE TABLE gpkg_geometry_columns (table_name TEXT PRIMARY KEY, column_name TEXT, geometry_type_name TEXT, srs_id INTEGER, z TINYINT, m TINYINT)");

        var fields = new List<string>();
        var types = new List<string>();
        var colDefs = new List<string> { "id INTEGER PRIMARY KEY AUTOINCREMENT" };
        if (attributeFields != null)
            foreach (var (name, type) in attributeFields)
            {
                fields.Add(name); types.Add(type);
                colDefs.Add($"\"{name}\" {type}");
            }
        colDefs.Add($"\"geom\" BLOB");
        Exec(conn, $"CREATE TABLE \"{layer}\" ({string.Join(", ", colDefs)})");
        Exec(conn, $"INSERT INTO gpkg_contents (table_name, data_type, identifier, srs_id) VALUES ('{layer}','features','{layer}',4326)");
        Exec(conn, $"INSERT INTO gpkg_geometry_columns VALUES ('{layer}','geom','{geometryType}',4326,0,0)");
        t.Commit();
        return new GeoPackageWriter(conn, layer, "geom", fields, types);
    }

    public void AddFeature(NtsGeometry geom, object?[]? values)
    {
        if (geom == null) return;
        var env = geom.EnvelopeInternal;
        if (env != null)
        {
            _minX = Math.Min(_minX, env.MinX); _minY = Math.Min(_minY, env.MinY);
            _maxX = Math.Max(_maxX, env.MaxX); _maxY = Math.Max(_maxY, env.MaxY);
        }
        var wkb = new WKBWriter(ByteOrder.LittleEndian).Write(geom);
        // GeoPackage 二进制头（正式规范 8 字节）：'G' 'P' + 版本 0 + 标志(小端、无 envelope) + srs_id 4326(int32 LE)。
        // 标志 0x11 那种写法既少写 2 字节 srs_id 又把「空几何」位点亮，GDAL 与自家读取器都解不出来。
        var header = new byte[] { 0x47, 0x50, 0x00, 0x01, 0xE6, 0x10, 0x00, 0x00 };
        var blob = new byte[header.Length + wkb.Length];
        Array.Copy(header, 0, blob, 0, header.Length);
        Array.Copy(wkb, 0, blob, header.Length, wkb.Length);

        var placeholders = new List<string> { "@geom" };
        var sql = $"INSERT INTO \"{_layer}\" (\"geom\"";
        if (_fields.Count > 0)
        {
            sql += ", " + string.Join(", ", _fields.Select(f => $"\"{f}\""));
            placeholders.AddRange(_fields.Select((_, i) => $"@p{i}"));
        }
        sql += $") VALUES ({string.Join(", ", placeholders)})";

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@geom", blob);
        if (values != null)
            for (int i = 0; i < _fields.Count; i++)
            {
                var v = i < values.Length ? values[i] : null;
                cmd.Parameters.AddWithValue($"@p{i}", v ?? DBNull.Value);
            }
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_minX <= _maxX)
            Exec(_conn, $"UPDATE gpkg_contents SET min_x={F(_minX)}, min_y={F(_minY)}, max_x={F(_maxX)}, max_y={F(_maxY)} WHERE table_name='{_layer}'");
        _conn.Dispose();
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
