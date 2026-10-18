using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §6–§7（第 23–27 项）+ ws-smoke.mjs 6/10b/12/15：会话生命周期。
/// 顶号、释放、重连接管这些语义只有在第二条连接真站起来时才验得到；
/// 回放帧的取用权限也在这一组里 —— 帧里带导演部全知视图，对抗方拿到就等于看底牌。
/// </summary>
[Collection("api")]
public sealed class SessionProtocolTests
{
    private readonly ApiFixture _api;

    public SessionProtocolTests(ApiFixture api) => _api = api;

    private Task<RoomSessions> OpenAsync(bool aiBlue = false) =>
        RoomSessions.CreateAsync(_api, new[] { "red", "blue", "director" }, aiBlue: aiBlue);

    private Task<JsonObject> RoomInfoAsync(string code) => _api.GetJsonAsync("/api/room/" + code);

    private static bool SideTaken(JsonObject info, string side) =>
        WsClient.BoolOf(info["side_taken"]?.AsObject(), side) == true;

    private async Task<bool> WaitRoomIdleAsync(string code, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            var info = await RoomInfoAsync(code);
            if (!SideTaken(info, "red") && !SideTaken(info, "blue")) return true;
            await Task.Delay(150);
        }
        return false;
    }

    private static async Task ResolveAsync(WsClient dir)
    {
        var before = dir.Turn;
        await dir.SendAsync(new JsonObject { ["type"] = "resolve" });
        (await dir.WaitUntilAsync(() => dir.Turn == before + 1, 15000))
            .Should().BeTrue($"结算要把回合从 {before} 推到 {before + 1}");
    }

    /// <summary>第 23、24、25 项：断开即释放阵营位，同 token 重连接管本方视图并持续收推送。</summary>
    [Fact]
    public async Task Disconnect_Frees_The_Slot_And_Token_Reconnect_Takes_It_Back()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var token = WsClient.StrOf(room["blue"].Welcome, "token");
            token.Should().NotBeEmpty("welcome 要给终端一个重连凭证");

            await room["blue"].Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "浏览器刷新",
                CancellationToken.None);
            (await room["blue"].WaitUntilAsync(() => room["blue"].Socket.State != WebSocketState.Open, 5000))
                .Should().BeTrue("刷新页面时这条会话要真的关掉");

            SideTaken(await RoomInfoAsync(room.Code), "blue").Should().BeFalse(
                "对抗方断开后阵营位要立刻释放，否则玩家刷新页面就进不来");

            var blue2 = await room.OpenAsync("blue", token: token);
            WsClient.StrOf(blue2.View, "kind").Should().Be("side", "同 token 重连应接管本方，而不是被占用拒掉");
            blue2.OwnUnits.Should().NotBeEmpty();

            var turnBefore = blue2.Turn;
            await ResolveAsync(room["director"]);
            (await blue2.WaitUntilAsync(() => blue2.Turn == turnBefore + 1, 15000))
                .Should().BeTrue("重连终端要持续收到推送，不能成为孤儿会话");
        }
    }

    /// <summary>第 26 项：同角色双开（带同 token）时旧终端收到 kicked，不会静默失联。</summary>
    [Fact]
    public async Task Same_Token_Takeover_Kicks_The_Old_Terminal()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var token = WsClient.StrOf(room["red"].Welcome, "token");
            var taker = await room.OpenAsync("red", token: token, awaitView: false);

            (await taker.WaitForTypeAsync("welcome", 1, 5000)).Should().BeTrue("同 token 的接管不该被占用拦下");
            (await room["red"].WaitForTypeAsync("kicked", 1, 5000)).Should().BeTrue("旧终端要收到 kicked");
            WsClient.StrOf(room["red"].Kicked, "reason").Should().NotBeEmpty("kicked 要带原因，前端才好提示");
        }
    }

    /// <summary>mjs 10c：阵营位被他人占用且没带 force 时拒加入并给原因；带 force 才能接管。</summary>
    [Fact]
    public async Task Foreign_Token_Needs_Force_To_Take_Over_A_Side_Slot()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var occupied = await room.OpenAsync("red", token: "notmytoken", awaitView: false);
            (await occupied.WaitForTypeAsync("error", 1, 5000)).Should().BeTrue();
            occupied.Errors.Should().Contain(m => m.Contains("角色位"));

            var forced = await room.OpenAsync("red", token: "notmytoken", force: true);
            WsClient.StrOf(forced.View, "kind").Should().Be("side", "加 force=1 就该接管成功");
        }
    }

    /// <summary>mjs 10b：观战不是对抗信息位，后来者免 force 直接顶号，同角色只剩一条会话。</summary>
    [Fact]
    public async Task Spectator_Takeover_Needs_No_Force()
    {
        var room = await RoomSessions.CreateAsync(_api, new[] { "director", "spectator" });
        await using (room)
        {
            var first = room["spectator"];
            var second = await room.OpenAsync("spectator", awaitView: false);
            (await first.WaitForTypeAsync("kicked", 1, 5000)).Should().BeTrue("第二个观战终端直接接管");
            (await second.WaitForTypeAsync("welcome", 1, 5000)).Should().BeTrue();

            var roles = (await RoomInfoAsync(room.Code))["roles"]!.AsObject();
            roles["spectator"]!.GetValue<int>().Should().Be(1, "顶号之后同角色只剩一条会话");
        }
    }

    /// <summary>mjs 15：全部终端下线后 side_taken 翻回 false、roles 归零（断开即释放）。</summary>
    [Fact]
    public async Task All_Terminals_Offline_Releases_Every_Slot()
    {
        var room = await OpenAsync();
        var code = room.Code;
        await room.DisposeAsync();

        (await WaitRoomIdleAsync(code, 6000)).Should().BeTrue("断开即释放：两侧阵营位都要空出来");
        var online = (await RoomInfoAsync(code))["roles"]!.AsObject()
            .Where(p => p.Value!.GetValue<int>() > 0).Select(p => p.Key);
        online.Should().BeEmpty("全部终端下线后每个角色位的会话计数都该归零");
    }

    /// <summary>mjs 6：回放帧含导演部视图，对抗方取用必须被拒。</summary>
    [Fact]
    public async Task Replay_Frames_Are_Reserved_For_Director_And_Spectator()
    {
        var room = await RoomSessions.CreateAsync(_api, new[] { "red", "director", "spectator" });
        await using (room)
        {
            await room["red"].SendAsync(new JsonObject { ["type"] = "frames" });
            (await room["red"].WaitForTypeAsync("error", 1, 5000)).Should().BeTrue();
            room["red"].Errors.Should().Contain(m => m.Contains("导演部"));

            // 帧表是结算时才长出来的，开局为空属于正常 —— 先推一回合再验取用权限
            await ResolveAsync(room["director"]);

            await room["director"].SendAsync(new JsonObject { ["type"] = "frames" });
            (await room["director"].WaitForTypeAsync("frames", 1, 8000)).Should().BeTrue();
            var frames = WsClient.ArrayOf(room["director"].OfType("frames").Last(), "frames");
            frames.Should().NotBeEmpty("结算过后导演部必须拿到帧，回放进度条才有起点");
            WsClient.NumberField(frames.Last(), "turn").Should().Be(room["director"].Turn - 1,
                "最后一帧对应刚落成的那一回合");

            await room["spectator"].SendAsync(new JsonObject { ["type"] = "frames" });
            (await room["spectator"].WaitForTypeAsync("frames", 1, 8000))
                .Should().BeTrue("观战端要能取帧做回放");
        }
    }

    /// <summary>mjs 12：回放帧契约 —— 每帧都带 battles（无交战也是空数组），并裁剪在 200 帧内。</summary>
    [Fact]
    public async Task Frame_Rest_Carries_Battles_Array_And_Stays_Capped()
    {
        var room = await RoomSessions.CreateAsync(_api, new[] { "director" }, name: "帧契约");
        await using (room)
        {
            for (var i = 0; i < 3; i++) await ResolveAsync(room["director"]);

            var frames = WsClient.ArrayOf(await _api.GetJsonAsync("/api/frames/" + room.Code), "frames");
            frames.Should().NotBeEmpty("GET /api/frames 不该返回空帧表");
            foreach (var f in frames)
                f.ContainsKey("battles").Should().BeTrue($"回放帧缺 battles 数组（回合 {f["turn"]}）");
            frames.Length.Should().BeLessThanOrEqualTo(200, "帧表要裁剪在 200 帧内");
        }
    }

    /// <summary>第 27 项：对面是 AI 代管时，人类就绪即自动结算，不用再等导演部点按钮。</summary>
    [Fact]
    public async Task Human_Ready_Against_An_Ai_Side_Auto_Resolves()
    {
        var room = await OpenAsync(aiBlue: true);
        await using (room)
        {
            var red = room["red"];
            var turnBefore = red.Turn;
            await red.SendAsync(new JsonObject { ["type"] = "ready", ["value"] = true });
            (await red.WaitUntilAsync(() => red.Turn == turnBefore + 1, 10000))
                .Should().BeTrue($"对面 AI 代管时，{turnBefore} 回合就绪就该自动结算，而不是空等");
        }
    }

    /// <summary>ping 保活与未知消息：挂着不动的终端要能靠 ping 维持，未知类型要回错误而不是拖死连接。</summary>
    [Fact]
    public async Task Ping_Keeps_The_Session_Alive_And_Unknown_Type_Reports_Back()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var red = room["red"];
            await red.SendAsync(new JsonObject { ["type"] = "ping" });
            (await red.WaitForTypeAsync("pong", 1, 5000)).Should().BeTrue();

            await red.SendAsync(new JsonObject { ["type"] = "teleport" });
            (await red.WaitUntilAsync(() => red.Errors.Any(m => m.Contains("teleport")), 5000))
                .Should().BeTrue("未知消息类型要回错误");
            red.Socket.State.Should().Be(WebSocketState.Open, "报完错这条会话还得继续可用");
        }
    }
}
