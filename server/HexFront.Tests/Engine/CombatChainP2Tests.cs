using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P2 回归：CF 战力链（doc §7）的单调性与封底。
/// 每个因子单独拨一下，战力必须朝设计文档说的方向动——
/// 系数被改乱、或某条链忘记进 CF，这里就会红。
/// 用自建小场景而不是随仓剧本：单因子实验要求其余因子全部钉死在名义值上。
/// </summary>
public class CombatChainP2Tests
{
    private const uint FixedNoise = 20261017;

    private static GameState Arena(Rules? rules = null, string season = "夏") => new()
    {
        Grid = new HexGrid(0, 0, 1250, 10, 10, 2.5, 1.0),
        Cells = new Dictionary<string, Cell>(),
        Units = new List<Unit>(),
        Rules = rules ?? LibraryService.DEFAULT_RULES,
        Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
        UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
        Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
        Weather = "clear",
        HourOfDay = 12,
        Season = season,
    };

    /// <summary>名义满员单位：非被测因子全部停在出厂值上。</summary>
    private static Unit Add(GameState g, string id, string side, string type, Hex pos)
    {
        var t = LibraryService.UNIT_TYPES[type];
        var u = new Unit
        {
            Id = id,
            Side = side,
            UnitType = type,
            Pos = pos,
            Personnel = t.Personnel,
            MaxPersonnel = t.Personnel,
            Morale = 80,
            Ammo = 1.0,
            Fuel = 1.0,
            Food = 1.0,
            SupplyRate = 1.0,
            Training = t.TrainingDefault,
            Experience = t.ExpDefault,
            CommanderQuality = 0.5,
            IsHQ = t.IsHQ,
        };
        g.Units.Add(u);
        return u;
    }

    /// <summary>红坦克攻蓝步枪，相邻两格开阔地。</summary>
    private static (GameState g, Unit att, Unit def) Clash(Rules? rules = null)
    {
        var g = Arena(rules);
        var att = Add(g, "T1", "red", "tank", new Hex(2, 2));
        var def = Add(g, "R1", "blue", "rifle", att.Pos.NeighborAt(4));
        return (g, att, def);
    }

    private static void SetTerrain(GameState g, Hex h, string terrain) =>
        g.Cells[h.Key] = new Cell { Terrain = terrain };

    /// <summary>把单位拨到「什么都没有」的状态，用来逼出损失/士气上下限。</summary>
    private static void Starve(Unit u)
    {
        u.SupplyRate = 0;
        u.Ammo = 0;
        u.Morale = 0;
        u.Training = 0;
        u.Experience = 0;
    }

    [Fact]
    public void Factor_Map_Exposes_The_Whole_Chain()
    {
        var (g, att, def) = Clash();
        var f = CombatChain.ComputeCf(g, att, def, false);

        f.ToMap().Keys.Should().BeEquivalentTo(new[]
        {
            "base", "terrain", "fort", "strength", "supply", "morale",
            "personnel", "commander", "weather", "air", "counter", "ammo", "cf",
        }, "战报要逐项解释贡献，少一个因子就少一条可调整的理由");

        f.ToMap()["cf"].Should().Be(Math.Round(f.Product, 3));
    }

    [Fact]
    public void Fort_Level_Cuts_Attacker_CF_And_Cushions_Defender_Morale()
    {
        var (g, att, def) = Clash();

        var f0 = CombatChain.ComputeCf(g, att, def, false);
        def.FortLevel = 3;
        var f3 = CombatChain.ComputeCf(g, att, def, false);

        f0.Fort.Should().Be(1.0);
        f3.Fort.Should().BeApproximately(1.0 / (1 + 0.15 * 3), 1e-9);
        f3.Product.Should().BeLessThan(f0.Product);

        def.FortLevel = 0;
        g.RngState = FixedNoise;
        var r0 = CombatChain.ResolveClash(g, att, def, false);
        def.FortLevel = 3;
        g.RngState = FixedNoise;
        var r3 = CombatChain.ResolveClash(g, att, def, false);

        r3.DefLossFrac.Should().BeLessThan(r0.DefLossFrac);
        r3.DefMoraleHit.Should().BeLessThan(r0.DefMoraleHit, "工事是守方士气的安慰项");
        r3.DefMoraleAfter.Should().BeGreaterThan(r0.DefMoraleAfter);
    }

