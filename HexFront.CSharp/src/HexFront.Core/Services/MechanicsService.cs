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
            if (!type.IsAir && type.SupplySource)
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
                    var strictFlag = g.Flags.GetValueOrDefault("strict_supply");
                    if (strictFlag is bool strictBool && strictBool)
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
        var strictFlag = g.Flags.GetValueOrDefault("strict_supply");
        var strict = strictFlag is bool strictBool && strictBool;
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

    /// <summary>
    /// Compute air control per hex from air units, anti-air and structures.
    /// Positive = red-leaning, negative = blue-leaning, in [-1,1]. Contested (|v|&lt;0.2) omitted.
    /// Ported from mechanics.dart computeAirControl.
    /// </summary>
    public static Dictionary<string, double> ComputeAirControl(GameState g)
    {
        var acc = new Dictionary<string, Dictionary<string, double>>
        {
            ["red"] = new(),
            ["blue"] = new(),
        };

        void Add(string side, Hex h, double v, int radius)
        {
            if (side != "red" && side != "blue") return;
            var m = acc[side];
            foreach (var c in h.Range(Math.Max(0, radius)))
            {
                if (!g.Cells.ContainsKey(c.Key)) continue;
                m[c.Key] = m.GetValueOrDefault(c.Key, 0.0) + v;
            }
        }

        var roleWeight = new Dictionary<string, double>
        {
            ["fighter"] = 1.0, ["strike"] = 0.45, ["recon"] = 0.25,
        };

        foreach (var u in g.Units)
        {
            if (!u.Alive) continue;
            var t = g.TypeOf(u);
            if (t.IsAir)
            {
                var zone = u.Airborne ? (u.MissionZone ?? u.Pos) : u.Pos;
                var w = roleWeight.GetValueOrDefault(t.AirRole, 0.1);
                Add(u.Side, zone, u.Mission == "patrol" ? w : w * 0.4, t.AirRange);
            }
            else if (t.AntiAir > 0)
            {
                Add(u.Side, u.Pos, t.AntiAir * 0.25, 3);
            }
            else if (t.Cls == "structure")
            {
                Add(u.Side, u.Pos, 0.2, 4);
            }
        }

        var keys = new HashSet<string>(acc["red"].Keys);
        keys.UnionWith(acc["blue"].Keys);
        var outMap = new Dictionary<string, double>();
        foreach (var k in keys)
        {
            var a = acc["red"].GetValueOrDefault(k, 0.0);
            var b = acc["blue"].GetValueOrDefault(k, 0.0);
            var tot = a + b;
            if (tot < 0.05) continue;
            var v = (a - b) / tot;
            if (Math.Abs(v) < 0.2) continue; // contested → no owner
            outMap[k] = Math.Max(-1.0, Math.Min(1.0, v));
        }
        return outMap;
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

    // ─────────── Dart fog.dart/mechanics.dart view-shape helpers ───────────

    private static readonly (string Kind, string Label)[] NodeKindOrder =
    {
        ("power_plant", "发电厂"),
        ("substation", "变电站"),
        ("airport_major", "干线机场"),
        ("airport", "野战机场"),
        ("rail_station", "火车站"),
        ("city_major", "大城市"),
        ("city", "城镇"),
    };

    /// <summary>该格是要点则返回类别（电厂 &gt; 变电站 &gt; 机场 &gt; 车站 &gt; 城镇）。</summary>
    public static string? NodeKindOf(Cell c)
    {
        if (c.PowerPlant) return "power_plant";
        if (c.Substation) return "substation";
        if (c.Airport >= 2) return "airport_major";
        if (c.Airport == 1) return "airport";
        if (c.RailStation) return "rail_station";
        if (c.City >= 2) return "city_major";
        if (c.City == 1) return "city";
        return null;
    }

    public static string? NodeLabel(string? kind) =>
        kind == null ? null : NodeKindOrder.FirstOrDefault(n => n.Kind == kind).Label;

    /// <summary>战场要点清单（纯地理，不含归属）：{key,q,r,kind,label}，按类别→坐标排序。</summary>
    public static List<Dictionary<string, object?>> ObjectiveNodesGeo(GameState g)
    {
        var order = NodeKindOrder.Select(n => n.Kind).ToList();
        var outList = new List<(string Key, int Q, int R, string Kind, string Label)>();
        foreach (var (key, cell) in g.Cells)
        {
            var kind = NodeKindOf(cell);
            if (kind == null) continue;
            var h = Hex.Parse(key);
            outList.Add((key, h.Q, h.R, kind, NodeLabel(kind)!));
        }
        return outList
            .OrderBy(x => order.IndexOf(x.Kind))
            .ThenBy(x => x.Key)
            .Select(x => new Dictionary<string, object?>
            {
                ["key"] = x.Key, ["q"] = x.Q, ["r"] = x.R, ["kind"] = x.Kind, ["label"] = x.Label,
            })
            .ToList();
    }

    /// <summary>要点归属：hexKey → 阵营。onlySide 用于双盲视图，只披露己方占领。</summary>
    public static Dictionary<string, string> NodeHoldersByHex(GameState g, string? onlySide = null)
    {
        var outMap = new Dictionary<string, string>();
        foreach (var (key, cell) in g.Cells)
        {
            if (NodeKindOf(cell) == null) continue;
            var holder = HolderAt(g, Hex.Parse(key));
            if (holder == null || (onlySide != null && holder != onlySide)) continue;
            outMap[key] = holder;
        }
        return outMap;
    }
}
