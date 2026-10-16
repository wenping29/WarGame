using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexFront.Core.Models;

// ─────────────── Terrain ───────────────

public sealed class TerrainDef
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public Dictionary<string, double> Cost { get; init; } = new();
    public double Defense { get; init; }
    public double ObsPenalty { get; init; }
    public double LosBlock { get; init; }
    public int MaxFort { get; init; }
    public bool Water { get; init; }
    public bool Impassable { get; init; }
}

// ─────────────── Unit Type ───────────────

public sealed class UnitTypeDef
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Cls { get; init; } = ""; // foot/horse/motor/mech/air/structure
    public int Firepower { get; init; }
    public int Protection { get; init; }
    public int Mobility { get; init; }
    public int Recon { get; init; }
    public int Load { get; init; }
    public double Consumption { get; init; }
    public int Personnel { get; init; }
    public bool IsHQ { get; init; }
    public bool SupplySource { get; init; }
    public bool IsAir => Cls == "air";
    public string AirRole { get; init; } = ""; // fighter/strike/transport (for air units)
    public double Points { get; init; }
    public List<EquipmentItem> Equipment { get; init; } = new();
    
    /// <summary>Mobility penalty based on terrain and status.</summary>
    public double MobilityPenalty() => 1.0; // Simplified, can be expanded
}

public sealed class EquipmentItem
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public int Count { get; init; }
}

// ─────────────── Weather ───────────────

public sealed class WeatherDef
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public double Mobility { get; init; } = 1.0;
    public double Recon { get; init; } = 1.0;
    public double Fire { get; init; } = 1.0;
}

// ─────────────── Rules ───────────────

public sealed class Rules
{
    public int MaxTurns { get; init; } = 14;
    public double MpBase { get; init; } = 1.0;
    public double SupplyRetention { get; init; } = 0.95;
    public double FatigueMarch { get; init; } = 1.0;
    public double FatigueCombat { get; init; } = 1.0;
    public double MoraleLossRouted { get; init; } = 20.0;
    public double FoodPerTurn { get; init; } = 0.05;
    public double SupplyCrit { get; init; } = 0.3;
    public int TurnHours { get; init; } = 4;
}

// ─────────────── Cell (hex terrain + features) ───────────────

public sealed class Cell
{
    public string Terrain { get; init; } = "clear";
    public double Elev { get; init; }
    public double Slope { get; init; }
    public bool Road { get; init; }
    public bool River { get; init; }
    public bool Bridge { get; init; }
    public int City { get; init; }          // 0..3
    public bool Railway { get; init; }
    public bool RailStation { get; init; }
    public bool Tunnel { get; init; }
    public bool Powerline { get; init; }
    public bool PowerPlant { get; init; }
    public bool Substation { get; init; }
    public int Airport { get; init; }       // 0=none, 1=field, 2=major

    public Cell With(string? terrain = null, bool? bridge = null, int? city = null, double? elev = null) =>
        new()
        {
            Terrain = terrain ?? Terrain,
            Elev = elev ?? Elev,
            Slope = Slope,
            Road = Road,
            River = River,
            Bridge = bridge ?? Bridge,
            City = city ?? City,
            Railway = Railway,
            RailStation = RailStation,
            Tunnel = Tunnel,
            Powerline = Powerline,
            PowerPlant = PowerPlant,
            Substation = Substation,
            Airport = Airport,
        };
}
