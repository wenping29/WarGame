using System;
using System.Collections.Generic;
using HexFront.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HexFront.Api.Controllers;

/// <summary>
/// SQLite 操作流水查询：兵棋推演各阶段操作（建局/指令/就绪/结算/导调/存档）的审计与复盘入口。
/// </summary>
[ApiController]
public class OpsLogController : ControllerBase
{
    private readonly OpsLogService _ops;

    public OpsLogController(OpsLogService ops) => _ops = ops;

    /// <summary>GET /api/ops/instances —— 有操作流水的实例列表（含条数与最后活跃时间）。</summary>
    [HttpGet("api/ops/instances")]
    public IActionResult Instances([FromQuery] int limit = 50)
        => Ok(new Dictionary<string, object?> { ["instances"] = _ops.ListInstances(Math.Clamp(limit, 1, 200)) });

    /// <summary>GET /api/ops/stats/{instance} —— 某实例各阶段条数概览。</summary>
    [HttpGet("api/ops/stats/{instance}")]
    public IActionResult Stats(string instance) => Ok(_ops.Stats(instance));

    /// <summary>
    /// GET /api/ops/{instance}?stage=orders&amp;turn=3&amp;limit=200
    /// 按阶段（instance_created/orders/clear_orders/ready/turn_resolved/director_cmd/save/instance_loaded）与回合过滤操作流水。
    /// </summary>
    [HttpGet("api/ops/{instance}")]
    public IActionResult Query(string instance, [FromQuery] string? stage, [FromQuery] int? turn, [FromQuery] int limit = 200)
    {
        if (!string.IsNullOrEmpty(stage) && !OpsLogService.IsValidStage(stage))
            return BadRequest(new Dictionary<string, object?> { ["error"] = $"未知 stage: {stage}", ["stages"] = OpsLogService.AllStages });
        return Ok(new Dictionary<string, object?>
        {
            ["instance"] = instance,
            ["stage"] = stage,
            ["turn"] = turn,
            ["ops"] = _ops.Query(instance, stage, turn, Math.Clamp(limit, 1, 1000)),
        });
    }
}
