using System;
using System.Collections.Generic;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Standard library constants ported 1:1 from Dart library.dart
/// (STANDARD_TERRAIN_JSON / STANDARD_UNIT_TYPES_JSON / STANDARD_WEATHER_JSON).
/// Scenarios and instances may layer overrides on top of these definitions.
/// </summary>
public static class LibraryService
{
    /// <summary>Mobility classes used for terrain cost lookups (Dart MOBILITY_CLASSES).</summary>
    public static readonly string[] MobilityClasses = { "foot", "horse", "motor", "mech" };

    private static Dictionary<string, double> Cost(double foot, double horse, double motor, double mech) => new()
    {
        ["foot"] = foot, ["horse"] = horse, ["motor"] = motor, ["mech"] = mech,
    };

    /// <summary>Standard terrain definitions matching Dart engine.</summary>
    public static readonly Dictionary<string, TerrainDef> STANDARD_TERRAIN = new()
    {
        ["clear"] = new TerrainDef
        {
            Id = "clear", Label = "开阔地",
            Cost = Cost(1.0, 0.8, 1.4, 1.2),
            Defense = 1.0, ObsPenalty = 1.0, LosBlock = 0.0, MaxFort = 2,
        },
        ["rough"] = new TerrainDef
        {
            Id = "rough", Label = "崎岖地",
            Cost = Cost(1.3, 1.6, 2.4, 1.8),
            Defense = 1.15, ObsPenalty = 0.85, LosBlock = 0.25, MaxFort = 3,
        },
        ["woods"] = new TerrainDef
        {
            Id = "woods", Label = "林地",
            Cost = Cost(1.5, 2.0, 2.6, 2.2),
            Defense = 1.35, ObsPenalty = 0.55, LosBlock = 0.7, MaxFort = 3,
        },
        ["hills"] = new TerrainDef
        {
            Id = "hills", Label = "丘陵",
            Cost = Cost(1.6, 1.8, 2.8, 2.2),
            Defense = 1.3, ObsPenalty = 0.9, LosBlock = 0.35, MaxFort = 3,
        },
        ["mountain"] = new TerrainDef
        {
            Id = "mountain", Label = "山地",
            Cost = Cost(2.2, 2.6, 4.0, 3.2),
            Defense = 1.55, ObsPenalty = 0.8, LosBlock = 0.75, MaxFort = 4,
        },
        ["city"] = new TerrainDef
        {
            Id = "city", Label = "城镇",
            Cost = Cost(1.4, 1.8, 2.0, 1.9),
            Defense = 1.7, ObsPenalty = 0.7, LosBlock = 0.55, MaxFort = 4,
        },
        ["swamp"] = new TerrainDef
        {
            Id = "swamp", Label = "沼泽",
            Cost = Cost(2.0, 2.6, 3.6, 3.0),
            Defense = 0.9, ObsPenalty = 0.75, LosBlock = 0.4, MaxFort = 1,
        },
        ["river"] = new TerrainDef
        {
            Id = "river", Label = "河流",
            Cost = Cost(2.6, 3.0, 5.0, 4.2),
            Defense = 0.8, ObsPenalty = 1.0, LosBlock = 0.0, MaxFort = 0, Water = true,
        },
        ["water"] = new TerrainDef
        {
            Id = "water", Label = "江湖",
            Cost = Cost(9.0, 9.0, 9.0, 9.0),
            Defense = 0.5, ObsPenalty = 1.0, LosBlock = 0.0, MaxFort = 0,
            Water = true, Impassable = true,
        },
        ["void"] = new TerrainDef
        {
            Id = "void", Label = "境外",
            Cost = Cost(1.0, 1.0, 1.0, 1.0),
            Defense = 1.0, ObsPenalty = 1.0, LosBlock = 0.0, MaxFort = 0,
            Impassable = true,
        },
    };

