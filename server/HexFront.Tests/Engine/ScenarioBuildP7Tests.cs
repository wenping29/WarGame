using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P7-a 回归：剧本 → 副本这一段（校验、OOB 人员水平、覆盖合并、点数预算、要点胜利）。
/// 一律用随仓剧本 rhine_corridor 真建局：自造 GameState 锁不住"剧本字段有没有真的落进引擎"，
/// 而这一段恰恰是向导提交的 JSON 与引擎之间唯一的接缝。
/// 对应 verify.dart 第 1/2/3/4/16/17/18/19/20/23–29/39/40/41/53/59/60/61 项。
/// </summary>
public class ScenarioBuildP7Tests
{
    /// <summary>取某一方的 HQ 单位：剧本里每方只有一个，指挥官信息只写在它的 OOB 条目上。</summary>
    private static Unit HeadquartersOf(GameState g, string side) =>
        g.Units.Single(u => u.Side == side && u.IsHQ);

    [Fact]
    public void Rhine_Corridor_Builds_A_Valid_Full_Battlefield()
    {
        var cfg = ScenarioService.LoadFromFile(RealScenario.ScenarioJsonPath);

        ScenarioService.Validate(cfg).Should().BeEmpty("随仓剧本必须开箱可校验");

        var g = ScenarioService.CreateGameState(cfg, RealScenario.GisDirectory);

        g.Units.Count.Should().BeGreaterThanOrEqualTo(15, "两侧 OOB 合计要真生成出兵力");
        g.Cells.Count.Should().BeGreaterThanOrEqualTo(400, "真实 GIS 六角格不能只落一小片");
        (g.Grid.Width * g.Grid.Height).Should().Be(g.Cells.Count, "格网尺寸与格数都取自数据包 meta，不能一半来自剧本 env");

        var badSpawn = g.Units
            .Where(u =>
            {
                var cell = g.GetCell(u.Pos);
                if (cell == null) return true;
                var td = g.TerrainOf(u.Pos);
                return td.Water || td.Impassable;
            })
            .Select(u => $"{u.Id}@{u.Pos.Key}")
            .ToList();
        badSpawn.Should().BeEmpty("剧本坐标可能落进水/绝壁格，SpawnAt 必须把单位挪到最近的可通行陆地");
    }

    [Fact]
    public void Oob_Commander_Lands_On_The_Unit_And_Absent_Commander_Uses_Neutral_Quality()
    {
        var g = RealScenario.Build();

        var redHq = HeadquartersOf(g, "red");
        redHq.CommanderName.Should().Be("崔可夫");
        redHq.CommanderQuality.Should().Be(0.75);

        var blueHq = HeadquartersOf(g, "blue");
        blueHq.CommanderName.Should().Be("冯·克勒格");
        blueHq.CommanderQuality.Should().Be(0.65, "剧本写的是 0–1 能力值，不是百分制");

        // E3 情报会带指挥官，缺省必须是"平庸指挥官"而不是 0 —— 否则兵种默认战力被悄悄改掉
        var plain = g.Units.First(u => u.UnitType == "tank");
        plain.CommanderName.Should().BeNull();
        plain.CommanderQuality.Should().Be(0.5);
    }

    [Fact]
    public void Overrides_Swap_Oob_Personnel_Weather_And_Flags()
    {
        var g = RealScenario.Build(RealScenario.Overrides("""
        {
          "sides": {
            "red": {
              "oob": [
                { "type": "tank", "qty": 4, "at": [6, 7], "training": 92, "experience": 88, "morale": 95 },
                { "type": "recy", "qty": 2, "at": [3, 8] }
              ]
            }
          },
          "weather": { "initial": "fog", "mode": "fixed" },
          "flags": { "strict_supply": true }
        }
        """));

        g.Units.Count(u => u.Side == "red" && u.Alive).Should().Be(6, "oob 数组整块替换剧本默认兵力");
        g.Units.Count(u => u.Side == "blue").Should().BeGreaterThan(0, "覆盖只动 red，蓝方仍继承剧本");

        g.Weather.Should().Be("fog");
        LibraryService.GetWeather(g.Weather).Label.Should().Be("浓雾");
        g.WeatherMode.Should().Be("fixed");
        FogService.FlagBool(g, "strict_supply", false).Should().BeTrue();

        foreach (var tank in g.Units.Where(u => u.UnitType == "tank" && u.Side == "red"))
        {
            tank.Training.Should().Be(92);
            tank.Experience.Should().Be(88);
            tank.Morale.Should().Be(95);
        }
    }

