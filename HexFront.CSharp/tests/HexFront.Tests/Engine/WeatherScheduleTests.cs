using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// 天气计划（weather.schedule）回归：剧本预排 → 开局只装载不启动；
/// 执行中按回合区间强制切换、暂停冻结、删除回到原演变模式。
/// 校验走与向导同一份 Validate，区间非法/天气未知/重叠都要被挡下。
/// </summary>
public class WeatherScheduleTests
{
    private static GameState BuildWithSchedule(uint seed = 20261016) => RealScenario.Build(RealScenario.Overrides("""
        {
          "weather": {
            "initial": "clear",
            "mode": "fixed",
            "schedule": [
              { "from": 2, "to": 3, "weather": "rain" },
              { "from": 4, "to": 6, "weather": "storm" }
            ]
          }
        }
        """), seed);

    [Fact]
    public void Scenario_Schedule_Loads_But_Stays_Off()
    {
        var g = BuildWithSchedule();

        g.WeatherSchedule.Should().HaveCount(2, "剧本预排的两段计划都要落进引擎");
        g.WeatherSchedule![0].FromTurn.Should().Be(2);
        g.WeatherSchedule![0].Weather.Should().Be("rain");
        g.WeatherSchedule![1].ToTurn.Should().Be(6);
        g.WeatherScheduleState.Should().BeNull("剧本只能预排，启动是导演部的专属操作");
        g.Weather.Should().Be("clear", "计划未启动时开局天气仍是 initial");
    }

    [Fact]
    public void Running_Schedule_Forces_Phase_By_Turn_Range()
    {
        var g = BuildWithSchedule();
        g.WeatherScheduleState = "running";

        // 结算末尾演变的是下一回合：Turn=1 时计划 T2-3 雨 → 下回合转雨
        g.Weather = "clear";
        g.Turn = 1;
        MechanicsService.EvolveWeather(g).Should().Be("rain", "下一回合落在 2–3 区间，强制降雨");
        g.Turn = 2;
        MechanicsService.EvolveWeather(g).Should().Be("rain");
        g.Turn = 3;
        MechanicsService.EvolveWeather(g).Should().Be("storm", "下一回合是第 4 回合，切入 4–6 暴雨区间");
        g.Turn = 7;
        MechanicsService.EvolveWeather(g).Should().Be("clear", "区间之外回落原演变模式（fixed → 保持当前）");
    }

    [Fact]
    public void Paused_Schedule_Freezes_Current_Weather()
    {
        var g = BuildWithSchedule();
        g.Weather = "rain";
        g.WeatherScheduleState = "paused";

        // 暂停后即使下一回合落在暴雨区间也不切换，除非导演部恢复/删除
        g.Turn = 3;
        MechanicsService.EvolveWeather(g).Should().Be("rain");
    }

    [Fact]
    public void Clone_Copies_Schedule_And_State()
    {
        var g = BuildWithSchedule();
        g.WeatherScheduleState = "running";

        var copy = g.Clone();
        copy.WeatherSchedule.Should().HaveCount(2);
        copy.WeatherScheduleState.Should().Be("running");

        // 整组替换互不影响（条目不可变，列表浅拷贝即可）
        g.WeatherSchedule = new List<WeatherPhase>();
        copy.WeatherSchedule.Should().HaveCount(2);
    }

    [Fact]
    public void Validate_Rejects_Bad_Ranges_Unknown_Weather_Overlap()
    {
        var cfg = RealScenario.Config("""
        {
          "weather": {
            "schedule": [
              { "from": 0, "to": 3, "weather": "rain" },
              { "from": 3, "to": 4, "weather": "lava" },
              { "from": 4, "to": 4, "weather": "storm" },
              { "from": 999, "to": 1000, "weather": "fog" }
            ]
          }
        }
        """);

        var errs = ScenarioService.Validate(cfg);
        errs.Should().Contain(e => e.Contains("0-3"), "from < 1 非法");
        errs.Should().Contain(e => e.Contains("lava"), "未知天气要报错");
        errs.Should().Contain(e => e.Contains("超出最大回合"), "越界区间要报错");
        errs.Should().Contain(e => e.Contains("重叠"), "3-4 与 4-4 相接即重叠");
    }

    [Fact]
    public void ResolveTurn_Emits_Schedule_Event_When_Weather_Switches()
    {
        var g = BuildWithSchedule(seed: 7);
        g.WeatherScheduleState = "running";

        // Turn 1 结束 → 第 2 回合进入 2-3 降雨区间，必须广播切换事件
        TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
        {
            ["red"] = new(), ["blue"] = new(),
        });

        g.Turn.Should().Be(2);
        g.Weather.Should().Be("rain");
        g.Events.Should().Contain(e => e.Code == "weather_schedule"
            && e.Text.Contains("降雨"), "事件文案用中文标签而不是天气 id");
    }
}
