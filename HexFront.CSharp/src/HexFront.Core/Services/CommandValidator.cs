using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>Command operation types.</summary>
public enum CommandOp
{
    Move,
    Attack,
    Fire,
    Dig,
    Rest,
    AirMission,
    Halt,
    /// <summary>线上写了个引擎不认的动作名；原样回给玩家 unknown_op，不能悄悄吞掉或降级成 halt。</summary>
    Unknown,
}

/// <summary>Player command issued to a unit.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(CommandWire.WireConverter))]
public sealed class Command
{
    public CommandOp Op { get; init; }
    /// <summary>线上原始动作名（仅 Op==Unknown 时用于回显与排错）。</summary>
    public string? OpRaw { get; init; }
    public string UnitId { get; init; } = "";
    public List<Hex> Path { get; init; } = new();      // For move: continuous adjacent path
    public string? Target { get; init; }                // For attack: enemy unit ID
    public Hex? Zone { get; init; }                     // For fire/air_mission: target hex
    public string Mission { get; init; } = "none";      // air_mission: patrol|strike|recon|none

    public Command Clone() => (Command)MemberwiseClone();
}

/// <summary>Validation error for a rejected command.</summary>
public sealed class CommandError
{
    public string UnitId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Message { get; init; } = "";

    public CommandError(string unitId, string reason, string message)
    {
        UnitId = unitId;
        Reason = reason;
        Message = message;
    }
}

/// <summary>
/// Server-side command validation: all legality checks before turn resolution.
/// Ported from commands.dart validateOrders with full compatibility.
/// </summary>
public static class CommandValidator
{
    /// <summary>Validate a batch of orders for a given side. Returns list of rejected commands with reasons.</summary>
    public static List<CommandError> ValidateOrders(GameState g, string side, List<Command> orders)
    {
        var errs = new List<CommandError>();

        // Observed enemy units (E1+ exposure) that can be targeted
        var observed = GetObservedEnemies(g, side);
        var usedUnits = new HashSet<string>();

        foreach (var c in orders)
        {
            var u = g.Units.FirstOrDefault(u => u.Id == c.UnitId);
            
            // Basic unit checks
            if (u == null || !u.Alive)
            {
                errs.Add(new CommandError(c.UnitId, "no_unit", "单位不存在或已损失"));
                continue;
            }

            if (u.Side != side)
            {
                errs.Add(new CommandError(c.UnitId, "not_yours", "不能指挥他方单位"));
                continue;
            }

            if (usedUnits.Contains(c.UnitId))
            {
                errs.Add(new CommandError(c.UnitId, "duplicate", "本回合该单位已受领其他指令"));
                continue;
            }

            // Routed units can only rest —— 溃退是结算落到 Status 上的状态，
            // 不能用 morale<=0 代理：士气见底不等于已经溃退，代理会让残兵连休整令都下不了。
            if (u.Status == UnitStatus.Routed && c.Op != CommandOp.Rest)
            {
                errs.Add(new CommandError(c.UnitId, "routed", "溃退中只能休整"));
                continue;
            }

            usedUnits.Add(c.UnitId);

            // Operation-specific validation
            switch (c.Op)
            {
                case CommandOp.Move:
                    ValidateMove(g, side, u, c, errs);
                    break;
                case CommandOp.Attack:
                    ValidateAttack(g, side, u, c, observed, errs);
                    break;
                case CommandOp.Fire:
                    ValidateFire(g, u, c, errs);
                    break;
                case CommandOp.AirMission:
                    ValidateAirMission(g, u, c, errs);
                    break;
                case CommandOp.Dig:
                case CommandOp.Rest:
                case CommandOp.Halt:
                    // No additional validation needed
                    break;
                default:
                    errs.Add(new CommandError(c.UnitId, "unknown_op", $"未知指令 {CommandWire.OpEcho(c)}"));
                    break;
            }
        }

        return errs;
    }

    private static void ValidateMove(GameState g, string side, Unit u, Command c, List<CommandError> errs)
    {
        var t = g.TypeOf(u);
        
        // Static structures cannot move
        if (t.Cls == "structure")
        {
            errs.Add(new CommandError(c.UnitId, "static", "固定设施不可机动"));
            return;
        }

        // Path must have at least 2 points (start + destination)
        if (c.Path.Count < 2)
        {
            errs.Add(new CommandError(c.UnitId, "bad_path", "路径至少需含起点与一个目标格"));
            return;
        }

        // Path must be contiguous (adjacent hexes)
        if (!IsAdjacentPath(c.Path))
        {
            errs.Add(new CommandError(c.UnitId, "not_adjacent", "路径存在跳格（非相邻移动）"));
            return;
        }

        // Path must start at unit's current position
        if (!c.Path[0].Equals(u.Pos))
        {
            errs.Add(new CommandError(c.UnitId, "path_start", "路径起点必须为单位当前格"));
            return;
        }

        // Check each step in the path
        for (var i = 1; i < c.Path.Count; i++)
        {
            var h = c.Path[i];

            // 以格集为准：数据包可能切掉边角，Grid 矩形内也可能有境外 void 格
            if (!g.Cells.ContainsKey(h.Key))
            {
                errs.Add(new CommandError(c.UnitId, "out_of_map", "路径越界"));
                return;
            }

            // Impassable terrain
            var td = g.TerrainOf(h);
            if (td.Impassable)
            {
                errs.Add(new CommandError(c.UnitId, "impassable", "路径经过不可通行地形"));
                return;
            }

            // Enemy-occupied hex (must fight first). 只有地面占领才拦路 ——
            // 敌方航空兵过境既不构成物理阻挡，也在迷雾里看不见，拿来拒玩家的移动令不公平。
            var enemies = g.Units
                .Where(o => o.Alive && o.Side != side && o.Pos == h && !g.TypeOf(o).IsAir)
                .ToList();
            if (enemies.Any())
            {
                errs.Add(new CommandError(c.UnitId, "blocked", "路径穿过敌方占据格，需先交战"));
                return;
            }
        }
    }

