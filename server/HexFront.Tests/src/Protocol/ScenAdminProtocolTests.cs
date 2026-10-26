using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 剧本管理 PUT /api/scenadmin/{id} 的 validateOnly 契约。
/// 这个开关原先声明成 bool 交给 MVC 的查询绑定，而它只认字面 true：编辑器点「✓ 校验」传 ?validateOnly=1
/// 会被静默当成 false，于是「只想校验」顺带把剧本写盘、还往 scenario_backups 里塞一行备份。
/// 请求体取真剧本再挪一格落位（仍是合法坐标），所以万一开关再次失效，落盘的也是同一份内容，测不出副作用才可怕。
/// 这里故意不测「不传开关要真的保存」：ApiFixture 只隔离了存档目录，剧本与 app.db 用的还是仓库里那份真数据。
/// </summary>
[Collection("api")]
public sealed class ScenAdminProtocolTests
{
    private const string Scenario = "rhine_corridor";
    private readonly ApiFixture _api;

    public ScenAdminProtocolTests(ApiFixture api) => _api = api;

    /// <summary>把真剧本里第一个带 at 落位的编制行往右挪一格，模拟「编辑器改了但还没想保存」。</summary>
    private async Task<string> EditedDocAsync()
    {
        var detail = await _api.GetJsonAsync("/api/scenadmin/" + Scenario);
        var doc = detail["doc"]?.DeepClone() as JsonObject;
        doc.Should().NotBeNull("剧本得先读得出来：" + detail.ToJsonString());
        var sides = doc!["sides"]?.AsObject();
        sides.Should().NotBeNull(Scenario + " 剧本里得要有 sides 阵营表");

        var moved = false;
        foreach (var kv in sides!)
        {
            if (kv.Value is not JsonObject sd) continue;
            if (sd["oob"] is not JsonArray oob) continue;
            foreach (var row in oob)
            {
                if (row?["at"] is not JsonArray at || at.Count < 2) continue;
                if (at[0]?.GetValueKind() != JsonValueKind.Number) continue;
                at[0] = JsonValue.Create(at[0]!.GetValue<int>() + 1);
                moved = true;
                break;
            }
            if (moved) break;
        }
        moved.Should().BeTrue(Scenario + " 剧本里得有一行带 at 落位的编制");
        return doc!.ToJsonString();
    }

    /// <summary>文档口径的 1、MVC 原生认的 true、以及大小写变体，都必须只校验不落盘。</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    public async Task Validate_Only_Spelling_ChecksWithout_Writing(string flag)
    {
        var response = await _api.PutJsonAsync(
            $"/api/scenadmin/{Scenario}?validateOnly={flag}", await EditedDocAsync());

        response["ok"]?.GetValue<bool>().Should().BeTrue(
            $"挪一格是合法落位，{flag} 校验不该报错：" + response.ToJsonString());
        response.ContainsKey("saved_at").Should().BeFalse(
            $"?validateOnly={flag} 走了保存分支，剧本被覆盖了：" + response.ToJsonString());
        response.ContainsKey("file").Should().BeFalse("不落盘就不该回文件名与备份号");
    }

    /// <summary>校验不通过的剧本，无论有没有开关都拒写（缺 sides 是必然报错的那一类）。</summary>
    [Fact]
    public async Task Invalid_Doc_Never_Saves()
    {
        var response = await _api.PutJsonAsync(
            "/api/scenadmin/" + Scenario, new JsonObject { ["id"] = Scenario }.ToJsonString());

        response["ok"]?.GetValue<bool>().Should().BeFalse();
        WsClient.StringsOf(response, "errors").Should().Contain(m => m.Contains("sides"),
            "缺 sides 必须报错：" + response.ToJsonString());
        response.ContainsKey("saved_at").Should().BeFalse("有报错就不许落盘：" + response.ToJsonString());
    }
}
