using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §4（第 16–19 项）+ ws-smoke.mjs 8b：导演部导调指令。
/// 导调是"教学模式"的入口，必须即时生效、按角色广播，且关雾是一次性照明而不是发档案。
/// </summary>
[Collection("api")]
public sealed class DirectorCmdProtocolTests
{
    private static readonly string[] Roles = { "red", "blue", "director", "spectator" };

    private readonly ApiFixture _api;

    public DirectorCmdProtocolTests(ApiFixture api) => _api = api;

    private Task<RoomSessions> OpenAsync() => RoomSessions.CreateAsync(_api, Roles);

    /// <summary>第 16 项：director_cmd weather 改天气，四端视图同步收到新天气。</summary>
    [Fact]
    public async Task Weather_Override_Reaches_Every_Terminal()
    {
        var room = await OpenAsync();
        await using (room)
        {
            await room["director"].SendAsync("weather", new JsonObject { ["id"] = "fog" });

            foreach (var role in Roles)
            {
                var client = room[role];
                (await client.WaitUntilAsync(() =>
                    WsClient.StrOf(client.View["weather"]?.AsObject(), "id") == "fog", 5000))
                    .Should().BeTrue($"{role} 该看到天气被改成浓雾");
            }
        }
    }

    /// <summary>第 17 项：AI 代管可切换，导演部视图里的 ai 标记跟着翻。</summary>
    [Fact]
    public async Task Ai_Management_Toggles_Appear_On_The_Director_View()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var dir = room["director"];
            WsClient.BoolOf(dir.View["ai"]?.AsObject(), "blue").Should().NotBeTrue("开局蓝方是真人接入");

            await dir.SendAsync("ai", new JsonObject { ["side"] = "blue", ["on"] = true });
            (await dir.WaitUntilAsync(() => WsClient.BoolOf(dir.View["ai"]?.AsObject(), "blue") == true, 5000))
                .Should().BeTrue("代管开关要回显在导演部视图上");

            await dir.SendAsync("ai", new JsonObject { ["side"] = "blue", ["on"] = false });
            (await dir.WaitUntilAsync(() => WsClient.BoolOf(dir.View["ai"]?.AsObject(), "blue") == false, 5000))
                .Should().BeTrue("关掉代管也要能关掉");
        }
    }

    /// <summary>第 18 项：导演部可关闭迷雾（教学/复盘模式），flags 与视图 fog 位同时翻转。</summary>
    [Fact]
    public async Task Fog_Can_Be_Turned_Off_For_Teaching()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var dir = room["director"];
            WsClient.BoolOf(dir.View, "fog").Should().BeTrue("开局默认有雾");

            await dir.SendAsync("fog", new JsonObject { ["on"] = false });
            (await dir.WaitUntilAsync(() => WsClient.BoolOf(dir.View, "fog") == false, 5000))
                .Should().BeTrue("关雾要立刻生效");
            WsClient.BoolOf(dir.View["flags"]?.AsObject(), "fog").Should().BeFalse("flags 里也要是 false");
        }
    }

    /// <summary>mjs 8b：明牌是照明不是档案 —— 关雾看得见全部敌人，重开雾不能留下这份永久档案。</summary>
    [Fact]
    public async Task Fog_Off_Illuminates_But_Leaves_No_Archive()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var dir = room["director"];
            var red = room["red"];
            await dir.SendAsync("fog", new JsonObject { ["on"] = false });
            (await red.WaitUntilAsync(() => WsClient.BoolOf(red.View, "fog") == false, 5000)).Should().BeTrue();

            // 只数存活敌人：导演部视图连打光的单位也带，SideView 的 contacts 不会给他们建档
            var enemies = dir.DirectorUnits.Count(u => WsClient.StrOf(u, "side") != "red"
                && WsClient.StrOf(u, "side") != "neutral" && WsClient.StrOf(u, "status") != "destroyed");
            enemies.Should().BeGreaterThan(0);
            WsClient.ArrayOf(red.View, "contacts").Length.Should().Be(enemies,
                "明牌时红方该看见每一个存活敌人");

            await dir.SendAsync("fog", new JsonObject { ["on"] = true });
            (await red.WaitUntilAsync(() => WsClient.BoolOf(red.View, "fog") == true
                && WsClient.ArrayOf(red.View, "contacts").Length < enemies, 5000))
                .Should().BeTrue($"重开雾后 contacts 要退回真实情报水位（明牌留了 {enemies} 条档案）");
        }
    }

    /// <summary>第 19 项：观战全知档可切换，切档瞬间观战端就拿到导演部视图。</summary>
    [Fact]
    public async Task Spectator_Omniscient_Flag_Switches_The_Spectator_View()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var dir = room["director"];
            var spec = room["spectator"];
            await dir.SendAsync("spectator_omniscient", new JsonObject { ["on"] = true });
            (await spec.WaitUntilAsync(() => WsClient.StrOf(spec.View, "kind") == "director", 5000))
                .Should().BeTrue("开了全知档的观战应看到导演部视图");

            await dir.SendAsync("spectator_omniscient", new JsonObject { ["on"] = false });
            (await spec.WaitUntilAsync(() => WsClient.StrOf(spec.View, "kind") == "spectator", 5000))
                .Should().BeTrue("关掉之后要退回公开情报层");
        }
    }

    /// <summary>导调权只在导演部：对抗方发 director_cmd 必须被拒。</summary>
    [Fact]
    public async Task Only_The_Director_Can_Issue_Cmd()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var red = room["red"];
            await red.SendAsync(new JsonObject
            {
                ["type"] = "director_cmd",
                ["action"] = "weather",
                ["id"] = "storm",
            });
            (await red.WaitForTypeAsync("error", 1, 5000)).Should().BeTrue();
            red.Errors.Should().Contain(m => m.Contains("导演部"));
            WsClient.StrOf(room["director"].View["weather"]?.AsObject(), "id").Should().NotBe("storm",
                "被拒的导调不该改了对局状态");
        }
    }

    /// <summary>未知天气 id 要报错而不是把对局状态改坏。</summary>
    [Fact]
    public async Task Unknown_Weather_Is_Rejected()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var before = WsClient.StrOf(room["director"].View["weather"]?.AsObject(), "id");
            await room["director"].SendAsync("weather", new JsonObject { ["id"] = "typhoon" });
            (await room["director"].WaitForTypeAsync("error", 1, 5000)).Should().BeTrue();
            room["director"].Errors.Should().Contain(m => m.Contains("未知天气"));
            WsClient.StrOf(room["director"].View["weather"]?.AsObject(), "id").Should().Be(before);
        }
    }
}
