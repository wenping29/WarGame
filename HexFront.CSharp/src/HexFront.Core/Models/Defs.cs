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
    public double Firepower { get; init; }
    public double Protection { get; init; }
    public double Mobility { get; init; }
    public double Recon { get; init; }
    public double Load { get; init; }
    public double Consumption { get; init; }
    public int Personnel { get; init; }
    public bool IsHQ { get; init; }
    public bool SupplySource { get; init; }
    public bool IsAir => Cls == "air";
    public bool IsStructure => Cls == "structure";
    public string AirRole { get; init; } = "none"; // none/fighter/strike/recon
    public int AirRange { get; init; }          // air unit operational radius (hexes)
    public double AntiAir { get; init; }         // anti-air fire strength
    public int Range { get; init; }              // indirect-fire range (hexes), 0 = direct
    public double TrainingDefault { get; init; } = 60;
    public double ExpDefault { get; init; } = 40;
    public Dictionary<string, double> Counters { get; init; } = new(); // target type -> multiplier
    public double Points { get; init; }
    public List<EquipmentItem> Equipment { get; init; } = new();

    /// <summary>负荷惩罚：装备越重越慢（Dart mobilityPenalty）。</summary>
    public double MobilityPenalty() => Math.Clamp(1.0 - 0.06 * (Load - 1.0), 0.6, 1.0);
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
    public double Combat { get; init; } = 1.0;
    public double Attrition { get; init; } = 1.0;
    public double Supply { get; init; } = 1.0;
    public int Visibility { get; init; } = 10;
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

    // Fog / observation parameters (Dart Rules defaults)
    public double LosElevStep { get; init; } = 60.0;
    public int ExposureE1 { get; init; } = 1;
    public int ExposureE2 { get; init; } = 3;
    public int ExposureE3 { get; init; } = 6;
    public int StaleTurns { get; init; } = 4;
}

// ─────────────── Day Phase ───────────────

/// <summary>Daylight phase modifiers. Ported from defs.dart DayPhase.</summary>
public sealed class DayPhase
{
    public string Id { get; }
    public string Label { get; }
    public double Recon { get; }
    public double Combat { get; }

    public DayPhase(string id, string label, double recon, double combat)
    {
        Id = id; Label = label; Recon = recon; Combat = combat;
    }

    public static DayPhase FromHours(int hourOfDay)
    {
        var h = ((hourOfDay % 24) + 24) % 24;
        if (h >= 6 && h < 9) return new DayPhase("dawn", "拂晓", 0.8, 0.95);
        if (h >= 9 && h < 17) return new DayPhase("day", "昼间", 1.0, 1.0);
        if (h >= 17 && h < 20) return new DayPhase("dusk", "黄昏", 0.8, 0.95);
        return new DayPhase("night", "夜间", 0.45, 0.75);
    }
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
