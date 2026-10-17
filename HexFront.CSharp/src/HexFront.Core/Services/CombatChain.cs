using System;
using System.Collections.Generic;
using System.Globalization;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// 战力影响链（doc §7）：CF = 各因子乘积，战报要能解释「为什么打不动」，
/// 所以因子逐个留名而不是只回传一个乘积。
/// </summary>
public sealed class CombatFactors
{
    public double Base { get; init; }
    public double Terrain { get; init; }
    public double Fort { get; init; }
    public double Strength { get; init; }
    public double Supply { get; init; }
    public double Morale { get; init; }
    public double Personnel { get; init; }
    public double Commander { get; init; }
    public double Weather { get; init; }
    public double Air { get; init; }
    public double Counter { get; init; }
    public double Ammo { get; init; }

    public double Product => Base * Terrain * Fort * Strength * Supply * Morale
                           * Personnel * Commander * Weather * Air * Counter * Ammo;

    /// <summary>线上形状：与因子链同序，前端战报直接按这个顺序渲染贡献值。</summary>
    public Dictionary<string, object?> ToMap()
    {
        var m = new Dictionary<string, object?>
        {
            ["base"] = R(Base),
            ["terrain"] = R(Terrain),
            ["fort"] = R(Fort),
            ["strength"] = R(Strength),
            ["supply"] = R(Supply),
            ["morale"] = R(Morale),
            ["personnel"] = R(Personnel),
            ["commander"] = R(Commander),
            ["weather"] = R(Weather),
            ["air"] = R(Air),
            ["counter"] = R(Counter),
            ["ammo"] = R(Ammo),
            ["cf"] = R(Product),
        };
        return m;
    }

    private static double R(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}

/// <summary>一次交战裁决的结果（含双方因子，供战报解释）。</summary>
public sealed class ClashOutcome
{
    public int AtkLoss { get; init; }
    public int DefLoss { get; init; }
    public double AtkCf { get; init; }
    public double DefCf { get; init; }
    public double DefLossFrac { get; init; }
    public double AtkLossFrac { get; init; }
    public double DefMoraleHit { get; init; }
    public double DefMoraleAfter { get; init; }
    public double AtkMoraleAfter { get; init; }
    public bool DefRouted { get; init; }
    public bool Captured { get; init; }
    public CombatFactors FactorsAtk { get; init; } = new();
    public CombatFactors FactorsDef { get; init; } = new();
}

/// <summary>
/// CF 因子链与交战裁决。所有系数取自 <see cref="CombatParams"/>（剧本 rules.combat 可覆盖），
/// 随机数只从 g.Rng 抽：每场交战固定 2 抽（守方噪声、攻方噪声），顺序不能变，否则续跑分叉。
/// </summary>
public static class CombatChain
{
    private const double Epsilon = 1e-9;

    /// <summary>K_supply：补给率映射到战力。宽松档最多削到 0.6，strict_supply 打开才掉到 0.25。</summary>
    public static double SupplyToCF(CombatParams cf, double rate, bool strict)
    {
        var base_ = strict ? cf.SupplyCfBaseStrict : cf.SupplyCfBase;
        var span = strict ? cf.SupplyCfSpanStrict : cf.SupplyCfSpan;
        return Clamp(base_ + span * rate, base_, 1.0);
    }

    /// <summary>K_personnel：训练度与经验进战力。</summary>
    public static double PersonnelFactor(CombatParams cf, Unit u) =>
        cf.PersonnelCfBase + cf.PersonnelCfTraining * u.Training + cf.PersonnelCfExperience * u.Experience;

    /// <summary>
    /// K_commander：本方指挥所在半径内按距离与指挥官素质取最优；
    /// 指挥所自身保底，全失联则整链打折（失联是惩罚，不是零战力）。
    /// </summary>
    public static double CommanderFactor(GameState g, Unit u)
    {
        var cf = g.Rules.Combat;
        var radius = (int)Math.Round(cf.CommanderRadius);
        var best = 0.0;

        foreach (var hq in g.Units)
        {
            if (!hq.Alive || hq.Side != u.Side || !hq.IsHQ) continue;
            var d = hq.Pos.DistanceTo(u.Pos);
            if (d > radius) continue;
            // 必须按浮点除：radius 是 int，整除会把距离梯度抹平成 1.0，越近越强的设计就没了
            var v = (1.0 - d / (double)(radius + 1)) *
                    (cf.CommanderQualityBase + cf.CommanderQualitySpan * hq.CommanderQuality);
            if (v > best) best = v;
        }

        if (u.IsHQ)
            best = Math.Max(best, cf.CommanderHqSelfBase + cf.CommanderHqSelfSpan * u.CommanderQuality);

        if (best <= Epsilon) return cf.CommanderLostline;
        return cf.CommanderGainBase + cf.CommanderGainSpan * best;
    }

    /// <summary>
    /// K_air：敌方掌握本格制空时按暴露度罚地面战力，开阔无工事最重，罚到 floor 封底。
    /// flags.air_superiority 关掉即整链免疫（导演部可锁定空权条件）。
    /// </summary>
    public static double AirPenalty(GameState g, Unit u)
    {
        var cf = g.Rules.Combat;
        if (!FogService.FlagBool(g, "air_superiority", true)) return 1.0;

        var ctl = g.AirControl.GetValueOrDefault(u.Pos.Key, 0.0);
        var enemyHolds = (ctl > cf.AirControlDeadband && u.Side == "blue") ||
                         (ctl < -cf.AirControlDeadband && u.Side == "red");
        if (Math.Abs(ctl) <= cf.AirControlDeadband || !enemyHolds) return 1.0;

        var td = g.TerrainOf(u.Pos);
        var exposure = Clamp(1.0 - td.Defense * cf.AirExposureTerrain - u.FortLevel * cf.AirExposureFort,
            cf.AirExposureMin, cf.AirExposureMax);
        return Clamp(1.0 - (1.0 - cf.AirGroundPenalty) * exposure, cf.AirPenaltyFloor, 1.0);
    }

