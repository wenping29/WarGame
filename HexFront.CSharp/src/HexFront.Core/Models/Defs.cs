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
    public int MaxTurns { get; init; } = 20;
    public double MpBase { get; init; } = 1.0;
    public double SupplyRetention { get; init; } = 0.85;
    public double FatigueMarch { get; init; } = 2.0;
    public double FatigueCombat { get; init; } = 1.0;
    public double MoraleLossRouted { get; init; } = 20.0;
    public double FoodPerTurn { get; init; } = 0.05;
    public double SupplyCrit { get; init; } = 0.4;
    public int TurnHours { get; init; } = 6;

    // Fog / observation parameters (Dart Rules defaults)
    public double LosElevStep { get; init; } = 60.0;
    public int ExposureE1 { get; init; } = 1;
    public int ExposureE2 { get; init; } = 3;
    public int ExposureE3 { get; init; } = 6;
    public int StaleTurns { get; init; } = 4;

    /// <summary>战力链全部系数：剧本 rules.combat 块可逐个覆盖，调参不改码（§7）。</summary>
    public CombatParams Combat { get; init; } = new();
}

/// <summary>
/// CF 因子乘积链与交战裁决的全部系数（§7「战力影响链」、doc M6「战报可解释各系数贡献」）。
/// 刻意做成数据而不是常量：战斗手感要能只用剧本 rules.combat 就调得动。
/// </summary>
public sealed class CombatParams
{
    public double AmmoPerAttack { get; init; } = 0.12;
    public double RoutMorale { get; init; } = 25;
    public double RoutRecover { get; init; } = 45;
    public double FortBonus { get; init; } = 0.15;
    public double AirGroundPenalty { get; init; } = 0.75;
    public double CommanderRadius { get; init; } = 8;
    public double NoiseAmp { get; init; } = 0.15;
    public double AmmoMinCf { get; init; } = 0.3;

    // K_supply：宽松档只削到 0.6，strict_supply 打开才掉到 0.25
    public double SupplyCfBase { get; init; } = 0.6;
    public double SupplyCfSpan { get; init; } = 0.4;
    public double SupplyCfBaseStrict { get; init; } = 0.25;
    public double SupplyCfSpanStrict { get; init; } = 0.75;

    // K_morale
    public double MoraleCfBase { get; init; } = 0.5;
    public double MoraleCfSpan { get; init; } = 0.7;
    public double MoraleCfMin { get; init; } = 0.4;
    public double MoraleCfMax { get; init; } = 1.2;

    // K_personnel
    public double PersonnelCfBase { get; init; } = 0.75;
    public double PersonnelCfTraining { get; init; } = 0.0025;
    public double PersonnelCfExperience { get; init; } = 0.0015;

    // K_commander
    public double CommanderGainBase { get; init; } = 0.9;
    public double CommanderGainSpan { get; init; } = 0.25;
    public double CommanderLostline { get; init; } = 0.8;
    public double CommanderQualityBase { get; init; } = 0.6;
    public double CommanderQualitySpan { get; init; } = 0.8;
    public double CommanderHqSelfBase { get; init; } = 0.5;
    public double CommanderHqSelfSpan { get; init; } = 0.5;
    public double CommanderBudgetMin { get; init; } = 0.6;
    public double CommanderBudgetMax { get; init; } = 1.15;

    // K_air：暴露度 = 1 - 地形防御·0.25 - 工事·0.12，惩罚封底 0.4
    public double AirExposureTerrain { get; init; } = 0.25;
    public double AirExposureFort { get; init; } = 0.12;
    public double AirExposureMin { get; init; } = 0.15;
    public double AirExposureMax { get; init; } = 1.0;
    public double AirPenaltyFloor { get; init; } = 0.4;
    public double AirControlDeadband { get; init; } = 0.0;

    // 交战损失：守方按 CF 比放大、攻方按 CF 比倒数缩小，各自封顶
    public double DefLossCoef { get; init; } = 0.055;
    public double DefLossCeil { get; init; } = 0.34;
    public double AtkLossCoef { get; init; } = 0.03;
    public double AtkLossCeil { get; init; } = 0.2;
    public double LossMinFrac { get; init; } = 0.005;
    public double MinCf { get; init; } = 0.01;

    // 守方士气跌落：基础 + 损失份额斜率 − 工事安慰项
    public double MoraleHitBase { get; init; } = 6;
    public double MoraleHitSpan { get; init; } = 18;
    public double MoraleHitLossScale { get; init; } = 4;
    public double MoraleHitFortPerLevel { get; init; } = 4;
    public double MoraleHitMin { get; init; } = 2;
    public double MoraleHitMax { get; init; } = 40;
    public double AtkMoraleLoss { get; init; } = 3;
    public double AtkMoraleGain { get; init; } = 2;
    public double AtkMoraleGainRatio { get; init; } = 2;

    // 季节消耗：冬 / 秋雨加重战损
    public double SeasonWinterAttrition { get; init; } = 1.25;
    public double SeasonAutumnAttrition { get; init; } = 1.08;
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
