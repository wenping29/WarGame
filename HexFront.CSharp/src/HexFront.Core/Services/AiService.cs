using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// AI 副官（doc §「AI 副官：按迷雾可见情报生成合法指令，可代管缺席阵营或全 AI 自动对抗」）。
///
/// 三条硬约束，缺一条这个副官就不合格：
/// 1) 情报口径与 <see cref="FogService.SideView"/> 同源 —— 己方单位全知，敌方只读 tracks。
///    任何直接扫 g.Units 拿敌方真值来瞄准的做法等于把迷雾删了，双盲对局当场变成作弊对局。
/// 2) 产出的令要过 <see cref="CommandValidator"/>：坏令宁可不出。
///    被拒的机动令不会只让一个单位不动，它会让托管方整回合的战线原地僵住。
/// 3) 不碰 g.Rng：指令由局面唯一决定，同局面重放必然同令，续跑/回放不会因为副官而分叉。
/// </summary>
public static class AiService
{
    /// <summary>一次侦察回报：位置是"最后所见"而不是实时位置，等级决定我们还敢基于它下什么令。</summary>
    private readonly record struct Contact(string Id, Hex At, int Level, string Cls);

    /// <summary>Generate orders for every alive unit of an AI-controlled side.</summary>
    public static List<Command> GenerateOrders(GameState g, string side)
    {
        var contacts = VisibleContacts(g, side);
        var goals = ObjectiveHexes(g, side);

        var candidates = new List<Command>();
        foreach (var u in g.Units.Where(o => o.Side == side && o.Alive).OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var order = Decide(g, u, contacts, goals);
            if (order != null) candidates.Add(order);
        }

        // 服务端口径复核：AI 与人类玩家共用同一套合法性判定，不允许有"只有机器能下"的令
        var errs = CommandValidator.ValidateOrders(g, side, candidates).Select(e => e.UnitId).ToHashSet();
        return candidates.Where(c => !errs.Contains(c.UnitId)).ToList();
    }

    /// <summary>
    /// 敌方情报：只取本方航迹；明牌（flags.fog=false）时按 SideView 的同一套实时合成 E3 接触。
    /// status=gone（目标已被歼或消失）的航迹不参与决策 —— 视图上都不画了，副官不该照着打。
    /// </summary>
    private static List<Contact> VisibleContacts(GameState g, string side)
    {
        List<Track> tracks;
        if (!FogService.FlagBool(g, "fog", true))
        {
            tracks = g.Units
                .Where(u => u.Alive && u.Side != side && u.Side != "neutral")
                .Select(u => new Track { UnitId = u.Id, Level = 3, LastSeen = u.Pos, LastSeenTurn = g.Turn })
                .ToList();
        }
        else
        {
            tracks = g.Tracks.TryGetValue(side, out var t) ? t.Values.ToList() : new List<Track>();
        }

        var outList = new List<Contact>();
        foreach (var tr in tracks.OrderBy(t => t.UnitId, StringComparer.Ordinal))
        {
            var u = g.FindUnit(tr.UnitId);
            if (u == null || !u.Alive || u.Side == side) continue;
            // 兵种/类别只有判明到 E2 档才披露，档位与 FogService.ContactOf 同源（level>=2 才发 type/cls）。
            // 低级航迹只是"那儿有过东西"的方位参考，拿它挑软柿子等于替玩家开了全图。
            var known = tr.Level >= 2;
            var td = g.TypeOf(u);
            outList.Add(new Contact(tr.UnitId, tr.LastSeen, tr.Level, known ? td.Cls : ""));
        }
        return outList;
    }

    /// <summary>
    /// 本方该去夺的格：胜利条件点名的目标格 + 战场要点（电厂/变电站/机场/车站/城镇）。
    /// 排序留给调用方按距离做，这里只保证候选集稳定（按格 key）。
    /// </summary>
    private static List<Hex> ObjectiveHexes(GameState g, string side)
    {
        var keys = new HashSet<string>();

        foreach (var vc in g.VictoryConds)
        {
            if (vc.Kind == "capture" && vc.Side == side && !string.IsNullOrEmpty(vc.Target))
                keys.Add(vc.Target!);
        }

        foreach (var (key, cell) in g.Cells)
        {
            if (MechanicsService.NodeKindOf(cell) == null) continue;
            keys.Add(key);
        }

        return keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(Hex.Parse)
            .ToList();
    }

    // ==================== 决策 ====================