    [Fact]
    public void Terrain_Defense_Dilutes_Attack_Power()
    {
        var (g, att, def) = Clash();
        var previous = double.MaxValue;

        foreach (var (terrain, defense) in new[]
        {
            ("clear", 1.0), ("rough", 1.15), ("woods", 1.35), ("mountain", 1.55),
        })
        {
            SetTerrain(g, def.Pos, terrain);
            var f = CombatChain.ComputeCf(g, att, def, false);

            f.Terrain.Should().BeApproximately(1.0 / defense, 1e-9, $"{terrain} 的地形防御没进战力链");
            f.Product.Should().BeLessThan(previous, "守方地形越硬，攻方战力越低");
            previous = f.Product;
        }
    }

    [Fact]
    public void Supply_Drives_CF_And_Strict_Mode_Is_Harsher()
    {
        var cf = LibraryService.DEFAULT_RULES.Combat;

        CombatChain.SupplyToCF(cf, 1.0, false).Should().BeApproximately(1.0, 1e-9);
        CombatChain.SupplyToCF(cf, 0.5, false).Should().BeApproximately(0.8, 1e-9);
        CombatChain.SupplyToCF(cf, 0.0, false).Should().BeApproximately(0.6, 1e-9, "宽松档最多削到 0.6");
        CombatChain.SupplyToCF(cf, 0.0, true).Should().BeApproximately(0.25, 1e-9, "strict_supply 才掉到 0.25");
        CombatChain.SupplyToCF(cf, -2.0, true).Should().BeApproximately(0.25, 1e-9, "越界不外插");
        CombatChain.SupplyToCF(cf, 9.0, false).Should().BeApproximately(1.0, 1e-9);

        var (g, att, def) = Clash();
        var full = CombatChain.ComputeCf(g, att, def, false);
        att.SupplyRate = 0.0;
        var starved = CombatChain.ComputeCf(g, att, def, false);

        starved.Product.Should().BeLessThan(full.Product);
        CombatChain.ComputeCf(g, att, def, true).Supply
            .Should().BeLessThan(starved.Supply, "同一条断供，strict 档必须更疼");
    }

    [Fact]
    public void Ammo_Druns_To_The_Floor_Not_To_Zero()
    {
        var (g, att, def) = Clash();

        att.Ammo = 1.0;
        CombatChain.ComputeCf(g, att, def, false).Ammo.Should().BeApproximately(1.0, 1e-9);
        att.Ammo = 0.5;
        CombatChain.ComputeCf(g, att, def, false).Ammo.Should().BeApproximately(0.75, 1e-9);
        att.Ammo = 0.0;
        CombatChain.ComputeCf(g, att, def, false).Ammo.Should().Be(0.3, "打光弹药只封到 ammo_min_cf，不是零战力");
        att.Ammo = -1.0;
        CombatChain.ComputeCf(g, att, def, false).Ammo.Should().Be(0.3);
    }

    [Fact]
    public void Morale_CF_Is_Monotone_Within_Bounds()
    {
        var (g, att, def) = Clash();

        att.Morale = 0;
        var low = CombatChain.ComputeCf(g, att, def, false).Morale;
        att.Morale = 50;
        var mid = CombatChain.ComputeCf(g, att, def, false).Morale;
        att.Morale = 100;
        var high = CombatChain.ComputeCf(g, att, def, false).Morale;

        low.Should().BeLessThan(mid);
        mid.Should().BeLessThan(high);
        high.Should().BeApproximately(1.2, 1e-9, "满士气正好顶到 morale_cf_max");

        // 边界靠系数本身夹住：把斜率放大后仍不能越出 min/max
        var rules = new Rules
        {
            Combat = new CombatParams
            {
                MoraleCfBase = 0.0, MoraleCfSpan = 4.0, MoraleCfMin = 0.4, MoraleCfMax = 0.9,
            },
        };
        var (g2, att2, def2) = Clash(rules);
        att2.Morale = 0;
        CombatChain.ComputeCf(g2, att2, def2, false).Morale.Should().Be(0.4);
        att2.Morale = 100;
        CombatChain.ComputeCf(g2, att2, def2, false).Morale.Should().Be(0.9);
    }

