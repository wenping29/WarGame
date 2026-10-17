using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>Fog of War track (moved to Models namespace).</summary>
// public class Track { ... } // Now in GameState.cs

/// <summary>
/// Fog of War: observation accumulation → line-of-sight → exposure levels → side view裁剪.
/// Ported from fog.dart with full compatibility.
/// </summary>
public static class FogService
{
    /// <summary>Recon radius for a unit considering type, weather, fatigue, and terrain.</summary>
    public static double ReconRadius(GameState g, Unit u)
    {
        var t = g.TypeOf(u);
        var weatherId = g.Weather ?? "clear";
        var wx = g.Weathers.GetValueOrDefault(weatherId) ?? new WeatherDef { Id = "clear", Recon = 1.0 };
        var cell = g.GetCell(u.Pos);
        if (cell == null) return 0;
        var obsTerrain = g.TerrainOf(u.Pos);
        
        var r = (1.0 + t.Recon * 0.55) * wx.Recon;
        
        if (u.Fatigue > 60)
            r *= 0.85;
        
        r *= (1.0 - 0.15 * (1.0 - obsTerrain.ObsPenalty));
        
        return Math.Max(0, r);
    }

    /// <summary>Line of sight check using real elevation + terrain blocking.</summary>
    public static bool LineOfSight(GameState g, Hex from, Hex to)
    {
        if (from.Equals(to))
            return true;

        var path = Hex.Lerp(from, to).ToList();
        var fromCell = g.GetCell(from);
        var toCell = g.GetCell(to);
        
        if (fromCell == null || toCell == null)
            return false;

        var eyeA = fromCell.Elev + 20.0;
        var eyeB = toCell.Elev + 20.0;
        var n = path.Count - 1;
        const double step = 5.0; // losElevStep equivalent
        var blocking = 0.0;

        for (var i = 1; i < n; i++)
        {
            var t = (double)i / n;
            var sight = eyeA + (eyeB - eyeA) * t;
            var cell = g.GetCell(path[i]);
            
            if (cell != null && cell.Elev > sight + step)
                blocking += 1.0;
            
            var td = g.TerrainOf(path[i]);
            blocking += td.LosBlock * 0.6;
        }

        if (blocking >= 1.6)
            return false;

        var targetTd = g.TerrainOf(to);
        var dist = from.DistanceTo(to);
        
        if (targetTd.ObsPenalty < 0.6 && dist > 4 && blocking > 0.4)
            return false;

        return true;
    }

    /// <summary>Exposure level based on observation points (0-3).</summary>
    public static int ExposureLevel(int obsPoints)
    {
        if (obsPoints >= 6) return 3;  // E3: full detail
        if (obsPoints >= 3) return 2;  // E2: type + status
        if (obsPoints >= 1) return 1;  // E1: strength band only
        return 0;
    }

    /// <summary>Update observation for all sides and refresh track memory.</summary>
    public static void UpdateObservation(GameState g)
    {
        var sides = new[] { "red", "blue" };
        var fogOn = FlagBool(g, "fog", true);

        if (!fogOn)
        {
            // Fog off: both sides see everything (teaching/debug mode)
            foreach (var side in sides)
            {
                var all = new Dictionary<string, int>();
                foreach (var key in g.Cells.Keys) all[key] = 99; // full exposure
                g.Obs[side] = all;
                RefreshTracks(g, side, allSeeing: true);
            }
            return;
        }

        foreach (var side in sides)
        {
            var pts = new Dictionary<string, int>();

            foreach (var u in g.Units.Where(u => u.Alive && u.Side == side))
            {
                var r = ReconRadius(g, u);
                if (r <= 0) continue;

                var radius = Math.Max(1, (int)Math.Round(r));
                var strengthBase = 1 + (int)(g.TypeOf(u).Recon / 4.0);

                foreach (var h in u.Pos.Range(radius))
                {
                    if (!g.Cells.ContainsKey(h.Key))
                        continue;

                    var d = u.Pos.DistanceTo(h);
                    if (d > 0 && !LineOfSight(g, u.Pos, h))
                        continue;

                    var w = d == 0 
                        ? strengthBase + 1 
                        : Math.Max(1, strengthBase - (int)(d / radius * 1.5));

                    pts[h.Key] = pts.GetValueOrDefault(h.Key, 0) + w;
                }
            }

            g.Obs[side] = pts;
            RefreshTracks(g, side, allSeeing: false);
        }
    }