    private static Command? Decide(GameState g, Unit u, List<Contact> contacts, List<Hex> objectives)
    {
        var t = g.TypeOf(u);

        if (t.IsAir) return AirOrder(g, u, t, contacts);

        // 溃退单位除休整外一律被拒（校验如此），直接给最合理的令省得白跑一趟
        if (u.Status == UnitStatus.Routed) return Rest(u);

        if (t.Cls == "structure") return null; // 雷达站/机场动不了，也挖不了工事

        var blocked = BlockedHexes(contacts);
        var strike = PickTarget(g, u, t, contacts);
        if (strike != null)
        {
            var dist = u.Pos.DistanceTo(strike.Value.At);
            // 炮兵按弹着点下令、不必贴脸；直瞄则要真进得去 —— AttackReach 用校验器同一个口径
            if (t.Range > 0 && dist > 1) return Fire(u, strike.Value.At);
            if (dist <= CommandValidator.AttackReach(t)) return Attack(u, strike.Value.Id);
        }

        var goal = AdvanceGoal(g, u, t, contacts, objectives);
        if (goal != null)
        {
            var move = MoveToward(g, u, t, goal.Value, blocked);
            if (move != null) return move;
        }

        // 没有可打的、也没有该去的：原地整备。疲劳高的先歇，否则下一回合连腿都迈不开
        if (u.Fatigue > 70) return Rest(u);
        if (g.TerrainOf(u.Pos).MaxFort > u.FortLevel) return Dig(u);
        return Halt(u);
    }

    /// <summary>
    /// 选一个可打的接触目标：判明程度优先、其次最近、再按 id 定序（同局面必须同选择）。
    /// 两条筛选都是为了让令过校验：攻击要求目标已被侦察（E1+），且没判明成飞机的目标别拿去撞 ——
    /// 无防空火力步兵打航空兵既不合理，也只会白占一个指令位。
    /// </summary>
    private static Contact? PickTarget(GameState g, Unit u, UnitTypeDef t, List<Contact> contacts)
    {
        if (t.Firepower <= 0) return null;
        var reach = CommandValidator.AttackReach(t);

        return contacts
            .Where(c => c.Level >= g.Rules.ExposureE1)
            .Where(c => c.Cls != "air" || t.AntiAir > 0)
            .Where(c => u.Pos.DistanceTo(c.At) <= Math.Max(1, reach))
            .OrderByDescending(c => c.Level)
            .ThenBy(c => u.Pos.DistanceTo(c.At))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Cast<Contact?>()
            .FirstOrDefault();
    }

    /// <summary>
    /// 该往哪走：已知敌军方向优先（副官要打仗，不是搬家），其次未在本方手里的胜利目标格/要点。
    /// 间接火力单位打到射程边就停 —— 把炮兵推到刺刀底下是最典型的"AI 送人头"。
    /// 指挥所一律不前推：它一死全军失联，朝贴脸的敌人机动就是自杀，剩下交给 Decide 的兜底分支就地构筑。
    /// </summary>
    private static Hex? AdvanceGoal(GameState g, Unit u, UnitTypeDef t, List<Contact> contacts, List<Hex> objectives)
    {
        if (u.IsHQ || t.IsHQ) return null;

        var foe = contacts
            .Where(c => c.Cls != "air" || t.AntiAir > 0)
            .OrderBy(c => u.Pos.DistanceTo(c.At))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Cast<Contact?>()
            .FirstOrDefault();

        if (foe != null)
        {
            var d = u.Pos.DistanceTo(foe.Value.At);
            if (t.Range > 0 && d <= t.Range) return null; // 已经在火舌覆盖里，别再往前拱
            return foe.Value.At;
        }

        var held = MechanicsService.NodeHoldersByHex(g, u.Side);
        var open = objectives
            .Where(h => !held.ContainsKey(h.Key))
            .OrderBy(h => u.Pos.DistanceTo(h))
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Cast<Hex?>()
            .FirstOrDefault();
        return open;
    }

    /// <summary>看得见的敌军占据格才绕开：与移动校验同一个口径（未知的敌人留给遭遇战揭晓）。</summary>
    private static HashSet<string> BlockedHexes(List<Contact> contacts) =>
        new HashSet<string>(contacts.Select(c => c.At.Key));

    /// <summary>
    /// 最短路径逼近：只走数据包里存在的格，绕开不可通行地形与本方已知的敌占格，最多递进 7 步
    /// （机动预算由引擎按 MP 截断，路径给长了不会超支，只是浪费搜索）。
    /// </summary>
    private static Command? MoveToward(GameState g, Unit u, UnitTypeDef t, Hex goal, HashSet<string> blocked)
    {
        if (u.Pos.Equals(goal)) return null;
        if (t.Mobility <= 0) return null;

        // 目标是「看得见敌人」的那一格时只能停在它旁边：移动校验不允许踏入已知敌占格（要先交战），
        // 把目标格排进路径等于每回合白拒一道令，战线就此僵在原地。
        var stopShort = blocked.Contains(goal.Key);
        var path = BreadthFirstPath(g, u.Pos, goal, blocked, stopShort);
        if (path == null || path.Count < 2) return null;

        if (path.Count > 8) path = path.GetRange(0, 8); // 起点 + 最多 7 步，剩下的下回合再说
        return new Command { Op = CommandOp.Move, UnitId = u.Id, Path = path };
    }

