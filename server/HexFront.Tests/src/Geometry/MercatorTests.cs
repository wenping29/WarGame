using FluentAssertions;
using HexFront.Core.Geometry;
using Xunit;

namespace HexFront.Tests.Geometry;

public class MercatorTests
{
    [Fact]
    public void LonLatToMercator_Roundtrips_At_Equator()
    {
        var (x, y) = Mercator.LonLatToMercator(0, 0);
        x.Should().Be(0);
        Math.Abs(y).Should().BeLessThan(1e-6, "equator y should be ~0 (floating-point drift OK)");
    }

    [Fact]
    public void LonLatToMercator_Roundtrips_Anywhere()
    {
        var (lon, lat) = (7.0, 51.3);
        var (x, y) = Mercator.LonLatToMercator(lon, lat);
        var (lon2, lat2) = Mercator.MercatorToLonLat(x, y);
        Math.Abs(lon2 - lon).Should().BeLessThan(1e-9);
        Math.Abs(lat2 - lat).Should().BeLessThan(1e-9);
    }

    [Fact]
    public void ScaleAtLat_Is_One_At_Equator()
    {
        Mercator.ScaleAtLat(0).Should().Be(1.0);
    }

    [Fact]
    public void ScaleAtLat_Grows_With_Latitude()
    {
        // At 51°N, scale ≈ 1/cos(51°) ≈ 1.589
        var scale = Mercator.ScaleAtLat(51.3);
        scale.Should().BeGreaterThan(1.5);
        scale.Should().BeLessThan(1.6);
    }

    [Fact]
    public void HalfCircumference_Matches_Standard_Value()
    {
        // Web Mercator half-circumference ≈ 20,037,508.34 m
        Math.Abs(Mercator.HalfCircumference - 20037508.342789244).Should().BeLessThan(0.01);
    }
}
