using System;
using System.Collections.Generic;
using System.Linq;

namespace HexFront.Core.Geometry;

/// <summary>
/// Pointy-top axial hex coordinate (q, r).
/// Cube coordinate s = -q-r is derived on demand.
/// </summary>
public readonly record struct Hex(int Q, int R) : IComparable<Hex>, IEquatable<Hex>
{
    public int S => -Q - R;

    public string Key => $"{Q},{R}";

    public static Hex Parse(string key)
    {
        var parts = key.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var q) || !int.TryParse(parts[1], out var r))
            throw new FormatException($"Invalid hex key: '{key}'");
        return new Hex(q, r);
    }

    /// <summary>Cube rounding: round fractional (fq, fr, fs) to nearest valid hex.</summary>
    public static Hex Round(double fq, double fr, double fs)
    {
        var rq = Math.Round(fq);
        var rr = Math.Round(fr);
        var rs = Math.Round(fs);

        var dq = Math.Abs(rq - fq);
        var dr = Math.Abs(rr - fr);
        var ds = Math.Abs(rs - fs);

        if (dq > dr && dq > ds)
            rq = -rr - rs;
        else if (dr > ds)
            rr = -rq - rs;
        // else rs = -rq - rr (already satisfied)

        return new Hex((int)rq, (int)rr);
    }

    /// <summary>6 axial neighbors in clockwise order starting from E.</summary>
    public static readonly Hex[] Directions = new[]
    {
        new Hex(1, 0), new Hex(1, -1), new Hex(0, -1),
        new Hex(-1, 0), new Hex(-1, 1), new Hex(0, 1),
    };

    public IEnumerable<Hex> Neighbors()
    {
        var q = Q; var r = R;
        return Directions.Select(d => new Hex(q + d.Q, r + d.R));
    }

    /// <summary>BFS distance in hex steps.</summary>
    public int DistanceTo(Hex other)
    {
        var dq = Math.Abs(Q - other.Q);
        var dr = Math.Abs(R - other.R);
        var ds = Math.Abs(S - other.S);
        return Math.Max(dq, Math.Max(dr, ds));
    }

    /// <summary>All hexes within radius n (inclusive).</summary>
    public IEnumerable<Hex> Range(int n)
    {
        for (var dq = -n; dq <= n; dq++)
        for (var dr = Math.Max(-n, -dq - n); dr <= Math.Min(n, -dq + n); dr++)
            yield return new Hex(Q + dq, R + dr);
    }

    /// <summary>Linear interpolation between two hexes (for line rasterization).</summary>
    public static IEnumerable<Hex> Lerp(Hex a, Hex b)
    {
        var n = a.DistanceTo(b);
        if (n == 0) { yield return a; yield break; }
        for (var i = 0; i <= n; i++)
        {
            var t = 1.0 / n * i;
            yield return Round(
                a.Q + (b.Q - a.Q) * t,
                a.R + (b.R - a.R) * t,
                a.S + (b.S - a.S) * t);
        }
    }

    public int CompareTo(Hex other)
    {
        var c = Q.CompareTo(other.Q);
        return c != 0 ? c : R.CompareTo(other.R);
    }

    public override string ToString() => $"({Q},{R})";
}
