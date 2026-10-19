using System;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// 昼夜交替回归：剧本 clock 夜幕窗口（支持跨零点）→ GameState.DayPhaseOf。
/// 默认窗口必须与出厂 DayPhase.FromHours 逐时刻一致；自定义窗口按
/// 「夜幕前后各 edge 小时为拂晓/黄昏，其余昼间」推算；夜太短按比例压缩过渡带。
/// 每回合推进小时走 rules.turn_hours，控制交替节奏。
/// </summary>
public class DayNightCycleTests
{
    private static GameState Build(string overridesJson) =>
        RealScenario.Build(RealScenario.Overrides(overridesJson));

    [Theory]
    [InlineData(5, "night")]
    [InlineData(6, "dawn")]
    [InlineData(8, "dawn")]
    [InlineData(9, "day")]
    [InlineData(12, "day")]
    [InlineData(16, "day")]
    [InlineData(17, "dusk")]
    [InlineData(19, "dusk")]
    [InlineData(20, "night")]
    [InlineData(23, "night")]
    [InlineData(2, "night")]
    public void Default_Window_Matches_Legacy_Phases(int hour, string phaseId)
    {
        var g = Build("""{ "clock": { "start_hour": 12 } }""");
        g.HourOfDay = hour;
        g.DayPhaseOf().Id.Should().Be(phaseId, $"缺省窗口（夜幕 20–6）下 {hour} 时应为 {phaseId}");
    }

    [Fact]
    public void Custom_Night_Window_Drives_Phases()
    {
        // 夜幕 22 → 8（跨零点）：8 时拂晓、14 时昼间、21 时黄昏、23 时夜间
        var g = Build("""{ "clock": { "start_hour": 12, "night_from": 22, "night_to": 8 } }""");
        g.NightFrom.Should().Be(22);
        g.NightTo.Should().Be(8);

        g.HourOfDay = 9;
        g.DayPhaseOf().Id.Should().Be("dawn");
        g.HourOfDay = 14;
        g.DayPhaseOf().Id.Should().Be("day");
        g.HourOfDay = 21;
        g.DayPhaseOf().Id.Should().Be("dusk");
        g.HourOfDay = 23;
        g.DayPhaseOf().Id.Should().Be("night");
        g.HourOfDay = 3;
        g.DayPhaseOf().Id.Should().Be("night", "跨零点段仍是夜间");
    }

    [Fact]
    public void Short_Night_Compresses_Dawn_And_Dusk()
    {
        // 夜幕 12 → 6：夜长 6 小时，拂晓/黄昏各压缩到 2 小时
        var g = Build("""{ "clock": { "start_hour": 12, "night_from": 12, "night_to": 6 } }""");
        g.HourOfDay = 6;
        g.DayPhaseOf().Id.Should().Be("dawn");
        g.HourOfDay = 8;
        g.DayPhaseOf().Id.Should().Be("day", "拂晓只占 6–8 点");
        g.HourOfDay = 10;
        g.DayPhaseOf().Id.Should().Be("dusk", "黄昏从 10 点开始（夜幕前 2 小时）");
        g.HourOfDay = 12;
        g.DayPhaseOf().Id.Should().Be("night");
    }

    [Fact]
    public void Night_Affects_Recon_And_Combat_Factors()
    {
        // 夜间侦察 0.45 / 战斗 0.75，昼间 1.0 —— 自定义夜幕窗口必须真实作用于机制
        var night = Build("""{ "clock": { "start_hour": 23, "night_from": 22, "night_to": 8 } }""");
        night.DayPhaseOf().Recon.Should().Be(0.45);
        night.DayPhaseOf().Combat.Should().Be(0.75);

        var day = Build("""{ "clock": { "start_hour": 12, "night_from": 22, "night_to": 8 } }""");
        day.DayPhaseOf().Recon.Should().Be(1.0);
        day.DayPhaseOf().Combat.Should().Be(1.0);
    }

    [Fact]
    public void Turn_Hours_Controls_Alternation_Pace()
    {
        // 每回合推进 12 小时：一回合就完成昼↔夜切换
        var g = Build("""
        { "clock": { "start_hour": 6 }, "rules": { "turn_hours": 12 } }
        """);
        g.Rules.TurnHours.Should().Be(12);
        g.DayPhaseOf().Id.Should().Be("dawn", "开局 6 点拂晓");

        TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
        {
            ["red"] = new(), ["blue"] = new(),
        });
        g.HourOfDay.Should().Be(18);
        g.DayPhaseOf().Id.Should().Be("dusk", "推进 12 小时到 18 点");
    }

    [Fact]
    public void Validate_Rejects_Bad_Clock_Config()
    {
        var errs = ScenarioService.Validate(RealScenario.Config("""
        {
          "clock": { "start_hour": 25, "night_from": 24, "night_to": -1 }
        }
        """));
        errs.Should().Contain(e => e.Contains("起始时刻 25"));
        errs.Should().Contain(e => e.Contains("夜幕起始 24"));
        errs.Should().Contain(e => e.Contains("夜幕结束 -1"));

        var same = ScenarioService.Validate(RealScenario.Config("""
        { "clock": { "night_from": 21, "night_to": 21 } }
        """));
        same.Should().Contain(e => e.Contains("夜幕起止相同"));

        var hours = ScenarioService.Validate(RealScenario.Config("""
        { "rules": { "turn_hours": 30 } }
        """));
        hours.Should().Contain(e => e.Contains("每回合推进 30 小时"));
    }

    [Fact]
    public void Clone_Copies_Night_Window()
    {
        var g = Build("""{ "clock": { "night_from": 21, "night_to": 5 } }""");
        var copy = g.Clone();
        copy.NightFrom.Should().Be(21);
        copy.NightTo.Should().Be(5);
    }
}
