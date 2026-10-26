using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HexFront.Core.Services;

namespace HexFront.Api.Services;

/// <summary>
/// 推演操作流水门面：保留原有公共 API，存储统一委托给 AppDbService（data/app.db）。
/// 阶段常量与白名单不变，OpsLogController 无需改动。
/// </summary>
public sealed class OpsLogService
{
    public const string StageCreated = "instance_created";
    public const string StageOrders = "orders";
    public const string StageClearOrders = "clear_orders";
    public const string StageReady = "ready";
    public const string StageTurnResolved = "turn_resolved";
    public const string StageDirectorCmd = "director_cmd";
    public const string StageSave = "save";
    public const string StageLoaded = "instance_loaded";
    public const string StageRoomOpened = "room_opened";

    private static readonly string[] Stages =
    {
        StageCreated, StageOrders, StageClearOrders, StageReady,
        StageTurnResolved, StageDirectorCmd, StageSave, StageLoaded, StageRoomOpened,
    };

    /// <summary>查询端 stage 过滤白名单。</summary>
    public static IReadOnlyList<string> AllStages => Stages;

    public static bool IsValidStage(string stage) => Array.IndexOf(Stages, stage) >= 0;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppDbService _db;
    private readonly ILogger<OpsLogService> _logger;

    public OpsLogService(AppDbService db, ILogger<OpsLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>建局/载入时登记实例（幂等）。</summary>
    public void LogInstance(string instanceId, string scenario, string? name, int? seed, string source)
    {
        _db.OpsLogInstance(instanceId, scenario, name, seed, source);
        Log(instanceId, source == "load" ? StageLoaded : StageCreated, "system", null,
            new Dictionary<string, object?> { ["scenario"] = scenario, ["name"] = name, ["seed"] = seed, ["source"] = source });
    }

    /// <summary>提交指令阶段：记录本方草案全量（replace 语义 + 逐条 wire JSON + 校验拒绝数）。</summary>
    public void LogOrders(string instanceId, string side, int turn, bool replace,
        IReadOnlyList<Command> commands, int rejectedCount)
    {
        Log(instanceId, StageOrders, side, turn, new Dictionary<string, object?>
        {
            ["replace"] = replace,
            ["count"] = commands.Count,
            ["commands"] = commands.Select(CommandWire.ToMap).ToList(),
            ["rejected"] = rejectedCount,
        });
    }

    /// <summary>回合结算阶段：记录双方最终指令、随机源起点、前后指纹与被拒清单。</summary>
    public void LogTurnResolved(string instanceId, int resolvedTurn, string resolvedBy, uint rngAtResolve,
        IReadOnlyDictionary<string, IReadOnlyList<Command>> orders, int rejected,
        string fpBefore, string fpAfter, bool over, string? winner)
    {
        Log(instanceId, StageTurnResolved, resolvedBy, resolvedTurn, new Dictionary<string, object?>
        {
            ["resolved_by"] = resolvedBy,
            ["rng_at_resolve"] = rngAtResolve,
            ["red"] = orders["red"].Select(CommandWire.ToMap).ToList(),
            ["blue"] = orders["blue"].Select(CommandWire.ToMap).ToList(),
            ["rejected"] = rejected,
            ["fingerprint_before"] = fpBefore,
            ["fingerprint_after"] = fpAfter,
            ["over"] = over,
            ["winner"] = winner,
        });
    }

    /// <summary>通用埋点（就绪/清令/导调/存档等）。写入失败只告警，绝不打断推演主流程。</summary>
    public void Log(string instanceId, string stage, string? actor, int? turn, object? payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            _db.OpsInsert(instanceId, DateTime.UtcNow.ToString("o"), stage, actor, turn, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "操作流水写入失败 stage={Stage} instance={Instance}", stage, instanceId);
        }
    }

    /// <summary>实例列表（含操作条数与最后活跃时间）。</summary>
    public List<Dictionary<string, object?>> ListInstances(int limit = 50) => _db.OpsInstances(limit);

    /// <summary>按阶段/回合查询某实例的操作流水（id 升序，取尾部 limit 条）。</summary>
    public List<Dictionary<string, object?>> Query(string instanceId, string? stage, int? turn, int limit = 200) =>
        _db.OpsQuery(instanceId, stage, turn, limit);

    /// <summary>某实例的各阶段条数概览。</summary>
    public Dictionary<string, object?> Stats(string instanceId) => _db.OpsStats(instanceId);
}