    private static List<Hex>? BreadthFirstPath(GameState g, Hex start, Hex goal,
        HashSet<string> blocked, bool stopShort)
    {
        var queue = new Queue<Hex>();
        var came = new Dictionary<string, Hex?> { [start.Key] = null };
        queue.Enqueue(start);
        var guard = 0;

        while (queue.Count > 0 && guard++ < 4000)
        {
            var cur = queue.Dequeue();
            if (cur.Equals(goal) || (stopShort && cur.DistanceTo(goal) == 1)) return Unwind(came, cur);

            foreach (var h in cur.Neighbors())
            {
                if (came.ContainsKey(h.Key)) continue;
                if (g.GetCell(h) == null) continue;
                var td = g.TerrainOf(h);
                if (td.Impassable) continue;
                // 水系不设限：校验只拦 Impassable，渡河的代价由引擎按 MP 扣。
                // 在这里写死「无桥不下水」会把整支部队钉在河岸上，对岸的要点永远夺不到。
                if (blocked.Contains(h.Key)) continue;

                came[h.Key] = cur;
                queue.Enqueue(h);
            }
        }
        return null;
    }

    private static List<Hex> Unwind(Dictionary<string, Hex?> came, Hex goal)
    {
        var path = new List<Hex>();
        Hex? cur = goal;
        while (cur != null)
        {
            path.Add(cur.Value);
            cur = came[cur.Value.Key];
        }
        path.Reverse();
        return path;
    }

    // ==================== 空中 ====================

    /// <summary>
    /// 航空兵每回合重下任务令（不下令就等于原地落地）。
    /// 战斗机压到敌人出现的空域抢 CAP，强击机扫已知地面接触，侦察机往最近接触方向前推。
    /// </summary>
    private static Command AirOrder(GameState g, Unit u, UnitTypeDef t, List<Contact> contacts)
    {
        var mission = t.AirRole switch { "fighter" => "patrol", "strike" => "strike", "recon" => "recon", _ => "none" };
        if (mission == "none") return Halt(u);

        var zone = AirZone(g, u, t, contacts, mission);
        return new Command { Op = CommandOp.AirMission, UnitId = u.Id, Mission = mission, Zone = zone };
    }

    /// <summary>
    /// 任务区落点：够得着就压到目标空域，够不着就朝它推到作战半径边缘（蹲在机场既夺不到制空也看不见东西）。
    /// 落点必须是地图上存在的格 —— 校验器按格集判 bad_zone，越界的空域令被拒后这架飞机整回合都不出勤。
    /// </summary>
    private static Hex AirZone(GameState g, Unit u, UnitTypeDef t, List<Contact> contacts, string mission)
    {
        var foe = contacts
            .Where(c => mission != "strike" || c.Cls != "air")
            .OrderBy(c => u.Pos.DistanceTo(c.At))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Cast<Contact?>()
            .FirstOrDefault();

        if (foe == null) return u.Pos;

        var at = foe.Value.At;
        if (u.Pos.DistanceTo(at) <= t.AirRange && g.Cells.ContainsKey(at.Key)) return at;

        var push = StepToward(u.Pos, at, t.AirRange);
        return g.Cells.ContainsKey(push.Key) ? push : u.Pos;
    }

    /// <summary>沿 from→to 方向走 steps 格的近似落点（六角格上按距离递减步进，够副官用就行）。</summary>
    private static Hex StepToward(Hex from, Hex to, int steps)
    {
        var cur = from;
        for (var i = 0; i < steps && !cur.Equals(to); i++)
        {
            var next = cur.Neighbors()
                .Where(h => h.DistanceTo(to) < cur.DistanceTo(to))
                .OrderBy(h => h.DistanceTo(to))
                .ThenBy(h => h.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next.Equals(cur)) break;
            cur = next;
        }
        return cur;
    }

    // ==================== 指令构造 ====================

    private static Command Attack(Unit u, string target) =>
        new() { Op = CommandOp.Attack, UnitId = u.Id, Target = target };

    private static Command Fire(Unit u, Hex zone) =>
        new() { Op = CommandOp.Fire, UnitId = u.Id, Zone = zone };

    private static Command Dig(Unit u) => new() { Op = CommandOp.Dig, UnitId = u.Id };

    private static Command Rest(Unit u) => new() { Op = CommandOp.Rest, UnitId = u.Id };

    private static Command Halt(Unit u) => new() { Op = CommandOp.Halt, UnitId = u.Id };
}
