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
/// P3-a 回归：后勤补给线与电网系数（doc §6.4 地物玩法表）。
/// 这一组锁的是"断链/绕行/前进补给节点/通电条件"四类玩法语义 ——
/// 补给是士气、弹药、机动的上游，上游走偏会一路污染下游战损，所以每条规则都要单独钉住。
/// </summary>
public class SupplyP3Tests
{
    /// <summary>一行七格的走廊：r=0 单行，邻格只在本行内连通，路径唯一 ⇒ 距离可手算。</summary>
    private static GameState Corridor()
    {
        var g = new GameState
        {
            Grid = new HexGrid(0, 0, 1250, 7, 1, 2.5, 1.0),
            Cells = new Dictionary<string, Cell>(),
            Units = new List<Unit>(),
            Rules = LibraryService.DEFAULT_RULES,
            Terrains = new Dictionary<string, TerrainDef>(LibraryService.STANDARD_TERRAIN),
            UnitTypes = new Dictionary<string, UnitTypeDef>(LibraryService.UNIT_TYPES),
            Weathers = new Dictionary<string, WeatherDef>(LibraryService.WEATHER_JSON),
            Weather = "clear",
            HourOfDay = 12,
        };
        for (var q = 0; q < 7; q++)
            g.Cells[$"{q},0"] = new Cell { Terrain = "clear" };
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

    private static void SetCell(GameState g, int q, int r, Cell cell) => g.Cells[$"{q},{r}"] = cell;

    private static int DistRed(GameState g, int q, int r = 0) =>
        MechanicsService.SupplyDistances(g)["red"][$"{q},{r}"];

    // ───────── 水系代价与渡河 ─────────

    [Fact]
    public void Uncrossed_Water_Costs_Three_Steps_In_The_Supply_Line()
    {
        var g = Corridor();
        SetCell(g, 3, 0, new Cell { Terrain = "river", River = true });
        Add(g, "S1", "red", "recy", 0);

        DistRed(g, 2).Should().Be(2);
        DistRed(g, 3).Should().Be(5, "无桥渡河按 3 倍代价绕行，而不是硬阻断或照旧 +1");
        DistRed(g, 4).Should().Be(6);
    }

    [Fact]
    public void Bridge_Skips_The_Water_Penalty()
    {
        var g = Corridor();
        SetCell(g, 3, 0, new Cell { Terrain = "river", River = true, Bridge = true });
        Add(g, "S1", "red", "recy", 0);

        DistRed(g, 3).Should().Be(3);
        DistRed(g, 4).Should().Be(4);
    }

    [Fact]
    public void Own_Troops_On_The_Hex_Make_It_Crossable()
    {
        var g = Corridor();
        SetCell(g, 3, 0, new Cell { Terrain = "river", River = true });
        Add(g, "S1", "red", "recy", 0);
        Add(g, "F1", "red", "engineer", 3); // 架浮桥/夺渡口

        DistRed(g, 3).Should().Be(3, "己方部队已站上渡口 ⇒ 本方视为可渡");
        DistRed(g, 4).Should().Be(4);
    }

    [Fact]
    public void Enemy_Troops_On_The_Hex_Cut_The_Line()
    {
        var g = Corridor();
        Add(g, "S1", "red", "recy", 0);
        Add(g, "B1", "blue", "rifle", 3);

        var dist = MechanicsService.SupplyDistances(g)["red"];
        dist.ContainsKey("4,0").Should().BeFalse("敌占格断链：其后再无通路，不可达格不入表由调用方按宽松默认处理");
        dist.ContainsKey("2,0").Should().BeTrue();
    }

    [Fact]
    public void Contested_Hex_Cuts_Nobody()
    {
        var g = Corridor();
        Add(g, "S1", "red", "recy", 0);
        Add(g, "R1", "red", "rifle", 3);
        Add(g, "B1", "blue", "rifle", 3);

        DistRed(g, 4).Should().Be(4, "两方同格 = 争夺中，谁都不算占，也就谁都不能靠它断链");
        DistRed(g, 5).Should().Be(5);
    }

    // ───────── 补给源 ─────────

    [Fact]
    public void Logistics_And_Structures_Are_Both_Supply_Sources()
    {
        var g = Corridor();
        Add(g, "S1", "red", "recy", 0);
        Add(g, "A1", "blue", "rifle", 6);
        Add(g, "R1", "red", "radar", 4); // 固定设施（cls=structure）

        DistRed(g, 0).Should().Be(0);
        DistRed(g, 4).Should().Be(0, "固定设施本身就是囤积点，不必再往前沿拖补给线");
        DistRed(g, 5).Should().Be(1);
    }

    [Fact]
    public void Airport_Node_Needs_Own_Ground_Troops_And_Air_units()
    {
        var g = Corridor();
        SetCell(g, 4, 0, new Cell { Terrain = "clear", Airport = 1 });
        Add(g, "S1", "red", "recy", 0);
        Add(g, "I1", "red", "rifle", 4);

        DistRed(g, 4).Should().Be(4, "只有地面部队站场、没有空运机组 ⇒ 场站只是块普通地");

        var jet = Add(g, "J1", "red", "fighter", 1);
        DistRed(g, 4).Should().Be(0, "夺下场站 + 该方仍有航空兵 ⇒ 前进补给节点，距离归 0");

        g.Units.Remove(jet);
        DistRed(g, 4).Should().Be(4, "航空兵撤离后空运断供，节点失效");
    }

    [Fact]
    public void Airborne_Unit_Over_A_Hex_Neither_Holds_Nor_Cuts_It()
    {
        var g = Corridor();
        Add(g, "S1", "red", "recy", 0);
        Add(g, "J1", "blue", "fighter", 3);

        DistRed(g, 4).Should().Be(4, "过境飞机不占地，不能凭一次飞越就掐断对方后勤");
    }

    // ───────── 补给系数 ─────────

    [Fact]
    public void Supply_Rate_Decays_By_Retention_And_Floors_At_Five_Percent()
    {
        var g = Corridor();
        Add(g, "S1", "red", "recy", 0);
        Add(g, "B1", "blue", "rifle", 3); // 掐断 3 号格之后

        var supply = MechanicsService.ComputeSupply(g)["red"];
        supply["0,0"].Should().Be(1.0);
        supply["1,0"].Should().BeApproximately(0.95, 1e-9, "非 strict_supply 一律按 0.95 宽松衰减");
        supply["2,0"].Should().BeApproximately(0.95 * 0.95, 1e-9);
        supply.ContainsKey("4,0").Should().BeFalse("不可达格不入表，宽松默认 0.7 由消费方给，不能在源上写死 0.05");
    }

    [Fact]
    public void Strict_Supply_Uses_The_Rule_Retention()
    {
        var g = Corridor();
        g.Flags["strict_supply"] = true;
        Add(g, "S1", "red", "recy", 0);

        var rate = MechanicsService.ComputeSupply(g)["red"]["3,0"];
        rate.Should().BeApproximately(Math.Pow(g.Rules.SupplyRetention, 3), 1e-9);
    }

    // ───────── 电网 ─────────

    [Fact]
    public void Power_Needs_Plant_And_A_Substation_Together()
    {
        var g = Corridor();
        SetCell(g, 1, 0, new Cell { Terrain = "clear", PowerPlant = true });
        SetCell(g, 5, 0, new Cell { Terrain = "clear", Substation = true });
        Add(g, "S1", "red", "recy", 0);
        Add(g, "P1", "red", "rifle", 1);

        MechanicsService.PowerFactor(g, "red").Should().Be(1.0, "只夺电厂不发电");

        Add(g, "P2", "red", "rifle", 5);
        MechanicsService.PowerFactor(g, "red").Should().BeApproximately(1.15, 1e-9, "电厂+变电站齐 ⇒ 满网通电，红利封顶 +15%");
        MechanicsService.PowerFactor(g, "blue").Should().Be(1.0, "未掌握电网的一方不吃红利");
    }

    [Fact]
    public void Contested_Grid_Node_Does_Not_Energize()
    {
        var g = Corridor();
        SetCell(g, 1, 0, new Cell { Terrain = "clear", PowerPlant = true });
        SetCell(g, 5, 0, new Cell { Terrain = "clear", Substation = true });
        Add(g, "S1", "red", "recy", 0);
        Add(g, "P1", "red", "rifle", 1);
        Add(g, "Q1", "red", "rifle", 5);
        Add(g, "Q2", "blue", "rifle", 5);

        MechanicsService.PowerFactor(g, "red").Should().Be(1.0, "变电站争夺中不算占 ⇒ 半边电网，不通电");
    }

    [Fact]
    public void Partial_Grid_Hold_Scales_The_Bonus()
    {
        var g = Corridor();
        SetCell(g, 1, 0, new Cell { Terrain = "clear", PowerPlant = true });
        SetCell(g, 5, 0, new Cell { Terrain = "clear", Substation = true });
        SetCell(g, 6, 0, new Cell { Terrain = "clear", Substation = true });
        Add(g, "S1", "red", "recy", 0);
        Add(g, "P1", "red", "rifle", 1);
        Add(g, "P2", "red", "rifle", 5);
        Add(g, "P3", "blue", "rifle", 6);

        // 3 个电网节点、红握 2 个（含电厂）⇒ 1 + 0.15×2/3
        MechanicsService.PowerFactor(g, "red").Should().BeApproximately(1.1, 1e-9);
    }

    [Fact]
    public void Grid_Bonus_Flows_Into_Supply()
    {
        var g = Corridor();
        SetCell(g, 1, 0, new Cell { Terrain = "clear", PowerPlant = true });
        SetCell(g, 2, 0, new Cell { Terrain = "clear", Substation = true });
        Add(g, "S1", "red", "recy", 0);

        var dry = MechanicsService.ComputeSupply(g)["red"]["6,0"];

        Add(g, "P1", "red", "rifle", 1);
        Add(g, "P2", "red", "rifle", 2);
        var wet = MechanicsService.ComputeSupply(g)["red"]["6,0"];

        wet.Should().BeGreaterThan(dry, "电网系数进补给：通电让远端最弱格上浮");
        wet.Should().BeApproximately(dry * 1.15, 1e-9);
    }

    [Fact]
    public void Gridless_Map_Gives_No_Bonus()
    {
        var g = Corridor();
        Add(g, "S1", "red", "recy", 0);
        Add(g, "P1", "red", "rifle", 1);

        MechanicsService.PowerFactor(g, "red").Should().Be(1.0);
    }

    // ───────── 要点归属 ─────────

    [Fact]
    public void Node_Holders_Exclude_Contested_And_Filter_By_Side()
    {
        var g = Corridor();
        SetCell(g, 1, 0, new Cell { Terrain = "clear", City = 2 });
        SetCell(g, 2, 0, new Cell { Terrain = "clear", RailStation = true });
        Add(g, "P1", "red", "rifle", 1);
        Add(g, "Q1", "red", "rifle", 2);
        Add(g, "Q2", "blue", "rifle", 2);

        var all = MechanicsService.NodeHoldersByHex(g);
        all.Should().ContainKey("1,0");
        all.ContainsKey("2,0").Should().BeFalse("争夺中不算占");

        var redOnly = MechanicsService.NodeHoldersByHex(g, onlySide: "red");
        redOnly.Keys.Should().BeEquivalentTo(new[] { "1,0" }, "双盲视图只披露己方占领，敌方占了哪个要点是情报");
        MechanicsService.NodeHoldersByHex(g, onlySide: "blue").Should().BeEmpty();
    }

    [Fact]
    public void Objective_Kinds_Precede_In_The_Doc_Order()
    {
        var g = Corridor();
        SetCell(g, 1, 0, new Cell { Terrain = "clear", City = 1 });
        SetCell(g, 2, 0, new Cell { Terrain = "clear", Airport = 2 });
        SetCell(g, 3, 0, new Cell { Terrain = "clear", Substation = true });
        SetCell(g, 4, 0, new Cell { Terrain = "clear", PowerPlant = true });

        MechanicsService.ObjectiveNodesGeo(g).Select(n => n["kind"]).Should().BeEquivalentTo(
            new[] { "power_plant", "substation", "airport_major", "city" }, "要点清单按类别序，前端图例才稳定");
    }
}
