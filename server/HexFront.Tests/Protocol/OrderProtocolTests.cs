using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §3（第 7–15 项）：双盲指令链路。
/// 指令草案只回令下的一方，导演部才看得见双方；非法指令必须被拦在 ready 之前，且不污染对局。
/// </summary>
[Collection("api")]
public sealed class OrderProtocolTests
{
    private readonly ApiFixture _api;

    public OrderProtocolTests(ApiFixture api) => _api = api;

    /// <summary>蓝方建成真接入（ai_blue=false）：就绪屏障不会因为 AI 代管而自己把回合结算掉，结算权留在用例手里。</summary>
    private Task<RoomSessions> OpenAsync() =>
        RoomSessions.CreateAsync(_api, new[] { "red", "blue", "director" }, aiBlue: false);

    /// <summary>odd-r 邻居表（与 Hex.cs、ws-smoke.mjs 同序）。</summary>
    private static readonly int[][][] Neighbors =
    {
        new[] { new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, -1 }, new[] { -1, 0 }, new[] { -1, 1 }, new[] { 0, 1 } },
        new[] { new[] { 1, 0 }, new[] { 1, -1 }, new[] { 0, -1 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 1, 1 } },
    };

    private static readonly HashSet<string> Impassable = new(StringComparer.Ordinal) { "water", "void" };

    /// <summary>
    /// 挑一条真能走的机动指令：红方自己看不见地形，所以借导演部的 cells 判断可通行，
    /// 但单位与路径只发给红方 —— 测的是服务端的裁剪，不是客户端的取数。
    /// 候选按地形代价从低到高排：机动预算会被天气/疲劳打折，选最省的一格才不会误判成"指令非法"。
    /// </summary>
    private static (string Unit, int[] From, int[] To)? PickMove(RoomSessions room)
    {
        var cells = WsClient.ArrayOf(room["director"].View, "cells")
            .ToDictionary(c => (WsClient.NumberField(c, "q")!.Value, WsClient.NumberField(c, "r")!.Value),
                c => WsClient.StrOf(c, "terrain"));
        var width = WsClient.NumberField(room["director"].View, "width") ?? 0;
        var height = WsClient.NumberField(room["director"].View, "height") ?? 0;
        string[] cheap = { "clear", "city", "road", "railway", "rough", "woods", "hills", "swamp", "river", "mountain" };

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < cheap.Length; i++) rank[cheap[i]] = i;

        var candidates = new List<(int Cost, string Unit, int[] From, int[] To)>();
        foreach (var u in room["red"].OwnUnits)
        {
            if (WsClient.StrOf(u, "status") == "destroyed") continue;
            var q = WsClient.NumberField(u, "q") ?? -1;
            var r = WsClient.NumberField(u, "r") ?? -1;
            if (q < 0) continue;
            foreach (var d in Neighbors[(r & 1) == 0 ? 0 : 1])
            {
                var n = new[] { q + d[0], r + d[1] };
                if (n[0] < 0 || n[1] < 0 || n[0] >= width || n[1] >= height) continue;
                if (!cells.TryGetValue((n[0], n[1]), out var terrain) || Impassable.Contains(terrain)) continue;
                candidates.Add((rank.TryGetValue(terrain, out var i) ? i : 99, WsClient.StrOf(u, "id"),
                    new[] { q, r }, n));
            }
        }

        if (candidates.Count == 0) return null;
        var best = candidates.OrderBy(c => c.Cost).ThenBy(c => c.Unit, StringComparer.Ordinal)
            .ThenBy(c => c.To[0]).ThenBy(c => c.To[1]).First();
        return (best.Unit, best.From, best.To);
    }

    private static async Task SubmitMoveAsync(RoomSessions room, (string Unit, int[] From, int[] To) move)
    {
        await room["red"].SendAsync(new JsonObject
        {
            ["type"] = "orders",
            ["replace"] = true,
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["op"] = "move",
                    ["unit"] = move.Unit,
                    ["path"] = new JsonArray
                    {
                        new JsonArray { move.From[0], move.From[1] },
                        new JsonArray { move.To[0], move.To[1] },
                    },
                },
            },
        });
    }

    private static JsonObject[] DraftsOf(RoomSessions room, string side) =>
        WsClient.ArrayOf(room["director"].View["orders"]?.AsObject(), side);

    /// <summary>第 7、8、9 项：草案回传本方、对方看不见、导演部双方可见。</summary>
    [Fact]
    public async Task Draft_Reaches_Owner_And_Director_But_Not_The_Opponent()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var move = PickMove(room);
            move.Should().NotBeNull("开局总得有一格能走的邻格");
            await SubmitMoveAsync(room, move!.Value);

            (await room["red"].WaitUntilAsync(() => WsClient.ArrayOf(room["red"].View, "draft").Length > 0))
                .Should().BeTrue("下过令的单位要在本方视图 draft 里看到草案");
            WsClient.ArrayOf(room["blue"].View, "draft").Should().BeEmpty("蓝方看不到红方的指令草案");
            (await room["director"].WaitUntilAsync(() => DraftsOf(room, "red").Length > 0))
                .Should().BeTrue("导演部得看得见红方已受领的指令");
            DraftsOf(room, "blue").Should().BeEmpty("蓝方没下令，导演部那侧就该是空");
        }
    }

    /// <summary>第 10、11、12 项：跳格指令被拒并给原因，ready 卡在拒绝态；改成合法指令后就绪。</summary>
    [Fact]
    public async Task Illegal_Order_Blocks_Ready_Until_It_Is_Fixed()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var move = PickMove(room)!.Value;
            var jump = new JsonObject
            {
                ["type"] = "orders",
                ["replace"] = true,
                ["commands"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["op"] = "move",
                        ["unit"] = move.Unit,
                        ["path"] = new JsonArray
                        {
                            new JsonArray { move.From[0], move.From[1] },
                            new JsonArray { move.From[0] + 4, move.From[1] },
                        },
                    },
                },
            };
            await room["red"].SendAsync(jump);
            (await room["red"].WaitUntilAsync(() => WsClient.ArrayOf(room["red"].View, "errors").Length > 0))
                .Should().BeTrue("非法指令要立刻回传原因");

            var errorsBefore = room["red"].Errors.Count;
            await room["red"].SendAsync(new JsonObject { ["type"] = "ready", ["value"] = true });
            (await room["red"].WaitUntilAsync(() => room["red"].Errors.Count > errorsBefore, 5000))
                .Should().BeTrue("ready 时还要再拦一次（提交只回 view.errors，不算这条）");
            room["red"].Errors.Should().Contain(m => m.Contains("非法指令"));
            room["red"].ReadyFlag.Should().NotBeTrue("非法指令没清干净就不该算就绪");

            await SubmitMoveAsync(room, move);
            (await room["red"].WaitUntilAsync(() => WsClient.ArrayOf(room["red"].View, "draft").Length > 0, 3000))
                .Should().BeTrue("改掉跳格之后草案要重新落回本方视图");
            await room["red"].SendAsync(new JsonObject { ["type"] = "ready", ["value"] = true });
            (await room["red"].WaitUntilAsync(() => room["red"].ReadyFlag == true))
                .Should().BeTrue("换成合法指令后就绪应当生效");
        }
    }

    /// <summary>第 13、14、15 项：导演部结算推进一回合，机动事件只发给 owning 阵营。</summary>
    [Fact]
    public async Task Resolve_Advances_One_Turn_And_Filters_Events_By_Side()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var move = PickMove(room)!.Value;
            await SubmitMoveAsync(room, move);
            // 结算走的是导演部那条连接，与红方 orders 不同源：不等草案落地就可能抢在指令之前结算
            (await room["red"].WaitUntilAsync(() => WsClient.ArrayOf(room["red"].View, "draft").Length > 0, 3000))
                .Should().BeTrue("结算前草案必须已经落到红方视图");
            var turnBefore = room["red"].Turn;

            await room["director"].SendAsync(new JsonObject { ["type"] = "resolve" });
            (await room["red"].WaitUntilAsync(() => room["red"].Turn == turnBefore + 1, 12000))
                .Should().BeTrue($"回合要从 {turnBefore} 推进到 {turnBefore + 1}");
            (await room["director"].WaitUntilAsync(() => room["director"].Turn == turnBefore + 1, 12000))
                .Should().BeTrue("导演部同步推进");

            (await room["red"].WaitUntilAsync(() => room["red"].Events.Count > 0, 8000))
                .Should().BeTrue("红方要收到本方事件");
            room["red"].Events.Select(e => WsClient.StrOf(e, "code")).Should().Contain("move",
                "红方机动了，本方就该看到 move 事件");
            room["blue"].Events.Should().NotContain(e =>
                WsClient.StrOf(e, "code") == "move" && WsClient.StrOf(e, "text").StartsWith("R"),
                "蓝方收到红方私有机动事件等于迷雾白做");
        }
    }

    /// <summary>mjs 第 3 项：非导演部发 resolve 必须被拒（结算权只在导演部）。</summary>
    [Fact]
    public async Task Only_The_Director_Can_Resolve()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var turnBefore = room["red"].Turn;
            await room["red"].SendAsync(new JsonObject { ["type"] = "resolve" });
            (await room["red"].WaitForTypeAsync("error", 1, 5000)).Should().BeTrue();
            room["red"].Errors.Should().Contain(m => m.Contains("导演部"));
            await Task.Delay(600);
            room["red"].Turn.Should().Be(turnBefore, "被拒的结算不能真的推进回合");
        }
    }

    /// <summary>mjs 第 2 项：pending 计数 —— 服务端按草案条数回给导演部，前端用它显示"对方是否已下令"。</summary>
    [Fact]
    public async Task Pending_Counts_Land_On_The_Director_Packet()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var move = PickMove(room)!.Value;
            await SubmitMoveAsync(room, move);
            (await room["red"].WaitUntilAsync(() => room["red"].PendingOf("red") == 1, 5000))
                .Should().BeTrue("一条指令就该数出 pending.red = 1");
            room["red"].PendingOf("blue").Should().Be(0, "蓝方没下令，pending.blue 得是 0");
        }
    }

    /// <summary>只有对抗方能提交指令：导演部代发必须被拒。</summary>
    [Fact]
    public async Task Non_Combatants_Cannot_Submit_Orders()
    {
        var room = await OpenAsync();
        await using (room)
        {
            await room["director"].SendAsync(new JsonObject
            {
                ["type"] = "orders",
                ["replace"] = true,
                ["commands"] = new JsonArray { new JsonObject { ["op"] = "rest", ["unit"] = "R1" } },
            });
            (await room["director"].WaitForTypeAsync("error", 1, 5000)).Should().BeTrue();
            room["director"].Errors.Should().Contain(m => m.Contains("对抗方"));
        }
    }
}