    [Fact]
    public void Personnel_Training_And_Experience_Both_Add_Power()
    {
        var cf = LibraryService.DEFAULT_RULES.Combat;
        var u = new Unit { Id = "X", Side = "red", UnitType = "rifle", Training = 60, Experience = 40 };

        var baseline = CombatChain.PersonnelFactor(cf, u);
        u.Training = 90;
        CombatChain.PersonnelFactor(cf, u).Should().BeGreaterThan(baseline);
        u.Training = 60;
        u.Experience = 95;
        CombatChain.PersonnelFactor(cf, u).Should().BeGreaterThan(baseline);

        var (g, att, def) = Clash();
        var raw = CombatChain.ComputeCf(g, att, def, false);
        att.Training = 100;
        att.Experience = 100;
        CombatChain.ComputeCf(g, att, def, false).Product.Should().BeGreaterThan(raw.Product);
    }

    [Fact]
    public void Commander_Near_HQ_Beats_Being_Cut_Off()
    {
        var (g, att, _) = Clash();

        CombatChain.CommanderFactor(g, att).Should().Be(0.8, "半径内没有指挥所就吃失联折扣");

        var hq = Add(g, "H1", "red", "hq", att.Pos.NeighborAt(0));
        var near = CombatChain.CommanderFactor(g, att);
        near.Should().BeGreaterThan(0.8, "靠住指挥所应当有增益");

        for (var i = 0; i < 12; i++) hq.Pos = hq.Pos.NeighborAt(0);
        CombatChain.CommanderFactor(g, att).Should().Be(0.8, "超出指挥半径要退回失联折扣");

        // 距离越近增益越大
        var (g3, att3, _) = Clash();
        var far = Add(g3, "H3", "red", "hq", att3.Pos.NeighborAt(0).NeighborAt(0)); // 2 格
        var farGain = CombatChain.CommanderFactor(g3, att3);
        far.Pos = att3.Pos.NeighborAt(0); // 1 格
        CombatChain.CommanderFactor(g3, att3).Should().BeGreaterThan(farGain, "同侧指挥所越近增益越大");

        var (g2, att2, _) = Clash();
        var hq2 = Add(g2, "H2", "red", "hq", att2.Pos);
        CombatChain.CommanderFactor(g2, hq2).Should().BeGreaterThan(0.8, "指挥所自身有保底");
    }

    [Fact]
    public void Air_Penalty_Hits_Only_Units_Under_Enemy_Sky()
    {
        var (g, att, def) = Clash();

        g.AirControl[def.Pos.Key] = 1.0; // 红方掌握该格制空
        CombatChain.AirPenalty(g, att).Should().Be(1.0, "己方空权不罚己方地面单位");
        var exposed = CombatChain.AirPenalty(g, def);
        exposed.Should().BeLessThan(1.0);
        exposed.Should().BeApproximately(1.0 - (1.0 - 0.75) * 0.75, 1e-9);

        def.FortLevel = 5;
        CombatChain.AirPenalty(g, def).Should().BeGreaterThan(exposed, "工事降低暴露度");

        def.FortLevel = 0;
        SetTerrain(g, def.Pos, "mountain");
        CombatChain.AirPenalty(g, def).Should().BeGreaterThan(exposed, "地形遮蔽降低暴露度");

        SetTerrain(g, def.Pos, "clear");
        g.Flags["air_superiority"] = false;
        CombatChain.AirPenalty(g, def).Should().Be(1.0, "导演部关掉空权条件后整链免疫");

        g.AirControl[def.Pos.Key] = 0.0;
        g.Flags.Remove("air_superiority");
        CombatChain.AirPenalty(g, def).Should().Be(1.0, "无归属格（死区内）不罚");
    }

