using System.Collections.Generic;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Standard library constants ported from Dart library.dart.
/// Provides terrain definitions, unit types, weather configs, and default rules.
/// </summary>
public static class LibraryService
{
    /// <summary>Standard terrain definitions matching Dart engine.</summary>
    public static readonly Dictionary<string, TerrainDef> STANDARD_TERRAIN = new()
    {
        ["clear"] = new TerrainDef
        {
            Id = "clear",
            Label = "平原",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 1.0, ["horse"] = 1.0, ["motor"] = 1.0,
                ["mech"] = 1.0, ["air"] = 1.0
            },
            Defense = 0.0,
            ObsPenalty = 0.0,
            LosBlock = 0.0,
            MaxFort = 3,
            Water = false,
            Impassable = false,
        },
        ["rough"] = new TerrainDef
        {
            Id = "rough",
            Label = "荒地",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 1.2, ["horse"] = 1.3, ["motor"] = 1.2,
                ["mech"] = 1.2, ["air"] = 1.0
            },
            Defense = 1.0,
            ObsPenalty = 0.1,
            LosBlock = 0.1,
            MaxFort = 2,
            Water = false,
            Impassable = false,
        },
        ["woods"] = new TerrainDef
        {
            Id = "woods",
            Label = "林地",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 1.5, ["horse"] = 1.8, ["motor"] = 1.6,
                ["mech"] = 1.5, ["air"] = 1.0
            },
            Defense = 2.0,
            ObsPenalty = 0.4,
            LosBlock = 0.5,
            MaxFort = 2,
            Water = false,
            Impassable = false,
        },
        ["hills"] = new TerrainDef
        {
            Id = "hills",
            Label = "丘陵",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 1.8, ["horse"] = 2.0, ["motor"] = 1.8,
                ["mech"] = 1.7, ["air"] = 1.0
            },
            Defense = 3.0,
            ObsPenalty = 0.2,
            LosBlock = 0.3,
            MaxFort = 3,
            Water = false,
            Impassable = false,
        },
        ["mountain"] = new TerrainDef
        {
            Id = "mountain",
            Label = "山地",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 2.5, ["horse"] = 3.0, ["motor"] = 99.0,
                ["mech"] = 99.0, ["air"] = 1.0
            },
            Defense = 4.0,
            ObsPenalty = 0.3,
            LosBlock = 0.7,
            MaxFort = 4,
            Water = false,
            Impassable = false,
        },
        ["city"] = new TerrainDef
        {
            Id = "city",
            Label = "城镇",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 1.0, ["horse"] = 1.0, ["motor"] = 1.0,
                ["mech"] = 1.0, ["air"] = 1.0
            },
            Defense = 3.0,
            ObsPenalty = 0.5,
            LosBlock = 0.6,
            MaxFort = 4,
            Water = false,
            Impassable = false,
        },
        ["swamp"] = new TerrainDef
        {
            Id = "swamp",
            Label = "沼泽",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 2.0, ["horse"] = 2.5, ["motor"] = 2.5,
                ["mech"] = 2.0, ["air"] = 1.0
            },
            Defense = 1.0,
            ObsPenalty = 0.3,
            LosBlock = 0.2,
            MaxFort = 1,
            Water = false,
            Impassable = false,
        },
        ["river"] = new TerrainDef
        {
            Id = "river",
            Label = "河流",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 99.0, ["horse"] = 99.0, ["motor"] = 99.0,
                ["mech"] = 99.0, ["air"] = 1.0
            },
            Defense = 0.0,
            ObsPenalty = 0.0,
            LosBlock = 0.0,
            MaxFort = 0,
            Water = true,
            Impassable = true,
        },
        ["water"] = new TerrainDef
        {
            Id = "water",
            Label = "水域",
            Cost = new Dictionary<string, double>
            {
                ["foot"] = 99.0, ["horse"] = 99.0, ["motor"] = 99.0,
                ["mech"] = 99.0, ["air"] = 1.0
            },
            Defense = 0.0,
            ObsPenalty = 0.0,
            LosBlock = 0.0,
            MaxFort = 0,
            Water = true,
            Impassable = true,
        },
    };

    /// <summary>Standard unit type definitions.</summary>
    public static readonly Dictionary<string, UnitTypeDef> UNIT_TYPES = new()
    {
        ["infantry"] = new UnitTypeDef
        {
            Id = "infantry",
            Label = "步兵营",
            Cls = "foot",
            Firepower = 3,
            Protection = 2,
            Mobility = 2,
            Recon = 2,
            Load = 10,
            Consumption = 1.0,
            Personnel = 800,
            IsHQ = false,
            SupplySource = false,
            Points = 1.0,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "rifle", Label = "步枪", Count = 600 },
                new() { Id = "mg", Label = "机枪", Count = 24 },
                new() { Id = "mortar", Label = "迫击炮", Count = 6 },
            },
        },
        ["armor"] = new UnitTypeDef
        {
            Id = "armor",
            Label = "装甲营",
            Cls = "horse",
            Firepower = 5,
            Protection = 4,
            Mobility = 4,
            Recon = 3,
            Load = 20,
            Consumption = 2.5,
            Personnel = 400,
            IsHQ = false,
            SupplySource = false,
            Points = 3.0,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "tank", Label = "主战坦克", Count = 40 },
                new() { Id = "ifv", Label = "步兵战车", Count = 20 },
            },
        },
        ["mechanized"] = new UnitTypeDef
        {
            Id = "mechanized",
            Label = "机械化步兵",
            Cls = "mech",
            Firepower = 4,
            Protection = 3,
            Mobility = 3,
            Recon = 3,
            Load = 15,
            Consumption = 2.0,
            Personnel = 600,
            IsHQ = false,
            SupplySource = false,
            Points = 2.5,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "apc", Label = "装甲输送车", Count = 30 },
                new() { Id = "rifle", Label = "步枪", Count = 500 },
            },
        },
        ["artillery"] = new UnitTypeDef
        {
            Id = "artillery",
            Label = "炮兵营",
            Cls = "motor",
            Firepower = 6,
            Protection = 1,
            Mobility = 2,
            Recon = 1,
            Load = 30,
            Consumption = 1.5,
            Personnel = 500,
            IsHQ = false,
            SupplySource = false,
            Points = 2.0,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "howitzer", Label = "榴弹炮", Count = 18 },
                new() { Id = "truck", Label = "卡车", Count = 40 },
            },
        },
        ["hq"] = new UnitTypeDef
        {
            Id = "hq",
            Label = "指挥部",
            Cls = "motor",
            Firepower = 1,
            Protection = 2,
            Mobility = 2,
            Recon = 4,
            Load = 50,
            Consumption = 1.0,
            Personnel = 200,
            IsHQ = true,
            SupplySource = true,
            Points = 5.0,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "radio", Label = "电台", Count = 20 },
                new() { Id = "vehicle", Label = "指挥车", Count = 10 },
            },
        },
        ["depot"] = new UnitTypeDef
        {
            Id = "depot",
            Label = "后勤仓库",
            Cls = "structure",
            Firepower = 0,
            Protection = 1,
            Mobility = 0,
            Recon = 0,
            Load = 100,
            Consumption = 0.5,
            Personnel = 100,
            IsHQ = false,
            SupplySource = true,
            Points = 2.0,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "storage", Label = "仓储设施", Count = 1 },
            },
        },
        ["radar"] = new UnitTypeDef
        {
            Id = "radar",
            Label = "雷达站",
            Cls = "structure",
            Firepower = 0,
            Protection = 2,
            Mobility = 0,
            Recon = 5,
            Load = 0,
            Consumption = 1.0,
            Personnel = 50,
            IsHQ = false,
            SupplySource = false,
            Points = 3.0,
            Equipment = new List<EquipmentItem>
            {
                new() { Id = "radar_dish", Label = "雷达天线", Count = 1 },
            },
        },
    };

    /// <summary>Weather definitions.</summary>
    public static readonly Dictionary<string, WeatherDef> WEATHER_JSON = new()
    {
        ["clear"] = new WeatherDef
        {
            Id = "clear",
            Label = "晴朗",
            Mobility = 1.0,
            Recon = 1.0,
            Fire = 1.0,
        },
        ["cloudy"] = new WeatherDef
        {
            Id = "cloudy",
            Label = "多云",
            Mobility = 1.0,
            Recon = 0.9,
            Fire = 0.95,
        },
        ["rain"] = new WeatherDef
        {
            Id = "rain",
            Label = "小雨",
            Mobility = 0.85,
            Recon = 0.8,
            Fire = 0.85,
        },
        ["storm"] = new WeatherDef
        {
            Id = "storm",
            Label = "暴雨",
            Mobility = 0.7,
            Recon = 0.6,
            Fire = 0.7,
        },
        ["fog"] = new WeatherDef
        {
            Id = "fog",
            Label = "大雾",
            Mobility = 0.9,
            Recon = 0.5,
            Fire = 0.8,
        },
        ["snow"] = new WeatherDef
        {
            Id = "snow",
            Label = "大雪",
            Mobility = 0.6,
            Recon = 0.7,
            Fire = 0.75,
        },
    };

    /// <summary>Default rules configuration.</summary>
    public static readonly Rules DEFAULT_RULES = new Rules
    {
        MaxTurns = 14,
        MpBase = 1.0,
        SupplyRetention = 0.95,
        FatigueMarch = 1.0,
        FatigueCombat = 1.0,
        MoraleLossRouted = 20.0,
    };

    /// <summary>Get terrain definition by ID.</summary>
    public static TerrainDef GetTerrain(string id)
    {
        return STANDARD_TERRAIN.GetValueOrDefault(id) ?? STANDARD_TERRAIN["clear"];
    }

    /// <summary>Get unit type definition by ID.</summary>
    public static UnitTypeDef GetUnitType(string id)
    {
        return UNIT_TYPES.GetValueOrDefault(id) ?? UNIT_TYPES["infantry"];
    }

    /// <summary>Get weather definition by ID.</summary>
    public static WeatherDef GetWeather(string id)
    {
        return WEATHER_JSON.GetValueOrDefault(id) ?? WEATHER_JSON["clear"];
    }
}
