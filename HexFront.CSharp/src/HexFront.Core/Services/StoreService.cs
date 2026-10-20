using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// 副本存档（§12.1）：一个实例一个目录，删目录即清空副本，不需要中心索引。
/// meta.json 记种子与进度，init.json 存开局全量，turns.jsonl 追加每回合双盲指令，
/// snapshot_t&lt;N&gt;.json 每 K 个已结算回合落一次全量快照；载入 = 最新快照 + 重放尾部指令。
/// </summary>
public static class StoreService
{
    /// <summary>全量快照周期（已结算回合数）。</summary>
    public const int SnapshotEveryTurns = 3;

    private const string MetaFileName = "meta.json";
    private const string InitFileName = "init.json";
    private const string TurnsFileName = "turns.jsonl";
    private const string SnapshotPrefix = "snapshot_t";

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // jsonl 一行一条，绝不能缩进
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>meta.json 内容。</summary>
    public sealed class SaveMetadata
    {
        [JsonPropertyName("instance")] public string Instance { get; set; } = "";
        [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
        public string ScenarioName { get; set; } = "";
        public string Name { get; set; } = "";
        public uint Seed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>已结算到的回合号（0 = 刚开局）。</summary>
        public int LastTurn { get; set; }
        public int MaxTurns { get; set; }
        public string? Description { get; set; }

        /// <summary>创建时提交的覆盖层原样留档（审阅/what-if 分岐要用）。</summary>
        public JsonElement? Overrides { get; set; }

        /// <summary>turns.jsonl 行数，列表接口现算，不落盘。</summary>
        [JsonIgnore] public int TurnsRecorded { get; set; }

        /// <summary>最新快照对应的已结算回合号。</summary>
        [JsonIgnore] public int SnapshotTurn { get; set; }
    }

    /// <summary>turns.jsonl 里的一条回合记录：双盲指令 + 结算元数据 + 前后指纹。</summary>
    public sealed class TurnRecord
    {
        public int Turn { get; set; }
        public DateTime At { get; set; }
        public string ResolvedBy { get; set; } = "auto";

        /// <summary>
        /// 结算开始前的随机源状态。副官补令本身不抽随机数（同局面必出同令），
        /// 但重放只跑指令、不跑补令，把随机源钉回这一刻，续跑才不会从第一次抖动开始分叉。
        /// </summary>
        public uint? RngAtResolve { get; set; }

        public List<Command> Red { get; set; } = new();
        public List<Command> Blue { get; set; } = new();
        public List<RejectedOrder> Rejected { get; set; } = new();
        public string FpBefore { get; set; } = "";
        public string FpAfter { get; set; } = "";
    }

    /// <summary>被拒指令（与 WS 的 rejected 条目同键）。</summary>
    public sealed class RejectedOrder
    {
        [JsonPropertyName("unit")] public string Unit { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("message")] public string Message { get; set; } = "";

        public static RejectedOrder From(CommandError e) =>
            new() { Unit = e.UnitId, Reason = e.Reason, Message = e.Message };
    }

    /// <summary>载入结果：状态、所用快照回合、重放的尾部回合数、指纹对不上的回合。</summary>
    public sealed record LoadOutcome(
        GameState Game,
        int SnapshotTurn,
        List<int> ReplayedTurns,
        List<string> FingerprintMismatches);

    // ==================== 写入 ====================

    /// <summary>开局落盘：写 meta.json，init.json 只在缺失时写（初始快照不可变）。</summary>
    public static SaveMetadata WriteInit(
        GameState g, string? name, string? description, string saveDirectory,
        JsonElement? overrides = null)
    {
        var dir = InstanceDirectory(saveDirectory, g.InstanceId);
        Directory.CreateDirectory(dir);

        var now = DateTime.UtcNow;
        var meta = ReadMetaFrom(dir, g.InstanceId) ?? new SaveMetadata { Instance = g.InstanceId, CreatedAt = now };
        meta.Instance = g.InstanceId;
        meta.Name = string.IsNullOrWhiteSpace(name) ? g.ScenarioName : name.Trim();
        meta.Description = description;
        meta.Scenario = g.ScenarioId;
        meta.ScenarioName = g.ScenarioName;
        meta.Seed = g.Seed;
        meta.MaxTurns = g.MaxTurns;
        meta.Overrides = overrides;
        meta.LastTurn = Math.Max(meta.LastTurn, g.Turn - 1);
        meta.UpdatedAt = now;

        var initPath = Path.Combine(dir, InitFileName);
        if (!File.Exists(initPath)) WriteJson(initPath, g);
        WriteJson(Path.Combine(dir, MetaFileName), meta);
        return meta;
    }