    [Fact]
    public void Air_Penalty_Never_Breaks_The_Floor()
    {
        var rules = new Rules
        {
            Combat = new CombatParams
            {
                AirGroundPenalty = 0.1, AirPenaltyFloor = 0.4, AirExposureMin = 1.0,
            },
        };
        var (g, _, def) = Clash(rules);
        g.AirControl[def.Pos.Key] = 1.0;
        CombatChain.AirPenalty(g, def).Should().Be(0.4);
    }

    [Fact]
    public void Counter_Matrix_Is_What_The_Doc_Says()
    {
        var (g, att, _) = Clash();
        var rifle = g.FindUnit("R1")!;
        var enemyTank = Add(g, "T2", "blue", "tank", att.Pos.NeighborAt(1));
        var gun = Add(g, "G1", "red", "gun", att.Pos.NeighborAt(2));

        CombatChain.ComputeCf(g, att, rifle, false).Counter.Should().Be(2.2);
        CombatChain.ComputeCf(g, att, enemyTank, false).Counter.Should().Be(0.9);
        CombatChain.ComputeCf(g, gun, enemyTank, false).Counter.Should().Be(2.0);
        CombatChain.ComputeCf(g, gun, rifle, false).Counter.Should().Be(1.0, "未定义的对位按 1.0 中性");

        // 对位直接进裁决：坦克打步枪的守方损失份额要明显高于坦克打坦克
        var (g2, att2, def2) = Clash();
        g2.RngState = FixedNoise;
        var vsRifle = CombatChain.ResolveClash(g2, att2, def2, false);

        var (g3, att3, _) = Clash();
        var tankDef = Add(g3, "R3", "blue", "tank", att3.Pos.NeighborAt(4));
        g3.RngState = FixedNoise;
        var vsTank = CombatChain.ResolveClash(g3, att3, tankDef, false);

        vsRifle.DefLossFrac.Should().BeGreaterThan(vsTank.DefLossFrac);
    }

    [Fact]
    public void Strength_Reduces_CF_Proportionally()
    {
        var (g, att, def) = Clash();
        var full = CombatChain.ComputeCf(g, att, def, false);
        att.Personnel = LibraryService.UNIT_TYPES["tank"].Personnel / 2;
        var half = CombatChain.ComputeCf(g, att, def, false);

        half.Strength.Should().BeApproximately(0.5, 1e-9);
        half.Product.Should().BeApproximately(full.Product * 0.5, 1e-9);
    }

    [Fact]
    public void Weather_And_Night_Suppress_Combat()
    {
        var (g, att, def) = Clash();
        CombatChain.ComputeCf(g, att, def, false).Weather.Should().Be(1.0, "晴 + 昼间");

        g.Weather = "storm";
        CombatChain.ComputeCf(g, att, def, false).Weather.Should().BeApproximately(0.8, 1e-9);

        g.Weather = "fog";
        CombatChain.ComputeCf(g, att, def, false).Weather.Should().BeApproximately(0.75, 1e-9);

        g.Weather = "clear";
        g.HourOfDay = 22;
        CombatChain.ComputeCf(g, att, def, false).Weather.Should().BeApproximately(0.75, 1e-9, "夜间 combat 系数 0.75");
    }

    [Fact]
    public void Loss_Fractions_Saturate_At_The_Ceilings()
    {
        var (g, att, def) = Clash();
        Starve(def);

        g.RngState = FixedNoise;
        var smash = CombatChain.ResolveClash(g, att, def, false);
        smash.DefLossFrac.Should().Be(0.34, "压倒性优势也不能一次削掉超过 def_loss_ceil");
        smash.AtkLoss.Should().Be(0, "份额低于 loss_min_frac 不产生 casualties");
        smash.DefRouted.Should().BeTrue();
        smash.Captured.Should().BeTrue();

        // 反向：以卵击石（饿肚子的步枪仰攻坚固山地坦克）
        var g2 = Arena();
        var weak = Add(g2, "R9", "red", "rifle", new Hex(3, 3));
        var strong = Add(g2, "T9", "blue", "tank", weak.Pos.NeighborAt(0));
        Starve(weak);
        SetTerrain(g2, strong.Pos, "mountain");
        strong.FortLevel = 4;

        g2.RngState = FixedNoise;
        var backfire = CombatChain.ResolveClash(g2, weak, strong, false);
        backfire.AtkLossFrac.Should().Be(0.2, "攻方损失封在 atk_loss_ceil");
        backfire.DefLossFrac.Should().BeLessThan(0.01, "打不动守方");
    }

