using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P3-c 回归：回合结算七阶段（doc §8）的分支与落状态。
/// 这里锁的是"谁在什么阶段动手、动完落成什么状态"：
/// 空中与对地支援共用 CF 链、机动/构筑/遭遇/突击/火力各自分支、恢复段的士气归位、胜负按 §6.4 的 kind 判。
/// 战报五类 kind（air/strike/encounter/combat/fire）必须都能真的产出来 —— 产不出来说明那条分支是死代码。
/// </summary>
public class TurnResolverP3Tests
{
    private const uint Seed = 4242;

    /// <summary>开阔地棋盘：默认单行走廊（遭遇/溃退只需要横向邻格），可指定多行与回合上限。</summary>
    private static GameState Board(int w = 14, int h = 3, Rules? rules = null, int maxTurns = 14)
    {
        var g = new GameState
        {
            Grid = new HexGrid(0, 0, 1250, w, h, 2.5, 1.0),
            Cells = new Dictionary<string, Cell>(),
            Units = new List<Unit>(),
            Rules = rules ?? LibraryService.DEFAULT_RULES,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            Weather = "clear",
            HourOfDay = 12,
            Turn = 1,
            MaxTurns = maxTurns,
            RngState = Seed,
        };
        for (var r = 0; r < h; r++)
            for (var q = 0; q < w; q++)
                g.Cells[$"{q},{r}"] = new Cell { Terrain = "clear" };
        return g;
    }

    /// <summary>把恢复段系数清零：想断言"某一支分支改了/没改状态"时，先让别的分支闭嘴。</summary>
    private static Rules Quiet() => new()
    {
        MoraleRecover = 0,
        FatigueRecover = 0,
        FatigueIdle = 0,
        AmmoRecover = 0,
        RoutMoraleNudge = 0,
    };

    private static Unit Add(GameState g, string id, string side, string type, int q, int r = 0)
    {
        var t = LibraryService.UNIT_TYPES[type];
        var u = new Unit
        {
            Id = id,
            Side = side,
            UnitType = type,
            Pos = new Hex(q, r),
            Personnel = t.Personnel,
            MaxPersonnel = t.Personnel,
            Morale = 80,
            Ammo = 1.0,
            Fuel = 1.0,
            Food = 1.0,
            Training = t.TrainingDefault,
            Experience = t.ExpDefault,
            CommanderQuality = 0.5,
            IsHQ = t.IsHQ,
        };
        g.Units.Add(u);
        return u;
    }

    /// <summary>下令方对某敌单位已有 E<paramref name="level"/> 级暴露航迹（结算读的是上一阶段刷新的 tracks）。</summary>
    private static void See(GameState g, string side, string unitId, int level)
    {
        if (!g.Tracks.TryGetValue(side, out var tracks))
            g.Tracks[side] = tracks = new Dictionary<string, Track>();
        tracks[unitId] = new Track { UnitId = unitId, Level = level, LastSeen = new Hex(0, 0), LastSeenTurn = g.Turn };
    }

    private static TurnResolver.TurnReport Run(GameState g, Dictionary<string, List<Command>>? orders = null) =>
        TurnResolver.ResolveTurn(g, orders ?? new Dictionary<string, List<Command>>());

    private static Dictionary<string, List<Command>> Orders(
        IEnumerable<Command>? red = null, IEnumerable<Command>? blue = null) => new()
    {
        ["red"] = red?.ToList() ?? new List<Command>(),
        ["blue"] = blue?.ToList() ?? new List<Command>(),
    };

    private static Command Move(string id, params (int q, int r)[] path) => new()
    {
        Op = CommandOp.Move,
        UnitId = id,
        Path = path.Select(p => new Hex(p.q, p.r)).ToList(),
    };

    private static Command Attack(string id, string target) => new()
    {
        Op = CommandOp.Attack,
        UnitId = id,
        Target = target,
    };

    private static Command Fire(string id, int q, int r) => new()
    {
        Op = CommandOp.Fire,
        UnitId = id,
        Zone = new Hex(q, r),
    };

    private static Command Air(string id, string mission, int q, int r) => new()
    {
        Op = CommandOp.AirMission,
        UnitId = id,
        Mission = mission,
        Zone = new Hex(q, r),
    };

    private static int FirstCode(List<WarEvent> events, string code) =>
        events.FindIndex(e => e.Code == code);