    private static void ValidateAttack(GameState g, string side, Unit u, Command c, 
        HashSet<string> observed, List<CommandError> errs)
    {
        var t = g.TypeOf(u);
        var target = g.Units.FirstOrDefault(tgt => tgt.Id == c.Target);

        // Target must exist and be alive
        if (target == null || !target.Alive)
        {
            errs.Add(new CommandError(c.UnitId, "no_target", "目标不存在"));
            return;
        }

        // Cannot attack friendly units
        if (target.Side == side)
        {
            errs.Add(new CommandError(c.UnitId, "friendly", "不可攻击己方"));
            return;
        }

        // Fog of war: target must be observed (E1+)
        if (!observed.Contains(target.Id))
        {
            errs.Add(new CommandError(c.UnitId, "not_observed", "目标未被侦察到，不可下令攻击"));
            return;
        }

        // Check range
        var dist = u.Pos.DistanceTo(target.Pos);
        var reach = AttackReach(t);

        if (dist > reach)
        {
            errs.Add(new CommandError(c.UnitId, "out_of_range", $"目标超出射程（{dist} > {reach}）"));
            return;
        }
    }

    private static void ValidateFire(GameState g, Unit u, Command c, List<CommandError> errs)
    {
        var t = g.TypeOf(u);

        // Only units with indirect-fire range (arty/mlrs) can zone fire
        if (t.Range <= 0)
        {
            errs.Add(new CommandError(c.UnitId, "not_artillery", "非炮兵单位不可间接火力覆盖"));
            return;
        }

        // Zone must be valid
        if (c.Zone == null || !g.Cells.ContainsKey(c.Zone.Value.Key))
        {
            errs.Add(new CommandError(c.UnitId, "bad_zone", "火力目标格无效"));
            return;
        }

        // 多给 1 格余量：炮兵按射界边缘覆盖预判集结地是常规用法，精度降级在结算里扣
        var dist = u.Pos.DistanceTo(c.Zone.Value);
        if (dist > t.Range + 1)
        {
            errs.Add(new CommandError(c.UnitId, "out_of_range", $"超出射程（{dist} > {t.Range + 1}）"));
            return;
        }

        // Blind fire allowed (accuracy penalty handled in resolution)
    }

    private static void ValidateAirMission(GameState g, Unit u, Command c, List<CommandError> errs)
    {
        var t = g.TypeOf(u);

        // Only air units can receive air missions
        if (!t.IsAir)
        {
            errs.Add(new CommandError(c.UnitId, "not_air", "仅空中单位可指派任务"));
            return;
        }

        // If mission is specified, zone must be valid
        if (c.Mission != "none")
        {
            if (c.Zone == null || !g.Cells.ContainsKey(c.Zone.Value.Key))
            {
                errs.Add(new CommandError(c.UnitId, "bad_zone", "任务区无效"));
                return;
            }

            var dist = u.Pos.DistanceTo(c.Zone.Value);
            if (dist > t.AirRange)
            {
                errs.Add(new CommandError(c.UnitId, "out_of_range", $"任务区超出作战半径（{dist} > {t.AirRange}）"));
                return;
            }
        }
    }

    /// <summary>Check if a path consists of contiguous adjacent hexes.</summary>
    private static bool IsAdjacentPath(List<Hex> path)
    {
        for (var i = 1; i < path.Count; i++)
        {
            if (path[i - 1].DistanceTo(path[i]) != 1)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 已暴露（E1+）的敌方单位 id 集合 —— 只有它们能被下攻击令（doc §3.4）。
    /// 读 tracks 而不是当场重算观测：玩家看到的是上一阶段刷新出来的航迹，
    /// 拿"这一回合刚移动出去的瞬时观测"去允许打看不见的目标，等于把迷雾删了。
    /// 明牌（fog=false）时全员可打，与 sideView 实时合成 E3 接触的口径一致。
    /// </summary>
    private static HashSet<string> GetObservedEnemies(GameState g, string side)
    {
        var enemies = g.Units.Where(u => u.Alive && u.Side != side && u.Side != "neutral");
        if (!FogService.FlagBool(g, "fog", true))
            return new HashSet<string>(enemies.Select(u => u.Id));

        var observed = new HashSet<string>();
        if (!g.Tracks.TryGetValue(side, out var tracks)) return observed;
        foreach (var (unitId, t) in tracks)
        {
            if (t.Level < g.Rules.ExposureE1) continue;
            observed.Add(unitId);
        }
        return observed;
    }

    /// <summary>
    /// 攻击射程：航空兵按作战半径，炮兵/反坦克炮等间接火力按自身射程，其余近战 1 格。
    /// 一律取自兵种表，不能写死 —— 剧本用 unit_type_defs 覆盖射程要立刻生效。
    /// </summary>
    private static int AttackReach(UnitTypeDef t) =>
        t.IsAir ? t.AirRange : (t.Range > 0 ? t.Range : 1);
}
