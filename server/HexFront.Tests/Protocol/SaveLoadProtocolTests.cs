using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace HexFront.Tests.Protocol;

/// <summary>
/// 对应 hexfront/bin/smoke_ws.dart §5（第 20–22 项）+ ws-smoke.mjs 7/8/8a：存档与载入的线上契约。
/// 存档不是"能写出一个文件"就行：副本 id 要稳、列表要看得见、载入要回到同一回合同一指纹，
/// 并且尾部重放不能报指纹警告 —— 这几条一起才等于"这局可以明天接着打"。
/// </summary>
[Collection("api")]
public sealed class SaveLoadProtocolTests
{
    private readonly ApiFixture _api;

    public SaveLoadProtocolTests(ApiFixture api) => _api = api;

    /// <summary>导演部结算到 turnBefore+1 后手动存档；返回房间与那一回合。</summary>
    private async Task<(RoomSessions Room, int Turn)> SaveAfterOneResolveAsync(string name)
    {
        var room = await RoomSessions.CreateAsync(_api, new[] { "director" }, name: name);
        var turnBefore = room["director"].Turn;
        await ResolveAsync(room["director"]);

        await room["director"].SendAsync(new JsonObject { ["type"] = "save", ["name"] = name });
        (await room["director"].WaitForTypeAsync("saved", 1, 8000)).Should().BeTrue("手动存档要有 saved 回执");
        return (room, turnBefore + 1);
    }

    private static async Task ResolveAsync(WsClient dir)
    {
        var before = dir.Turn;
        await dir.SendAsync(new JsonObject { ["type"] = "resolve" });
        (await dir.WaitUntilAsync(() => dir.Turn == before + 1, 15000))
            .Should().BeTrue($"结算要把回合从 {before} 推到 {before + 1}");
    }

    /// <summary>第 20、21、22 项：存档进列表 → 载入成新房间 → 回合数与房间里那一局一致。</summary>
    [Fact]
    public async Task Save_Is_Listed_And_Reloads_At_The_Same_Turn()
    {
        var (room, turn) = await SaveAfterOneResolveAsync("存档往返");
        await using (room)
        {
            var instance = _api.InstanceIdOf(room.Created);
            var saved = room["director"].OfType("saved").Last();
            WsClient.StrOf(saved, "instance").Should().Be(instance, "手动存档必须落在同一副本上");
            // saved.turn 取的是 meta.last_turn —— 已结算到的回合，不是房间里的续跑回合
            WsClient.NumberField(saved, "turn").Should().Be(turn - 1, "回执里的回合是刚落盘那一回合");
            WsClient.NumberField(saved["meta"]?.AsObject(), "last_turn").Should().Be(turn - 1);

            var scenarios = await _api.GetJsonAsync("/api/scenarios");
            WsClient.ArrayOf(scenarios, "saves").Select(s => WsClient.StrOf(s, "instance"))
                .Should().Contain(instance, "存完就要在存档列表里看得见");

            var loaded = await _api.PostJsonAsync("/api/saves/load",
                new JsonObject { ["instance"] = instance }.ToJsonString());
            var code = WsClient.StrOf(loaded, "code");
            code.Should().NotBeEmpty("存档要能载入成新房间");
            WsClient.NumberField(loaded, "resumed_turn").Should().Be(turn, "载入后回合数一致");
            WsClient.StringsOf(loaded, "replay_warnings").Should().BeEmpty("重放不该报指纹不一致");

            var reopened = await WsClient.ConnectAsync(_api, code, "director");
            await using (reopened)
            {
                (await reopened.WaitForTypesAsync("welcome", "state")).Should().BeTrue();
                reopened.Turn.Should().Be(turn, "新房间首包就得是续跑回合");
            }
        }
    }

