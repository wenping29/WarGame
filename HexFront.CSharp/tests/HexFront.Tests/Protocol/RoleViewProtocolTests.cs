using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §1–§2（第 1–6 项）：四个终端真接进同一个房间，断言各自收到了什么。
/// 双盲与视图裁剪没法在同进程调函数验 —— 必须有一条真连接站在另一端上看。
/// </summary>
[Collection("api")]
public sealed class RoleViewProtocolTests
{
    private static readonly string[] Roles = { "red", "blue", "director", "spectator" };

    private readonly ApiFixture _api;

    public RoleViewProtocolTests(ApiFixture api) => _api = api;

    private Task<RoomSessions> OpenAsync() => RoomSessions.CreateAsync(_api, Roles);

    /// <summary>第 1、2 项：两个对抗方各自拿到 kind=side 的本方明细，拿不到对方的。</summary>
    [Fact]
    public async Task Both_Sides_Get_Their_Own_View()
    {
        var room = await OpenAsync();
        await using (room)
        {
            foreach (var role in new[] { "red", "blue" })
            {
                var client = room[role];
                WsClient.StrOf(client.View, "kind").Should().Be("side", role + " 视图角色");
                client.OwnUnits.Should().NotBeEmpty(role + " 应有本方单位明细");
                WsClient.ArrayOf(client.View, "units").Should().BeEmpty(
                    role + " 的视图里不该有全量 units 字段");
            }
        }
    }

    /// <summary>第 3 项：导演部全知 —— 单位明细比任何一方的己方名单都长。</summary>
    [Fact]
    public async Task Director_View_Is_Omniscient()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var dir = room["director"];
            WsClient.StrOf(dir.View, "kind").Should().Be("director");
            dir.DirectorUnits.Should().NotBeEmpty();
            dir.DirectorUnits.Length.Should().BeGreaterThan(room["red"].OwnUnits.Length,
                "导演部看得见全部兵力，不该和单方视图一样多");
        }
    }

    /// <summary>第 4 项：观战默认非全知 —— 没有未暴露敌方明细可拿。</summary>
    [Fact]
    public async Task Spectator_Is_Not_Omniscient_By_Default()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var spec = room["spectator"];
            WsClient.StrOf(spec.View, "kind").Should().Be("spectator");
            spec.View.ContainsKey("units").Should().BeFalse(
                "观战视图里出现 units 等于把底牌全发出去");
        }
    }

    /// <summary>第 5 项：红方己方名单与蓝方单位集合不交叉（视图裁剪没漏）。</summary>
    [Fact]
    public async Task Side_Rosters_Do_Not_Overlap()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var redIds = room["red"].OwnUnits.Select(u => WsClient.StrOf(u, "id")).ToHashSet();
            var blueIds = room["director"].DirectorUnits
                .Where(u => WsClient.StrOf(u, "side") == "blue")
                .Select(u => WsClient.StrOf(u, "id")).ToHashSet();

            redIds.Should().NotBeEmpty();
            blueIds.Should().NotBeEmpty();
            redIds.Intersect(blueIds).Should().BeEmpty("红方名单里混进了蓝方单位");
        }
    }

    /// <summary>welcome 首包自带视图与房间公开信息（P6-a：前端靠它落第一帧，不用再等 state）。</summary>
    [Fact]
    public async Task Welcome_Carries_View_And_Room_Info()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var welcome = room["red"].Welcome;
            welcome.Should().NotBeNull();
            WsClient.StrOf(welcome, "type").Should().Be("welcome");
            WsClient.StrOf(welcome, "token").Should().NotBeEmpty("重连接管本方视图要靠这个 token");
            WsClient.StrOf(welcome!["room"]?.AsObject(), "code").Should().Be(room.Code);
            WsClient.StrOf(welcome["view"]?.AsObject(), "kind").Should().Be("side");
            room["red"].OfType("state").Should().NotBeEmpty("welcome 之后还要跟一包完整 state");
        }
    }

    /// <summary>mjs 第 0 项：房间必须由 REST 创建，未知房间码连 /ws 一律握手就被拒（不再静默建局）。</summary>
    [Fact]
    public async Task Unknown_Room_Code_Is_Rejected_At_Handshake()
    {
        var opened = await WsClient.OpensAsync(_api, "NOSUCH0", "director");
        opened.Should().BeFalse("未知房间码不该被静默建成一局");
    }

    /// <summary>第 6 项：阵营位已占用时第二次加入被拒，并说明可以用 force 接管。</summary>
    [Fact]
    public async Task Occupied_Side_Slot_Is_Refused()
    {
        var room = await OpenAsync();
        await using (room)
        {
            var second = await room.OpenAsync("red", awaitView: false);
            (await second.WaitForTypeAsync("error", 1, 5000)).Should().BeTrue("占用阵营位要收到 error");
            second.Errors.Should().ContainSingle().Which.Should().Contain("角色位").And.Contain("force");
            (await second.WaitUntilAsync(() => second.Socket.CloseStatus.HasValue, 5000)).Should().BeTrue(
                "拒完之后服务端要关这条连接，不能留半开会话");
        }
    }
}
