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
/// 剧本格号口径 oob_space：绑定服务器六角格包后，部队和胜利要点不能再挤在西南角。
/// 剧本的 at/target 是在 environment 那块小棋盘上排版的，包通常大好几倍；
/// 声明 oob_space=environment 时按"中心对齐 + 等比拉伸"铺满实际棋盘，缺省（board）一律按绝对格号原样落位，
/// 这样向导里经纬度换算出来的格号不会被二次映射。
/// </summary>
public sealed class OobHexSpaceTests : IDisposable
{
    private const string PkgId = "hg_oob_space_test";
    private const int EnvW = 26, EnvH = 20, BoardW = 52, BoardH = 40;
    private readonly string _gridDir;

    public OobHexSpaceTests()
    {
        _gridDir = Path.Combine(Path.GetTempPath(), "hexfront-oob-" + Guid.NewGuid().ToString("N")[..8]);
        // 全 clear 的 52×40 网络包（正好是剧本 environment 的两倍）：落位不受地形通阻干扰，格号可以逐格钉死
        HexGridPackageService.Save(_gridDir, new HexGridDoc
        {
            Id = PkgId, Name = "oob_space 测试包", Rows = BoardH, Cols = BoardW,
            FlatToFlat = 1000.0, West = 6.80, South = 50.90, DefaultTerrain = "clear",
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_gridDir, true); } catch (IOException) { /* 临时目录删不掉不影响结论 */ }
    }

    /// <summary>与引擎同一套拉伸口径：两端格心对齐，中间等比，四舍五入后截进棋盘。</summary>
    private static int Axis(int v, int from, int to) =>
        Math.Clamp((int)Math.Round((v - (from - 1) / 2.0) * (to - 1.0) / (from - 1) + (to - 1) / 2.0,
            MidpointRounding.AwayFromZero), 0, to - 1);

    private static string Want(int q, int r) => $"{Axis(q, EnvW, BoardW)},{Axis(r, EnvH, BoardH)}";

    private static string Syn(string? space, bool bound) => $$"""
    {
      "id": "synthetic_oob_space",
      "name": "格号口径测试",
      "max_turns": 6,
      "environment": { "width": 26, "height": 20, "cell_km": 2.0, "center_lonlat": [6.95, 51.42], "height_seed": 11 },
      "oob_space": {{(space == null ? "null" : "\"" + space + "\"")}},
      "hexgrid": {{(bound ? "\"" + PkgId + "\"" : "null")}},
      "sides": {
        "red":  { "name": "红方", "oob": [ { "type": "rifle", "qty": 3, "at": [0, 0] } ] },
        "blue": { "name": "蓝方", "oob": [ { "type": "rifle", "qty": 1, "at": [25, 19] } ] }
      },
      "victory": [ { "kind": "capture", "side": "red", "target": "13,6" } ]
    }
    """;

    private GameState BuildSyn(bool bound, string? space) => ScenarioService.CreateGameState(
        ScenarioService.LoadFromJson(Syn(space, bound)), RealScenario.GisDirectory, 20261028,
        hexGridDir: _gridDir);

    private static IEnumerable<Hex> OobPositions(GameState g, string side) =>
        g.Units.Where(u => u.Side == side).Select(u => u.Pos);

    [Fact]
    public void Environment_Space_Stretches_Oob_Onto_The_Real_Board()
    {
        var g = BuildSyn(bound: true, "environment");

        g.Grid.Width.Should().Be(BoardW);
        g.Grid.Height.Should().Be(BoardH);
        // 红方一个编制 3 个：排版时是 (0,0)(1,0)(2,0) 三个相邻格，铺满后仍是相邻队形、间距同步放大
        OobPositions(g, "red").Should().Equal(new Hex(0, 0), new Hex(2, 0), new Hex(4, 0));
        // 蓝方压在剧本小棋盘东南角 (25,19)，铺满后必须落到实际棋盘那头
        OobPositions(g, "blue").Should().Equal(new Hex(BoardW - 1, BoardH - 1));
    }

    [Fact]
    public void Capture_Target_Travels_With_The_Troops()
    {
        // 要点格不跟着映射的话，部队铺满了、要夺取的格还留在原来的角落，终局条件就悬空了
        var g = BuildSyn(bound: true, "environment");

        g.VictoryConds.Single().Target.Should().Be(Want(13, 6)).And.NotBe("13,6");
        g.Grid.Contains(Hex.Parse(g.VictoryConds.Single().Target!)).Should().BeTrue();
    }

