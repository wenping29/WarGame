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
/// P3-d 回归：AI 副官（doc「按迷雾可见情报生成合法指令，可代管缺席阵营或全 AI 自动对抗」）。
/// 这里锁的是"副官凭什么下这道令"：情报只能来自本方航迹（与 sideView 同一份），
/// 指令必须过服务端校验（坏令会让托管方整回合僵住），且不能抽随机数（否则回放必然分叉）。
/// </summary>
public class AiOfficerP3Tests
{
    private const uint Seed = 90210;

    private static GameState Board(int w = 14, int h = 3)
    {
        var g = new GameState
        {
            Grid = new HexGrid(0, 0, 1250, w, h, 2.5, 1.0),
            Cells = new Dictionary<string, Cell>(),
            Units = new List<Unit>(),
            Rules = LibraryService.DEFAULT_RULES,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            Weather = "clear",
            HourOfDay = 12,
            Turn = 1,
            RngState = Seed,
        };
        for (var r = 0; r < h; r++)
            for (var q = 0; q < w; q++)
                g.Cells[$"{q},{r}"] = new Cell { Terrain = "clear" };
        return g;
    }

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

    /// <summary>红方对某敌单位已有 E<paramref name="level"/> 级航迹，最后所见就在它当前格。</summary>
    private static void See(GameState g, string unitId, int level)
    {
        var u = g.FindUnit(unitId) ?? throw new InvalidOperationException(unitId);
        if (!g.Tracks.TryGetValue("red", out var tracks))
            g.Tracks["red"] = tracks = new Dictionary<string, Track>();
        tracks[unitId] = new Track
        {
            UnitId = unitId,
            Level = level,
            LastSeen = u.Pos,
            LastSeenTurn = g.Turn,
        };
    }

    private static void CaptureGoal(GameState g, string hexKey) =>
        g.VictoryConds.Add(new VictoryCond { Kind = "capture", Side = "red", Target = hexKey });

    /// <summary>副官能给本方每个存活单位都下令（漏掉谁都等于托管方少一支部队）。</summary>
    private static List<Command> Act(GameState g, string side = "red") => AiService.GenerateOrders(g, side);

    private static List<Dictionary<string, object?>> ViewContacts(GameState g, string side) =>
        ((IEnumerable<object?>?)FogService.SideView(g, side)["contacts"])!
            .Cast<Dictionary<string, object?>>()
            .ToList();

    private static string Shape(IEnumerable<Command> orders) => string.Join("|", orders.Select(c =>
        $"{c.Op}:{c.UnitId}:{c.Target}:{c.Zone?.Key ?? "-"}:{string.Join(">", c.Path.Select(h => h.Key))}"));

    // ==================== 情报口径 ====================

