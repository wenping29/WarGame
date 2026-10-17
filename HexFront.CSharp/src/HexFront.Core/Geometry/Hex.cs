using System;
using System.Collections.Generic;
using System.Linq;

namespace HexFront.Core.Geometry;

/// <summary>
/// Pointy-top odd-r offset hex coordinate: Q = column, R = row.
/// Odd rows are shifted half a cell right, so a width×height grid tiles a rectangle
/// (index = r*width + q). Cube/axial math is done by converting to axial internally:
/// aq = Q - (R - parity(R))/2, ar = R.
/// </summary>
public readonly record struct Hex(int Q, int R) : IComparable<Hex>, IEquatable<Hex>
{
    private static int Parity(int r) => r & 1;

    public int AxialQ => Q - (R - Parity(R)) / 2;
    public int AxialR => R;
    public int AxialS => -AxialQ - AxialR;

    public string Key => $"{Q},{R}";

    public static Hex Parse(string key)
    {
        var parts = key.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var q) || !int.TryParse(parts[1], out var r))
            throw new FormatException($"Invalid hex key: '{key}'");
        return new Hex(q, r);
    }

    public static Hex FromAxial(int aq, int ar) => new(aq + (ar - Parity(ar)) / 2, ar);

    /// <summary>Round fractional axial coordinates to the nearest hex (cube rounding).</summary>
    public static Hex Round(double faq, double far)
    {
        var fas = -faq - far;
        var rq = Math.Round(faq);
        var rr = Math.Round(far);
        var rs = Math.Round(fas);

        var dq = Math.Abs(rq - faq);
        var dr = Math.Abs(rr - far);
        var ds = Math.Abs(rs - fas);

        if (dq > dr && dq > ds)
            rq = -rr - rs;
        else if (dr > ds)
            rr = -rq - rs;

        return FromAxial((int)rq, (int)rr);
    }

    private static readonly (int Dq, int Dr)[] EvenRowDirections =
    {
        (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1),
    };

    private static readonly (int Dq, int Dr)[] OddRowDirections =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1),
    };

    /// <summary>The 6 direction deltas for a given row parity (0=even, 1=odd).</summary>
    public static (int Dq, int Dr)[] DirectionsFor(int row) => (row & 1) == 0 ? EvenRowDirections : OddRowDirections;

    /// <summary>6 neighbors, clockwise starting from E.</summary>
    public IEnumerable<Hex> Neighbors()
    {
        var q = Q; var r = R;
        return DirectionsFor(r).Select(d => new Hex(q + d.Dq, r + d.Dr));
    }

    /// <summary>Neighbor in direction index 0..5 (clockwise from E), or null when r is out of parity range.</summary>
    public Hex NeighborAt(int direction)
    {
        var d = DirectionsFor(R)[direction];
        return new Hex(Q + d.Dq, R + d.Dr);
    }

    /// <summary>Distance in hex steps.</summary>
    public int DistanceTo(Hex other)
    {
        var dq = Math.Abs(AxialQ - other.AxialQ);
        var dr = Math.Abs(AxialR - other.AxialR);
        var ds = Math.Abs(AxialS - other.AxialS);
        return Math.Max(dq, Math.Max(dr, ds));
    }

    /// <summary>All hexes within radius n (inclusive).</summary>
    public IEnumerable<Hex> Range(int n)
    {
        var aq = AxialQ; var ar = AxialR;
        for (var dq = -n; dq <= n; dq++)
        for (var dr = Math.Max(-n, -dq - n); dr <= Math.Min(n, -dq + n); dr++)
            yield return FromAxial(aq + dq, ar + dr);
    }

    /// <summary>Hex line between two hexes (for line rasterization).</summary>
    public static IEnumerable<Hex> Lerp(Hex a, Hex b)
    {
        var n = a.DistanceTo(b);
        if (n == 0) { yield return a; yield break; }
        for (var i = 0; i <= n; i++)
        {
            var t = i / (double)n;
            yield return Round(
                a.AxialQ + (b.AxialQ - a.AxialQ) * t,
                a.AxialR + (b.AxialR - a.AxialR) * t);
        }
    }

    public int CompareTo(Hex other)
    {
        var c = R.CompareTo(other.R);
        return c != 0 ? c : Q.CompareTo(other.Q);
    }

    public override string ToString() => $"({Q},{R})";
}
