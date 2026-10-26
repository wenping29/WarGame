using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P4 回归：兵种属性覆盖要「改了就要付钱」（设计文档 §6.4 实测 26 → 45.9 / 20 → 22.1），
/// 并且覆盖后的定义必须真的进引擎状态与预算，不能只停在向导里。
/// </summary>
public class TypeDefOverrideP4Tests
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static Dictionary<string, JsonElement> Patch(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, WireJson)!;

    private static UnitTypeDef Tank => LibraryService.UNIT_TYPES["tank"];
    private static UnitTypeDef Arty => LibraryService.UNIT_TYPES["arty"];

    [Fact]
    public void Pricing_Doubles_Tank_Firepower_To_45_9_Points()
    {
        // 文档实测：坦克火力 24→48、机动 11→14、消耗 3→1 ⇒ 26 → 45.9 点
        var patch = Patch("""{"id":"tank","firepower":48,"mobility":14,"consumption":1}""");
        UnitTypeDefService.PricedPoints(Tank, patch).Should().Be(45.9);
    }

    [Fact]
    public void Pricing_Trims_Arty_Mobility_To_22_1_Points()
    {
        // 文档实测：炮兵火力 30→42、机动 6→4 ⇒ 20 → 22.1 点
        var patch = Patch("""{"id":"arty","firepower":42,"mobility":4}""");
        UnitTypeDefService.PricedPoints(Arty, patch).Should().Be(22.1);
    }

    [Fact]
    public void Pricing_Keeps_Explicit_Points_And_Load_Costs_Nothing()
    {
        var patch = Patch("""{"id":"tank","load":4.0}""");
        // 负荷是「负担」：调高反而便宜（结算里真的扣 MP），不该按涨价处理 ⇒ 26 × (1 − 0.12×(4−2.6)/2.6)
        UnitTypeDefService.PricedPoints(Tank, patch).Should().Be(24.3);

        var priced = Patch("""{"id":"tank","firepower":48,"points":30}""");
        UnitTypeDefService.Apply(Tank, priced).Points.Should().Be(30, "显式标价不再二次计价");
    }

    [Fact]
    public void Pricing_Clamps_Multiplier_To_0_35_And_2_6()
    {
        var nerf = Patch("""{"id":"tank","firepower":0,"protection":0.05,"mobility":0,"recon":0,"personnel":1}""");
        UnitTypeDefService.PricedPoints(Tank, nerf).Should().Be(Math.Round(26 * 0.35, 1));

        var buff = Patch("""{"id":"tank","firepower":120,"protection":8,"mobility":60,"recon":20,"personnel":5000}""");
        UnitTypeDefService.PricedPoints(Tank, buff).Should().Be(Math.Round(26 * 2.6, 1));
    }

    [Fact]
    public void Apply_Writes_Stats_But_Keeps_Rule_Semantics()
    {
        var patch = Patch("""{"id":"tank","firepower":48,"mobility":14,"consumption":1,"anti_air":2,"range":5}""");
        var t = UnitTypeDefService.Apply(Tank, patch);
        t.Firepower.Should().Be(48);
        t.Mobility.Should().Be(14);
        t.Consumption.Should().Be(1);
        t.AntiAir.Should().Be(2);
        t.Range.Should().Be(5);
        t.Cls.Should().Be(Tank.Cls);
        t.IsHQ.Should().Be(Tank.IsHQ);
        t.Counters.Should().Equal(Tank.Counters);
    }

    [Fact]
    public void Validate_Rejects_Unknown_Type_And_Non_Whitelisted_Fields()
    {
        UnitTypeDefService.Validate(new[] { Patch("""{"id":"ufo","firepower":1}""") })
            .Should().Equal("兵种属性覆盖只允许改既有兵种，未知兵种 ufo");

        UnitTypeDefService.Validate(new[] { Patch("""{"id":"tank","cls":"air","is_hq":true}""") })
            .Should().Equal("兵种 tank 不允许覆盖字段 cls", "兵种 tank 不允许覆盖字段 is_hq");
    }

    [Fact]
    public void Validate_Rejects_Out_Of_Range_And_Non_Numeric_Values()
    {
        UnitTypeDefService.Validate(new[] { Patch("""{"id":"tank","firepower":200}""") })
            .Should().Equal("兵种 tank 的 firepower=200 超出允许区间 0.0–120.0");

        UnitTypeDefService.Validate(new[] { Patch("""{"id":"tank","mobility":"fast"}""") })
            .Should().Equal("兵种 tank 的 mobility 必须是数值");

        UnitTypeDefService.Validate(new[] { Patch("""{"id":"tank","points":0}""") })
            .Should().Equal("兵种 tank 的显式点数必须大于 0");
    }

    [Fact]
    public void Scenario_Overrides_Reach_The_Game_State_And_The_Priced_Oob()
    {
        var root = RepoRoot();
        var raw = File.ReadAllText(Path.Combine(root, "data", "scenarios", "rhine_corridor.json"));
        var node = JsonNode.Parse(raw)!.AsObject();
        node["unit_type_defs"] = (JsonArray)JsonNode.Parse("""[{"id":"tank","firepower":48,"mobility":14,"consumption":1}]""")!;

        var cfg = ScenarioService.LoadFromJson(node.ToJsonString());
        var g = ScenarioService.CreateGameState(cfg, Path.Combine(root, "data", "gis"), 7);

        g.UnitTypes["tank"].Firepower.Should().Be(48);
        g.UnitTypes["tank"].Points.Should().Be(45.9);
        var tankUnit = g.Units.First(u => u.UnitType == "tank");
        g.TypeOf(tankUnit).Firepower.Should().Be(48);
        g.TypeOf(tankUnit).Points.Should().Be(45.9);
        // 没被覆盖的兵种仍是出厂价
        g.UnitTypes["rifle"].Points.Should().Be(LibraryService.UNIT_TYPES["rifle"].Points);
    }

    [Fact]
    public void Type_Points_Reports_Only_Overridden_Types()
    {
        var patches = new[] { Patch("""{"id":"tank","firepower":48}"""), Patch("""{"id":"arty","mobility":4}""") };
        var eff = UnitTypeDefService.EffectiveLibrary(patches);
        UnitTypeDefService.TypePoints(patches, eff).Should()
            .Equal(new Dictionary<string, double> { ["tank"] = 40.3, ["arty"] = 17.7 });
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "scenarios", "rhine_corridor.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 data/scenarios/rhine_corridor.json");
    }
}