    [Fact]
    public void Officer_Never_Shoots_What_The_View_Does_Not_Disclose()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "A1", "red", "arty", 4);
        Add(g, "B1", "blue", "rifle", 6);      // 贴着红方，迷雾里却什么都没有
        Add(g, "B2", "blue", "tank", 11);

        var blind = Act(g, "red");
        blind.Should().NotBeEmpty("无接触时副官也该给出整备令");
        blind.Should().OnlyContain(c => c.Op == CommandOp.Dig || c.Op == CommandOp.Halt || c.Op == CommandOp.Rest,
            "没有任何航迹就没有任何可打的targets：拿服务端真值瞄准等于把迷雾删了");

        See(g, "B1", 1);
        var seen = Act(g, "red");
        seen.Where(c => c.Op == CommandOp.Attack).Select(c => c.Target)
            .Should().OnlyContain(id => ViewContacts(g, "red").Any(vc => (string)vc["id"]! == id),
                "攻击令的目标必须逐个出现在本方视图接触列表里");
        seen.Should().Contain(c => c.Op == CommandOp.Attack && c.Target == "B1");
        seen.Should().NotContain(c => c.Op == CommandOp.Attack && c.Target == "B2",
            "B2 从未被侦察，副官不能知道它在 (11,0)");
    }

    [Fact]
    public void Unknown_Neighbour_Is_Walked_Into_Not_Sniped_At()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "B1", "blue", "rifle", 6); // 未知格：看得见就不该叫遭遇
        CaptureGoal(g, "10,0");

        var orders = Act(g, "red");
        var move = orders.SingleOrDefault(c => c.Op == CommandOp.Move && c.UnitId == "R1");
        move.Should().NotBeNull("doc §2：进入未知格是「遭遇」而不是被拒的机动令");
        move!.Path.Select(h => h.Key).Should().Contain("6,0");
        orders.Should().NotContain(c => c.Op == CommandOp.Attack, "没有航迹就没有攻击依据");

        var report = TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
        {
            ["red"] = orders,
            ["blue"] = new List<Command>(),
        });
        report.Events.Should().Contain(e => e.Code == "encounter",
            "副官踏进未知敌占格必须由结算揭晓遭遇战");
    }

    [Fact]
    public void Only_Anti_Air_Ground_Units_Ever_Point_At_An_Aircraft()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "J1", "blue", "fighter", 6);
        See(g, "J1", 3); // 判明到 E2 以上，副官知道那是飞机

        Act(g, "red").Should().NotContain(c => c.Op == CommandOp.Attack && c.Target == "J1",
            "步兵对喷气机开火既不合理，也是引擎放行后的空战漏洞");

        var withAa = Board();
        Add(withAa, "S1", "red", "aa", 5);
        Add(withAa, "J1", "blue", "fighter", 6);
        See(withAa, "J1", 3);
        Act(withAa).Should().Contain(c => c.Op == CommandOp.Attack && c.Target == "J1",
            "接战空中目标正是防空兵种的本职");
    }

    // ==================== 战术形态 ====================

    [Fact]
    public void Artillery_Shells_From_The_Range_Edge_Instead_Of_Charging()
    {
        var g = Board();
        Add(g, "A1", "red", "arty", 2);  // 射程 3
        Add(g, "B1", "blue", "rifle", 5);
        See(g, "B1", 3);

        var order = Act(g, "red").Single();
        order.Op.Should().Be(CommandOp.Fire, "炮兵推到刺刀底下是最典型的 AI 送人头");
        order.Zone!.Value.Key.Should().Be("5,0");
        CommandValidator.ValidateOrders(g, "red", new List<Command> { order }).Should().BeEmpty();
    }

    [Fact]
    public void Headquarters_Digs_In_And_Fixed_Facilities_Get_No_Order()
    {
        var g = Board();
        Add(g, "H1", "red", "hq", 3);
        Add(g, "X1", "red", "radar", 4);
        Add(g, "B1", "blue", "rifle", 5);
        See(g, "B1", 3);

        var orders = Act(g, "red");
        orders.Should().NotContain(c => c.UnitId == "X1", "雷达站既动不了也挖不了");
        orders.Single(c => c.UnitId == "H1").Op.Should().Be(CommandOp.Dig,
            "指挥所一死全军失联，贴脸的敌人要就地构筑而不是迎面送过去");
    }

    [Fact]
    public void River_Is_Crossed_Not_Worshipped()
    {
        var g = Board(w: 12, h: 3);
        for (var r = 0; r < 3; r++) g.Cells[$"6,{r}"] = new Cell { Terrain = "river" };
        Add(g, "R1", "red", "rifle", 4);
        CaptureGoal(g, "9,0");

        var move = Act(g, "red").Single(c => c.UnitId == "R1");
        move.Op.Should().Be(CommandOp.Move);
        move.Path.Select(h => h.Key).Should().Contain("6,0",
            "校验只拦 Impassable：副官自己再加一道「无桥不下水」就把整支部队钉死在河岸");
        CommandValidator.ValidateOrders(g, "red", new List<Command> { move }).Should().BeEmpty();
    }

    // ==================== 可重放性 ====================

    [Fact]
    public void Officer_Orders_Are_A_Pure_Function_Of_The_Situation()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "A1", "red", "arty", 4);
        Add(g, "B1", "blue", "rifle", 6);
        See(g, "B1", 3);

        var rngBefore = g.RngState;
        var first = Shape(Act(g, "red"));
        g.RngState.Should().Be(rngBefore, "补令抽随机数会让同种子回放从第一次抖动开始分叉");

        var again = Shape(Act(g, "red"));
        again.Should().Be(first);

        var twin = g.Clone();
        Shape(Act(twin, "red")).Should().Be(first, "同局面必须同令，否则存档续跑对不上账");
        twin.RngState.Should().Be(rngBefore);
    }

    [Fact]
    public void Every_Alive_Unit_Gets_Exactly_One_Order()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "R2", "red", "rifle", 6);
        CaptureGoal(g, "10,0");

        var orders = Act(g, "red");
        orders.GroupBy(c => c.UnitId).Should().OnlyContain(gr => gr.Count() == 1,
            "一单位一道令：重名会被校验器整条拒成 duplicate");
        orders.Select(c => c.UnitId).Should().BeEquivalentTo(new[] { "R1", "R2" });
    }

    // ==================== 真剧本自动对抗 ====================

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "scenarios", "rhine_corridor.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 data/scenarios/rhine_corridor.json");
    }

    private static GameState NewGame(uint seed)
    {
        var root = RepoRoot();
        var cfg = ScenarioService.LoadFromFile(Path.Combine(root, "data", "scenarios", "rhine_corridor.json"));
        var g = ScenarioService.CreateGameState(cfg, Path.Combine(root, "data", "gis"), seed);
        g.WeatherMode = "realistic";
        return g;
    }

    [Fact]
    public void Auto_Battle_Fights_And_Keeps_The_Front_Moving()
    {
        var g = NewGame(20261018);
        var objective = Hex.Parse("18,10");
        var startMean = RedMeanDepth(g, objective);
        var home = g.Units.ToDictionary(u => u.Id, u => u.Pos.Key);
        var rejected = new List<string>();
        var battles = new List<string>();
        var movedUnits = new HashSet<string>();

        for (var t = 0; t < 8 && !g.Over; t++)
        {
            var orders = new Dictionary<string, List<Command>>
            {
                ["red"] = Act(g, "red"),
                ["blue"] = AiService.GenerateOrders(g, "blue"),
            };
            var report = TurnResolver.ResolveTurn(g, orders);
            rejected.AddRange(report.Rejected.Select(e => $"{g.Turn}:{e.UnitId}/{e.Reason}"));
            battles.AddRange(report.Battles.Select(b => b.Kind));
            foreach (var (id, pos) in home)
                if (g.FindUnit(id)?.Pos.Key != pos) movedUnits.Add(id);
            foreach (var u in g.Units) home[u.Id] = u.Pos.Key;
        }

        rejected.Should().BeEmpty("副官的令要能被引擎原样收下：一条坏令就是一次白跑的托管回合");
        battles.Should().NotBeEmpty("八回合双 AI 连一场交战都没有，说明副官只在原地挖工事");
        movedUnits.Count.Should().BeGreaterThan(g.Units.Count / 3,
            "大多数单位都没动 = 机动令被自己的规则挡死了");
        RedMeanDepth(g, objective).Should().BeLessThan(startMean, "战线应当朝胜利目标推进，而不是各自筑垒");
    }

    /// <summary>红方存活单位到目标格的平均深度：全灭时取 0，让断言落在"推进"而不是除零。</summary>
    private static double RedMeanDepth(GameState g, Hex to) =>
        g.Units.Where(u => u.Side == "red" && u.Alive)
            .Select(u => (double)u.Pos.DistanceTo(to))
            .DefaultIfEmpty(0)
            .Average();
}
