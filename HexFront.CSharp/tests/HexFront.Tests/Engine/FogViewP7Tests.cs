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
/// P7-a 回归：迷雾情报档位与三视图字段（对齐 hexfront/bin/verify.dart §1/§2/§9 的 5、6、7、8、9、10、54–58 项）。
/// 这一组锁的是"谁能看到什么"——视图裁剪一旦走偏，对抗公平性直接失效，
/// 而且它不会被回合推进类断言发现（turn 照样加一，只是情报漏了）。
/// </summary>
public class FogViewP7Tests
{
    private const int BoardWidth = 42;

    /// <summary>一行 42 格的开阔地：双方隔全盘，侦察半径必然覆盖不到，用于测"开局互不可见"。</summary>
    private static GameState Board()
    {
        var g = new GameState
        {
            Grid = new HexGrid(0, 0, 1250, BoardWidth, 1, 2.5, 1.0),
            Cells = new Dictionary<string, Cell>(),
            Units = new List<Unit>(),
            Rules = LibraryService.DEFAULT_RULES,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            Weather = "clear",
            HourOfDay = 12,
            Turn = 1,
            MaxTurns = 14,
        };
        for (var q = 0; q < BoardWidth; q++) g.Cells[$"{q},0"] = new Cell { Terrain = "clear" };
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

    /// <summary>直接把观测档案写进 tracks：视图裁剪只读 tracks，绕开侦察/通视才能钉住档位本身。</summary>
    private static void Expose(GameState g, string observer, string targetId, int level)
    {
        var target = g.FindUnit(targetId)!;
        if (!g.Tracks.TryGetValue(observer, out var tracks))
            g.Tracks[observer] = tracks = new Dictionary<string, Track>();
        tracks[targetId] = new Track
        {
            UnitId = targetId,
            Level = level,
            LastSeen = target.Pos,
            LastSeenTurn = g.Turn,
        };
    }

    private static Dictionary<string, object?>? Contact(GameState g, string side, string id) =>
        ((List<Dictionary<string, object?>>?)FogService.SideView(g, side)["contacts"])?.FirstOrDefault(c => (string?)c["id"] == id);

    // ───────── 开局互不可见（verify §1-5） ─────────

    [Fact]
    public void Fresh_Board_Keeps_Both_Sides_Blind()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 2);
        Add(g, "B1", "blue", "rifle", BoardWidth - 3);

        FogService.UpdateObservation(g);

