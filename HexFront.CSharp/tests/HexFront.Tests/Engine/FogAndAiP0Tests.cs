using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P0 回归：迷雾"关雾不留档案"、AI 补令路径合法性。
/// 用随仓剧本 rhine_corridor 真建局，避免自造 GameState 与真实 OOB 脱节。
/// </summary>
public class FogAndAiP0Tests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "scenarios", "rhine_corridor.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 data/scenarios/rhine_corridor.json");
    }

    private static GameState NewGame()
    {
        var root = RepoRoot();
        var cfg = ScenarioService.LoadFromFile(Path.Combine(root, "data", "scenarios", "rhine_corridor.json"));
        return ScenarioService.CreateGameState(cfg, Path.Combine(root, "data", "gis"));
    }

    private static List<Dictionary<string, object?>> ContactsOf(GameState g, string side) =>
        ((IEnumerable<object?>?)FogService.SideView(g, side)["contacts"])!
            .Cast<Dictionary<string, object?>>().ToList();

    [Fact]
    public void Fog_Off_Shows_Everything_But_Writes_No_Archive()
    {
        var g = NewGame();
        g.Flags["fog"] = false;
        FogService.UpdateObservation(g);

        var aliveEnemies = g.Units.Count(u => u.Alive && u.Side != "red" && u.Side != "neutral");
        var contacts = ContactsOf(g, "red");

        contacts.Should().HaveCount(aliveEnemies, "明牌时红方应当看见全部敌方单位");
        contacts.Should().OnlyContain(c => (int)c["level"]! == 3, "明牌按 E3 精确展示");
        g.Tracks.TryGetValue("red", out var tracks);
        (tracks?.Count ?? 0).Should().Be(0, "明牌是一次性照明，不该写成情报档案");
    }

    [Fact]
    public void Reenabling_Fog_Does_Not_Keep_Fog_Off_Intel()
    {
        var g = NewGame();
        g.Flags["fog"] = false;
        FogService.UpdateObservation(g);
        g.Flags["fog"] = true;
        FogService.UpdateObservation(g);

        var aliveEnemies = g.Units.Count(u => u.Alive && u.Side != "red" && u.Side != "neutral");
        ContactsOf(g, "red").Count.Should().BeLessThan(aliveEnemies,
            "重开雾后只保留真实观测到的目标，关雾期间看到的不能留下");
    }

    [Fact]
    public void Ai_Move_Orders_Start_At_Unit_Position()
    {
        var g = NewGame();
        var orders = AiService.GenerateOrders(g, "red");
        var moves = orders.Where(c => c.Op == CommandOp.Move).ToList();

        moves.Should().NotBeEmpty("AI 托管方应当产出机动令");

        var pathErrors = CommandValidator.ValidateOrders(g, "red", orders)
            .Where(e => e.Reason is "path_start" or "bad_path" or "not_adjacent")
            .ToList();
        pathErrors.Should().BeEmpty("AI 机动令必须通过服务端路径校验，否则托管方一整回合不动");
    }
}