    /// <summary>mjs 7/8：存档落盘文件与每回合记录里的随机源起点。</summary>
    [Fact]
    public async Task Archive_Lands_On_Disk_With_The_Rng_Start_Point()
    {
        var (room, turn) = await SaveAfterOneResolveAsync("落盘检查");
        await using (room)
        {
            var dir = Path.Combine(_api.SaveDirectory, _api.InstanceIdOf(room.Created));
            foreach (var file in new[] { "meta.json", "init.json", "turns.jsonl", "snapshot_t1.json" })
                File.Exists(Path.Combine(dir, file)).Should().BeTrue($"副本存档目录缺 {file}（{dir}）");

            var firstLine = (await File.ReadAllLinesAsync(Path.Combine(dir, "turns.jsonl"))).First();
            JsonNode.Parse(firstLine)!.AsObject().ContainsKey("rng_at_resolve").Should().BeTrue(
                "回合记录必须记结算起点的随机源状态，否则尾部重放拨不回原位：" + firstLine);
            WsClient.NumberField(room["director"].View, "turn").Should().Be(turn, "存档期间回合别再动");
        }
    }

    /// <summary>mjs 8/8a：载入房间的指纹 = 房间指纹；续跑房间必须有带 battles 的首帧。</summary>
    [Fact]
    public async Task Resumed_Room_Keeps_The_Fingerprint_And_Seeds_A_Replay_Frame()
    {
        var (room, turn) = await SaveAfterOneResolveAsync("指纹续跑");
        await using (room)
        {
            var live = await _api.GetJsonAsync("/api/room/" + room.Code);
            var fingerprint = WsClient.StrOf(live, "fingerprint");
            fingerprint.Should().NotBeEmpty();

            var loaded = await _api.PostJsonAsync("/api/saves/load", new JsonObject
            {
                ["instance"] = _api.InstanceIdOf(room.Created),
            }.ToJsonString());
            WsClient.StrOf(loaded, "fingerprint").Should().Be(fingerprint,
                "存档 + 重放尾部必须回到房间里那个指纹（P1 确定性）");

            var frames = WsClient.ArrayOf(
                await _api.GetJsonAsync("/api/frames/" + WsClient.StrOf(loaded, "code")), "frames");
            frames.Should().NotBeEmpty("续跑房间要补一帧 seed 帧，前端回放才有第一帧");
            frames[0].ContainsKey("battles").Should().BeTrue(
                "seed 帧缺 battles 数组会让前端的 Frame 类型解不出来");
            WsClient.NumberField(frames[0], "turn").Should().Be(turn - 1, "seed 帧是已结算那一回合");
        }
    }

    /// <summary>自动快照与尾部重放：满 3 回合落一份快照，载入时只重放快照之后的那一回合。</summary>
    [Fact]
    public async Task Snapshots_Roll_Every_Three_Turns_And_The_Tail_Replays()
    {
        var room = await RoomSessions.CreateAsync(_api, new[] { "director" }, name: "尾部重放");
        await using (room)
        {
            for (var i = 0; i < 4; i++) await ResolveAsync(room["director"]);

            var instance = _api.InstanceIdOf(room.Created);
            File.Exists(Path.Combine(_api.SaveDirectory, instance, "snapshot_t3.json"))
                .Should().BeTrue("满 3 回合应自动落一份快照");

            var loaded = await _api.PostJsonAsync("/api/saves/load",
                new JsonObject { ["instance"] = instance }.ToJsonString());
            WsClient.NumberField(loaded, "snapshot_turn").Should().Be(3, "应从快照 t3 续跑");
            // 只重放快照之后的那一回合：t1-t3 从快照直接恢复，重放 t4 才说明尾部机制在工作
            WsClient.NodesOf(loaded, "replayed_turns").Select(n => n!.GetValue<int>()).Should().Equal(4);
            WsClient.StringsOf(loaded, "replay_warnings").Should().BeEmpty("尾部重放指纹要完全吻合");
            WsClient.NumberField(loaded, "resumed_turn").Should().Be(5);
        }
    }
}
