using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        /// <summary>向导第 4 步的兵种属性覆盖：原样保留补丁键，交给 <see cref="UnitTypeDefService"/> 校验与计价。</summary>
        public List<Dictionary<string, JsonElement>>? UnitTypeDefs { get; init; }
        /// <summary>剧本里的键名是 <c>victory</c>（doc §6.4），不是属性名推导出来的 victory_conds。</summary>
        [System.Text.Json.Serialization.JsonPropertyName("victory")]
        public List<VictoryCondition> VictoryConds { get; init; } = new();
        public TerrainSpec? Terrain { get; init; }
        public Dictionary<string, object>? Flags { get; init; }
        public string? GisPackage { get; init; }
        public EnvSpec? Environment { get; init; }
        public WeatherCfg? Weather { get; init; }
        public ClockCfg? Clock { get; init; }
        public Dictionary<string, SideConfig>? Sides { get; init; }

        /// <summary>
        /// 剧本 rules 块（snake_case 键，可含 combat 子块）：出厂默认之上的数值覆盖。
        /// 刻意保留原始 JsonElement 而不是直接反序列化成 Rules，合并要走 <see cref="MergeRules"/>，
        /// 这样逐层覆盖时缺键仍继承默认。
        /// </summary>
        public Dictionary<string, JsonElement>? Rules { get; init; }
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
        /// <summary>
        /// 天气计划（可选）：按回合区间预排多段天气。
        /// 副本开局时只装载不启动 —— 启动/暂停/删除/改排是导演部的专属操作。
        /// </summary>
        public List<WeatherPhaseCfg>? Schedule { get; init; }
    }

    /// <summary>剧本 weather.schedule 条目：[from, to] 闭区间回合 → weather。</summary>
    public class WeatherPhaseCfg
    {
        public int From { get; init; } = 1;
        public int To { get; init; } = 1;
        public string Weather { get; init; } = "clear";
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

    /// <summary>
    /// OOB entry: N units of a type deployed around a hex.
    /// 人员水平（训练度/经验/指挥官）只能由 OOB 表达（doc §5：兵种属性是本局共用的装备基线），缺省才回落到兵种出厂值。
    /// </summary>
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
        /// <summary>0–100；null 表示沿用兵种出厂训练度。</summary>
        public double? Training { get; init; }
        /// <summary>0–100；null 表示沿用兵种出厂经验。</summary>
        public double? Experience { get; init; }
        /// <summary>剧本允许 <c>"commander": "姓名"</c> 或 <c>{"name","quality"}</c> 两种写法，故留原始 JSON 到生成时解析。</summary>
        public JsonElement? Commander { get; init; }
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

    /// <summary>
    /// 剧本胜利块：与线上 <c>victory</c> 数组同形（doc §6.4），kind/side/target/amount/turns 五项。
    /// 旧的 RedThreshold/BlueThreshold 那套"双方各配一个阈值"是发明，剧本里根本不存在这些键，
    /// 所以胜负从来不会由剧本达成 —— 现在按 kind 逐条判定本方是否达成。
    /// </summary>
    public class VictoryCondition
    {
        public string Kind { get; init; } = "annihilate";
        public string Side { get; init; } = "red";
        public string? Target { get; init; }
        public int Amount { get; init; }
        public int Turns { get; init; }
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

    private static readonly JsonSerializerOptions RulesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 把剧本 rules 块叠加到出厂默认之上：只改出现的键，缺键继承默认，未知键忽略（§7「全系数外置可配，调参不改码」）。
    /// 走 JsonNode 逐层合并而不是直接反序列化，未来加一层「副本级覆盖」也不用改这段。
    /// </summary>
    public static Rules MergeRules(Rules baseRules, IReadOnlyDictionary<string, JsonElement>? block)
    {
        if (block is null || block.Count == 0) return baseRules;

        var root = JsonSerializer.SerializeToNode(baseRules, RulesJsonOptions)!.AsObject();
        foreach (var (key, value) in block)
            Overlay(root, key, value);

        return root.Deserialize<Rules>(RulesJsonOptions) ?? baseRules;
    }

    private static void Overlay(JsonObject target, string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && target[key] is JsonObject child)
        {
            foreach (var p in value.EnumerateObject())
                Overlay(child, p.Name, p.Value);
            return;
        }
        target[key] = JsonNode.Parse(value.GetRawText());
    }

    /// <summary>Create a GameState from scenario config. gisDir enables loading the referenced hex package.</summary>
    public static GameState CreateGameState(ScenarioConfig config, string? gisDir = null,
        uint seed = 42, string? instanceId = null)
    {
        var (grid, cells) = LoadGisCells(config.GisPackage, gisDir)
            ?? BuildProceduralGrid(config.Environment ?? new EnvSpec());

        var g = new GameState
        {
            Grid = grid,
            Cells = cells,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = UnitTypeDefService.EffectiveLibrary(config.UnitTypeDefs),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            ScenarioId = config.Id,
            ScenarioName = config.Name,
            Turn = 1,
            MaxTurns = config.MaxTurns,
            HourOfDay = config.Clock?.StartHour ?? 6,
            Season = config.Clock?.Season ?? "夏",
            Weather = config.Weather?.Initial ?? "clear",
            WeatherMode = config.Weather?.Mode ?? config.WeatherMode,
            // 剧本只预排计划（未启动）；执行控制归导演部
            WeatherSchedule = config.Weather?.Schedule?.Select(p => new WeatherPhase
            {
                FromTurn = p.From,
                ToTurn = p.To,
                Weather = p.Weather,
            }).ToList(),
            Over = false,
            Winner = null,
            Rules = MergeRules(LibraryService.DEFAULT_RULES, config.Rules),
            Flags = config.Flags ?? new Dictionary<string, object>(),
            VictoryConds = new List<VictoryCond>(),
            Units = new List<Unit>(),
            Obs = new Dictionary<string, Dictionary<string, int>>(),
            Tracks = new Dictionary<string, Dictionary<string, Track>>(),
            AirControl = new Dictionary<string, double>(),
            Events = new List<WarEvent>(),
            Seed = seed,
            InstanceId = instanceId ?? Guid.NewGuid().ToString("N")[..12],
        };

        // Rng 是只读属性、构造时先落在 0，种子只能在初始化器之后灌进去：整局随机流都从这一个起点起算
        g.RngState = seed;

        // Parse victory conditions
        foreach (var vc in config.VictoryConds)
        {
            g.VictoryConds.Add(new VictoryCond
            {
                Kind = (vc.Kind ?? "").Trim().ToLowerInvariant(),
                Side = string.IsNullOrWhiteSpace(vc.Side) ? "red" : vc.Side.Trim().ToLowerInvariant(),
                Target = vc.Target,
                Amount = vc.Amount,
                Turns = vc.Turns,
            });
        }

        // Deploy units from sides OOB (real scenario format)
        PlaceOob(g, config);

        // Deploy units from inline unit list (simple scenario format)
        foreach (var dep in config.Units)
        {
            var unitType = g.UnitTypes.GetValueOrDefault(dep.Type) ?? LibraryService.GetUnitType(dep.Type);
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
                FuelStation = JsonBool(h, "fuel_station"),
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
                    var (commander, commanderQuality) = CommanderOf(entry);
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
                        Training = entry.Training ?? t.TrainingDefault,
                        Experience = entry.Experience ?? t.ExpDefault,
                        CommanderName = commander,
                        CommanderQuality = commanderQuality,
                        IsHQ = t.IsHQ,
                    });
                }
            }
        }
    }

    /// <summary>指挥官两种写法都要收：裸字符串只给名字，对象可另给 0–1 能力值（doc §5 人员水平）。</summary>
    private static (string? Name, double Quality) CommanderOf(OobEntry entry)
    {
        if (entry.Commander is not { } cmd) return (null, 0.5);
        if (cmd.ValueKind == JsonValueKind.String) return (cmd.GetString(), 0.5);
        if (cmd.ValueKind != JsonValueKind.Object) return (null, 0.5);
        var name = cmd.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var quality = cmd.TryGetProperty("quality", out var q) && q.TryGetDouble(out var v) ? v : 0.5;
        return (name, quality);
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

    /// <summary>
    /// 剧本校验（doc §5 两条约束：点数预算 + 引用完整性）。
    /// <paramref name="eff"/> 必须是覆盖后的有效定义库，否则改过装备性能的兵种按出厂价算，预算报红当场失真；
    /// 缺省按 <c>cfg.UnitTypeDefs</c> 现算一份。
    /// </summary>
    public static List<string> Validate(ScenarioConfig cfg, Dictionary<string, UnitTypeDef>? eff = null)
    {
        eff ??= UnitTypeDefService.EffectiveLibrary(cfg.UnitTypeDefs);
        var errs = new List<string>();
        if (cfg.Sides == null || cfg.Sides.Count == 0)
        {
            errs.Add("缺少 sides 阵营定义");
        }
        else
        {
            foreach (var (side, sc) in cfg.Sides)
            {
                if (sc.Oob.Count == 0)
                {
                    errs.Add($"阵营 {side} 的 oob 为空");
                    continue;
                }
                foreach (var e in sc.Oob)
                {
                    if (!eff.ContainsKey(e.Type)) errs.Add($"阵营 {side} 引用未知兵种 {e.Type}");
                    if (e.At == null) errs.Add($"阵营 {side} 单位 {e.Type} 缺少坐标 at");
                }
                var used = OobPoints(eff, sc.Oob);
                if (sc.PointsBudget > 0 && used > sc.PointsBudget + 0.001)
                    errs.Add($"阵营 {side} OOB 点数 {used:F1} 超出预算 {sc.PointsBudget:F1}");
            }
        }

        var initial = cfg.Weather?.Initial;
        if (initial != null && !LibraryService.WEATHER_JSON.ContainsKey(initial))
            errs.Add($"未知天气 {initial}");

        // 天气计划：区间合法、天气存在、回合不越界、区间互不重叠
        if (cfg.Weather?.Schedule is { Count: > 0 } sched)
        {
            WeatherPhaseCfg? prev = null;
            foreach (var p in sched.OrderBy(p => p.From))
            {
                if (p.From < 1 || p.To < p.From)
                    errs.Add($"天气计划时段 {p.From}-{p.To} 无效（需 1 ≤ from ≤ to）");
                else if (p.To > cfg.MaxTurns)
                    errs.Add($"天气计划时段 {p.From}-{p.To} 超出最大回合 {cfg.MaxTurns}");
                if (!LibraryService.WEATHER_JSON.ContainsKey(p.Weather))
                    errs.Add($"天气计划引用未知天气 {p.Weather}");
                if (prev != null && prev.To >= p.From)
                    errs.Add($"天气计划时段重叠：{prev.From}-{prev.To} 与 {p.From}-{p.To}");
                prev = p;
            }
        }

        foreach (var vc in cfg.VictoryConds)
        {
            if (!IsVictoryKind(vc.Kind))
                errs.Add($"未知胜利条件种类 {vc.Kind}（可用：annihilate / capture / nodes / survive）");
            if (vc.Kind == "capture" && string.IsNullOrWhiteSpace(vc.Target))
                errs.Add("capture 胜利条件缺少 target 格坐标");
        }

        return errs;
    }

    /// <summary>OOB 点数合计：未知兵种计 0（引用错误由 <see cref="Validate"/> 单独报出）。</summary>
    public static double OobPoints(IReadOnlyDictionary<string, UnitTypeDef> eff, IEnumerable<OobEntry> oob) =>
        oob.Sum(e => eff.TryGetValue(e.Type, out var t) ? t.Points * e.Qty : 0.0);

    /// <summary>
    /// 副本覆盖入口（doc §5：每步都是"覆盖/不覆盖"，不覆盖即继承剧本默认）。
    /// 合并必须发生在反序列化之前：ScenarioConfig 是 init-only，先解析再改对象改不动，
    /// 而数组（oob / victory / unit_type_defs）本就是整块替换。
    /// </summary>
    public static ScenarioConfig MergeOverrides(string scenarioJson, JsonElement? overrides)
    {
        var node = JsonNode.Parse(scenarioJson)?.AsObject()
            ?? throw new InvalidOperationException("剧本 JSON 无效");
        if (overrides is { } ov && ov.ValueKind == JsonValueKind.Object)
        {
            var overNode = JsonNode.Parse(ov.GetRawText())?.AsObject();
            if (overNode != null) DeepMergeInto(node, overNode);
        }
        return LoadFromJson(node.ToJsonString());
    }

    /// <summary>深合并语义与 Dart 版一致：覆盖里的 null 表示"不改"。</summary>
    private static void DeepMergeInto(JsonObject target, JsonObject over)
    {
        foreach (var (k, v) in over.ToList())
        {
            if (v == null) continue;
            if (v is JsonObject vo && target[k] is JsonObject to)
            {
                DeepMergeInto(to, vo);
            }
            else
            {
                target[k] = v.DeepClone();
            }
        }
    }

    /// <summary>
    /// 胜利种类白名单（doc §6.4/§8）：annihilate 歼灭比例 · capture 独占要点格 · nodes 同时掌握 N 处要点 · survive 撑满回合。
    /// 认不出的 kind 不参与结算，但必须由剧本校验报错 —— 静默降级会让胜负条件凭空变化。
    /// </summary>
    public static bool IsVictoryKind(string kind) =>
        kind is "annihilate" or "capture" or "nodes" or "survive";
}
