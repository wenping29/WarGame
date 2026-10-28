using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using HexFront.Core.GisTools;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NetTopologySuite.IO;
using Xunit;

namespace HexFront.Tests.Gis;

/// <summary>
/// GeoPackage 几何二进制的两种头写法回归。
///
/// 这块曾经整片静默失效：读取器只认 10 字节草案头，而仓库里的 gpkg 全部由 GDAL 生成
/// （8 字节正式头），于是 water/roads/airports 叠加恒为 0 要素且无任何报错。
/// 头写法用 UPDATE 注入到真实库里，避免依赖外部数据文件。
/// </summary>
public sealed class GeoPackageBinaryHeaderTests : IDisposable
{
    private readonly string _path;

    public GeoPackageBinaryHeaderTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"hf_gpkg_hdr_{Guid.NewGuid():N}.gpkg");
        using var w = GeoPackageWriter.Create(_path, "lines", "MULTILINESTRING",
            new[] { ("name", "TEXT") });
        w.AddFeature(Line((0.0, 0.0), (1.0, 1.0)), new object?[] { "seed" });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // 连接池不释放，Windows 上删不掉临时库
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void WriterOutput_RoundTrips()
    {
        using var gp = GeoPackageReader.Open(_path);
        var layer = gp.ReadLayer("lines");
        layer.Features.Should().HaveCount(1);
        layer.Features[0].Geometry.Length.Should().BeApproximately(Math.Sqrt(2), 1e-9);
    }

    [Fact]
    public void GdalStyleHeader_WithXyEnvelope_IsDecoded()
    {
        // GDAL 写法：'G' 'P' version=0 flags=0x03(小端 + XY envelope) srs_id=4326 + 4×double + WKB
        ReplaceBlob(BuildHeader(magic: "GP", flags: 0x03, envelope: Doubles(11, 12, 13, 14), Line((1.0, 2.0), (3.0, 4.0))));
        ReadSingle().Coordinates.Should().Equal(Coords((1, 2), (3, 4)));
    }

    [Fact]
    public void DraftStyleHeader_WithXyEnvelope_IsDecoded()
    {
        // 草案写法：'G' 'P' 'K' 'G' version=0 flags=0x03 srs_id=4326 + 4×double + WKB（比上面多 2 字节）
        ReplaceBlob(BuildHeader(magic: "GPKG", flags: 0x03, envelope: Doubles(21, 22, 23, 24), Line((5.0, 6.0), (7.0, 8.0))));
        ReadSingle().Coordinates.Should().Equal(Coords((5, 6), (7, 8)));
    }

    [Fact]
    public void HeaderWithoutEnvelope_IsDecoded()
    {
        ReplaceBlob(BuildHeader(magic: "GP", flags: 0x01, envelope: Array.Empty<byte>(), Line((9.0, 10.0), (11.0, 12.0))));
        ReadSingle().Coordinates.Should().Equal(Coords((9, 10), (11, 12)));
    }

    /// <summary>envelope 指示符长度按草案头算不通时（旧写出器截断头），靠 WKB 起点扫描兜住。</summary>
    [Fact]
    public void TruncatedLegacyHeader_IsRecoveredByScan()
    {
        var blob = new byte[] { 0x47, 0x50, 0x4B, 0x47, 0x00, 0x11, 0x2C, 0x10 };
        ReplaceBlob(blob.Concat(Wkb(Line((5.0, 6.0), (7.0, 8.0)))).ToArray());
        ReadSingle().Coordinates.Should().Equal(Coords((5, 6), (7, 8)));
    }

    [Fact]
    public void NonGeoPackageBlob_IsRejected()
    {
        ReplaceBlob(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B });
        using var gp = GeoPackageReader.Open(_path);
        gp.ReadLayer("lines").Features.Should().BeEmpty();
    }

    // ---------------- 内部 ----------------

    private NtsGeometry ReadSingle()
    {
        using var gp = GeoPackageReader.Open(_path);
        var features = gp.ReadLayer("lines").Features;
        features.Should().HaveCount(1);
        return features[0].Geometry;
    }

    private void ReplaceBlob(byte[] blob)
    {
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE lines SET geom=@g";
        cmd.Parameters.AddWithValue("@g", blob);
        cmd.ExecuteNonQuery();
    }

    private static byte[] BuildHeader(string magic, byte flags, byte[] envelope, NtsGeometry geometry)
    {
        bool draft = magic == "GPKG";
        using var ms = new MemoryStream();
        ms.Write(draft ? new byte[] { 0x47, 0x50, 0x4B, 0x47 } : new byte[] { 0x47, 0x50 });
        ms.WriteByte(0x00);                                // version
        ms.WriteByte(flags);                               // 小端 + envelope 指示符
        ms.Write(new byte[] { 0xE6, 0x10, 0x00, 0x00 });   // srs_id 4326 int32 LE
        ms.Write(envelope);
        ms.Write(Wkb(geometry));
        return ms.ToArray();
    }

    private static byte[] Doubles(params double[] values)
    {
        var b = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
            BitConverter.GetBytes(values[i]).CopyTo(b, i * 8);
        return b;
    }

    private static byte[] Wkb(NtsGeometry g)
        => new WKBWriter(ByteOrder.LittleEndian).Write(g);

    private static Coordinate[] Coords(params (double X, double Y)[] pts)
        => pts.Select(p => new Coordinate(p.X, p.Y)).ToArray();

    private static MultiLineString Line(params (double X, double Y)[] pts)
        => new GeometryFactory().CreateMultiLineString(new[] { new LineString(Coords(pts)) });
}
