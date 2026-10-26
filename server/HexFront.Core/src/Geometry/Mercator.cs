using System;

namespace HexFront.Core.Geometry;

/// <summary>
/// EPSG:3857 (Web Mercator) coordinate helpers.
/// All distances in meters unless noted otherwise.
/// </summary>
public static class Mercator
{
    /// <summary>Earth radius used by Web Mercator (meters).</summary>
    public const double R = 6378137.0;

    /// <summary>Half circumference of the Earth at the equator (meters).</summary>
    public const double HalfCircumference = Math.PI * R; // ≈ 20,037,508.34 m

    /// <summary>WGS84 lon/lat → Web Mercator x/y (meters).</summary>
    public static (double X, double Y) LonLatToMercator(double lonDeg, double latDeg)
    {
        var x = lonDeg * HalfCircumference / 180.0;
        var latRad = latDeg * Math.PI / 180.0;
        var y = R * Math.Log(Math.Tan(Math.PI / 4 + latRad / 2));
        return (x, y);
    }

    /// <summary>Web Mercator x/y (meters) → WGS84 lon/lat.</summary>
    public static (double Lon, double Lat) MercatorToLonLat(double x, double y)
    {
        var lon = x / HalfCircumference * 180.0;
        var lat = (2 * Math.Atan(Math.Exp(y / R)) - Math.PI / 2) * 180.0 / Math.PI;
        return (lon, lat);
    }

    /// <summary>Mercator scale factor at given latitude: 1/cos(lat).
    /// This converts ground-meter distances to Mercator-meter distances.</summary>
    public static double ScaleAtLat(double latDeg)
    {
        var rad = latDeg * Math.PI / 180.0;
        return 1.0 / Math.Cos(rad);
    }
}