    [Fact]
    public void Point_Budget_Is_Auditable_And_Overrun_Get_Reported()
    {
        var eff = LibraryService.UNIT_TYPES;
        ScenarioService.OobPoints(eff, new[]
        {
            new ScenarioService.OobEntry { Type = "tank", Qty = 40, At = new[] { 6, 7 } }
        }).Should().BeGreaterThan(1000, "点数按覆盖后的定义库可核算（doc §5 预算约束的前提）");

        var greedy = RealScenario.Config("""
        {
          "sides": {
            "red": { "oob": [ { "type": "tank", "qty": 40, "at": [6, 7] } ] }
          }
        }
        """);

        ScenarioService.Validate(greedy).Should().ContainSingle()
            .Which.Should().MatchRegex(@"阵营 red OOB 点数 \d+\.?\d* 超出预算 320\.0");
    }

    /// <summary>
    /// 地物矢量要真的写进引擎格网（verify §9-39/40/41）：字段读漏了不会报错，
    /// 只是铁路不给折扣、电厂不发电、机场不当补给节点 —— 玩法静默消失。
    /// </summary>
    [Fact]
    public void Ground_Features_From_The_Gis_Package_Reach_The_Engine_Grid()
    {
        var g = RealScenario.Build(RealScenario.Overrides("""{ "gis_package": "rhine_ruhr_2p5km.json" }"""));

        int Count(Func<Cell, bool> p) => g.Cells.Values.Count(p);
        Count(c => c.Railway).Should().BeGreaterThan(0);
        Count(c => c.Tunnel).Should().BeGreaterThan(0);
        Count(c => c.Powerline).Should().BeGreaterThan(0);
        Count(c => c.PowerPlant).Should().BeGreaterThan(0);
        Count(c => c.Substation).Should().BeGreaterThan(0);
        Count(c => c.Airport > 0).Should().BeGreaterThan(0);
        g.Cells.Values.Should().OnlyContain(c => c.Airport <= 2, "机场等级只用 1/2（野战/干线），第 3 档前端没有画法");

        var broken = g.Cells.Where(e =>
        {
            var c = e.Value;
            return c.RailStation && !c.Railway
                || c.Bridge && !c.River
                || c.Tunnel && !(c.Railway || c.Road)
                || c.Tunnel && g.TerrainOf(Hex.Parse(e.Key)).Water;
        }).Select(e => e.Key).ToList();
        broken.Should().BeEmpty("车站必在铁路上、桥必跨河、隧道在线上且不在水面（doc §6.4 地物不变式）");

        // 存档走 snake_case：键名对不上时字段会静默留空，地物玩法在续局后当场消失
        var wire = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        var back = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(g, wire), wire)!;
        Count(c => c.Railway).Should().Be(back.Cells.Values.Count(c => c.Railway));
        Count(c => c.Tunnel).Should().Be(back.Cells.Values.Count(c => c.Tunnel));
        Count(c => c.Airport > 0).Should().Be(back.Cells.Values.Count(c => c.Airport > 0));
    }

    /// <summary>
    /// 几何必须整份取自数据包 meta（verify §7-23~29）：半径/原点若退回 env 默认，
    /// 引擎格网与在线瓦片就不再同一墨卡托框架，表现是兵力块随缩放漂移，很难归因。
    /// </summary>
    [Fact]
    public void Geo_Geometry_Is_Inherited_From_The_Data_Package()
    {
        var cfg = ScenarioService.LoadFromFile(RealScenario.ScenarioJsonPath);
        var g = RealScenario.Build();

        var pkg = JsonSerializer.Deserialize<HexPackage>(
            File.ReadAllText(Path.Combine(RealScenario.GisDirectory, cfg.GisPackage!)),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
            })!;

        g.Grid.Width.Should().Be(pkg.Meta.Width);
        g.Grid.Height.Should().Be(pkg.Meta.Height);
        g.Cells.Count.Should().Be(pkg.Meta.Width * pkg.Meta.Height, "副本格数取自数据包，不是 env 默认值");

        g.Grid.Radius.Should().BeApproximately(pkg.Meta.RadiusM, 1e-6, "引擎半径必须用数据包算出来的那个");
        (g.Grid.FlatToFlat / (pkg.Meta.CellGroundM * pkg.Meta.MercatorScale) - 1.0)
            .Should().BeLessThan(1e-9, "对边距 = 实地口径 × 墨卡托放大，两个口径必须自洽");
        g.Grid.CellGroundKm.Should().BeGreaterThan(0);
        g.Grid.MercatorScale.Should().BeGreaterThan(1.0, "50°N 附近墨卡托放大必然 >1，写成 1 等于把实地当屏幕距离");

        g.Grid.Ox.Should().BeApproximately(pkg.Meta.MercatorOx, 1e-6);
        g.Grid.Oy.Should().BeApproximately(pkg.Meta.MercatorOy, 1e-6);

        var (cx, cy) = Mercator.LonLatToMercator(pkg.Meta.CenterLonLat[0], pkg.Meta.CenterLonLat[1]);
        var back = g.Grid.Nearest(cx, cy);
        g.Grid.Contains(back).Should().BeTrue("剧本中心经纬度必须落回格网之内");
        Math.Abs(back.Q - (pkg.Meta.Width - 1) / 2.0).Should().BeLessThanOrEqualTo(1.0);
        Math.Abs(back.R - (pkg.Meta.Height - 1) / 2.0).Should().BeLessThanOrEqualTo(1.0);

        // geo 进存档：续局若把半径丢了，同一份剧本在续局后画出的格网就不是原来那张
        var wire = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        var rt = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(g, wire), wire)!;
        rt.Grid.Radius.Should().Be(g.Grid.Radius);
        rt.Grid.CellGroundKm.Should().Be(g.Grid.CellGroundKm);
        rt.Grid.Ox.Should().Be(g.Grid.Ox);
    }

    [Fact]
    public void Holding_Three_Battlefield_Nodes_Ends_The_Game_For_Red()
    {
        var g = RealScenario.Build(RealScenario.Overrides("""
        {
          "gis_package": "rhine_ruhr_2p5km.json",
          "victory": [ { "kind": "nodes", "side": "red", "target": null, "amount": 3, "turns": 0 } ]
        }
        """));

        var occupied = g.Units.Where(u => u.Alive).Select(u => u.Pos.Key).ToHashSet();
        var freeNodes = MechanicsService.ObjectiveNodesGeo(g)
            .Where(n => !occupied.Contains((string)n["key"]!))
            .Take(3)
            .Select(n => (string)n["key"]!)
            .ToList();
        freeNodes.Should().HaveCount(3, "鲁尔数据包要有足够多无人占据的要点，否则这条用例测不到东西");

        var ground = g.Units
            .Where(u => u.Side == "red" && u.Alive && !g.TypeOf(u).IsAir)
            .Take(freeNodes.Count)
            .ToList();
        for (var i = 0; i < freeNodes.Count; i++) ground[i].Pos = Hex.Parse(freeNodes[i]);
        MechanicsService.NodeHoldersByHex(g, "red").Count.Should().BeGreaterThanOrEqualTo(3);

        TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
        {
            ["red"] = new(),
            ["blue"] = new(),
        });

        g.Over.Should().BeTrue("同时掌握够多处战场要点即达成（doc §6.4 kind=nodes）");
        g.Winner.Should().Be("red");
    }

    /// <summary>
    /// 带地物的真实地图要能连续推演（verify §9-61）：1645 格把补给 BFS 与观测刷新推到另一个量级，
    /// 结算里任何一处只在稠密格网下才踩到的空引用，都只能在这张图上暴露。
    /// </summary>
    [Fact]
    public void Auto_Battle_Runs_On_The_Feature_Map_Without_Stalling()
    {
        var g = RealScenario.Build(RealScenario.Overrides("""{ "gis_package": "rhine_ruhr_2p5km.json" }"""));
        g.Cells.Count.Should().BeGreaterThan(1000, "这张图的意义就是稠密地物格网，退回小图等于没测");

        var turnAtStart = g.Turn;
        var fpAtStart = g.Fingerprint();
        for (var t = 0; t < 4; t++)
        {
            TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
            {
                ["red"] = AiService.GenerateOrders(g, "red"),
                ["blue"] = AiService.GenerateOrders(g, "blue"),
            });
        }

        (g.Turn - turnAtStart).Should().Be(4, "四个回合都得真正推进回合号");
        g.Fingerprint().Should().NotBe(fpAtStart, "推了四回合指纹没变 = 结算被静默跳过");
        MechanicsService.ComputeSupply(g)["red"].Should().NotBeEmpty("地物再多也不能把双方都算成断供");
        MechanicsService.ComputeSupply(g)["blue"].Should().NotBeEmpty();
    }
}
