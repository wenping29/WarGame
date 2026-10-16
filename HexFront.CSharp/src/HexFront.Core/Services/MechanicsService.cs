using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Supply lines, power grid, objective nodes — ported from mechanics.dart.
/// Handles multi-source BFS for supply distances, retention-based supply coefficients,
/// power factor calculation, and node holder visibility for fog of war.
/// </summary>
public static class MechanicsService
{
    /// <summary>Multi-source BFS: compute distance from each cell to nearest supply source.</summary>
    /// <param name="g">Game state with units and grid</param>
    /// <param name="side">Which side's supply sources to use</param>
    /// <returns>Dict of hex key → distance (-1 if unreachable)</returns>
    public static Dictionary<string, int> SupplyDistances(GameState g, string side)
    {
        var dist = new Dictionary<string, int>();
        var frontier = new Queue<Hex>();
        
        // Find all supply sources for this side (logistics units, airports, depots)
        var sources = new List<Hex>();
        foreach (var u in g.Units.Where(u => u.Side == side && u.Alive))
        {
            var type = g.TypeOf(u);
            if (!type.IsAir && (u.UnitType == "logistics" || u.UnitType == "depot"))
            {
                sources.Add(u.Pos);
            }
            // Airports act as supply nodes when air units are present
            if (type.IsAir)
            {
                var cell = g.GetCell(u.Pos);
                if (cell != null && cell.Airport > 0)
                {
                    // Check if ground unit holds this airport
                    var holder = HolderAt(g, u.Pos);
                    if (holder == side)
                    {
                        sources.Add(u.Pos);
                    }
                }
            }
        }

        // Initialize BFS
        foreach (var src in sources)
        {
            var key = src.Key;
            if (!dist.ContainsKey(key))
            {
                dist[key] = 0;
                frontier.Enqueue(src);
            }
        }

        // BFS expansion
        while (frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            var curDist = dist[cur.Key];

            foreach (var neighbor in cur.Neighbors())
            {
                var nKey = neighbor.Key;
                if (dist.ContainsKey(nKey))
                    continue;

                // Enemy-occupied cells block supply
                var cell = g.GetCell(neighbor);
                if (cell == null)
                    continue;

                var occupier = OccupierAt(g, neighbor);
                if (occupier != null && occupier != side)
                    continue;

                // River without bridge/ferry blocks supply (unless strict_supply is off)
                if (cell.River && !cell.Bridge && !HasFerry(g, neighbor, side))
                {
                    if (g.Flags.GetValueOrDefault("strict_supply", false))
                        continue;
                }

                dist[nKey] = curDist + 1;
                frontier.Enqueue(neighbor);
            }
        }

        // Mark unreachable cells
        foreach (var h in g.Grid.AllCells())
        {
            if (!dist.ContainsKey(h.Key))
                dist[h.Key] = -1;
        }

        return dist;
    }

    /// <summary>Compute supply coefficient R = retention^distance for each cell.</summary>
    public static Dictionary<string, double> ComputeSupply(GameState g, string side)
    {
        var strict = g.Flags.GetValueOrDefault("strict_supply", false);
        var distances = SupplyDistances(g, side);
        var retention = strict ? g.Rules.SupplyRetention : 0.95;
        var powerFactor = PowerFactor(g, side);
        var result = new Dictionary<string, double>();

        foreach (var kvp in distances)
        {
            if (kvp.Value >= 0)
            {
                var supply = Math.Pow(retention, kvp.Value) * powerFactor;
                result[kvp.Key] = Math.Max(0.05, Math.Min(1.0, supply));
            }
            else
            {
                result[kvp.Key] = 0.05; // Unreachable cells get minimum supply
            }
        }

        return result;
    }

    /// <summary>Power factor: held plant + substations boost supply retention.</summary>
    /// <returns>Multiplier in range [1.0, 1.15]</returns>
    public static double PowerFactor(GameState g, string side)
    {
        var plants = 0;
        var heldPlants = 0;
        var substations = 0;
        var heldSubstations = 0;

        foreach (var h in g.Grid.AllCells())
        {
            var cell = g.GetCell(h);
            if (cell == null) continue;

            if (cell.PowerPlant)
            {
                plants++;
                if (HolderAt(g, h) == side)
                    heldPlants++;
            }

            if (cell.Substation)
            {
                substations++;
                if (HolderAt(g, h) == side)
                    heldSubstations++;
            }
        }

        if (plants == 0)
            return 1.0; // No power infrastructure

        // Factor = 1.0 + 0.15 × (held_nodes / total_nodes), capped at 1.15
        var totalNodes = plants + substations;
        var heldNodes = heldPlants + heldSubstations;
        
        if (totalNodes == 0)
            return 1.0;

        return Math.Min(1.15, 1.0 + 0.15 * heldNodes / totalNodes);
    }

    /// <summary>Determine which side currently holds a hex (based on unit presence).</summary>
    public static string? HolderAt(GameState g, Hex h)
    {
        var sides = new HashSet<string>();
        foreach (var u in g.Units.Where(u => u.Alive && u.Pos.Equals(h)))
        {
            sides.Add(u.Side);
        }

        if (sides.Count == 0)
            return null;
        if (sides.Count == 1)
            return sides.First();
        
        // Contested: return null or last mover? For now, contested = no holder
        return null;
    }

    /// <summary>Find occupier of a hex (for supply blocking logic).</summary>
    private static string? OccupierAt(GameState g, Hex h)
    {
        // Non-air units occupy their hex
        var groundUnits = g.Units.Where(u => u.Alive && u.Pos.Equals(h) && !g.TypeOf(u).IsAir).ToList();
        
        if (groundUnits.Count == 0)
            return null;
        if (groundUnits.Count == 1)
            return groundUnits[0].Side;
        
        // Multiple sides = contested
        var sides = groundUnits.Select(u => u.Side).Distinct().ToList();
        return sides.Count == 1 ? sides[0] : null;
    }

    /// <summary>Check if a hex has ferry/bridge capability for a given side.</summary>
    private static bool HasFerry(GameState g, Hex h, string side)
    {
        // Engineering units can create ferries
        foreach (var u in g.Units.Where(u => u.Alive && u.Side == side && u.Pos.Equals(h)))
        {
            if (u.UnitType == "engineer")
                return true;
        }
        return false;
    }

    /// <summary>Objective nodes: extract from cell metadata or scenario config.</summary>
    public static List<(Hex Pos, string Name, int Amount)> ObjectiveNodes(GameState g)
    {
        var nodes = new List<(Hex, string, int)>();
        
        foreach (var h in g.Grid.AllCells())
        {
            var cell = g.GetCell(h);
            if (cell == null) continue;

            // City cells with level > 0 are objective nodes
            if (cell.City > 0)
            {
                var amount = cell.City; // 1 = minor, 2 = major
                var name = $"City_{h.Q}_{h.R}"; // TODO: Load from scenario config
                nodes.Add((h, name, amount));
            }
        }

        return nodes;
    }

    /// <summary>Node holders per side: returns side → list of node names held.</summary>
    public static Dictionary<string, List<string>> NodeHolders(GameState g, string? onlySide = null)
    {
        var sides = onlySide != null ? new[] { onlySide } : new[] { "red", "blue", "neutral" };
        var result = new Dictionary<string, List<string>>();

        foreach (var side in sides)
        {
            result[side] = new List<string>();
        }

        var nodes = ObjectiveNodes(g);
        foreach (var (pos, name, _) in nodes)
        {
            var holder = HolderAt(g, pos);
            if (holder != null && result.ContainsKey(holder))
            {
                result[holder].Add(name);
            }
        }

        return result;
    }
}
