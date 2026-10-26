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

        // 独立验证 reader 的行列换算（ElevationAt/LandcoverAt 内部使用同一 floor 公式）
        int col = (int)((lon - tile.West) / tile.ResLon);
        int row = (int)((tile.North - lat) / tile.ResLat);
        Assert.Equal(expCol, col);
        Assert.Equal(expRow, row);

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