    [Fact]
    public void Clash_Is_Reproducible_From_Rng_State()
    {
        var (g, att, def) = Clash();

        g.RngState = FixedNoise;
        var a = CombatChain.ResolveClash(g, att, def, false);
        g.RngState = FixedNoise;
        var b = CombatChain.ResolveClash(g, att, def, false);

        a.DefLoss.Should().Be(b.DefLoss);
        a.AtkLoss.Should().Be(b.AtkLoss);
        a.DefMoraleAfter.Should().Be(b.DefMoraleAfter);
        g.RngState.Should().NotBe(FixedNoise, "每场交战恰好抽两下，随机源必须前进");

        g.RngState = FixedNoise + 1;
        var c = CombatChain.ResolveClash(g, att, def, false);
        (c.DefLossFrac == a.DefLossFrac && c.AtkLossFrac == a.AtkLossFrac).Should().BeFalse();
    }

    [Theory]
    [InlineData("夏", 1.0)]
    [InlineData("冬", 1.25)]
    [InlineData("秋", 1.08)]
    [InlineData("雨", 1.08)]
    public void Season_Adds_Attrition(string season, double expected)
    {
        MechanicsService.SeasonAttrition(Arena(season: season)).Should().Be(expected);
    }

    [Fact]
    public void Weather_Evolves_On_The_Markov_Chain_And_Never_Invents_Cloudy()
    {
        var g = Arena();
        g.WeatherMode = "realistic";
        g.Weather = "clear";

        var seen = new HashSet<string>();
        for (var i = 0; i < 400; i++)
        {
            g.Weather = MechanicsService.EvolveWeather(g);
            seen.Add(g.Weather!);
        }

        seen.Should().NotContain("cloudy", "旧数组用的是 cloudy，天气库里根本没有这个 id");
        seen.Should().BeSubsetOf(LibraryService.WEATHER_JSON.Keys, "演变结果必须是库里定义过的天气");
        seen.Should().Contain(new[] { "clear", "overcast", "rain" });
    }

    [Fact]
    public void Weather_Evolution_Consumes_Exactly_One_Draw()
    {
        var g = Arena();
        g.WeatherMode = "realistic";
        g.RngState = FixedNoise;
        MechanicsService.EvolveWeather(g);

        var reference = new SeededRng(FixedNoise);
        reference.NextDouble();
        g.RngState.Should().Be(reference.State, "每回合恰好一抽；抽数变了重放就会整体错位");
    }

    [Fact]
    public void Fixed_Or_Director_Weather_Stays_Put()
    {
        foreach (var mode in new[] { "fixed", "director" })
        {
            var g = Arena();
            g.WeatherMode = mode;
            g.Weather = "storm";
            var state = g.RngState;

            MechanicsService.EvolveWeather(g).Should().Be("storm", $"{mode} 模式由导演部或剧本锁定");
            g.RngState.Should().Be(state, "锁定模式不能白吃随机数，否则重放对不上");
        }
    }

    [Fact]
    public void Same_Seed_Produces_The_Same_Weather_Trace()
    {
        static string[] Trace(uint seed)
        {
            var g = Arena();
            g.WeatherMode = "realistic";
            g.Weather = "overcast";
            g.RngState = seed;
            var trace = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                g.Weather = MechanicsService.EvolveWeather(g);
                trace.Add(g.Weather!);
            }
            return trace.ToArray();
        }