    [Fact]
    public void Unbound_Scenario_Keeps_Its_Oob_Untouched()
    {
        var g = BuildSyn(bound: false, "environment");

        g.Grid.Width.Should().Be(EnvW);
        // 棋盘就是 environment 那块，映射必须退化成恒等，历史剧本开局一格都不能动
        // （程序化地形可能有河/山挡住格心，SpawnAt 只在 4 格内找落脚位，所以按 4 格容差钉"没被拉伸"）
        var want = new[] { new Hex(0, 0), new Hex(1, 0), new Hex(2, 0) };
        var got = OobPositions(g, "red").ToList();
        got.Should().HaveCount(want.Length);
        for (var i = 0; i < want.Length; i++) got[i].DistanceTo(want[i]).Should().BeLessThanOrEqualTo(4);
        g.Units.Should().OnlyContain(u => u.Pos.Q < EnvW && u.Pos.R < EnvH);
        g.VictoryConds.Single().Target.Should().Be("13,6");
    }

    [Fact]
    public void Absent_Space_Is_Board_Coords_And_Not_Rescaled()
    {
        // 缺省口径 = 实际棋盘绝对格号（向导写死的就是这个口径），绑了大包也照原格号落子
        var g = BuildSyn(bound: true, null);

        OobPositions(g, "red").Should().Equal(new Hex(0, 0), new Hex(1, 0), new Hex(2, 0));
        OobPositions(g, "blue").Should().Equal(new Hex(25, 19));
        g.VictoryConds.Single().Target.Should().Be("13,6");
    }

    [Fact]
    public void Board_Space_Is_Distinct_From_The_Environment_Space()
    {
        var stretched = BuildSyn(bound: true, "environment");
        var absolute = BuildSyn(bound: true, "board");

        absolute.Units.Select(u => u.Pos.Q).Max().Should().BeLessThan(stretched.Units.Select(u => u.Pos.Q).Max(),
            "board 口径若不铺满，蓝方会整团挤在西侧");
    }

    [Fact]
    public void Real_Scenario_No_Longer_Crams_The_Force_In_A_Corner()
    {
        var cfg = ScenarioService.MergeOverrides(RealScenario.ScenarioJson,
            RealScenario.Overrides($$"""{ "hexgrid": "{{PkgId}}", "oob_space": "environment" }"""));
        var g = ScenarioService.CreateGameState(cfg, RealScenario.GisDirectory, 20261028, hexGridDir: _gridDir);

        g.Grid.Width.Should().Be(BoardW);
        var qs = g.Units.Select(u => u.Pos.Q).ToList();
        var rs = g.Units.Select(u => u.Pos.R).ToList();
        g.Units.Should().OnlyContain(u => g.Grid.Contains(u.Pos), "铺满后仍要落在棋盘内");

        // 红方原排板在西侧、蓝方在东侧：等比铺满后两翼各自顶到实际棋盘两头，不再是同一角
        qs.Min().Should().BeLessThanOrEqualTo((int)(BoardW * 0.15));
        qs.Max().Should().BeGreaterThanOrEqualTo((int)(BoardW * 0.85));
        (qs.Max() - qs.Min()).Should().BeGreaterThan(EnvW, "部队横向铺满度要明显好于 26 格的小棋盘");
        (rs.Max() - rs.Min()).Should().BeGreaterThan(EnvH / 2, "纵向同样要铺开，红蓝两翼不再叠在同一角");

        var target = g.VictoryConds.Single(v => v.Kind == "capture").Target!;
        target.Should().Be(Want(18, 10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("board")]
    [InlineData("environment")]
    public void Validate_Accepts_The_Whitelisted_Spaces(string? space)
    {
        ScenarioService.Validate(ScenarioService.LoadFromJson(Syn(space, bound: false)))
            .Should().NotContain(e => e.Contains("oob_space"));
    }

    [Fact]
    public void Validate_Rejects_A_Typo_In_The_Space_Key()
    {
        // 写错口径不会报错只会静默按绝对格号落子，部队挤在角上很难查，所以必须亮红
        var errs = ScenarioService.Validate(ScenarioService.LoadFromJson(Syn("env", bound: true)));

        errs.Should().ContainSingle(e => e.Contains("oob_space"));
    }
}
