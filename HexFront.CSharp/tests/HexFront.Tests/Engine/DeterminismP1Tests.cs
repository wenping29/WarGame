using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using HexFront.Core.Geometry;
using HexFront.Core.Models;
using HexFront.Core.Services;
using Xunit;

namespace HexFront.Tests.Engine;

/// <summary>
/// P1 回归：同种子同指令必须同结果，存档往返后要能接着跑出同一个指纹。
/// 用随仓剧本真建局，AI 补令也走同一条随机流，覆盖比手写几条指令大得多。
/// </summary>
public class DeterminismP1Tests : IDisposable
{
    private readonly string _scenarioPath;
    private readonly string _gisDir;
    private readonly string _saveDir;

    public DeterminismP1Tests()
    {
        var root = RepoRoot();
        _scenarioPath = Path.Combine(root, "data", "scenarios", "rhine_corridor.json");
        _gisDir = Path.Combine(root, "data", "gis");
        _saveDir = Path.Combine(Path.GetTempPath(), "hf-p1-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_saveDir)) Directory.Delete(_saveDir, true);
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "scenarios", "rhine_corridor.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 data/scenarios/rhine_corridor.json");
    }

    private GameState NewGame(uint seed)
    {
        var cfg = ScenarioService.LoadFromFile(_scenarioPath);
        var g = ScenarioService.CreateGameState(cfg, _gisDir, seed);
        g.WeatherMode = "realistic"; // 让每回合都走天气演变，把随机源用在刀刃上
        return g;
    }

    /// <summary>双 AI 逐回合补令：红先蓝后，两边同种子时这条序列完全一致。</summary>
    private static Dictionary<string, List<Command>> AiOrders(GameState g) => new()
    {
        ["red"] = AiService.GenerateOrders(g, "red"),
        ["blue"] = AiService.GenerateOrders(g, "blue"),
    };

    [Fact]
    public void Rng_Matches_Reference_Vector()
    {
        // 与 rng.dart mulberry32 同一支：seed 42 前 5 个 uint32 与末状态
        var rng = new SeededRng(42);
        var draws = Enumerable.Range(0, 5).Select(_ => rng.NextUInt32()).ToList();

        draws.Should().Equal(0x99e1ef7cu, 0x72c32b8au, 0xda3b32c0u, 0xab73b0adu, 0x2cc09a8au);
        rng.State.Should().Be(0x21d961f3u);
    }

    [Fact]
    public void NextInt_Is_Inclusive_And_Uses_Full_Range()
    {
        var rng = new SeededRng(7);
        var seen = new HashSet<int>();
        for (var i = 0; i < 300; i++)
        {
            var v = rng.NextInt(-1, 1);
            v.Should().BeInRange(-1, 1);
            seen.Add(v);
        }
        seen.Should().BeEquivalentTo(new[] { -1, 0, 1 });
    }

    [Fact]
    public void Same_Seed_Same_Orders_Produce_Identical_Turns()
    {
        var a = NewGame(777);
        var b = NewGame(777);

        for (var t = 0; t < 6 && !a.Over; t++)
        {
            TurnResolver.ResolveTurn(a, AiOrders(a));
            TurnResolver.ResolveTurn(b, AiOrders(b));

            b.Fingerprint().Should().Be(a.Fingerprint(), $"第 {t + 1} 回合指纹必须对拍一致");
            b.RngState.Should().Be(a.RngState);
        }
    }

    [Fact]
    public void Different_Seed_Diverges_In_Rng_Stream()
    {
        var a = NewGame(1);
        var b = NewGame(2);
        TurnResolver.ResolveTurn(a, AiOrders(a));
        TurnResolver.ResolveTurn(b, AiOrders(b));

        a.RngState.Should().NotBe(b.RngState);
    }

    [Fact]
    public void Snapshot_Plus_Tail_Replay_Resumes_Exactly()
    {
        var live = NewGame(2026);
        StoreService.WriteInit(live, "P1 往返", "test", _saveDir);

        for (var t = 0; t < 4; t++)
        {
            var orders = AiOrders(live);
            var rngAtResolve = live.RngState;
            var rep = TurnResolver.ResolveTurn(live, orders);
            var resolved = live.Turn - 1;
            StoreService.AppendTurn(live.InstanceId, new StoreService.TurnRecord
            {
                Turn = resolved,
                At = DateTime.UtcNow,
                ResolvedBy = "test",
                RngAtResolve = rngAtResolve,
                Red = orders["red"],
                Blue = orders["blue"],
                FpBefore = rep.FingerprintBefore,
                FpAfter = rep.FingerprintAfter,
            }, _saveDir);
            if (StoreService.ShouldSnapshot(resolved, live.Over)) StoreService.WriteSnapshot(live, _saveDir);
        }

        // 3 个已结算回合该落一张全量快照
        File.Exists(Path.Combine(_saveDir, live.InstanceId, "snapshot_t3.json")).Should().BeTrue();

        var outcome = StoreService.Load(live.InstanceId, _saveDir);
        outcome.SnapshotTurn.Should().Be(3);
        outcome.ReplayedTurns.Should().Equal(4);
        outcome.FingerprintMismatches.Should().BeEmpty();

        outcome.Game.Fingerprint().Should().Be(live.Fingerprint());
        outcome.Game.RngState.Should().Be(live.RngState, "续局的随机源必须接上，否则从这里开始必然分叉");

        // 续跑一回合：载入局和一直跑着的局仍要同指纹
        TurnResolver.ResolveTurn(outcome.Game, AiOrders(outcome.Game));
        TurnResolver.ResolveTurn(live, AiOrders(live));
        outcome.Game.Fingerprint().Should().Be(live.Fingerprint());
    }