        Trace(1234).Should().Equal(Trace(1234));
        Trace(1234).Should().NotEqual(Trace(4321));
    }

    [Fact]
    public void Factory_Rules_Match_The_Doc()
    {
        var r = LibraryService.DEFAULT_RULES;
        r.MaxTurns.Should().Be(20);
        r.SupplyRetention.Should().BeApproximately(0.85, 1e-9);
        r.FatigueMarch.Should().Be(2.0);
        r.SupplyCrit.Should().BeApproximately(0.4, 1e-9);
        r.TurnHours.Should().Be(6);
        r.Combat.RoutMorale.Should().Be(25);
        r.Combat.RoutRecover.Should().Be(45);
    }

    [Fact]
    public void Scenario_Rules_Block_Overrides_Combat_Coefficients()
    {
        const string scenario = """
        {
          "id": "p2", "name": "P2", "max_turns": 6,
          "rules": { "max_turns": 30, "combat": { "fort_bonus": 0.5, "morale_hit_max": 50 } }
        }
        """;

        var cfg = ScenarioService.LoadFromJson(scenario);
        var rules = ScenarioService.MergeRules(LibraryService.DEFAULT_RULES, cfg.Rules);

        rules.MaxTurns.Should().Be(30);
        rules.Combat.FortBonus.Should().BeApproximately(0.5, 1e-9);
        rules.Combat.MoraleHitMax.Should().Be(50);
        rules.Combat.RoutMorale.Should().Be(25, "未覆盖的系数必须继承默认值");
        rules.Combat.DefLossCoef.Should().BeApproximately(0.055, 1e-9);

        ScenarioService.MergeRules(LibraryService.DEFAULT_RULES, null).Should().BeSameAs(LibraryService.DEFAULT_RULES);
    }

    [Fact]
    public void Coefficients_Are_Read_From_Rules_Not_Hardcoded()
    {
        var (g, att, def) = Clash(new Rules { Combat = new CombatParams { FortBonus = 0.6 } });
        def.FortLevel = 2;
        CombatChain.ComputeCf(g, att, def, false).Fort.Should().BeApproximately(1.0 / 2.2, 1e-9);

        var (g5, att5, def5) = Clash(new Rules { Combat = new CombatParams { DefLossCoef = 0.011 } });
        g5.RngState = FixedNoise;
        var weakBlow = CombatChain.ResolveClash(g5, att5, def5, false);

        var (g1, att1, def1) = Clash();
        g1.RngState = FixedNoise;
        var normal = CombatChain.ResolveClash(g1, att1, def1, false);

        weakBlow.DefLossFrac.Should().BeApproximately(normal.DefLossFrac / 5, 1e-9);
    }

    [Fact]
    public void Battle_Record_Carries_The_Factor_Breakdown_To_The_Director()
    {
        var (g, att, def) = Clash();
        // 被测项是战报里的因子链，不是视野；按迷雾规矩补一条 E1 航迹，免得进攻令卡在 not_observed 上
        g.Tracks["red"] = new Dictionary<string, Track>
        {
            ["R1"] = new Track { UnitId = "R1", Level = 1, LastSeen = def.Pos, LastSeenTurn = g.Turn },
        };

        var report = TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
        {
            ["red"] = new List<Command> { new() { Op = CommandOp.Attack, UnitId = "T1", Target = "R1" } },
            ["blue"] = new List<Command>(),
        });

        report.Rejected.Should().BeEmpty(
            $"进攻令被拒就验不到战报：{string.Join(",", report.Rejected.Select(e => e.Reason))}");

        var battle = report.Battles.Should().ContainSingle().Subject;
        battle.Kind.Should().Be("combat");
        battle.Attacker.Should().Be("T1");
        battle.Defender.Should().Be("R1");
        battle.DefCf.Should().BeGreaterThan(0);
        battle.FactorsAtk.Counter.Should().Be(2.2);
        battle.FactorsAtk.ToMap().Should().ContainKey("fort");
        battle.FactorsDef.Base.Should().Be(10);

        att.Ammo.Should().BeApproximately(0.88, 1e-9, "每次开火扣 ammo_per_attack");
        def.Personnel.Should().BeLessThan(LibraryService.UNIT_TYPES["rifle"].Personnel);
    }
}
