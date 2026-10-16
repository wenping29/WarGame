using System;
using System.Collections.Generic;

namespace HexFront.Core.Geometry;

/// <summary>
/// Pointy-top hex grid geometry in EPSG:3857 (Web Mercator).
/// All distances are in Mercator meters. Ground-km distances
/// must be converted via mercatorScale = 1/cos(centerLat).
/// </summary>
public sealed class HexGrid
{
    /// <summary>Mercator x of the origin (hex 0,0 center).</summary>
    public double Ox { get; }

    /// <summary>Mercator y of the origin (hex 0,0 center).</summary>
    public double Oy { get; }

    /// <summary>Hex radius (center-to-corner) in Mercator meters.</summary>
    public double Radius { get; }

    /// <summary>Number of columns (q range: 0..Width-1).</summary>
    public int Width { get; }

    /// <summary>Number of rows (r range: 0..Height-1).</summary>
    public int Height { get; }

    /// <summary>Ground-kilometer flat-to-flat distance (the "对边距" spec value).</summary>
    public double CellGroundKm { get; }

    /// <summary>Mercator scale factor used when this grid was built.</summary>
    public double MercatorScale { get; }

    /// <summary>Flat-to-flat distance in Mercator meters (= √3 × Radius).</summary>
    public double FlatToFlat => Math.Sqrt(3) * Radius;

    /// <summary>Row step in Mercator meters (= 1.5 × Radius).</summary>
    public double RowStep => 1.5 * Radius;

    public HexGrid(double ox, double oy, double radius, int width, int height,
        double cellGroundKm, double mercatorScale)
    {
        Ox = ox;
        Oy = oy;
        Radius = radius;
        Width = width;
        Height = height;
        CellGroundKm = cellGroundKm;
        MercatorScale = mercatorScale;
    }

    /// <summary>Hex (q,r) center in Mercator meters.</summary>
    public (double X, double Y) Center(int q, int r)
    {
        var x = Ox + q * FlatToFlat + (r & 1) * (FlatToFlat / 2);
        var y = Oy - r * RowStep;
        return (x, y);
    }

    /// <summary>Nearest hex to a point in Mercator meters.</summary>
    public Hex Nearest(double mx, double my)
    {
        // Inverse of Center(): solve for fractional (fq, fr), then cube-round.
        var x = mx - Ox;
        var y = my - Oy;

        // For odd-r offset layout:
        //   x = fq * w + (fr_rounded & 1) * w/2
        //   y = -fr * h
        var fr = -y / RowStep;
        var frRounded = (int)Math.Round(fr);
        var offset = (frRounded & 1) * 0.5;
        var fq = (x - offset * FlatToFlat) / FlatToFlat;

        // Convert axial to cube for rounding
        var fs = -fq - fr;
        return Hex.Round(fq, fr, fs);
    }

    /// <summary>All valid hex coordinates in this grid.</summary>
    public IEnumerable<Hex> AllCells()
    {
        for (var r = 0; r < Height; r++)
        for (var q = 0; q < Width; q++)
            yield return new Hex(q, r);
    }

    public bool Contains(Hex h) => h.Q >= 0 && h.Q < Width && h.R >= 0 && h.R < Height;

    /// <summary>BFS distance field from source hexes. Returns dict of hex → distance (unreachable = -1).</summary>
    public Dictionary<Hex, int> DistanceField(IEnumerable<Hex> sources, Func<Hex, Hex, bool>? passable = null)
    {
        var dist = new Dictionary<Hex, int>();
        var queue = new Queue<Hex>();

        foreach (var src in sources)
        {
            if (Contains(src))
            {
                dist[src] = 0;
                queue.Enqueue(src);
            }
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            var curDist = dist[cur];

            foreach (var neighbor in cur.Neighbors())
            {
                if (!Contains(neighbor) || dist.ContainsKey(neighbor))
                    continue;

                // Check passability if predicate provided
                if (passable != null && !passable(cur, neighbor))
                    continue;

                dist[neighbor] = curDist + 1;
                queue.Enqueue(neighbor);
            }
        }

        // Mark unreachable cells as -1
        foreach (var h in AllCells())
        {
            if (!dist.ContainsKey(h))
                dist[h] = -1;
        }

        return dist;
    }

    /// <summary>A* pathfinding with custom cost function. Returns path or empty list if unreachable.</summary>
    public List<Hex> Pathfind(Hex start, Hex goal, Func<Hex, double>? heuristic = null, Func<Hex, Hex, double>? cost = null)
    {
        if (!Contains(start) || !Contains(goal))
            return new List<Hex>();

        // Default heuristic: hex distance
        heuristic ??= h => h.DistanceTo(goal);
        
        // Default cost: uniform 1.0 per step
        cost ??= (_, _) => 1.0;

        var openSet = new SortedSet<(double F, int TieBreak, Hex H)>();
        var cameFrom = new Dictionary<Hex, Hex>();
        var gScore = new Dictionary<Hex, double> { [start] = 0 };
        var tieBreaker = 0;

        openSet.Add((heuristic(start), tieBreaker++, start));

        while (openSet.Count > 0)
        {
            var current = openSet.Min.H;
            openSet.Remove(openSet.Min);

            if (current.Equals(goal))
            {
                // Reconstruct path
                var path = new List<Hex> { goal };
                var node = goal;
                while (cameFrom.ContainsKey(node))
                {
                    node = cameFrom[node];
                    path.Insert(0, node);
                }
                return path;
            }

            foreach (var neighbor in current.Neighbors())
            {
                if (!Contains(neighbor))
                    continue;

                var tentativeG = gScore[current] + cost(current, neighbor);

                if (!gScore.ContainsKey(neighbor) || tentativeG < gScore[neighbor])
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeG;
                    var fScore = tentativeG + heuristic(neighbor);
                    
                    // Remove old entry if exists
                    var oldEntry = openSet.FirstOrDefault(e => e.H.Equals(neighbor));
                    if (!oldEntry.Equals(default((double, int, Hex))))
                        openSet.Remove(oldEntry);
                    
                    openSet.Add((fScore, tieBreaker++, neighbor));
                }
            }
        }

        return new List<Hex>(); // No path found
    }
}
