using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §8（第 28–32 项）：兵种属性覆盖的 REST 契约。
/// 引擎侧的算法在 TypeDefOverrideP4Tests 里已经验过，这里验的是向导拿到的那份线上答复：
/// 谁计价、谁被拒、拒的时候文案怎么写给前端、非法覆盖能不能真的建出副本。
/// </summary>
[Collection("api")]
public sealed class OverrideRestProtocolTests
{
    private readonly ApiFixture _api;

    public OverrideRestProtocolTests(ApiFixture api) => _api = api;

    private static string Body(params string[] defs) => new JsonObject
    {
        ["scenario"] = "rhine_corridor",
        ["overrides"] = new JsonObject { ["unit_type_defs"] = JsonNode.Parse("[" + string.Join(",", defs) + "]") },
    }.ToJsonString();

    private Task<JsonObject> ValidateAsync(params string[] defs) => _api.PostJsonAsync("/api/validate", Body(defs));

    private static string[] Errors(JsonObject response) => WsClient.StringsOf(response, "errors");

    /// <summary>第 28 项：覆盖后的点数由服务端计价，前端不自己算（同一把尺子只能在引擎里）。</summary>
    [Fact]
    public async Task Overridden_Types_Are_Priced_By_The_Server()
    {
        var response = await ValidateAsync("""{"id":"tank","firepower":48,"mobility":14}""");
        var points = response["type_points"]?["tank"];
        points.Should().NotBeNull("覆盖过的兵种也要在 type_points 里给前端一个数");
        points!.GetValue<double>().Should().BeGreaterThan(26, "把坦克加强到 48 火力，点数必须涨过出厂的 26");
        // 覆盖本身合法：不该出现覆盖类报错；涨上去的点数要照样进 OOB 预算这条独立检查
        Errors(response).Should().NotContain(m => m.Contains("覆盖"), "合法的属性覆盖不该报覆盖错误");
        Errors(response).Should().Contain(m => m.Contains("超出预算"),
            "加强后红方 OOB 破预算，必须回给向导而不是悄悄放行：" + response.ToJsonString());
    }

    /// <summary>第 29 项：规则语义字段（cls）不允许覆盖 —— 那是结算分支的开关，不是数值。</summary>
    [Fact]
    public async Task Rule_Semantic_Fields_Cannot_Be_Overridden()
    {
        var response = await ValidateAsync("""{"id":"tank","cls":"air"}""");
        Errors(response).Should().Contain(m => m.Contains("不允许覆盖字段 cls"));
    }

    /// <summary>第 30 项：越界属性被拒（数值区间是引擎算得过来的前提）。</summary>
    [Fact]
    public async Task Out_Of_Range_Attributes_Are_Rejected()
    {
        var response = await ValidateAsync("""{"id":"tank","firepower":900}""");
        Errors(response).Should().Contain(m => m.Contains("超出允许区间"));
    }

    /// <summary>第 31 项：未知兵种覆盖被拒 —— 覆盖只能改出厂定义，不能凭空造兵种。</summary>
    [Fact]
    public async Task Unknown_Unit_Type_Override_Is_Rejected()
    {
        var response = await ValidateAsync("""{"id":"mecha","firepower":5}""");
        Errors(response).Should().Contain(m => m.Contains("未知兵种 mecha"));
    }

    /// <summary>第 32 项：/api/validate 放行的检查，建副本时必须同样拦住（前端绕过向导直接 POST 也不行）。</summary>
    [Fact]
    public async Task Illegal_Override_Cannot_Create_An_Instance()
    {
        var created = await _api.PostJsonAsync("/api/instances", new JsonObject
        {
            ["scenario"] = "rhine_corridor",
            ["name"] = "非法覆盖",
            ["overrides"] = JsonNode.Parse(
                """{"unit_type_defs":[{"id":"tank","cls":"air"}]}""")!,
        }.ToJsonString());

        WsClient.StrOf(created, "code").Should().BeEmpty("非法覆盖不该建得出房间");
        created.ContainsKey("error").Should().BeTrue("要回错误体，向导才显示得出来");
    }

    /// <summary>合法覆盖能建出副本，且 seed 被采纳（同种子可复现是存档往返的前提）。</summary>
    [Fact]
    public async Task Legal_Override_Creates_A_Seeded_Instance()
    {
        var created = await _api.PostJsonAsync("/api/instances", new JsonObject
        {
            ["scenario"] = "rhine_corridor",
            ["name"] = "合法覆盖",
            ["seed"] = 424242,
            ["overrides"] = JsonNode.Parse(
                """{"unit_type_defs":[{"id":"tank","firepower":48}]}""")!,
        }.ToJsonString());

        WsClient.StrOf(created, "code").Should().NotBeEmpty("合法覆盖要能建房：" + created.ToJsonString());
        WsClient.NumberField(created, "seed").Should().Be(424242, "seed 没被采纳就没法复现这一局");
    }

    /// <summary>剧本不存在时 /api/validate 回业务错误而不是 500。</summary>
    [Fact]
    public async Task Missing_Senario_Validate_Returns_An_Explanation()
    {
        var response = await _api.PostJsonAsync("/api/validate", new JsonObject
        {
            ["scenario"] = "no_such_scenario",
        }.ToJsonString());
        Errors(response).Should().Contain(m => m.Contains("剧本不存在"));
    }
}