    /// <summary>攻方对守方的 CF：守方地形/工事进链，攻方补给/士气/弹药进链。</summary>
    public static CombatFactors ComputeCf(GameState g, Unit att, Unit def, bool strictSupply)
    {
        var cf = g.Rules.Combat;
        var attType = g.TypeOf(att);
        var defType = g.TypeOf(def);
        var td = g.TerrainOf(def.Pos);
        var wx = g.WeatherOf();
        var day = DayPhase.FromHours(g.HourOfDay);

        var ammo = Clamp(att.Ammo <= 0
            ? cf.AmmoMinCf
            : 0.5 + 0.5 * att.Ammo, cf.AmmoMinCf, 1.0);

        return new CombatFactors
        {
            Base = attType.Firepower,
            Terrain = 1.0 / Math.Max(Epsilon, td.Defense),
            Fort = 1.0 / (1.0 + cf.FortBonus * def.FortLevel),
            Strength = att.Personnel / Math.Max(1.0, attType.Personnel),
            Supply = SupplyToCF(cf, att.SupplyRate, strictSupply),
            Morale = Clamp(cf.MoraleCfBase + att.Morale / 100 * cf.MoraleCfSpan, cf.MoraleCfMin, cf.MoraleCfMax),
            Personnel = PersonnelFactor(cf, att),
            Commander = CommanderFactor(g, att),
            Weather = wx.Combat * day.Combat,
            Air = AirPenalty(g, att),
            Counter = attType.Counters.GetValueOrDefault(defType.Id, 1.0),
            Ammo = ammo,
        };
    }

    /// <summary>
    /// 交战裁决：CF 比 → 双方损失份额 + 士气跌落 + 可能的溃退/易手。
    /// 本方法只算不改状态（士气结果也留在返回值里），落状态由 TurnResolver 统一做，
    /// 这样遭遇战/突击/火力准备三条分支共用同一份裁决而不会各写一套。
    /// </summary>
    public static ClashOutcome ResolveClash(GameState g, Unit att, Unit def, bool strictSupply)
    {
        var cf = g.Rules.Combat;
        var fa = ComputeCf(g, att, def, strictSupply);
        var fd = ComputeCf(g, def, att, strictSupply);
        var atkCf = fa.Product;
        var defCf = Math.Max(cf.MinCf, fd.Product);
        var ratio = atkCf / defCf;

        var wx = g.WeatherOf();
        var day = DayPhase.FromHours(g.HourOfDay);
        var seasonK = MechanicsService.SeasonAttrition(g);

        // 两抽顺序即协议：先守方噪声、后攻方噪声，重放必须复现同一串
        var noiseA = g.Rng.Jitter(cf.NoiseAmp);
        var noiseD = g.Rng.Jitter(cf.NoiseAmp);

        var defLossFrac = Clamp(cf.DefLossCoef * ratio * noiseA * wx.Attrition * day.Combat * seasonK,
            0.0, cf.DefLossCeil);
        var atkLossFrac = Clamp(cf.AtkLossCoef / ratio * noiseD * wx.Attrition * seasonK, 0.0, cf.AtkLossCeil);

        var defLoss = Math.Max(defLossFrac >= cf.LossMinFrac ? 1 : 0, (int)Math.Round(defLossFrac * def.Personnel));
        var atkLoss = Math.Max(atkLossFrac >= cf.LossMinFrac ? 1 : 0, (int)Math.Round(atkLossFrac * att.Personnel));

        // 守方士气跌落（工事是安慰项），攻方按吃亏/占便宜微调
        var moraleHit = Clamp(cf.MoraleHitBase + cf.MoraleHitSpan * (defLossFrac * cf.MoraleHitLossScale)
            - cf.MoraleHitFortPerLevel * def.FortLevel, cf.MoraleHitMin, cf.MoraleHitMax);
        var defMoraleAfter = Clamp(def.Morale - moraleHit, 0, 100);
        var atkMoraleAfter = Clamp(att.Morale
            - (atkLoss > 0 ? cf.AtkMoraleLoss : 0)
            + (defLoss > atkLoss * cf.AtkMoraleGainRatio ? cf.AtkMoraleGain : 0), 0, 100);

        var routed = defMoraleAfter < cf.RoutMorale;

        return new ClashOutcome
        {
            AtkLoss = atkLoss,
            DefLoss = defLoss,
            AtkCf = atkCf,
            DefCf = defCf,
            DefLossFrac = defLossFrac,
            AtkLossFrac = atkLossFrac,
            DefMoraleHit = moraleHit,
            DefMoraleAfter = defMoraleAfter,
            AtkMoraleAfter = atkMoraleAfter,
            DefRouted = routed,
            Captured = routed || def.Personnel - defLoss <= 0,
            FactorsAtk = fa,
            FactorsDef = fd,
        };
    }

    /// <summary>因子链文本摘要：战报标题行用，明细另走 factors_atk/def。</summary>
    public static string CfSummary(double atkCf, double defCf) =>
        $"CF {atkCf.ToString("F1", CultureInfo.InvariantCulture)} : {defCf.ToString("F1", CultureInfo.InvariantCulture)}";

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