    /// <summary>追加一条回合记录。同回合及之后的旧记录会被剔除（分岐重算后不留脏尾部）。</summary>
    public static void AppendTurn(string instance, TurnRecord record, string saveDirectory)
    {
        var dir = InstanceDirectory(saveDirectory, instance);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, TurnsFileName);

        var lines = new List<string>();
        if (File.Exists(file))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var turn = doc.RootElement.GetProperty("turn").GetInt32();
                    if (turn < record.Turn) lines.Add(line);
                }
                catch
                {
                    // 坏行直接丢弃：留着它，重放会在错误的位置续跑
                }
            }
        }
        lines.Add(JsonSerializer.Serialize(record, CompactOptions));
        File.WriteAllLines(file, lines);
    }

    /// <summary>该已结算回合是否该落全量快照。</summary>
    public static bool ShouldSnapshot(int resolvedTurn, bool over) =>
        over || resolvedTurn % SnapshotEveryTurns == 0;

    /// <summary>落全量快照 snapshot_t&lt;已结算回合&gt;.json 并刷新 meta。</summary>
    public static SaveMetadata WriteSnapshot(
        GameState g, string saveDirectory, string? name = null, string? description = null)
    {
        var dir = InstanceDirectory(saveDirectory, g.InstanceId);
        Directory.CreateDirectory(dir);

        var resolvedTurn = Math.Max(0, g.Turn - 1);
        var initPath = Path.Combine(dir, InitFileName);
        if (!File.Exists(initPath)) WriteJson(initPath, g);
        WriteJson(Path.Combine(dir, $"{SnapshotPrefix}{resolvedTurn}.json"), g);

        var meta = EnsureMeta(dir, g);
        meta.LastTurn = Math.Max(meta.LastTurn, resolvedTurn);
        meta.SnapshotTurn = Math.Max(meta.SnapshotTurn, resolvedTurn);
        if (!string.IsNullOrWhiteSpace(name)) meta.Name = name;
        if (description != null) meta.Description = description;
        meta.UpdatedAt = DateTime.UtcNow;
        WriteJson(Path.Combine(dir, MetaFileName), meta);
        return meta;
    }

    // ==================== 文档化存取（SQLite 真源路径） ====================

    /// <summary>一个副本的全部内容文档：meta/init/各回合快照/turns（jsonl 全文）。磁盘与 SQLite 两种载体共用。</summary>
    public sealed record SaveDocSet(
        string? MetaJson,
        string? InitJson,
        IReadOnlyList<(int Turn, string Json)> Snapshots,
        string? TurnsJsonl);

    /// <summary>开局文档组：meta 全量 + 首次 init 全文（prev==null 时生成，初始快照不可变）。</summary>
    public static (SaveMetadata Meta, string? InitJson, string MetaJson) BuildInitDocs(
        GameState g, string? name, string? description, JsonElement? overrides, SaveMetadata? prev)
    {
        var now = DateTime.UtcNow;
        var meta = prev ?? new SaveMetadata { Instance = g.InstanceId, CreatedAt = now };
        meta.Instance = g.InstanceId;
        meta.Name = string.IsNullOrWhiteSpace(name) ? g.ScenarioName : name.Trim();
        meta.Description = description;
        meta.Scenario = g.ScenarioId;
        meta.ScenarioName = g.ScenarioName;
        meta.Seed = g.Seed;
        meta.MaxTurns = g.MaxTurns;
        meta.Overrides = overrides;
        meta.LastTurn = Math.Max(meta.LastTurn, g.Turn - 1);
        meta.UpdatedAt = now;
        var initJson = prev == null ? JsonSerializer.Serialize(g, IndentedOptions) : null;
        return (meta, initJson, JsonSerializer.Serialize(meta, IndentedOptions));
    }

    /// <summary>快照文档组：snapshot 全文 + 首次 init（prev==null 时）+ 刷新后的 meta。</summary>
    public static (SaveMetadata Meta, string SnapshotJson, string? InitJson, string MetaJson) BuildSnapshotDocs(
        GameState g, SaveMetadata? prev, string? name = null, string? description = null)
    {
        var resolvedTurn = Math.Max(0, g.Turn - 1);
        var meta = prev ?? new SaveMetadata
        {
            Instance = g.InstanceId,
            Name = g.Name,
            Scenario = g.ScenarioId,
            ScenarioName = g.ScenarioName,
            Seed = g.Seed,
            MaxTurns = g.MaxTurns,
            CreatedAt = DateTime.UtcNow,
        };
        meta.LastTurn = Math.Max(meta.LastTurn, resolvedTurn);
        meta.SnapshotTurn = Math.Max(meta.SnapshotTurn, resolvedTurn);
        if (!string.IsNullOrWhiteSpace(name)) meta.Name = name;
        if (description != null) meta.Description = description;
        meta.UpdatedAt = DateTime.UtcNow;
        var initJson = prev == null ? JsonSerializer.Serialize(g, IndentedOptions) : null;
        return (meta, JsonSerializer.Serialize(g, IndentedOptions), initJson,
            JsonSerializer.Serialize(meta, IndentedOptions));
    }

    /// <summary>回合指令 jsonl 读-改-写：剔除 ≥ 新回合的旧记录后追加（分岐重算不留脏尾部）。坏行直接丢弃。</summary>
    public static string AppendTurnJsonl(string? existingJsonl, TurnRecord record)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(existingJsonl))
        {
            foreach (var raw in existingJsonl.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.GetProperty("turn").GetInt32() < record.Turn) lines.Add(line);
                }
                catch
                {
                    // 坏行直接丢弃：留着它，重放会在错误的位置续跑
                }
            }
        }
        lines.Add(JsonSerializer.Serialize(record, CompactOptions));
        return string.Join("\n", lines);
    }

    /// <summary>meta 文档 → SaveMetadata（坏/空当没有）。</summary>
    public static SaveMetadata? ParseMetaJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<SaveMetadata>(json, IndentedOptions);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>turns（jsonl 全文）→ 回合记录列表（单行损坏不影响其余回合重放）。</summary>
    public static List<TurnRecord> ParseTurnsJsonl(string? jsonl)
    {
        var outList = new List<TurnRecord>();
        if (string.IsNullOrEmpty(jsonl)) return outList;
        foreach (var raw in jsonl.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var rec = JsonSerializer.Deserialize<TurnRecord>(line, CompactOptions);
                if (rec != null) outList.Add(rec);
            }
            catch
            {
                // 单行损坏不影响其余回合重放
            }
        }
        return outList;
    }

    /// <summary>从内容文档载入：最新快照（无快照回退 init）+ 重放其后指令记录。磁盘与 SQLite 共用。</summary>
    public static LoadOutcome LoadFromDocs(SaveDocSet docs, string instance)
    {
        var bestTurn = -1;
        string? bestJson = null;
        foreach (var (turn, json) in docs.Snapshots)
            if (turn > bestTurn) { bestTurn = turn; bestJson = json; }

        bestJson ??= docs.InitJson;
        if (string.IsNullOrEmpty(bestJson))
            throw new FileNotFoundException($"存档不存在: {instance}");

        GameState state;
        try
        {
            state = JsonSerializer.Deserialize<GameState>(bestJson, IndentedOptions)
                ?? throw new InvalidOperationException("存档解析失败");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"存档损坏: {instance}（{ex.Message}）", ex);
        }

        var snapshotTurn = Math.Max(0, bestTurn);
        var replayed = new List<int>();
        var mismatches = new List<string>();

        foreach (var record in ParseTurnsJsonl(docs.TurnsJsonl).Where(r => r.Turn > snapshotTurn))
        {
            if (record.RngAtResolve is uint rngAtResolve) state.RngState = rngAtResolve;
            if (record.FpBefore.Length > 0 && state.Fingerprint() != record.FpBefore)
                mismatches.Add($"回合 {record.Turn}: 起点指纹 存档 {record.FpBefore} ≠ 重放 {state.Fingerprint()}");

            var orders = new Dictionary<string, List<Command>>
            {
                ["red"] = record.Red,
                ["blue"] = record.Blue,
            };
            var report = TurnResolver.ResolveTurn(state, orders);
            replayed.Add(record.Turn);
            if (record.FpAfter.Length > 0 && report.FingerprintAfter != record.FpAfter)
                mismatches.Add($"回合 {record.Turn}: 终点指纹 存档 {record.FpAfter} ≠ 重放 {report.FingerprintAfter}");
        }

        return new LoadOutcome(state, snapshotTurn, replayed, mismatches);
    }

    /// <summary>磁盘目录 → 内容文档（SQLite 不可用时的磁盘兜底读也走这套）。</summary>
    private static SaveDocSet ReadDocSet(string dir)
    {
        var snapshots = new List<(int Turn, string Json)>();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, SnapshotPrefix + "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (int.TryParse(name[SnapshotPrefix.Length..], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var turn))
                    snapshots.Add((turn, File.ReadAllText(file)));
            }
        }

        var initPath = Path.Combine(dir, InitFileName);
        var initJson = File.Exists(initPath) ? File.ReadAllText(initPath) : null;
        var turnsPath = Path.Combine(dir, TurnsFileName);
        var turnsJsonl = File.Exists(turnsPath) ? File.ReadAllText(turnsPath) : null;
        return new SaveDocSet(null, initJson, snapshots, turnsJsonl);
    }

    // ==================== 读取 ====================

    /// <summary>meta.json；缺字段时用状态补齐，坏文件当没有。</summary>
    public static SaveMetadata? ReadMeta(string instance, string saveDirectory)
    {
        var dir = InstanceDirectory(saveDirectory, SafeInstance(instance));
        return Directory.Exists(dir) ? ReadMetaFrom(dir, instance) : null;
    }

    /// <summary>列出全部副本（按创建时间倒序）。</summary>
    public static List<SaveMetadata> ListSaves(string saveDirectory)
    {
        var outList = new List<SaveMetadata>();
        if (!Directory.Exists(saveDirectory)) return outList;

        foreach (var dir in Directory.GetDirectories(saveDirectory))
        {
            var instance = Path.GetFileName(dir);
            var meta = ReadMetaFrom(dir, instance);
            if (meta == null) continue; // 没有 meta.json 的目录不是副本
            meta.TurnsRecorded = CountTurnLines(dir);
            meta.SnapshotTurn = LatestSnapshotTurn(dir);
            outList.Add(meta);
        }

        outList.Sort((a, b) => string.CompareOrdinal(b.CreatedAt.ToString("o"), a.CreatedAt.ToString("o")));
        return outList;
    }

    /// <summary>载入：最新快照（无快照回退 init.json）+ 重放其后指令记录（磁盘兜底路径）。</summary>
    public static LoadOutcome Load(string instance, string saveDirectory)
    {
        var dir = InstanceDirectory(saveDirectory, SafeInstance(instance));
        return LoadFromDocs(ReadDocSet(dir), instance);
    }

    /// <summary>只要状态的重载（调用方不关心重放细节时用）。</summary>
    public static GameState LoadGame(string instance, string saveDirectory) =>
        Load(instance, saveDirectory).Game;

    public static List<TurnRecord> ReadTurnRecords(string instance, string saveDirectory) =>
        ReadTurnRecords(InstanceDirectory(saveDirectory, SafeInstance(instance)));

    /// <summary>删副本 = 删目录。</summary>
    public static void DeleteSave(string instance, string saveDirectory)
    {
        var dir = InstanceDirectory(saveDirectory, SafeInstance(instance));
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    // ==================== 内部 ====================

    /// <summary>实例 id 直接当目录名用，先挡住 ../ 与非法字符（id 来自客户端）。</summary>
    private static string SafeInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Contains("..", StringComparison.Ordinal)
            || instance.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"非法实例 id: {instance}");
        return instance;
    }

    private static string InstanceDirectory(string saveDirectory, string instance) =>
        Path.Combine(saveDirectory, instance);

    private static SaveMetadata? ReadMetaFrom(string dir, string instance)
    {
        var file = Path.Combine(dir, MetaFileName);
        if (!File.Exists(file)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<SaveMetadata>(File.ReadAllText(file), IndentedOptions);
            if (meta == null) return null;
            if (string.IsNullOrEmpty(meta.Instance)) meta.Instance = instance;
            return meta;
        }
        catch
        {
            return null;
        }
    }

    private static SaveMetadata EnsureMeta(string dir, GameState g) =>
        ReadMetaFrom(dir, g.InstanceId) ?? new SaveMetadata
        {
            Instance = g.InstanceId,
            Name = g.Name,
            Scenario = g.ScenarioId,
            ScenarioName = g.ScenarioName,
            Seed = g.Seed,
            MaxTurns = g.MaxTurns,
            CreatedAt = DateTime.UtcNow,
        };

    private static int LatestSnapshotTurn(string dir)
    {
        var best = 0;
        foreach (var file in Directory.GetFiles(dir, SnapshotPrefix + "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name[SnapshotPrefix.Length..], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var turn) && turn > best) best = turn;
        }
        return best;
    }

    private static int CountTurnLines(string dir)
    {
        var file = Path.Combine(dir, TurnsFileName);
        if (!File.Exists(file)) return 0;
        return File.ReadLines(file).Count(l => !string.IsNullOrWhiteSpace(l));
    }

    private static List<TurnRecord> ReadTurnRecords(string dir)
    {
        var file = Path.Combine(dir, TurnsFileName);
        return ParseTurnsJsonl(File.Exists(file) ? File.ReadAllText(file) : null);
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, IndentedOptions));
}