    [Fact]
    public void Load_Without_Snapshot_Replays_Whole_Log()
    {
        var live = NewGame(31337);
        StoreService.WriteInit(live, "P1 全量重放", null, _saveDir);
        for (var t = 0; t < 2; t++)
        {
            var orders = AiOrders(live);
            var rngAtResolve = live.RngState;
            var rep = TurnResolver.ResolveTurn(live, orders);
            StoreService.AppendTurn(live.InstanceId, new StoreService.TurnRecord
            {
                Turn = live.Turn - 1,
                RngAtResolve = rngAtResolve,
                Red = orders["red"],
                Blue = orders["blue"],
                FpBefore = rep.FingerprintBefore,
                FpAfter = rep.FingerprintAfter,
            }, _saveDir);
        }

        var outcome = StoreService.Load(live.InstanceId, _saveDir);
        outcome.SnapshotTurn.Should().Be(0);
        outcome.ReplayedTurns.Should().Equal(1, 2);
        outcome.Game.Fingerprint().Should().Be(live.Fingerprint());
    }

    [Fact]
    public void Turn_Log_Keeps_Wire_Shape_And_Rewinds_On_Rebranch()
    {
        var g = NewGame(5);
        var cmd = new Command
        {
            Op = CommandOp.Move,
            UnitId = "R1",
            Path = { new Hex(3, 4), new Hex(4, 4) },
        };

        for (var turn = 1; turn <= 4; turn++)
            StoreService.AppendTurn(g.InstanceId, Record(turn, new List<Command> { cmd }), _saveDir);

        var lines = File.ReadAllLines(Path.Combine(_saveDir, g.InstanceId, "turns.jsonl"));
        lines.Should().HaveCount(4);
        using (var doc = JsonDocument.Parse(lines[0]))
        {
            doc.RootElement.GetProperty("red")[0].GetProperty("op").GetString().Should().Be("move");
            doc.RootElement.GetProperty("red")[0].GetProperty("path")[1][0].GetInt32().Should().Be(4);
        }

        // 从第 3 回合另开分岐：同回合号及其后的旧记录要被顶掉
        StoreService.AppendTurn(g.InstanceId, Record(3, new List<Command> { cmd }), _saveDir);
        var records = StoreService.ReadTurnRecords(g.InstanceId, _saveDir);
        records.Select(r => r.Turn).Should().Equal(1, 2, 3);
        records[2].Red.Should().HaveCount(1);
        records[2].Red[0].Path.Should().Equal(new Hex(3, 4), new Hex(4, 4));
    }

    private static StoreService.TurnRecord Record(int turn, List<Command> red) => new()
    {
        Turn = turn,
        At = DateTime.UtcNow,
        ResolvedBy = "test",
        Red = red,
    };

    [Fact]
    public void Save_Listing_Reads_Instance_Directories()
    {
        var g = NewGame(9);
        StoreService.WriteInit(g, "副本甲", "描述", _saveDir);
        StoreService.AppendTurn(g.InstanceId, Record(1, new List<Command>()), _saveDir);
        StoreService.WriteSnapshot(g, _saveDir);

        var listing = StoreService.ListSaves(_saveDir);
        var row = listing.Should().ContainSingle().Subject;
        row.Instance.Should().Be(g.InstanceId);
        row.Name.Should().Be("副本甲");
        row.Seed.Should().Be(9);
        row.TurnsRecorded.Should().Be(1);
        row.LastTurn.Should().Be(0); // 手动存档在第 1 回合：还没有回合结算完

        StoreService.DeleteSave(g.InstanceId, _saveDir);
        StoreService.ListSaves(_saveDir).Should().BeEmpty();
    }

    [Fact]
    public void Instance_Id_Never_Escapes_Save_Dir()
    {
        var act = () => StoreService.Load("../../etc/passwd", _saveDir);
        act.Should().Throw<ArgumentException>();
    }
}
