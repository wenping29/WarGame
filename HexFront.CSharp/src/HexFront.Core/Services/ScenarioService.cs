using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Scenario configuration loader for loading game setups from JSON files.
/// Handles OOB (Order of Battle), terrain specs, victory conditions, and initial deployments.
/// </summary>
public static class ScenarioService
{
    /// <summary>Scenario configuration structure.</summary>
    public class ScenarioConfig
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public int MaxTurns { get; init; } = 14;
        public string WeatherMode { get; init; } = "director";
        public bool StrictSupply { get; init; } = false;
        
        public List<UnitDeployment> Units { get; init; } = new();
        public List<VictoryCondition> VictoryConds { get; init; } = new();
        public TerrainSpec? Terrain { get; init; }
        public Dictionary<string, object>? Flags { get; init; }
        public string? GisPackage { get; init; }
        public EnvSpec? Environment { get; init; }
        public WeatherCfg? Weather { get; init; }
        public ClockCfg? Clock { get; init; }
        public Dictionary<string, SideConfig>? Sides { get; init; }
    }

    /// <summary>Map environment spec from scenario "environment" block.</summary>
    public class EnvSpec
    {
        public int Width { get; init; } = 26;
        public int Height { get; init; } = 20;
        public double CellKm { get; init; } = 5.0;
        public int HeightSeed { get; init; } = 7;
        public double[]? CenterLonLat { get; init; }
    }

    /// <summary>Weather block: initial condition + evolution mode.</summary>
    public class WeatherCfg
    {
        public string Initial { get; init; } = "clear";
        public string Mode { get; init; } = "director";
    }

    /// <summary>Clock block: start hour and season.</summary>
    public class ClockCfg
    {
        public int StartHour { get; init; } = 6;
        public string Season { get; init; } = "夏";
    }

    /// <summary>One side's order of battle.</summary>
    public class SideConfig
    {
        public string Name { get; init; } = "";
        public double PointsBudget { get; init; }
        public List<OobEntry> Oob { get; init; } = new();
    }

    /// <summary>OOB entry: N units of a type deployed around a hex.</summary>
    public class OobEntry
    {
        public string Type { get; init; } = "rifle";
        public int Qty { get; init; } = 1;
        public int[]? At { get; init; }
        public double PersonnelRatio { get; init; } = 1.0;
        public double Morale { get; init; } = 70;
        public double Fatigue { get; init; } = 0;
        public double Ammo { get; init; } = 1.0;
        public double Fuel { get; init; } = 1.0;
        public double Food { get; init; } = 1.0;
        public int Fort { get; init; } = 0;
    }

    /// <summary>Unit deployment specification.</summary>
    public class UnitDeployment
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "rifle";
        public string Side { get; init; } = "red";
        public int Q { get; init; }
        public int R { get; init; }
        public double Morale { get; init; } = 80;
        public double Fatigue { get; init; } = 0;
        public double Ammo { get; init; } = 1.0;
        public double Fuel { get; init; } = 1.0;
        public int Personnel { get; init; } = -1; // -1 means use type default
    }

    /// <summary>Victory condition specification.</summary>
    public class VictoryCondition
    {
        public string Type { get; init; } = "attrition";
        public int RedThreshold { get; init; }
        public int BlueThreshold { get; init; }
        public int RedHoldCount { get; init; }
        public int BlueHoldCount { get; init; }
    }

    /// <summary>Terrain specification for generation.</summary>
    public class TerrainSpec
    {
        public double[] Bbox { get; init; } = new double[4];
        public double CellKm { get; init; } = 2.5;
        public int Seed { get; init; } = 42;
        public bool Offline { get; init; } = true;
        public bool Features { get; init; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>Load scenario from JSON file path.</summary>
    public static ScenarioConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Scenario file not found: {path}");

        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    /// <summary>Load scenario from JSON string.</summary>
    public static ScenarioConfig LoadFromJson(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<ScenarioConfig>(json, JsonOptions);
            return config ?? throw new InvalidOperationException("Failed to deserialize scenario");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid scenario JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Create a GameState from scenario config. gisDir enables loading the referenced hex package.</summary>
    public static GameState CreateGameState(ScenarioConfig config, string? gisDir = null)
    {
        var (grid, cells) = LoadGisCells(config.GisPackage, gisDir)
            ?? BuildProceduralGrid(config.Environment ?? new EnvSpec());

        var g = new GameState
        {
            Grid = grid,
            Cells = cells,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            ScenarioId = config.Id,
            ScenarioName = config.Name,
            Turn = 1,
            MaxTurns = config.MaxTurns,
            HourOfDay = config.Clock?.StartHour ?? 6,
            Season = config.Clock?.Season ?? "夏",
            Weather = config.Weather?.Initial ?? "clear",
            WeatherMode = config.Weather?.Mode ?? config.WeatherMode,
            Over = false,
            Winner = null,
            Rules = LibraryService.DEFAULT_RULES,
            Flags = config.Flags ?? new Dictionary<string, object>(),
            VictoryConds = new List<VictoryCond>(),
            Units = new List<Unit>(),
            Obs = new Dictionary<string, Dictionary<string, int>>(),
            Tracks = new Dictionary<string, Dictionary<string, Track>>(),
            AirControl = new Dictionary<string, double>(),
            Events = new List<WarEvent>(),
        };

        // Parse victory conditions
        foreach (var vc in config.VictoryConds)
        {
            g.VictoryConds.Add(new VictoryCond
            {
                Type = ParseVictoryType(vc.Type),
                RedThreshold = vc.RedThreshold,
                BlueThreshold = vc.BlueThreshold,
                RedHoldCount = vc.RedHoldCount,
                BlueHoldCount = vc.BlueHoldCount,
            });
        }

        // Deploy units from sides OOB (real scenario format)
        PlaceOob(g, config);

        // Deploy units from inline unit list (simple scenario format)
        foreach (var dep in config.Units)
        {
            var unitType = LibraryService.GetUnitType(dep.Type);
            var personnel = dep.Personnel > 0 ? dep.Personnel : unitType.Personnel;

            var u = new Unit
            {
                Id = dep.Id,
                Side = dep.Side,
                UnitType = dep.Type,
                Pos = SpawnAt(g, new Hex(dep.Q, dep.R), new HashSet<string>()),
                Personnel = personnel,
                MaxPersonnel = personnel,
                Morale = dep.Morale,
                Fatigue = dep.Fatigue,
                Ammo = dep.Ammo,
                Fuel = dep.Fuel,
                Food = 1.0,
                SupplyRate = 1.0,
                Status = UnitStatus.Ready,
                Casualties = 0,
                Airborne = false,
                Mission = "none",
                FortLevel = 0,
                Training = unitType.TrainingDefault,
                Experience = unitType.ExpDefault,
                IsHQ = unitType.IsHQ,
            };

            g.Units.Add(u);
        }

        FogService.UpdateObservation(g);

        return g;
    }

    /// <summary>Load real GIS hex package (data/gis/*.json) into grid geometry + cells.</summary>
    private static (HexGrid, Dictionary<string, Cell>)? LoadGisCells(string? pkgName, string? gisDir)
    {
        if (string.IsNullOrEmpty(pkgName) || string.IsNullOrEmpty(gisDir)) return null;
        var path = Path.IsPathRooted(pkgName) ? pkgName : Path.Combine(gisDir, pkgName);
        if (!File.Exists(path)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("hexes", out var hexesEl) || hexesEl.ValueKind != JsonValueKind.Array)
            return null;

        var cells = new Dictionary<string, Cell>();
        foreach (var h in hexesEl.EnumerateArray())
        {
            if (!h.TryGetProperty("q", out var qEl) || !qEl.TryGetInt32(out var q)) continue;
            if (!h.TryGetProperty("r", out var rEl) || !rEl.TryGetInt32(out var r)) continue;
            var pos = new Hex(q, r);
            cells[pos.Key] = new Cell
            {
                Terrain = JsonStr(h, "terrain") ?? "clear",
                Elev = JsonDbl(h, "elev"),
                Slope = JsonDbl(h, "slope"),
                Road = JsonBool(h, "road"),
                River = JsonBool(h, "river"),
                Bridge = JsonBool(h, "bridge"),
                City = (int)JsonDbl(h, "city"),
                Railway = JsonBool(h, "railway"),
                RailStation = JsonBool(h, "rail_station"),
                Tunnel = JsonBool(h, "tunnel"),
                Powerline = JsonBool(h, "powerline"),
                PowerPlant = JsonBool(h, "power_plant"),
                Substation = JsonBool(h, "substation"),
                Airport = (int)JsonDbl(h, "airport"),
            };
        }
        if (cells.Count == 0) return null;

        var meta = root.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object
            ? m : default(JsonElement);

        var width = (int)JsonDbl(meta, "width", SpanOf(cells, true));
        var height = (int)JsonDbl(meta, "height", SpanOf(cells, false));
        var cellKm = JsonDbl(meta, "cell_km", 5.0);
        var scale = MercatorScaleFrom(JsonArr(meta, "center_lonlat"));
        var radius = JsonDbl(meta, "radius_m", 0);
        if (radius <= 0) radius = cellKm * 1000.0 * scale / Math.Sqrt(3.0);
        var ox = JsonDbl(meta, "mercator_ox", 4500000);
        var oy = JsonDbl(meta, "mercator_oy", 3200000);

        var grid = new HexGrid(ox, oy, radius, width, height, cellKm, scale);
        return (grid, cells);
    }

    /// <summary>Self-contained procedural terrain when no GIS package is available.</summary>
    private static (HexGrid, Dictionary<string, Cell>) BuildProceduralGrid(EnvSpec env)
    {
        var w = env.Width > 0 ? env.Width : 26;
        var hgt = env.Height > 0 ? env.Height : 20;
        var seedH = env.HeightSeed;
        var scale = MercatorScaleFrom(env.CenterLonLat);
        var radius = env.CellKm * 1000.0 * scale / Math.Sqrt(3.0);
        var grid = new HexGrid(4500000, 3200000, radius, w, hgt, env.CellKm, scale);

        var cells = new Dictionary<string, Cell>();
        var roadR = (int)Math.Round(hgt / 2.0);
        for (var q = 0; q < w; q++)
        {
            for (var r = 0; r < hgt; r++)
            {
                var n = ValueNoise(q, r, seedH);
                var slopeN = ValueNoise(q + 97, r + 31, seedH + 1);
                string terrain;
                double elev;
                if (n < 0.18) { terrain = "river"; elev = 40 + n * 100; }
                else if (n < 0.36) { terrain = "clear"; elev = 80 + n * 400; }
                else if (n < 0.55) { terrain = "woods"; elev = 120 + n * 500; }
                else if (n < 0.74) { terrain = "hills"; elev = 220 + n * 900; }
                else if (n < 0.9) { terrain = "rough"; elev = 320 + n * 1100; }
                else { terrain = "mountain"; elev = 500 + n * 1600; }

                var city = slopeN > 0.86 && n > 0.2 && n < 0.6 ? 2 : 0;
                var onRoad = r == roadR;
                cells[new Hex(q, r).Key] = new Cell
                {
                    Terrain = terrain,
                    Elev = Math.Round(elev / 10) * 10,
                    Slope = slopeN * 22,
                    Road = onRoad,
                    River = terrain == "river",
                    Bridge = onRoad && terrain == "river",
                    City = city,
                };
            }
        }
        return (grid, cells);
    }

    /// <summary>Deploy side OOB entries: qty copies around "at", spawning on nearest passable free hex.</summary>
    private static void PlaceOob(GameState g, ScenarioConfig config)
    {
        if (config.Sides == null) return;
        var prefix = new Dictionary<string, string> { ["red"] = "R", ["blue"] = "B", ["neutral"] = "N" };
        var taken = new HashSet<string>();
        var counter = 0;

        foreach (var (sideKey, sideCfg) in config.Sides)
        {
            foreach (var entry in sideCfg.Oob)
            {
                if (!g.UnitTypes.TryGetValue(entry.Type, out var t)) continue;
                var at = entry.At is { Length: >= 2 } ? entry.At : new[] { 0, 0 };
                for (var i = 0; i < entry.Qty; i++)
                {
                    var want = new Hex(at[0] + i % 3, at[1] + (i / 3) % 4);
                    var pos = SpawnAt(g, want, taken);
                    taken.Add(pos.Key);
                    counter++;
                    var personnel = Math.Max(1, (int)Math.Round(t.Personnel * entry.PersonnelRatio));
                    g.Units.Add(new Unit
                    {
                        Id = (prefix.GetValueOrDefault(sideKey, "X")) + counter,
                        Side = sideKey,
                        UnitType = t.Id,
                        Pos = pos,
                        Personnel = personnel,
                        MaxPersonnel = personnel,
                        Morale = entry.Morale,
                        Fatigue = entry.Fatigue,
                        Ammo = entry.Ammo,
                        Fuel = entry.Fuel,
                        Food = entry.Food,
                        SupplyRate = 1.0,
                        Status = UnitStatus.Ready,
                        Airborne = false,
                        Mission = "none",
                        FortLevel = entry.Fort,
                        Training = t.TrainingDefault,
                        Experience = t.ExpDefault,
                        IsHQ = t.IsHQ,
                    });
                }
            }
        }
    }

    /// <summary>Scenario coords may land on water/edge hexes after real-DEM grids: find nearest passable free hex.</summary>
    private static Hex SpawnAt(GameState g, Hex want, HashSet<string> taken)
    {
        bool Ok(Hex h)
        {
            var c = g.GetCell(h);
            if (c == null) return false;
            var t = g.Terrains.GetValueOrDefault(c.Terrain) ?? LibraryService.GetTerrain(c.Terrain);
            if (t.Water || t.Impassable) return false;
            return !taken.Contains(h.Key);
        }

        if (Ok(want)) return want;
        foreach (var h in want.Range(4))
        {
            if (h != want && Ok(h)) return h;
        }
        return want;
    }

    private static int SpanOf(Dictionary<string, Cell> cells, bool onQ)
    {
        var lo = int.MaxValue;
        var hi = int.MinValue;
        foreach (var k in cells.Keys)
        {
            var h = Hex.Parse(k);
            var v = onQ ? h.Q : h.R;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        return hi - lo + 1;
    }

    private static double MercatorScaleFrom(double[]? centerLonLat)
    {
        var lat = centerLonLat is { Length: >= 2 } ? centerLonLat[1] : 0.0;
        return 1.0 / Math.Cos(Math.Clamp(lat, -84.0, 84.0) * Math.PI / 180.0);
    }

    /// <summary>Deterministic value noise ported from scenario.dart _valueNoise (reproducible terrain).</summary>
    private static double ValueNoise(int x, int y, int seed)
    {
        static double V(int ix, int iy, int sd)
        {
            unchecked
            {
                var h = (uint)(ix * 374761393L + iy * 668265263L + (long)sd * 2147483647L);
                h = (h ^ (h >> 13)) & 0xFFFFFFFF;
                h = (uint)(h * 1274126177UL);
                return ((h ^ (h >> 16)) & 0xFFFFFFu) / 16777216.0;
            }
        }

        var xf = x / 4.0;
        var yf = y / 4.0;
        var x0 = (int)Math.Floor(xf);
        var y0 = (int)Math.Floor(yf);
        var tx = xf - x0;
        var ty = yf - y0;
        double S(double t) => t * t * (3 - 2 * t);
        var a = V(x0, y0, seed);
        var b = V(x0 + 1, y0, seed);
        var c = V(x0, y0 + 1, seed);
        var d = V(x0 + 1, y0 + 1, seed);
        var u = S(tx);
        var vv = S(ty);
        return (a * (1 - u) + b * u) * (1 - vv) + (c * (1 - u) + d * u) * vv;
    }

    private static string? JsonStr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static double JsonDbl(JsonElement el, string name, double fallback = 0) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.TryGetDouble(out var v)
            ? v : fallback;

    private static bool JsonBool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static double[]? JsonArr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<double>();
        foreach (var item in p.EnumerateArray())
            if (item.TryGetDouble(out var v)) list.Add(v);
        return list.Count > 0 ? list.ToArray() : null;
    }

    /// <summary>Get list of available scenarios from directory.</summary>
    public static List<ScenarioInfo> ListScenarios(string directory)
    {
        var scenarios = new List<ScenarioInfo>();

        if (!Directory.Exists(directory))
            return scenarios;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var config = LoadFromFile(file);
                scenarios.Add(new ScenarioInfo
                {
                    Id = config.Id,
                    Name = config.Name,
                    Description = config.Description,
                    Path = file,
                });
            }
            catch
            {
                // Skip invalid files
            }
        }

        return scenarios;
    }

    /// <summary>Scenario metadata for listing.</summary>
    public class ScenarioInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Path { get; init; } = "";
    }

    private static VictoryType ParseVictoryType(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "attrition" => VictoryType.Attrition,
            "hold_objective" => VictoryType.HoldObjective,
            "control_nodes" => VictoryType.ControlNodes,
            _ => VictoryType.Attrition,
        };
    }
}
