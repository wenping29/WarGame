using HexFront.Api.Services;
using Xunit;

namespace HexFront.Tests.Gis;

public class TerrainTileReaderTests
{
    private static string TilePath() => Path.Combine(AppContext.BaseDirectory, "rhein.ter");

    [Fact]
    public void Load_ParsesHeader_Correctly()
    {
        var tile = TerrainTile.Load(TilePath());

        Assert.Equal(1, tile.Version);
        Assert.Equal(4326, tile.Crs);
        Assert.Equal(4.0, tile.West, 6);
        Assert.Equal(48.0, tile.South, 6);
        Assert.Equal(9.0, tile.East, 6);
        Assert.Equal(51.5, tile.North, 6);
        Assert.Equal(600, tile.Cols);
        Assert.Equal(420, tile.Rows);
        Assert.Equal(0.008333333, tile.ResLon, 9);
        Assert.Equal(0.008333333, tile.ResLat, 9);
        Assert.True(tile.HasLandcover);
        Assert.Equal(-32768, tile.ElevNoData);
        Assert.Equal(0, tile.LcNoData);
    }

    public static IEnumerable<object[]> SamplePoints() => new List<object[]>
    {
        // lat, lon, expectedCol, expectedRow, expectedElev, expectedLandcover
        new object[] { 50.0, 6.0, 240, 180, 343, 10 },
        new object[] { 51.0, 8.0, 480, 60, 466, 10 },
        new object[] { 48.5, 4.5, 60, 360, 134, 40 },
        new object[] { 51.5, 9.0, 599, 0, 240, 40 },
        new object[] { 48.0, 4.0, 0, 419, 174, 10 },
        new object[] { 49.0, 7.0, 360, 300, 231, 30 },
    };

    [Theory]
    [MemberData(nameof(SamplePoints))]
    public void Sample_MatchesPythonExtraction(double lat, double lon, int expCol, int expRow,
        short expElev, byte expLc)
    {
        var tile = TerrainTile.Load(TilePath());

        Assert.True(tile.Contains(lat, lon));

        // 行列换算走 reader 自己的方法（内部含闭区间钳制），不在测试里重抄一份裸公式：
        // 抄一份的话，边界点（东北角 lon==East / 西南角 lat==South）会因浮点误差算出越界的
        // 600 / 420，与实现漂移成假失败。期望值 599 / 419 对应"边界点归属最后一格"的闭区间语义。
        Assert.Equal(expCol, tile.ColAt(lon));
        Assert.Equal(expRow, tile.RowAt(lat));

        Assert.Equal(expElev, tile.ElevationAt(lat, lon));
        Assert.Equal(expLc, tile.LandcoverAt(lat, lon));

        Assert.True(tile.TrySample(lat, lon, out short elev, out byte lc));
        Assert.Equal(expElev, elev);
        Assert.Equal(expLc, lc);
    }

    [Fact]
    public void TrySample_OutsideTile_ReturnsFalse()
    {
        var tile = TerrainTile.Load(TilePath());
        // 赤道附近，明显在瓦片 [4..9, 48..51.5] 之外
        Assert.False(tile.Contains(0.0, 0.0));
        Assert.False(tile.TrySample(0.0, 0.0, out _, out _));
    }
}