        g.Tracks.GetValueOrDefault("red").Should().BeEmpty("开局双方隔着全盘，谁都不该有敌方档案");
        g.Tracks.GetValueOrDefault("blue").Should().BeEmpty();
        Contact(g, "red", "B1").Should().BeNull();
    }

    // ───────── 暴露档位决定情报颗粒（verify §2-9/10） ─────────

    [Fact]
    public void E1_Reveals_Only_A_Strength_Band()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 2);
        Add(g, "B1", "blue", "tank", BoardWidth - 3);
        Expose(g, "red", "B1", 1);

        var c = Contact(g, "red", "B1");

        c.Should().NotBeNull();
        c!["strength_band"].Should().NotBeNull("E1 至少给一个兵力档，否则接触列表等于没有情报");
        // 档位不够时服务端是"不发这个键"，不是发 null —— 断言必须按缺键写
        c.Should().NotContainKey("type");
        c.Should().NotContainKey("personnel");
        c.Should().NotContainKey("commander");
    }

    [Fact]
    public void Type_Arrives_At_E2_But_Personnel_Only_At_E3()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 2);
        Add(g, "B1", "blue", "tank", BoardWidth - 3);

        Expose(g, "red", "B1", 2);
        var at2 = Contact(g, "red", "B1");
        at2!["type"].Should().Be("tank");
        at2.Should().NotContainKey("personnel", "精确兵员要到 E3 才发");

        Expose(g, "red", "B1", 3);
        var at3 = Contact(g, "red", "B1");
        at3!["personnel"].Should().Be((int)LibraryService.UNIT_TYPES["tank"].Personnel);
        at3["equipment"].Should().NotBeNull();
    }

    [Fact]
    public void No_Contact_Below_E3_Carries_Personnel()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 2);
        Add(g, "B1", "blue", "tank", BoardWidth - 3);
        Add(g, "B2", "blue", "gun", BoardWidth - 4);
        Expose(g, "red", "B1", 1);
        Expose(g, "red", "B2", 2);

        var contacts = (List<Dictionary<string, object?>>)FogService.SideView(g, "red")["contacts"]!;

        contacts.Should().HaveCount(2);
        contacts.Where(c => Convert.ToInt32(c["level"]) < 3)
            .Should().OnlyContain(c => !c.ContainsKey("personnel"),
            "漏一条就等于把对手底牌发下去");
    }

    [Fact]
    public void A_Dead_Under_Observation_Still_Reports_Gone()
    {
        var g = Board();
        Add(g, "R1", "red", "rifle", 2);
        var victim = Add(g, "B1", "blue", "tank", BoardWidth - 3);
        Expose(g, "red", "B1", 3);
        victim.Personnel = 0;

        var c = Contact(g, "red", "B1");

        // 档案按龄衰减而不是按死亡清除，所以中间态是 status=gone，前端要有这个标签
        c!["status"].Should().Be("gone");
        c.Should().NotContainKey("personnel");
    }

    // ───────── 要点：地理公开、归属私有（verify §9-54/55/56/57/58） ─────────

    private static GameState WithNodes(GameState g)
    {
        g.Cells["5,0"] = new Cell { Terrain = "clear", PowerPlant = true };
        g.Cells["9,0"] = new Cell { Terrain = "clear", Substation = true };
        return g;
    }

    [Fact]
    public void Node_Geography_Is_Public_But_Ownership_Is_Not()
    {
        var g = WithNodes(Board());
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "B1", "blue", "rifle", 9);

        var nodes = (List<Dictionary<string, object?>>)FogService.DirectorView(g)["nodes"]!;
        nodes.Should().HaveCount(2, "要点是地图上的事实，对谁都不该藏");
        nodes.Should().OnlyContain(n => n["label"] != null && n["kind"] != null,
            "要点清单要带类别与中文标签，否则前端只能显示一串坐标");

        var redView = FogService.SideView(g, "red");
        var blueView = FogService.SideView(g, "blue");
        ((List<Dictionary<string, object?>>)redView["nodes"]!).Should().HaveCount(nodes.Count,
            "地形不是情报：双方视图里的要点地理必须与导演部同数");
        ((List<Dictionary<string, object?>>)blueView["nodes"]!).Should().HaveCount(nodes.Count);

        var redHolders = (Dictionary<string, string>)redView["node_holders"]!;
        var blueHolders = (Dictionary<string, string>)blueView["node_holders"]!;

        redHolders.Should().Equal(new Dictionary<string, string> { ["5,0"] = "red" });
        blueHolders.Should().Equal(new Dictionary<string, string> { ["9,0"] = "blue" });
        redHolders.Keys.Intersect(blueHolders.Keys).Should().BeEmpty();
    }

    [Fact]
    public void Director_Sees_Both_Grid_Factors_And_Spectator_Sees_Neither_Ownership()
    {
        var g = WithNodes(Board());
        Add(g, "R1", "red", "rifle", 5);
        Add(g, "B1", "blue", "rifle", 9);

        var director = FogService.DirectorView(g);
        ((Dictionary<string, object?>)director["power"]!).Should().ContainKeys("red", "blue");
        ((Dictionary<string, string>)director["node_holders"]!).Should().HaveCount(2);

        var spectator = FogService.SpectatorView(g);
        spectator.Should().NotContainKey("node_holders", "观战看的是情报层，不是归属层");
        spectator.Should().ContainKey("nodes");
        ((Dictionary<string, object?>)spectator["summary"]!).Should().ContainKeys(
            "red_units", "blue_units", "red_personnel", "blue_personnel");
    }

    // ───────── 双 AI 预热后仍然双盲（verify §2-6/7/8） ─────────

    /// <summary>
    /// 8 回合而不是剧本默认的 3：实测真实 DEM 下侦察半径被天气/昼夜压到 1–2 格，
    /// 前线要到第 6 回合才第一次互相照面（红方 T6 才出现首条航迹）。
    /// 回合数属于 #94「AI 推进过慢」，这里只锁"照面之后档案与视图一致"。
    /// </summary>
    [Fact]
    public void Warmup_Discloses_Only_What_The_Archive_Holds()
    {
        var g = RealScenario.WarmUp(8);

        var blueIds = g.Units.Where(u => u.Side == "blue").Select(u => u.Id).ToHashSet();
        var blueAlive = g.Units.Count(u => u.Side == "blue" && u.Alive);
        var view = FogService.SideView(g, "red");
        var contacts = RealScenario.ContactsOf(g, "red");
        var exposed = contacts.Select(c => (string)c["id"]!).ToHashSet();
        var ownIds = ((List<Dictionary<string, object?>>)view["own"]!).Select(o => (string)o["id"]!).ToHashSet();

        exposed.Should().NotBeEmpty("八回合双 AI 对抗后侦察必须留下档案，否则迷雾里的部队永远隐形");
        ownIds.Should().NotContain(i => blueIds.Contains(i), "己方清单里混进敌方单位就是泄漏");
        exposed.Should().BeSubsetOf(g.Tracks["red"].Keys, "接触列表只能来自本方观测档案");
        exposed.Count.Should().BeLessThan(blueAlive, "红方不该在八个回合里看光蓝方全军");
    }
}
