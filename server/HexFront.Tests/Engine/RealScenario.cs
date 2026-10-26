using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HexFront.Core.Models;
using HexFront.Core.Services;

namespace HexFront.Tests.Engine;

/// <summary>
/// 随仓剧本 rhine_corridor 的真建局入口。
/// 剧本→副本这一段只能用真数据：自造 GameState 锁不住"剧本字段有没有真落进引擎"，
/// 而这条接缝正是向导提交的 JSON 与内核之间唯一的接口。
/// </summary>
internal static class RealScenario
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "scenarios", "rhine_corridor.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 data/scenarios/rhine_corridor.json");
    }

    public static string ScenarioJsonPath => Path.Combine(RepoRoot(), "data", "scenarios", "rhine_corridor.json");
    public static string ScenarioJson => File.ReadAllText(ScenarioJsonPath);
    public static string GisDirectory => Path.Combine(RepoRoot(), "data", "gis");

    /// <summary>overrides 走线上同一份深合并，测出来的就是 POST /api/instances 的语义。</summary>
    public static GameState Build(JsonElement? overrides = null, uint seed = 20261016) =>
        ScenarioService.CreateGameState(ScenarioService.MergeOverrides(ScenarioJson, overrides), GisDirectory, seed);

    public static JsonElement Overrides(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>只合并不建局：预算校验发生在副本创建之前，不该被生成流程吃掉。</summary>
    public static ScenarioService.ScenarioConfig Config(string overridesJson) =>
        ScenarioService.MergeOverrides(ScenarioJson, Overrides(overridesJson));

    /// <summary>双 AI 连推 <paramref name="turns"/> 回合，用来把"开局全盲"预热成"部分暴露"。</summary>
    public static GameState WarmUp(int turns, uint seed = 20261016)
    {
        var g = Build(null, seed);
        for (var i = 0; i < turns; i++)
        {
            TurnResolver.ResolveTurn(g, new Dictionary<string, List<Command>>
            {
                ["red"] = AiService.GenerateOrders(g, "red"),
                ["blue"] = AiService.GenerateOrders(g, "blue"),
            });
        }
        return g;
    }

    public static List<Dictionary<string, object?>> ContactsOf(GameState g, string side) =>
        ((IEnumerable<object?>?)FogService.SideView(g, side)["contacts"])!
            .Cast<Dictionary<string, object?>>().ToList();
}