    // ───────── 战报种类与阶段顺序 ─────────

    [Fact]
    public void Turn_Produces_All_Five_Battle_Kinds()
    {
        var g = Board();
        // 空战：双方战斗机同一任务区
        Add(g, "F1", "red", "fighter", 0);
        Add(g, "F2", "blue", "fighter", 5);
        // 对地空袭：强击机扫射蓝步兵
        Add(g, "S1", "red", "strike_air", 6, 1);
        Add(g, "B1", "blue", "rifle", 7, 1);
        // 遭遇战：红坦克开进未被侦察的蓝步兵格
        Add(g, "T1", "red", "tank", 0, 2);
        Add(g, "B2", "blue", "rifle", 1, 2);
        // 间接火力：红炮兵覆盖蓝炮兵
        Add(g, "A1", "red", "arty", 2, 2);
        Add(g, "B3", "blue", "arty", 5, 2);
        // 突击：明牌近距攻击
        Add(g, "R1", "red", "rifle", 9);
        Add(g, "B4", "blue", "rifle", 10);
        See(g, "red", "B4", 2);

        var rep = Run(g, Orders(
            new[] { Air("F1", "patrol", 2, 0), Air("S1", "strike", 7, 1), Move("T1", (0, 2), (1, 2)),
                    Fire("A1", 5, 2), Attack("R1", "B4") },
            new[] { Air("F2", "patrol", 2, 0) }));

        rep.Battles.Select(b => b.Kind).Distinct().Should()
            .BeEquivalentTo(new[] { "air", "strike", "encounter", "combat", "fire" },
                "doc §8 的五类交战分支必须都能真的产出");
        rep.Battles.Should().OnlyContain(b => b.FactorsAtk.ToMap().ContainsKey("cf"),
            "每条战报都要带得出因子链（doc M6 可解释）");
        rep.Events.Select(e => e.Code).Should().Contain(new[]
        {
            "air_clash", "air_strike", "encounter", "combat", "arty",
        });
    }

    [Fact]
    public void Seven_Phases_Run_In_The_Documented_Order()
    {
        var g = Board();
        Add(g, "S1", "red", "strike_air", 0, 1);
        Add(g, "B1", "blue", "rifle", 1, 1);
        Add(g, "T1", "red", "tank", 0, 2);
        Add(g, "B2", "blue", "rifle", 1, 2);
        Add(g, "E1", "red", "engineer", 3, 2);

        var rep = Run(g, Orders(new[]
        {
            Air("S1", "strike", 1, 1), Move("T1", (0, 2), (1, 2)), Move("E1", (3, 2), (4, 2)),
        }));

        var mission = FirstCode(rep.Events, "air_mission");
        var strike = FirstCode(rep.Events, "air_strike");
        var move = FirstCode(rep.Events, "move");
        var encounter = FirstCode(rep.Events, "encounter");
        var end = FirstCode(rep.Events, "turn_end");

        mission.Should().BeGreaterThan(-1, "①任务指派");
        strike.Should().BeGreaterThan(mission, "①对地空袭在任务指派之后");
        move.Should().BeGreaterThan(strike, "④机动排在①空中之后");
        encounter.Should().BeGreaterThan(move, "④b遭遇要等全场机动落位后才揭晓，否则等于当着玩家的面穿墙");
        end.Should().Be(rep.Events.Count - 1, "⑦回合推进与 turn_end 收尾");
        rep.Events[move].Text.Should().Contain("E1", "④内部按单位 id 稳定序，与指令提交顺序无关");
    }