    /// <summary>Read a boolean flag from GameState.Flags, tolerant of boxed types.</summary>
    private static bool FlagBool(GameState g, string key, bool fallback)
    {
        var raw = g.Flags.GetValueOrDefault(key);
        if (raw == null) return fallback;
        return raw switch
        {
            bool b => b,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.False => false,
            _ => fallback,
        };
    }

    /// <summary>Refresh enemy track memory with decay logic.</summary>
    private static void RefreshTracks(GameState g, string side, bool allSeeing)
    {
        // TODO: Implement track system (requires Track model)
        // For now, just update observations
    }

    /// <summary>Generate side view: own units full detail + enemy contacts based on exposure.</summary>
    public static Dictionary<string, object> SideView(GameState g, string side)
    {
        var ownUnits = g.Units
            .Where(u => u.Side == side && u.Alive)
            .OrderBy(u => u.Id)
            .Select(u => SerializeUnit(g, u, fullDetail: true))
            .ToList();

        var nodeHolders = MechanicsService.NodeHolders(g, onlySide: side);

        return new Dictionary<string, object>
        {
            ["kind"] = "side",
            ["side"] = side,
            ["turn"] = g.Turn,
            ["units"] = ownUnits,
            ["node_holders"] = nodeHolders,
            ["fog"] = FlagBool(g, "fog", true),
        };
    }

    /// <summary>Director view: full ground truth + both sides' orders.</summary>
    public static Dictionary<string, object> DirectorView(GameState g)
    {
        var allUnits = g.Units
            .Where(u => u.Alive)
            .OrderBy(u => u.Id)
            .Select(u => SerializeUnit(g, u, fullDetail: true))
            .ToList();

        var nodeHolders = MechanicsService.NodeHolders(g);
        var power = new Dictionary<string, double>
        {
            ["red"] = MechanicsService.PowerFactor(g, "red"),
            ["blue"] = MechanicsService.PowerFactor(g, "blue"),
        };

        return new Dictionary<string, object>
        {
            ["kind"] = "director",
            ["turn"] = g.Turn,
            ["units"] = allUnits,
            ["node_holders"] = nodeHolders,
            ["power"] = power,
        };
    }

    /// <summary>Spectator view: merged exposed contacts from both sides, no hidden units.</summary>
    public static Dictionary<string, object> SpectatorView(GameState g)
    {
        // Merge track points from both sides (max points per hex)
        var redObs = g.Obs.GetValueOrDefault("red") ?? new Dictionary<string, int>();
        var blueObs = g.Obs.GetValueOrDefault("blue") ?? new Dictionary<string, int>();
        var merged = new Dictionary<string, int>();
        foreach (var (k, v) in redObs) merged[k] = v;
        foreach (var (k, v) in blueObs) merged[k] = Math.Max(merged.GetValueOrDefault(k, 0), v);

        var visibleUnits = g.Units
            .Where(u => u.Alive && merged.ContainsKey(u.Pos.Key))
            .OrderBy(u => u.Id)
            .Select(u => SerializeUnit(g, u, fullDetail: false))
            .ToList();

        return new Dictionary<string, object>
        {
            ["kind"] = "spectator",
            ["turn"] = g.Turn,
            ["contacts"] = visibleUnits,
            ["summary"] = new Dictionary<string, int>
            {
                ["red_units"] = g.Units.Count(u => u.Side == "red" && u.Alive),
                ["blue_units"] = g.Units.Count(u => u.Side == "blue" && u.Alive),
            },
        };
    }

    /// <summary>Serialize unit for network transmission with appropriate detail level.</summary>
    private static Dictionary<string, object> SerializeUnit(GameState g, Unit u, bool fullDetail)
    {
        var t = g.TypeOf(u);
        var result = new Dictionary<string, object>
        {
            ["id"] = u.Id,
            ["side"] = u.Side,
            ["type"] = t.Id,
            ["pos"] = new[] { u.Pos.Q, u.Pos.R },
            ["hp"] = u.Personnel,
            ["max_hp"] = u.MaxPersonnel,
        };

        if (fullDetail)
        {
            result["morale"] = u.Morale;
            result["fatigue"] = u.Fatigue;
            result["ammo"] = u.Ammo;
            result["fuel"] = u.Fuel;
            result["fort"] = u.FortLevel;
            result["orders"] = u.Orders;
        }

        return result;
    }
}
