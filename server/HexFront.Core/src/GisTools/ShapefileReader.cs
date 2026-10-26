using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.IO;

namespace HexFront.Core.GisTools;

/// <summary>
/// 托管 shapefile 读取器（无 GDAL 依赖）：解析 .shp（Point/PolyLine/Polygon 及其 Z 变体）+ .dbf（属性）
/// + .shx（索引，可选）。几何还原为 NetTopologySuite IGeometry，供 fetch-water 转 gpkg。
/// 仅读 X/Y 坐标（忽略 Z/M），属性按字符串读取。
/// </summary>
public sealed class ShapefileReader
{
    public List<ShpFeature> Features { get; } = new();
    /// <summary>DBF 字段名（与每个 ShpFeature.Attributes 的下标一一对应），供转 gpkg 时保留列。</summary>
    public List<string> Fields { get; } = new();

    public static ShapefileReader Open(string basePath)
    {
        var r = new ShapefileReader();
        r.Read(basePath);
        return r;
    }

    private void Read(string basePath)
    {
        string shp = basePath + ".shp";
        string dbf = basePath + ".dbf";
        if (!File.Exists(shp)) throw new FileNotFoundException("缺少 .shp", shp);

        List<string> fieldNames;
        List<string[]?> records;
        if (File.Exists(dbf))
            fieldNames = ReadDbf(dbf, out records);
        else
        {
            fieldNames = new List<string>();
            records = new List<string[]?>();
        }
        Fields.AddRange(fieldNames);
        var attrByIndex = new Dictionary<int, string[]?>();
        for (int i = 0; i < records.Count; i++) attrByIndex[i] = records[i];

        using var fs = new FileStream(shp, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        br.ReadInt32(); // file code (BE)
        br.BaseStream.Seek(32, SeekOrigin.Begin);
        int shapeType = br.ReadInt32(); // LE
        // 逐记录读
        br.BaseStream.Seek(100, SeekOrigin.Begin);
        int idx = 0;
        while (br.BaseStream.Position < br.BaseStream.Length - 4)
        {
            br.ReadInt32(); // record number (BE)
            int contentLen = Swap(br.ReadInt32()); // BE words
            long recStart = br.BaseStream.Position;
            int recType = br.ReadInt32();
            var geom = ParseGeometry(recType, br);
            if (geom != null)
            {
                attrByIndex.TryGetValue(idx, out var attrs);
                Features.Add(new ShpFeature { Geometry = geom, Attributes = attrs ?? Array.Empty<string>() });
            }
            br.BaseStream.Seek(recStart + contentLen * 2, SeekOrigin.Begin);
            idx++;
        }
    }

    private static NtsGeometry? ParseGeometry(int type, BinaryReader br)
    {
        switch (type)
        {
            case 1: // Point
            case 11: // PointZ
                {
                    double x = br.ReadDouble(), y = br.ReadDouble();
                    if (type == 11) { br.ReadDouble(); br.ReadDouble(); } // skip Z,M
                    return new Point(x, y);
                }
            case 8: // MultiPoint
            case 18:
                {
                    br.ReadBytes(32); // bbox
                    int n = br.ReadInt32();
                    var pts = new Point[n];
                    for (int i = 0; i < n; i++) pts[i] = new Point(br.ReadDouble(), br.ReadDouble());
                    if (type == 18) br.ReadBytes(n * 16);
                    return new MultiPoint(pts);
                }
            case 3: // PolyLine
            case 5: // Polygon
            case 13:
            case 15:
                {
                    br.ReadBytes(32); // bbox
                    int numParts = br.ReadInt32();
                    int numPoints = br.ReadInt32();
                    var parts = new int[numParts];
                    for (int i = 0; i < numParts; i++) parts[i] = br.ReadInt32();
                    var coords = new Coordinate[numPoints];
                    for (int i = 0; i < numPoints; i++) coords[i] = new Coordinate(br.ReadDouble(), br.ReadDouble());
                    bool hasZ = type == 13 || type == 15;
                    if (hasZ) br.ReadBytes(numPoints * 8 + (numParts > 0 ? 16 : 0)); // Z + Z-range
                    if (hasZ) br.ReadBytes(numPoints * 8 + (numParts > 0 ? 16 : 0)); // M + M-range
                    return BuildMulti(type, parts, coords);
                }
            default:
                return null;
        }
    }

    private static NtsGeometry? BuildMulti(int type, int[] parts, Coordinate[] coords)
    {
        var factory = new GeometryFactory();
        bool polygon = type == 5 || type == 15;
        if (parts.Length == 0) return null;
        if (parts.Length == 1)
        {
            int start = parts[0];
            int end = coords.Length;
            var c = new Coordinate[end - start];
            Array.Copy(coords, start, c, 0, c.Length);
            return polygon ? factory.CreatePolygon(c) : factory.CreateLineString(c);
        }
        if (polygon)
        {
            var polys = new Polygon[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int start = parts[i];
                int end = (i + 1 < parts.Length) ? parts[i + 1] : coords.Length;
                var c = new Coordinate[end - start];
                Array.Copy(coords, start, c, 0, c.Length);
                polys[i] = factory.CreatePolygon(c);
            }
            return factory.CreateMultiPolygon(polys);
        }
        else
        {
            var lines = new LineString[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int start = parts[i];
                int end = (i + 1 < parts.Length) ? parts[i + 1] : coords.Length;
                var c = new Coordinate[end - start];
                Array.Copy(coords, start, c, 0, c.Length);
                lines[i] = factory.CreateLineString(c);
            }
            return factory.CreateMultiLineString(lines);
        }
    }

    private static List<string> ReadDbf(string path, out List<string[]?> records)
    {
        records = new List<string[]?>();
        var fields = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        br.ReadBytes(4); // version + ymd
        int numRecords = br.ReadInt32();
        int headerSize = br.ReadInt16();
        int recordSize = br.ReadInt16();
        br.BaseStream.Seek(32, SeekOrigin.Begin); // 跳过保留 + 字段描述起始前的字节
        int fieldCount = (headerSize - 33) / 32; // 32 字节头 + 1 终止符(0x0D)
        for (int i = 0; i < fieldCount; i++)
        {
            var nameBytes = br.ReadBytes(11);
            int len = 0; while (len < 11 && nameBytes[len] != 0) len++;
            fields.Add(Encoding.ASCII.GetString(nameBytes, 0, len));
            br.ReadBytes(1); // type
            br.ReadBytes(4); // displacement
            int fLen = br.ReadByte();
            br.ReadBytes(15);
            // 记录每个字段长度，便于按定长切分
        }
        // 重新读字段长度（上面已跳过描述，需重新定位）
        fs.Seek(32, SeekOrigin.Begin);
        var fieldLens = new List<int>();
        for (int i = 0; i < fieldCount; i++)
        {
            br.ReadBytes(16);            // name(11)+type(1)+disp(4)
            fieldLens.Add(br.ReadByte()); // length
            br.ReadBytes(15);
        }
        fs.Seek(headerSize, SeekOrigin.Begin);
        for (int r = 0; r < numRecords; r++)
        {
            br.ReadByte(); // deletion flag
            var values = new string[fieldCount];
            for (int f = 0; f < fieldCount; f++)
            {
                var raw = br.ReadBytes(fieldLens[f]);
                values[f] = Encoding.UTF8.GetString(raw).TrimEnd('\0', ' ', '\r', '\n');
            }
            records.Add(values);
        }
        return fields;
    }

    private static int Swap(int v) => ((v >> 24) & 0xFF) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
}

public sealed class ShpFeature
{
    public NtsGeometry Geometry { get; init; } = null!;
    public string[] Attributes { get; init; } = Array.Empty<string>();
}