    [Fact]
    public void Turn_End_Names_The_Day_Phase_And_Weather()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 0);

        var rep = Run(g);
        var last = rep.Events.Last();

        last.Code.Should().Be("turn_end");
        last.Text.Should().Contain("回合 1 结束").And.Contain("黄昏").And.Contain("晴");
        g.Turn.Should().Be(2);
        g.HourOfDay.Should().Be(18, "TurnHours=6 ⇒ 12 点推到 18 点（黄昏）");
    }

    // ───────── ① 空中 ─────────

    [Fact]
    public void Air_Mission_None_Grounds_The_Aircraft_And_Frees_The_Zone()
    {
        var g = Board();
        Add(g, "F1", "red", "fighter", 0);

        Run(g, Orders(new[] { Air("F1", "patrol", 2, 0) }));

        g.AirControl.ContainsKey("2,0").Should().BeTrue("出动的战斗机要占住任务区的制空权重");

        Run(g, Orders(new[] { Air("F1", "none", 2, 0) }));

        var f = g.FindUnit("F1")!;
        f.Airborne.Should().BeFalse();
        f.MissionZone.Should().BeNull("收回任务还留着任务区，等于给落地的飞机继续占空域");
        g.AirControl.ContainsKey("2,0").Should().BeFalse();
    }

    [Fact]
    public void Air_Clash_Shares_The_CF_Chain_Instead_Of_A_Ratio_Band()
    {
        var g = Board();
        Add(g, "F1", "red", "fighter", 0);
        Add(g, "F2", "blue", "fighter", 5);

        var rep = Run(g, Orders(
            new[] { Air("F1", "patrol", 2, 0) },
            new[] { Air("F2", "patrol", 2, 0) }));

        var air = rep.Battles.Should().ContainSingle().Subject;
        air.Kind.Should().Be("air");
        air.FactorsAtk.ToMap().Keys.Should().HaveCount(13);
        air.AtkCf.Should().BeGreaterThan(0);
        air.Losses.Should().Be(air.AtkLoss + air.DefLoss);
        rep.Events.Should().Contain(e => e.Code == "air_clash" && e.Text.Contains("CF "));
    }

    [Fact]
    public void Strike_Losses_Are_Scaled_By_The_Delivery_Coefficient()
    {
        var (zeroG, zeroOrders) = StrikeArena(0);
        var zero = Run(zeroG, zeroOrders);

        zero.Battles.Should().ContainSingle().Subject.Kind.Should().Be("strike");
        zero.Battles.Single().DefLoss.Should().Be(0, "投放效能=0 ⇒ 弹着全落空：这才证明系数真进了链，不只是名义存在");
        zeroG.FindUnit("B1")!.Personnel.Should().Be(RifleStrength);

        var halfArena = StrikeArena(0.7);
        var half = Run(halfArena.G, halfArena.Orders);
        var bigArena = StrikeArena(2.8);
        var big = Run(bigArena.G, bigArena.Orders);

        var halfLoss = half.Battles.Single().DefLoss;
        var bigLoss = big.Battles.Single().DefLoss;
        bigLoss.Should().BeGreaterThan(halfLoss, "同一弹着结果乘不同投放效能，落地损失要拉开");
        big.Battles.Single().Losses.Should().Be(bigLoss, "空袭是单向损失，攻方不记战损");

        var bomber = bigArena.G.FindUnit("S1")!;
        bomber.Ammo.Should().Be(0.75, "一轮空袭耗 0.25 弹药");
        bigArena.G.FindUnit("B1")!.Morale.Should().Be(72, "空袭压 8 点士气");
        bigArena.G.FindUnit("B1")!.Personnel.Should().Be(RifleStrength - bigLoss);
    }

    private const int RifleStrength = 600;

    /// <summary>红强击机扫射一格蓝步兵的最小场景；同一随机起点，只拨投放效能。</summary>
    private static (GameState G, Dictionary<string, List<Command>> Orders) StrikeArena(double scale)
    {
        var g = Board(h: 1, rules: new Rules
        {
            MoraleRecover = 0, FatigueRecover = 0, FatigueIdle = 0, AmmoRecover = 0,
            Combat = new CombatParams { StrikeLossScale = scale },
        });
        Add(g, "S1", "red", "strike_air", 0);
        Add(g, "B1", "blue", "rifle", 1).Personnel = RifleStrength;
        return (g, Orders(new[] { Air("S1", "strike", 1, 0) }));
    }

    [Fact]
    public void Fighters_Survive_Hostile_Airspace_While_Others_Get_Shot_Down()
    {
        var hit = FirstRngStateThatHits(0.12);

        var scout = Board();
        var b1 = Add(scout, "C1", "blue", "recon_air", 3);
        b1.Airborne = true;
        b1.Mission = "recon";
        b1.MissionZone = new Hex(3, 0);
        scout.RngState = hit;
        scout.AirControl["3,0"] = 1.0; // 红方掌握该空域

        var scoutRep = Run(scout);
        scout.FindUnit("C1")!.Status.Should().Be(UnitStatus.Destroyed);
        scoutRep.Events.Should().Contain(e => e.Code == "air_lost" && e.Text.Contains("敌控空域"));

        var jet = Board();
        var b2 = Add(jet, "F1", "blue", "fighter", 3);
        b2.Airborne = true;
        b2.Mission = "patrol";
        b2.MissionZone = new Hex(3, 0);
        jet.RngState = hit;
        jet.AirControl["3,0"] = 1.0;

        Run(jet);
        jet.FindUnit("F1")!.Alive.Should().BeTrue("战斗机本来就是去抢空域的，不能按过境风险算损失");
    }

    /// <summary>取第一个"首抽命中 p"的随机状态：风险判定只吃第一抽，测试要能稳定站在命中这一支上。</summary>
    private static uint FirstRngStateThatHits(double p)
    {
        for (uint s = 1; s < 500_000; s++)
        {
            if (new SeededRng(s).Chance(p)) return s;
        }
        throw new InvalidOperationException($"找不到命中 {p} 的随机状态");
    }

    // ───────── ④ 机动与工事 ─────────

    [Fact]
    public void Dig_Raises_Fort_To_The_Terrain_Cap_And_Engineer_Doubles()
    {
        var g = Board(rules: Quiet());
        var rifle = Add(g, "R1", "red", "rifle", 0);
        var engi = Add(g, "E1", "red", "engineer", 1);

        Run(g, Orders(new[] { Dig("R1"), Dig("E1") }));
        rifle.FortLevel.Should().Be(1);
        engi.FortLevel.Should().Be(2, "工兵是专业构筑兵力，进度翻倍");

        Run(g, Orders(new[] { Dig("R1"), Dig("E1") }));
        rifle.FortLevel.Should().Be(2, "开阔地工事上限 2 级（doc §6.3）");
        engi.FortLevel.Should().Be(2);

        var rep = Run(g, Orders(new[] { Dig("R1") }));
        rifle.FortLevel.Should().Be(2);
        rep.Events.Should().NotContain(e => e.Code == "dig", "已到上限不再报构筑进度");
    }

    private static Command Dig(string id) => new() { Op = CommandOp.Dig, UnitId = id };

    [Fact]
    public void Halt_And_Rest_Normalize_Back_To_Ready()
    {
        var g = Board(rules: Quiet());
        var rest = Add(g, "R1", "red", "rifle", 0);
        var halt = Add(g, "H1", "red", "rifle", 1);

        Run(g, Orders(new[] { new Command { Op = CommandOp.Rest, UnitId = "R1" },
                              new Command { Op = CommandOp.Halt, UnitId = "H1" } }));

        rest.Status.Should().Be(UnitStatus.Ready, "回合内标记在恢复段末归位");
        halt.Status.Should().Be(UnitStatus.Ready);
        rest.Personnel.Should().Be(LibraryService.UNIT_TYPES["rifle"].Personnel);
    }

    [Fact]
    public void Moving_Off_A_Fortified_Hex_Abandons_The_Works()
    {
        var g = Board(rules: Quiet());
        var tank = Add(g, "T1", "red", "tank", 0);
        tank.FortLevel = 2;

        Run(g, Orders(new[] { Move("T1", (0, 0), (1, 0)) }));

        tank.Pos.Should().Be(new Hex(1, 0));
        tank.FortLevel.Should().Be(0, "doc §8 的撤除工事就是离开工事格，线上没有独立动作");
    }

    [Fact]
    public void Encounter_Takes_One_Defender_In_Stable_Order()
    {
        var g = Board(h: 1, rules: Quiet());
        Add(g, "T1", "red", "tank", 2);
        Add(g, "B2", "blue", "rifle", 3).Personnel = 800;
        Add(g, "B1", "blue", "rifle", 3).Personnel = 800;

        var rep = Run(g, Orders(new[] { Move("T1", (2, 0), (3, 0)) }));

        rep.Battles.Should().ContainSingle().Subject.Defender.Should().Be("B1",
            "一格多守军只打一场、且按 id 稳定序取首；连续吃 CF 裁决的攻方损失没有上界");
        rep.Events.Should().Contain(e => e.Code == "encounter");
    }

    [Fact]
    public void Unseen_Defender_Does_Not_Block_The_Path_But_Springs_The_Ambush()
    {
        var g = Board(h: 1, rules: Quiet());
        Add(g, "T1", "red", "tank", 0);
        Add(g, "B1", "blue", "rifle", 1);

        var rep = Run(g, Orders(new[] { Move("T1", (0, 0), (1, 0)) }));

        rep.Rejected.Should().BeEmpty("移动路径只校验己方可通行性（doc §2）：没侦察到的敌人不该拦住玩家的令");
        T1(g).Pos.Should().Be(new Hex(1, 0));
        rep.Events.Should().Contain(e => e.Code == "encounter", "进入未知格由结算揭晓 ⇒ 遭遇战");
        g.UnitsAt(new Hex(1, 0)).Select(u => u.Side).Should().Contain(new[] { "red", "blue" });
    }

    [Fact]
    public void Seen_Defender_Still_Blocks_The_Path()
    {
        var g = Board(h: 1, rules: Quiet());
        Add(g, "T1", "red", "tank", 0);
        Add(g, "B1", "blue", "rifle", 1);
        See(g, "red", "B1", 1);

        var rep = Run(g, Orders(new[] { Move("T1", (0, 0), (1, 0)) }));

        rep.Rejected.Should().ContainSingle().Subject.Reason.Should().Be("blocked");
        rep.Battles.Should().BeEmpty("看得见的敌人必须先交战，不许直接踩过去");
    }

    private static Unit T1(GameState g) => g.FindUnit("T1")!;

    // ───────── ⑤ 战斗 ─────────

    [Fact]
    public void Assault_Skips_Targets_That_Stepped_Out_Of_Reach()
    {
        var g = Board(rules: Quiet());
        var tank = Add(g, "T1", "red", "tank", 0);
        var foe = Add(g, "B1", "blue", "rifle", 1);
        See(g, "red", "B1", 2);

        var rep = Run(g, Orders(
            new[] { Attack("T1", "B1") },
            new[] { Move("B1", (1, 0), (2, 0), (3, 0)) }));

        rep.Battles.Should().BeEmpty("射程按结算时刻的实测距离复核：机动阶段会改变双方格位");
        tank.Pos.Should().Be(new Hex(0, 0));
        foe.Pos.Should().Be(new Hex(3, 0));
    }

    [Fact]
    public void Blind_Fire_Is_Degraded_Against_Observed_Fire()
    {
        LibraryService.DEFAULT_RULES.Combat.FireBlindEfficiency.Should().Be(0.6,
            "doc §3.4 盲打精度");

        var seen = BlindSetup(level: 3);
        var seenRep = Run(seen, Orders(new[] { Fire("A1", 3, 0) }));
        var blind = BlindSetup(level: 0);
        var blindRep = Run(blind, Orders(new[] { Fire("A1", 3, 0) }));

        var observedLoss = seenRep.Battles.Should().ContainSingle().Subject;
        var blindLoss = blindRep.Battles.Should().ContainSingle().Subject;
        observedLoss.Kind.Should().Be("fire");
        blindLoss.DefLoss.Should().BeLessThan(observedLoss.DefLoss,
            "同一弹着结果，坐标蒙的比判明的少打折");
        seenRep.Events.Should().Contain(e => e.Code == "arty" && e.Text.Contains("校射 100%"));
        blindRep.Events.Should().Contain(e => e.Code == "arty" && e.Text.Contains("校射 60%"));
    }

    private static GameState BlindSetup(int level)
    {
        var g = Board(h: 1, rules: Quiet());
        Add(g, "A1", "red", "arty", 0);
        Add(g, "B1", "blue", "rifle", 3);
        if (level > 0) See(g, "red", "B1", level);
        return g;
    }

    [Fact]
    public void Fire_On_Empty_Zone_Burns_Ammo_And_Reports_A_Miss()
    {
        var g = Board(h: 1, rules: Quiet());
        var arty = Add(g, "A1", "red", "arty", 0);

        var rep = Run(g, Orders(new[] { Fire("A1", 2, 0) }));

        rep.Battles.Should().BeEmpty("覆盖空格不产生战报");
        rep.Events.Should().Contain(e => e.Code == "fire_miss");
        arty.Ammo.Should().Be(0.88, "盲打照样烧弹药：AmmoPerAttack=0.12");
    }

    [Fact]
    public void Combat_Orders_Are_Sorted_By_Target_Hex_Then_Attacker()
    {
        var g = Board(rules: Quiet());
        Add(g, "A1", "red", "arty", 7);
        Add(g, "A2", "red", "arty", 5);
        Add(g, "B1", "blue", "arty", 10);
        Add(g, "B2", "blue", "arty", 2);

        // 下达顺序故意与稳定序相反：结果顺序只认「目标格 key → 攻方 id」
        var rep = Run(g, Orders(new[] { Fire("A2", 2, 0), Fire("A1", 10, 0) }));

        rep.Battles.Select(b => b.Attacker).Should().Equal(new[] { "A1", "A2" },
            "\"10,0\" 按 key 排在 \"2,0\" 之前：顺序不能由 id 命名或指令提交顺序决定");
    }

    [Fact]
    public void Destroyed_Defender_Yields_The_Hex_To_The_Attacker()
    {
        var g = Board(h: 1, rules: Quiet());
        var tank = Add(g, "T1", "red", "tank", 2);
        var remnant = Add(g, "B1", "blue", "rifle", 3);
        remnant.Personnel = 1;
        See(g, "red", "B1", 2);

        var rep = Run(g, Orders(new[] { Attack("T1", "B1") }));

        rep.Events.Should().Contain(e => e.Code == "destroyed");
        tank.Pos.Should().Be(new Hex(3, 0), "守方被歼 ⇒ 攻方压上占据该格（要点易手）");
    }

    [Fact]
    public void Routed_Defender_Falls_Back_Toward_Its_Own_Depth()
    {
        var g = Board(h: 1, rules: Quiet());
        Add(g, "T1", "red", "tank", 6);
        var foe = Add(g, "B1", "blue", "rifle", 5);
        foe.Morale = 1;
        var home = Add(g, "H1", "blue", "hq", 4);
        See(g, "red", "B1", 2);

        var rep = Run(g, Orders(new[] { Attack("T1", "B1") }));

        rep.Battles.Should().ContainSingle().Subject.Routed.Should().BeTrue();
        foe.Status.Should().Be(UnitStatus.Routed);
        foe.Pos.Should().Be(new Hex(4, 0), $"朝本方纵深（指挥所 @({home.Pos.Q},{home.Pos.R})）最近的可站格");
        rep.Events.Should().Contain(e => e.Code == "rout" && e.Text.Contains("溃退至 4,0"));
    }

    [Fact]
    public void Rout_Without_Escape_Hex_Keeps_The_Unit_On_The_Spot()
    {
        var g = Board(h: 1, rules: Quiet());
        Add(g, "T1", "red", "tank", 6);
        Add(g, "R1", "red", "rifle", 4);
        var foe = Add(g, "B1", "blue", "rifle", 5);
        foe.Morale = 1;
        Add(g, "H1", "blue", "hq", 0);
        See(g, "red", "B1", 2);

        var rep = Run(g, Orders(new[] { Attack("T1", "B1") }));

        foe.Status.Should().Be(UnitStatus.Routed);
        foe.Pos.Should().Be(new Hex(5, 0));
        rep.Events.Should().Contain(e => e.Code == "rout" && e.Text.Contains("无退路"));
    }

    // ───────── ⑥ 消耗与恢复 ─────────

    [Fact]
    public void Fired_Unit_Heals_The_Next_Turn_Not_The_Same_One()
    {
        var g = Board(rules: new Rules { FatigueRecover = 0, AmmoRecover = 0 });
        var unit = Add(g, "R1", "red", "rifle", 0);
        unit.Status = UnitStatus.Fired;
        unit.Morale = 50;

        Run(g);
        unit.Status.Should().Be(UnitStatus.Ready, "回合内战场标记要归位，否则打过一次火就永久锁在交火状态");
        unit.Morale.Should().Be(50, "本回合交过火的单位当回合不回血");
        unit.Fatigue.Should().Be(2.0, "未休整则按 FatigueIdle 继续积累疲劳");

        Run(g);
        unit.Morale.Should().BeGreaterThan(50, "归位后下一回合正常重建士气（doc §9 那条不可达恢复分支的同一种病）");
    }

    [Fact]
    public void Rally_Needs_The_Morale_Threshold_But_Rementants_Still_Kick()
    {
        var g = Board(rules: new Rules { FatigueRecover = 0, AmmoRecover = 0, MoraleRecover = 0 });
        var rallying = Add(g, "R1", "red", "rifle", 0);
        rallying.Status = UnitStatus.Routed;
        rallying.Morale = 60;
        var stuck = Add(g, "S1", "red", "rifle", 1);
        stuck.Status = UnitStatus.Routed;
        stuck.Morale = 10;

        var rep = Run(g);

        rallying.Status.Should().Be(UnitStatus.Ready);
        rep.Events.Should().Contain(e => e.Code == "recover" && e.Text.Contains("R1"));
        stuck.Status.Should().Be(UnitStatus.Routed);
        stuck.Morale.Should().Be(12, "士气触底的残兵要有微量重建，否则溃退不可逆");
    }

    [Fact]
    public void Cut_Supply_Falls_Back_To_The_Lenient_Default()
    {
        var g = Board(rules: Quiet());
        var unit = Add(g, "R1", "red", "rifle", 0);
        unit.SupplyRate = 1.0;

        Run(g);

        unit.SupplyRate.Should().Be(0.7, "无任何补给源的阵营按宽松默认而不是整体挨饿");
    }

    // ───────── ⑦ 胜负 ─────────

    private static GameState Endgame(string kind, string side, string? target = null,
        int amount = 0, int turns = 0, int w = 8, int maxTurns = 14)
    {
        var g = Board(w, 1, rules: Quiet(), maxTurns: maxTurns);
        g.VictoryConds.Add(new VictoryCond
        {
            Kind = kind, Side = side, Target = target, Amount = amount, Turns = turns,
        });
        return g;
    }

    [Fact]
    public void Annihilate_Condition_Ends_The_Game()
    {
        var g = Endgame("annihilate", "red", amount: 70);
        var foe = Add(g, "B1", "blue", "rifle", 3);
        foe.Personnel = 100;
        foe.Casualties = 900;
        Add(g, "R1", "red", "rifle", 0);

        var rep = Run(g);

        g.Over.Should().BeTrue();
        g.Winner.Should().Be("red");
        g.EndedTurn.Should().Be(2);
        rep.Events.Should().Contain(e => e.Code == "game_over" && e.Text.Contains("歼灭目标"));
    }

    [Fact]
    public void Capture_Condition_Respects_Contested_Hex()
    {
        var solo = Endgame("capture", "red", target: "3,0");
        Add(solo, "R1", "red", "rifle", 3);
        Run(solo).Events.Should().Contain(e => e.Code == "game_over" && e.Text.Contains("独占要点 3,0"));
        solo.Winner.Should().Be("red");

        var contested = Endgame("capture", "red", target: "3,0");
        Add(contested, "R1", "red", "rifle", 3);
        Add(contested, "B1", "blue", "rifle", 3);
        Run(contested);
        contested.Over.Should().BeFalse("两方同格＝争夺中，不算占（doc §6.4 与视图共用同一口径）");
    }

    [Fact]
    public void Nodes_Condition_Counts_Objective_Hexes()
    {
        var g = Endgame("nodes", "red", amount: 2, w: 6);
        g.Cells["1,0"] = new Cell { Terrain = "clear", City = 2 };
        g.Cells["4,0"] = new Cell { Terrain = "clear", RailStation = true };
        Add(g, "R1", "red", "rifle", 1);
        Add(g, "R2", "red", "rifle", 4);

        Run(g);
        g.Winner.Should().Be("red");

        var one = Endgame("nodes", "red", amount: 2, w: 6);
        one.Cells["1,0"] = new Cell { Terrain = "clear", City = 2 };
        one.Cells["4,0"] = new Cell { Terrain = "clear", RailStation = true };
        Add(one, "R1", "red", "rifle", 1);
        Add(one, "B1", "blue", "rifle", 4);
        Run(one);
        one.Over.Should().BeFalse("被争夺的要点不计入掌握数");
    }

    [Fact]
    public void Survive_Condition_Wins_On_The_Clock()
    {
        var g = Endgame("survive", "blue", turns: 1);
        Add(g, "B1", "blue", "rifle", 0);

        var rep = Run(g);

        g.Winner.Should().Be("blue", "蓝方的胜利就是撑到推演结束");
        rep.Events.Should().Contain(e => e.Code == "game_over" && e.Text.Contains("蓝方胜"));
    }

    [Fact]
    public void Turn_Limit_Falls_Back_To_The_Personnel_Tiebreak()
    {
        var g = Endgame("annihilate", "red", amount: 99, maxTurns: 1);
        var red = Add(g, "R1", "red", "rifle", 0);
        var blue = Add(g, "B1", "blue", "rifle", 1);
        red.Personnel = 800;
        blue.Personnel = 80;

        var rep = Run(g);

        g.Over.Should().BeTrue("回合耗尽必须落终局标记，不能无限悬着");
        g.Winner.Should().Be("red");
        rep.Events.Should().Contain(e => e.Code == "game_over" && e.Text.Contains("回合耗尽"));
    }

    [Fact]
    public void Game_Over_Is_Final_And_Repeatable()
    {
        var g = Endgame("capture", "red", target: "3,0");
        Add(g, "R1", "red", "rifle", 3);

        Run(g);
        var before = g.EndedTurn;
        var rep = Run(g);

        g.Over.Should().BeTrue();
        g.EndedTurn.Should().Be(before, "已终局不再二次判定，终局回合停在第一次");
        rep.Events.Should().NotContain(e => e.Code == "game_over");
    }

    [Fact]
    public void Director_View_Reports_Victory_In_Wire_Shape()
    {
        var g = Board();
        g.VictoryConds.Add(new VictoryCond
        {
            Kind = "capture", Side = "blue", Target = "7,3", Amount = 0, Turns = 9,
        });

        var view = FogService.DirectorView(g);
        var rows = (List<Dictionary<string, object?>>)view["victory"]!;

        rows.Should().ContainSingle();
        rows[0].Keys.Should().BeEquivalentTo(new[] { "kind", "side", "target", "amount", "turns" },
            "线上字段就是 WireVictory 这五个，多一层改名前端要跟着瞎");
        rows[0]["kind"].Should().Be("capture");
        rows[0]["target"].Should().Be("7,3");
        rows[0]["turns"].Should().Be(9);
    }

    [Fact]
    public void Scenario_Victory_Array_Binds_To_The_Kind_Whitelist()
    {
        var root = RepoRoot();
        var cfg = ScenarioService.LoadFromFile(Path.Combine(root, "data", "scenarios", "rhine_corridor.json"));

        cfg.VictoryConds.Should().NotBeEmpty("剧本键名是 victory，属性名推导出来的 victory_conds 从来没绑上过");
        cfg.VictoryConds.Should().OnlyContain(v => ScenarioService.IsVictoryKind(v.Kind));
        cfg.VictoryConds.Select(v => v.Kind).Should().Contain(new[] { "capture", "survive" });

        var g = ScenarioService.CreateGameState(cfg, Path.Combine(root, "data", "gis"), seed: 11);
        g.VictoryConds.Should().HaveCount(cfg.VictoryConds.Count);
        g.VictoryConds.Select(v => (v.Kind, v.Side, v.Target, v.Turns)).Should()
            .Equal(cfg.VictoryConds.Select(v => (v.Kind, v.Side, v.Target, v.Turns)));
        g.VictoryConds.Should().Contain(v => v.Kind == "capture" && v.Target == "18,10");
        g.VictoryConds.Should().Contain(v => v.Kind == "survive" && v.Side == "blue" && v.Turns == 14);
    }

    [Fact]
    public void Ai_Auto_Battle_Terminates_And_Stays_Interactive()
    {
        var root = RepoRoot();
        var g = ScenarioService.CreateGameState(
            ScenarioService.LoadFromFile(Path.Combine(root, "data", "scenarios", "rhine_corridor.json")),
            Path.Combine(root, "data", "gis"), seed: 2026);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var turns = 0;
        while (!g.Over && turns < g.MaxTurns + 2)
        {
            TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
            {
                ["red"] = AiService.GenerateOrders(g, "red"),
                ["blue"] = AiService.GenerateOrders(g, "blue"),
            });
            turns++;
        }
        sw.Stop();

        g.Over.Should().BeTrue($"剧本挂了 victory 条件且回合耗尽有兜底判定，绝不能悬着不终局（跑了 {turns} 回合）");
        g.Winner.Should().NotBeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(5000, "双 AI 自动推演要能在浏览器里当演示放，14 回合不该卡手");
        g.Events.Should().Contain(e => e.Code == "game_over");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "scenarios", "rhine_corridor.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 data/scenarios/rhine_corridor.json");
    }
}
