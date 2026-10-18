using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace HexFront.Tests.Performance;

/// <summary>
/// doc §13.2 验收线「14 回合全 AI 对抗 &lt; 5s」的常驻回归。
///
/// 口径要分清，两条数字不可互相替代：
/// - 这里量的是**纯引擎**：建局（读 GIS 数据包）+ 双 AI 补令 + 逐回合七阶段结算，进程内直调，无 HTTP、无 WS、无 JSON 序列化。
/// - scripts/ws-smoke.mjs 第 8d 项量的是**整链路**：REST 建房 + WS 逐回合 resolve + 状态广播，含网络与序列化开销。
/// </summary>
public class EnginePerfBaselineTests
{
    private const uint Seed = 731001;   // 与 ws-smoke 8d 同一个种子，两条数字才能对着看
    private const int Turns = 14;       // data/scenarios/rhine_corridor.json 的 max_turns
    private const int BudgetMs = 5000;  // doc §13.2 门槛

    private readonly ITestOutputHelper _out;

    public EnginePerfBaselineTests(ITestOutputHelper outHelper) => _out = outHelper;

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
        var g = ScenarioService.CreateGameState(cfg, Path.Combine(root, "data", "gis"), Seed);
        g.WeatherMode = "realistic"; // 和 ws-smoke 8d 一样让每回合走天气演变，随机源用量对齐
        return g;
    }

    private static Dictionary<string, List<Command>> AiOrders(GameState g) => new()
    {
        ["red"] = AiService.GenerateOrders(g, "red"),
        ["blue"] = AiService.GenerateOrders(g, "blue"),
    };

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Full_Ai_Battle_14_Turns_Under_5_Seconds()
    {
        var setup = Stopwatch.StartNew();
        var g = NewGame();
        setup.Stop();

        var perTurn = new List<(int turn, long ai, long resolve)>();
        var battles = 0;
        var rejected = 0;
        var loop = Stopwatch.StartNew();
        for (var t = 0; t < Turns && !g.Over; t++)
        {
            var before = g.Turn;
            var aiWatch = Stopwatch.StartNew();
            var orders = AiOrders(g);
            aiWatch.Stop();
            var repWatch = Stopwatch.StartNew();
            var report = TurnResolver.ResolveTurn(g, orders);
            repWatch.Stop();

            battles += report.Battles.Count;
            rejected += report.Rejected.Count;
            perTurn.Add((before, aiWatch.ElapsedMilliseconds, repWatch.ElapsedMilliseconds));
        }
        loop.Stop();
        var totalMs = loop.ElapsedMilliseconds;

        var fp = g.Fingerprint();
        var aiTotal = perTurn.Sum(x => x.ai);
        var resolveTotal = perTurn.Sum(x => x.resolve);
        _out.WriteLine($"seed {Seed} · 建局 {setup.ElapsedMilliseconds}ms（{g.Units.Count} 单位 / {g.Cells.Count} 格）");
        _out.WriteLine($"循环 {totalMs}ms / {perTurn.Count} 回合 = 均 {Total(perTurn.Count, totalMs)}ms/回合"
            + $"（补令 {aiTotal}ms + 结算 {resolveTotal}ms，余量是 GC 与计时本身）");
        _out.WriteLine("逐回合 ms: " + string.Join(", ", perTurn.Select(x => $"t{x.turn}=ai{x.ai}/rs{x.resolve}")));
        _out.WriteLine($"终局 over={g.Over} winner={g.Winner ?? "平"} 交战 {battles} 场 被拒令 {rejected} 条 指纹 {fp}");

        // 计时只有「确实干了 14 回合的活」才等于验收线，提前终局会让 5s 门槛变成空断言
        perTurn.Count.Should().Be(Turns,
            $"必须跑满 {Turns} 回合：第 4 项实测本种子是在 max_turns 上终局");
        battles.Should().BeGreaterThan(0, "零交战的双 AI 推演不是有效负载，测出来的耗时没有代表性");
        rejected.Should().BeEmpty("副官的令全部被引擎收下，托管回合没有白跑");

        totalMs.Should().BeLessThan(BudgetMs,
            $"doc §13.2：{Turns} 回合全 AI 对抗必须 < {BudgetMs}ms");
    }

    /// <summary>
    /// 同一份负载跑第二遍（JIT 已热）：两遍指纹必须一致，且第二遍是稳态口径。
    /// 这一条锁的是「性能数字来自可重放的同一局」，不是随手跑一次撞上的好运气。
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Second_Run_Is_Steady_State_And_Reproduces_The_Fingerprint()
    {
        var cold = Play();
        var warm = Play();

        _out.WriteLine($"首跑 {cold.ms}ms 指纹 {cold.fp}（含冷启动 JIT）");
        _out.WriteLine($"次跑 {warm.ms}ms 指纹 {warm.fp}（稳态）");

        warm.fp.Should().Be(cold.fp, "同种子同指令必须同一终局，否则计时对象根本不是同一局");
        warm.ms.Should().BeLessThan(BudgetMs);
        warm.ms.Should().BeLessOrEqualTo(cold.ms, "稳态不该比首跑更慢——慢了说明有累积泄漏");
    }

    private static (long ms, string fp, int turns) Play()
    {
        var g = NewGame();
        var sw = Stopwatch.StartNew();
        for (var t = 0; t < Turns && !g.Over; t++)
            TurnResolver.ResolveTurn(g, AiOrders(g));
        sw.Stop();
        return (sw.ElapsedMilliseconds, g.Fingerprint(), g.Turn);
    }

    private static string Total(int turns, long ms) => turns == 0 ? "n/a" : (ms / (double)turns).ToString("F1");
}
