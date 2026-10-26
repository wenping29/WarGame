using System;
using System.Collections.Generic;

namespace HexFront.Core.GisTools;

/// <summary>
/// 六角格覆盖：WGS84 经纬度 ↔ 六角格 (q,r) 的矢量逆变换（cube 取整），与 tools/gis/rasters.py 的
/// _coords_to_cells / _pixel_cells 同一套约定，栅格与矢量共用不会各自漂开。
/// 几何参数由 (center_lon, center_lat, width, height, cell_km) 反推，与 HexGenService.BuildGrid 一致。
/// </summary>
public sealed class GridCoverage
{
    private const double HalfCirc = 20037508.342789244;
    private const double Rm = HalfCirc / Math.PI;

    public double MercatorOx { get; }
    public double MercatorOy { get; }
    public double HexWidthM { get; }
    public double RowStepM { get; }
    public int Width { get; }
    public int Height { get; }
    public double CenterLon { get; }
    public double CenterLat { get; }
    public double CellKm { get; }
    public double Scale { get; }

    public GridCoverage(double centerLon, double centerLat, int width, int height, double cellKm)
    {
        CenterLon = centerLon; CenterLat = centerLat; Width = width; Height = height; CellKm = cellKm;
        Scale = 1.0 / Math.Max(0.05, Math.Cos(Math.Min(85.0, Math.Max(-85.0, centerLat)) * Math.PI / 180.0));
        double cellGroundM = cellKm * 1000.0;
        double cellM = cellGroundM * Scale;
        double radiusM = cellM / Math.Sqrt(3.0);
        HexWidthM = Math.Sqrt(3.0) * radiusM;
        RowStepM = 1.5 * radiusM;
        double cx0 = centerLon * HalfCirc / 180.0;
        double cy0 = Rm * Math.Log(Math.Tan((90.0 + centerLat) * Math.PI / 360.0));
        MercatorOx = cx0 - HexWidthM * ((width - 1) / 2.0 + 0.25);
        MercatorOy = cy0 + RowStepM * ((height - 1) / 2.0);
    }

    /// <summary>单个经纬度 → (q, r)，越界返回 (-1,-1)。</summary>
    public (int q, int r) CellAt(double lon, double lat)
    {
        double mx = lon * HalfCirc / 180.0;
        double my = Rm * Math.Log(Math.Tan((90.0 + lat) * Math.PI / 360.0));
        double fr = (MercatorOy - my) / RowStepM;
        double fq = (mx - MercatorOx) / HexWidthM - fr / 2.0;
        double fs = -fq - fr;
        double rq = Math.Round(fq), rr = Math.Round(fr), rs = Math.Round(fs);
        double dq = Math.Abs(rq - fq), dr = Math.Abs(rr - fr), ds = Math.Abs(rs - fs);
        if (dq > dr && dq > ds) rq = -rr - rs;
        else if (dr >= dq && dr > ds) rr = -rq - rs;
        int q = (int)rq, r = (int)rr;
        if (q < 0 || q >= Width || r < 0 || r >= Height) return (-1, -1);
        return (q, r);
    }

    /// <summary>批量经纬度 → 格 index 与 inside 掩码（与 Python 向量化实现一致）。</summary>
    public void CellsAt(double[] lon, double[] lat, out int[] q, out int[] r, out bool[] inside)
    {
        int n = lon.Length;
        q = new int[n]; r = new int[n]; inside = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var (qi, ri) = CellAt(lon[i], lat[i]);
            q[i] = qi; r[i] = ri;
            inside[i] = qi >= 0;
        }
    }

    /// <summary>格心 (q,r) → WGS84 (lat, lon)。</summary>
    public (double lat, double lon) CellLonLat(int q, int r)
    {
        double cx = MercatorOx + HexWidthM * (q + (r & 1) / 2.0);
        double cy = MercatorOy - RowStepM * r;
        double lon = cx * 180.0 / HalfCirc;
        double lat = (2.0 * Math.Atan(Math.Exp(cy / Rm)) - Math.PI / 2.0) * 180.0 / Math.PI;
        return (lat, lon);
    }

    /// <summary>全格网的 WGS84 外轮廓 bbox [west, south, east, north]。</summary>
    public double[] Bbox()
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int r = 0; r < Height; r++)
            for (int q = 0; q < Width; q++)
            {
                double cx = MercatorOx + HexWidthM * (q + (r & 1) / 2.0);
                double cy = MercatorOy - RowStepM * r;
                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
            }
        minX -= HexWidthM / 2; maxX += HexWidthM / 2;
        minY -= HexWidthM / (2 * Math.Sqrt(3)); maxY += HexWidthM / (2 * Math.Sqrt(3));
        double w = minX * 180.0 / HalfCirc;
        double e = maxX * 180.0 / HalfCirc;
        double s = (2.0 * Math.Atan(Math.Exp(minY / Rm)) - Math.PI / 2.0) * 180.0 / Math.PI;
        double n = (2.0 * Math.Atan(Math.Exp(maxY / Rm)) - Math.PI / 2.0) * 180.0 / Math.PI;
        return new[] { w, s, e, n };
    }
}