    /// <summary>Standard unit types. Points = budget price per table-of-organization element.</summary>
    public static readonly Dictionary<string, UnitTypeDef> UNIT_TYPES = new()
    {
        ["rifle"] = new UnitTypeDef
        {
            Id = "rifle", Label = "步兵", Cls = "foot",
            Mobility = 5, Firepower = 10, Protection = 1.0, Recon = 2, Load = 1.0,
            Consumption = 1.0, Personnel = 600, Points = 10,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "rifle", Label = "步枪", Count = 520 },
                new() { Id = "mg", Label = "机枪", Count = 18 },
                new() { Id = "mortar", Label = "迫击炮", Count = 6 },
            },
        },
        ["light_inf"] = new UnitTypeDef
        {
            Id = "light_inf", Label = "轻步兵", Cls = "foot",
            Mobility = 6, Firepower = 8, Protection = 0.85, Recon = 3, Load = 0.7,
            Consumption = 0.8, Personnel = 450, Points = 8,
            Counters = new Dictionary<string, double> { ["armor"] = 0.6 },
        },
        ["rifle_auto"] = new UnitTypeDef
        {
            Id = "rifle_auto", Label = "汽车步兵", Cls = "motor",
            Mobility = 9, Firepower = 11, Protection = 0.9, Recon = 2, Load = 1.4,
            Consumption = 1.6, Personnel = 650, Points = 13,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "truck", Label = "载重卡车", Count = 60 },
            },
        },
        ["mec_inf"] = new UnitTypeDef
        {
            Id = "mec_inf", Label = "机械化步兵", Cls = "mech",
            Mobility = 10, Firepower = 14, Protection = 1.35, Recon = 2.5, Load = 1.8,
            Consumption = 2.0, Personnel = 600, Points = 19,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "apc", Label = "装甲输送车", Count = 70 },
            },
        },
        ["armor"] = new UnitTypeDef
        {
            Id = "armor", Label = "装甲侦察", Cls = "mech",
            Mobility = 12, Firepower = 9, Protection = 1.2, Recon = 5, Load = 1.5,
            Consumption = 2.2, Personnel = 260, Points = 14,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "armcar", Label = "装甲侦察车", Count = 30 },
            },
        },
        ["tank"] = new UnitTypeDef
        {
            Id = "tank", Label = "坦克", Cls = "mech",
            Mobility = 11, Firepower = 24, Protection = 2.4, Recon = 3, Load = 2.6,
            Consumption = 3.0, Personnel = 220, Points = 26,
            Counters = new Dictionary<string, double>
            {
                ["rifle"] = 2.2, ["light_inf"] = 2.0, ["tank"] = 0.9,
            },
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "tank", Label = "主战坦克", Count = 22 },
            },
        },
        ["gun"] = new UnitTypeDef
        {
            Id = "gun", Label = "反坦克炮", Cls = "motor",
            Mobility = 7, Firepower = 18, Protection = 0.9, Recon = 2, Load = 1.9,
            Consumption = 1.6, Personnel = 180, Points = 13,
            Counters = new Dictionary<string, double> { ["tank"] = 2.0, ["armor"] = 1.8 },
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "atgun", Label = "反坦克炮", Count = 12 },
            },
        },
        ["arty"] = new UnitTypeDef
        {
            Id = "arty", Label = "炮兵", Cls = "motor",
            Mobility = 6, Firepower = 30, Protection = 0.7, Recon = 1.5, Load = 2.8,
            Consumption = 2.4, Personnel = 240, Points = 20, Range = 3,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "howitzer", Label = "榴弹炮", Count = 12 },
            },
        },
        ["engineer"] = new UnitTypeDef
        {
            Id = "engineer", Label = "工兵", Cls = "foot",
            Mobility = 4, Firepower = 6, Protection = 1.0, Recon = 2, Load = 1.6,
            Consumption = 1.0, Personnel = 200, Points = 9,
        },
        ["signals"] = new UnitTypeDef
        {
            Id = "signals", Label = "通讯兵", Cls = "motor",
            Mobility = 8, Firepower = 2, Protection = 0.6, Recon = 4, Load = 1.2,
            Consumption = 1.0, Personnel = 90, Points = 7,
        },
        ["aa"] = new UnitTypeDef
        {
            Id = "aa", Label = "防空", Cls = "motor",
            Mobility = 8, Firepower = 8, Protection = 0.8, Recon = 3, Load = 2.0,
            Consumption = 1.8, Personnel = 150, Points = 12, AntiAir = 3.0,
        },
        ["recy"] = new UnitTypeDef
        {
            Id = "recy", Label = "辎重", Cls = "motor",
            Mobility = 6, Firepower = 1, Protection = 0.5, Recon = 1, Load = 3.2,
            Consumption = 0.4, Personnel = 120, Points = 8, SupplySource = true,
        },
        ["hq"] = new UnitTypeDef
        {
            Id = "hq", Label = "指挥所", Cls = "foot",
            Mobility = 4, Firepower = 3, Protection = 0.8, Recon = 2, Load = 1.0,
            Consumption = 0.6, Personnel = 80, Points = 6, IsHQ = true,
        },
        ["radar"] = new UnitTypeDef
        {
            Id = "radar", Label = "雷达站", Cls = "structure",
            Mobility = 0, Firepower = 0, Protection = 0.4, Recon = 9, Load = 1.0,
            Consumption = 0.5, Personnel = 40, Points = 9,
        },
        ["airbase"] = new UnitTypeDef
        {
            Id = "airbase", Label = "机场", Cls = "structure",
            Mobility = 0, Firepower = 2, Protection = 0.6, Recon = 3, Load = 1.0,
            Consumption = 1.0, Personnel = 150, Points = 10,
        },
        ["fighter"] = new UnitTypeDef
        {
            Id = "fighter", Label = "战斗机", Cls = "air",
            Mobility = 30, Firepower = 16, Protection = 0.8, Recon = 8, Load = 1.0,
            Consumption = 2.0, Personnel = 4, Points = 22,
            AirRole = "fighter", AirRange = 10,
        },
        ["strike_air"] = new UnitTypeDef
        {
            Id = "strike_air", Label = "强击机", Cls = "air",
            Mobility = 26, Firepower = 26, Protection = 0.7, Recon = 5, Load = 1.2,
            Consumption = 2.4, Personnel = 4, Points = 24,
            AirRole = "strike", AirRange = 8,
        },
        ["recon_air"] = new UnitTypeDef
        {
            Id = "recon_air", Label = "侦察机", Cls = "air",
            Mobility = 28, Firepower = 1, Protection = 0.5, Recon = 14, Load = 0.8,
            Consumption = 1.6, Personnel = 3, Points = 16,
            AirRole = "recon", AirRange = 12,
        },
    };

    /// <summary>Weather definitions (Dart STANDARD_WEATHER_JSON).</summary>
    public static readonly Dictionary<string, WeatherDef> WEATHER_JSON = new()
    {
        ["clear"] = new WeatherDef { Id = "clear", Label = "晴", Mobility = 1.0, Recon = 1.05, Combat = 1.0, Attrition = 1.0, Supply = 1.0, Visibility = 12 },
        ["overcast"] = new WeatherDef { Id = "overcast", Label = "多云", Mobility = 1.0, Recon = 1.0, Combat = 1.0, Attrition = 1.0, Supply = 1.0, Visibility = 10 },
        ["rain"] = new WeatherDef { Id = "rain", Label = "降雨", Mobility = 0.85, Recon = 0.8, Combat = 0.92, Attrition = 1.15, Supply = 0.85, Visibility = 6 },
        ["storm"] = new WeatherDef { Id = "storm", Label = "暴雨", Mobility = 0.65, Recon = 0.6, Combat = 0.8, Attrition = 1.4, Supply = 0.6, Visibility = 3 },
        ["snow"] = new WeatherDef { Id = "snow", Label = "降雪", Mobility = 0.6, Recon = 0.75, Combat = 0.85, Attrition = 1.6, Supply = 0.7, Visibility = 4 },
        ["fog"] = new WeatherDef { Id = "fog", Label = "浓雾", Mobility = 0.9, Recon = 0.35, Combat = 0.75, Attrition = 1.0, Supply = 0.95, Visibility = 1 },
        ["sandstorm"] = new WeatherDef { Id = "sandstorm", Label = "沙尘", Mobility = 0.8, Recon = 0.5, Combat = 0.9, Attrition = 1.2, Supply = 0.9, Visibility = 2 },
    };

    /// <summary>
    /// 出厂规则：数值全部落在 Rules/CombatParams 的初始化器上，
    /// 剧本 rules / rules.combat 块按同名键覆盖（§7 调参不改码）。
    /// </summary>
    public static readonly Rules DEFAULT_RULES = new Rules();

    /// <summary>
    /// 天气马尔科夫链：当前天气 → [后继天气, 概率]，每行概率合计 1。
    /// 键必须与 WEATHER_JSON 同源，历史上这里写过库里没有的 cloudy，导致演变静默降级成 clear。
    /// </summary>
    public static readonly Dictionary<string, (string Id, double P)[]> WEATHER_CHAIN = new()
    {
        ["clear"] = new[] { ("clear", 0.62), ("overcast", 0.28), ("rain", 0.07), ("fog", 0.03) },
        ["overcast"] = new[] { ("overcast", 0.5), ("clear", 0.22), ("rain", 0.2), ("fog", 0.05), ("storm", 0.03) },
        ["rain"] = new[] { ("rain", 0.4), ("overcast", 0.3), ("storm", 0.15), ("clear", 0.15) },
        ["storm"] = new[] { ("storm", 0.3), ("rain", 0.45), ("overcast", 0.25) },
        ["snow"] = new[] { ("snow", 0.6), ("overcast", 0.3), ("clear", 0.1) },
        ["fog"] = new[] { ("fog", 0.35), ("overcast", 0.35), ("clear", 0.3) },
        ["sandstorm"] = new[] { ("sandstorm", 0.35), ("clear", 0.45), ("overcast", 0.2) },
    };

    /// <summary>Get terrain definition by ID (Dart fallback: void).</summary>
    public static TerrainDef GetTerrain(string id)
    {
        return STANDARD_TERRAIN.GetValueOrDefault(id) ?? STANDARD_TERRAIN["void"];
    }

    /// <summary>Get unit type definition by ID (Dart fallback: rifle).</summary>
    public static UnitTypeDef GetUnitType(string id)
    {
        return UNIT_TYPES.GetValueOrDefault(id) ?? UNIT_TYPES["rifle"];
    }

    /// <summary>Get weather definition by ID (Dart fallback: clear).</summary>
    public static WeatherDef GetWeather(string id)
    {
        return WEATHER_JSON.GetValueOrDefault(id) ?? WEATHER_JSON["clear"];
    }
}
