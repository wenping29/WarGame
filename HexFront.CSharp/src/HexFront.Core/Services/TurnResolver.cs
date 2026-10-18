using System;
using System.Collections.Generic;
using System.Linq;
using HexFront.Core.Geometry;
using HexFront.Core.Models;

namespace HexFront.Core.Services;

/// <summary>
/// Turn resolution with 7-phase deterministic adjudication.
/// Ported from Dart turn.dart resolveTurn.
/// </summary>
public static class TurnResolver
{
    /// <summary>Turn report containing all events and validation results.</summary>
    public class TurnReport
    {
        public List<CommandError> Rejected { get; init; } = new();
        public List<WarEvent> Events { get; init; } = new();
        public List<BattleRecord> Battles { get; init; } = new();
        public string FingerprintBefore { get; init; } = "";
        public string FingerprintAfter { get; init; } = "";
    }

    /// <summary>Battle record for combat logging：战报要能逐因子解释贡献（doc M6 验收）。</summary>
    public class BattleRecord
    {
        public int Turn { get; init; }
        public string Kind { get; init; } = ""; // air/strike/combat/fire/encounter
        public string Attacker { get; init; } = "";
        public string Defender { get; init; } = "";
        public int Losses { get; init; }
        public int AtkLoss { get; init; }
        public int DefLoss { get; init; }
        public double AtkCf { get; init; }
        public double DefCf { get; init; }
        public bool Routed { get; init; }
        public CombatFactors FactorsAtk { get; init; } = new();
        public CombatFactors FactorsDef { get; init; } = new();
    }

    /// <summary>Resolve a complete turn with orders from all sides.</summary>
    public static TurnReport ResolveTurn(GameState g, Dictionary<string, List<Command>> ordersBySide)
    {
        var before = g.Fingerprint();
        var events = new List<WarEvent>();
        var battles = new List<BattleRecord>();
        var rejected = new List<CommandError>();

        void Emit(string code, string text, List<string> sides)
        {
            var e = new WarEvent { Turn = g.Turn, Code = code, Text = text, Sides = sides };
            g.Events.Add(e);
            events.Add(e);
        }

        // Phase 0: Validate orders (server authority)
        var accepted = ValidateAllOrders(g, ordersBySide, rejected, Emit);

        // flags 回读自存档时是 JsonElement，只能经 FlagBool 取；这里取错会让 K_supply 档位静默失效
        var strict = FogService.FlagBool(g, "strict_supply", false);

        // Phase 1: Air phase (missions → air combat → air control → ground strike)
        AirPhase(g, accepted, strict, Emit, battles);

        // Phase 2: Observation update
        FogService.UpdateObservation(g);

        // Phase 3: Supply resolution
        ResolveSupply(g, strict, Emit);

        // Phase 4: Movement (including fortification)
        var moved = MovementPhase(g, accepted, Emit);

        // Phase 4b: Encounters (moving into unknown enemy hex)
        Encounters(g, moved, strict, Emit, battles);

        // Phase 5: Combat
        CombatPhase(g, accepted, strict, Emit, battles);

        // Phase 6: Recovery & attrition
        RecoveryPhase(g, Emit);

        // Phase 7: State advancement
        g.HourOfDay = (g.HourOfDay + g.Rules.TurnHours) % 24;
        g.Weather = MechanicsService.EvolveWeather(g);
        g.Turn += 1;

        CheckVictory(g, Emit);

        var day = DayPhase.FromHours(g.HourOfDay);
        Emit("turn_end", $"回合 {g.Turn - 1} 结束（{day.Label} / {g.WeatherOf().Label}）",
            new List<string> { "red", "blue", "spectator" });

        return new TurnReport
        {
            Rejected = rejected,
            Events = events,
            Battles = battles,
            FingerprintBefore = before,
            FingerprintAfter = g.Fingerprint(),
        };
    }

    // ==================== Phase 0: Validation ====================

    private static Dictionary<string, List<Command>> ValidateAllOrders(
        GameState g, Dictionary<string, List<Command>> ordersBySide,
        List<CommandError> rejected, Action<string, string, List<string>> emit)
    {
        var accepted = new Dictionary<string, List<Command>>();

        foreach (var side in new[] { "red", "blue" })
        {
            var orders = ordersBySide.GetValueOrDefault(side, new List<Command>());
            var errors = CommandValidator.ValidateOrders(g, side, orders);
            rejected.AddRange(errors);

            var badIds = errors.Select(e => e.UnitId).ToHashSet();
            var goodOrders = orders.Where(c => !badIds.Contains(c.UnitId)).ToList();
            accepted[side] = goodOrders;

            foreach (var e in errors)
            {
                emit("order_rejected", $"指令被拒：{e.UnitId} {e.Message}", new List<string> { side });
            }
        }

        return accepted;
    }

    // ==================== Phase 1: Air ====================

    /// <summary>
    /// ① 空中阶段：任务指派 → 空战 → 敌控空域风险 → 对地空袭 → 制空权重算（doc §8）。
    /// 空战与对地打击共用 CF 链：另开一条"比值带"损失公式会让战报无法逐因子解释（doc M6）。
    /// </summary>
    private static void AirPhase(GameState g, Dictionary<string, List<Command>> accepted, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        var cf = g.Rules.Combat;

        foreach (var side in new[] { "red", "blue" })
        {
            foreach (var c in accepted.GetValueOrDefault(side, new List<Command>()))
            {
                if (c.Op != CommandOp.AirMission) continue;
                var u = g.FindUnit(c.UnitId);
                if (u == null || !u.Alive) continue;

                // 收回任务（none）必须连带清空任务区：制空权重算按 MissionZone 记账，
                // 留着上一回合的任务区等于给一架已落地的飞机继续占空域。
                var mission = string.IsNullOrWhiteSpace(c.Mission) ? "none" : c.Mission;
                u.Mission = mission;
                u.MissionZone = mission == "none" ? null : c.Zone;
                u.Airborne = mission != "none";
                emit("air_mission", $"{u.Id} 起飞执行 {mission} 任务", new List<string> { side });
            }
        }

        // 空战：同一空域内双方航空兵接战。空域按 key、单位按 id 取稳定序 ——
        // Dictionary 的枚举顺序会随插入历史变化，谁先开火不能由这种巧合决定。
        var zones = new Dictionary<Hex, List<Unit>>();
        foreach (var u in g.Units)
        {
            if (!u.Alive || !u.Airborne) continue;
            var t = g.TypeOf(u);
            if (t.AirRole != "fighter") continue;

            var zone = u.MissionZone ?? u.Pos;
            if (!zones.ContainsKey(zone))
                zones[zone] = new List<Unit>();
            zones[zone].Add(u);
        }

        foreach (var entry in zones.OrderBy(kv => kv.Key.Key, StringComparer.Ordinal))
        {
            var zone = entry.Key;
            var reds = entry.Value.Where(f => f.Side == "red").OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
            var blues = entry.Value.Where(f => f.Side == "blue").OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
            if (reds.Count == 0 || blues.Count == 0) continue;

            foreach (var a in reds)
            {
                foreach (var b in blues)
                {
                    if (!a.Alive || !b.Alive) continue;

                    var res = CombatChain.ResolveClash(g, a, b, strict);
                    ApplyAirLoss(g, a, res.AtkLoss, emit);
                    ApplyAirLoss(g, b, res.DefLoss, emit);

                    battles.Add(new BattleRecord
                    {
                        Turn = g.Turn,
                        Kind = "air",
                        Attacker = a.Id,
                        Defender = b.Id,
                        Losses = res.AtkLoss + res.DefLoss,
                        AtkLoss = res.AtkLoss,
                        DefLoss = res.DefLoss,
                        AtkCf = res.AtkCf,
                        DefCf = res.DefCf,
                        Routed = res.DefRouted,
                        FactorsAtk = res.FactorsAtk,
                        FactorsDef = res.FactorsDef,
                    });

                    emit("air_clash", $"空战 @{zone.Q},{zone.R}：{a.Id} ↔ {b.Id}（{CombatChain.CfSummary(res.AtkCf, res.DefCf)}）",
                        new List<string> { "red", "blue", "spectator" });
                }
            }
        }

        // 非战斗机在敌方控制空域被击落的风险
        foreach (var u in g.Units.OrderBy(x => x.Id, StringComparer.Ordinal).ToList())
        {
            if (!u.Alive || !u.Airborne) continue;
            var t = g.TypeOf(u);
            if (t.AirRole == "fighter") continue;

            var z = u.MissionZone ?? u.Pos;
            var ctl = g.AirControl.GetValueOrDefault(z.Key, 0);
            var hostile = (ctl > 0.2 && u.Side == "blue") || (ctl < -0.2 && u.Side == "red");

            if (hostile && g.Rng.Chance(0.12))
            {
                u.Personnel = 0;
                u.Status = UnitStatus.Destroyed;
                emit("air_lost", $"{u.Id} 在敌控空域被击落", new List<string> { u.Side, "spectator" });
            }
        }

        // 对地打击：CF 链给的是弹着效果，落地人员损失再乘投放效能
        foreach (var u in g.Units.OrderBy(x => x.Id, StringComparer.Ordinal).ToList())
        {
            if (!u.Alive || !u.Airborne) continue;
            var t = g.TypeOf(u);
            if (t.AirRole != "strike") continue;
            if (u.Ammo < 0.15) continue;

            u.Ammo = Clamp(u.Ammo - 0.25, 0, 1);

            var z = u.MissionZone ?? u.Pos;
            var targets = g.UnitsAt(z)
                .Where(tgt => tgt.Side != u.Side && !g.TypeOf(tgt).IsAir)
                .OrderBy(tgt => tgt.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var tgt in targets)
            {
                var res = CombatChain.ResolveClash(g, u, tgt, strict);
                var loss = Math.Min(tgt.Personnel, (int)Math.Round(res.DefLoss * cf.StrikeLossScale));
                tgt.Personnel -= loss;
                tgt.Casualties += loss;
                tgt.Morale = Clamp(tgt.Morale - 8, 0, 100);

                battles.Add(new BattleRecord
                {
                    Turn = g.Turn,
                    Kind = "strike",
                    Attacker = u.Id,
                    Defender = tgt.Id,
                    Losses = loss,
                    DefLoss = loss,
                    AtkCf = res.AtkCf,
                    DefCf = res.DefCf,
                    FactorsAtk = res.FactorsAtk,
                    FactorsDef = res.FactorsDef,
                });

                emit("air_strike", $"{u.Id} 空袭 {tgt.Id} 于 {z.Q},{z.R}，损失 {loss} 人",
                    new List<string> { tgt.Side, u.Side, "spectator" });

                CheckDestroy(g, tgt, emit);
            }
        }

        // 制空权重算
        g.AirControl.Clear();
        var newControl = MechanicsService.ComputeAirControl(g);
        foreach (var (k, v) in newControl)
            g.AirControl[k] = v;
    }

    private static void ApplyAirLoss(GameState g, Unit u, int loss, Action<string, string, List<string>> emit)
    {
        if (loss <= 0) return;
        u.Personnel -= loss;
        u.Casualties += loss;

        if (u.Personnel <= 0)
        {
            u.Status = UnitStatus.Destroyed;
            emit("air_lost", $"{u.Id} 战损", new List<string> { u.Side, "spectator" });
        }
        else
        {
            u.Ammo = Clamp(u.Ammo - 0.2, 0, 1);
        }
    }

    // ==================== Phase 3: Supply ====================

    private static void ResolveSupply(GameState g, bool strict, Action<string, string, List<string>> emit)
    {
        var supplyBySide = MechanicsService.ComputeSupply(g);

        foreach (var u in g.Units)
        {
            if (!u.Alive) continue;

            var m = supplyBySide.GetValueOrDefault(u.Side);
            // 不可达格不入表（无补给源的阵营不能整体挨饿），按宽松默认给一个基础补给率
            u.SupplyRate = m != null && m.TryGetValue(u.Pos.Key, out var s) ? s : g.Rules.DefaultSupply;

            var t = g.TypeOf(u);
            var drain = t.Consumption * 0.06 * (strict ? 1.0 : 0.5) * (1.0 - u.SupplyRate) + g.Rules.FoodPerTurn * 0.4;

            u.Food = Clamp(u.Food + (u.SupplyRate - 1.0) * 0.6 - drain * 0.3, 0, 1);
            u.Fuel = Clamp(u.Fuel + (u.SupplyRate - 1.0) * 0.5 - (t.Cls == "motor" || t.Cls == "mech" ? 0.03 : 0.005), 0, 1);

            if (u.Food < 0.2)
            {
                u.Morale = Clamp(u.Morale - 4, 0, 100);
                if (g.Turn % 3 == 0)
                    emit("hunger", $"{u.Id} 给养断绝，士气下滑", new List<string> { u.Side });
            }

            if (u.Fuel <= 0.01 && (t.Cls == "motor" || t.Cls == "mech"))
            {
                u.Status = UnitStatus.Ready;
                if (u.SupplyRate < g.Rules.SupplyCrit)
                    emit("fuel_out", $"{u.Id} 油料耗尽，车辆瘫滞", new List<string> { u.Side });
            }

            if (u.SupplyRate < g.Rules.SupplyCrit && strict)
            {
                emit("supply_cut", $"{u.Id} 补给线被切断（R={u.SupplyRate:F2}）", new List<string> { u.Side });
            }
        }
    }

    // ==================== Phase 4: Movement ====================

    private static List<Unit> MovementPhase(GameState g, Dictionary<string, List<Command>> accepted,
        Action<string, string, List<string>> emit)
    {
        var moved = new List<Unit>();
        var wx = g.WeatherOf();
        var day = DayPhase.FromHours(g.HourOfDay);
        var cf = g.Rules.Combat;

        var plan = accepted.Values
            .SelectMany(orders => orders.Where(c => c.Op == CommandOp.Move))
            .OrderBy(c => c.UnitId, StringComparer.Ordinal)
            .ToList();

        foreach (var c in plan)
        {
            var u = g.FindUnit(c.UnitId);
            if (u == null || !u.Alive) continue;

            var t = g.TypeOf(u);
            var cls = t.Cls == "air" ? "mech" : t.Cls;
            var hq = CombatChain.CommanderFactor(g, u);

            var budget = t.Mobility * g.Rules.MpBase * t.MobilityPenalty() * wx.Mobility * day.Recon;
            if (u.Fuel <= 0.01 && (cls == "motor" || cls == "mech")) budget *= 0.25;
            if (u.Fatigue > 70) budget *= 0.7;
            if (u.FortLevel > 0) budget *= 0.8; // Abandon fortifications
            // 指挥链系数只在 [0.6,1.15] 区间内缩放机动预算（取整的是预算，不是把预算夹到上限）
            budget = Math.Round(budget * Clamp(hq, cf.CommanderBudgetMin, cf.CommanderBudgetMax));

            var spent = 0.0;
            var cur = u.Pos;
            var moved_any = false;

            foreach (var h in c.Path.Skip(1))
            {
                var td = g.TerrainOf(h);
                var cell = g.GetCell(h);
                if (cell == null || td.Impassable) break;

                var step = StepCost(td, cell, cls);
                if (spent + step > budget) break;

                spent += step;
                cur = h;
                moved_any = true;

                // 踩进有敌方地面部队的格子就停脚 —— 校验只看下令方看得见谁（doc §2「进入未知格可能遭遇」），
                // 所以"从敌人队列上径直走过去"必须由结算拦下：不拦，遭遇战就只剩终点恰好压到人家里这一种触发方式。
                // 谓词与 Encounters 同一条，免得出现"停了却没遭遇"的空转。
                if (g.UnitsAt(h).Any(e => e.Side != u.Side && !g.TypeOf(e).IsAir)) break;
            }

            if (!moved_any || cur == u.Pos) continue;

            // Execute movement：离开工事格即弃工（这就是 doc §8 的"撤除工事"，线上没有独立动作）
            u.Fatigue = Clamp(u.Fatigue + g.Rules.FatigueMarch * (spent / Math.Max(1, budget)) * 6, 0, 100);
            u.Morale = Clamp(u.Morale - (u.Fatigue > 80 ? 3 : 0), 0, 100);
            u.FortLevel = 0;
            u.Pos = cur;
            u.Status = UnitStatus.Moved;
            moved.Add(u);

            emit("move", $"{u.Id} 机动至 {cur.Q},{cur.R}（耗 MP {Math.Round(spent)}）", new List<string> { u.Side });
        }

        // 构筑工事 / 停止：本回合没有机动的单位才动得了土（校验已保证一单位一令）
        foreach (var side in new[] { "red", "blue" })
        {
            foreach (var c in accepted.GetValueOrDefault(side, new List<Command>()))
            {
                if (c.Op is not (CommandOp.Dig or CommandOp.Halt or CommandOp.Rest)) continue;
                var u = g.FindUnit(c.UnitId);
                if (u == null || !u.Alive) continue;

                if (c.Op == CommandOp.Dig)
                {
                    var cap = g.TerrainOf(u.Pos).MaxFort;
                    if (u.FortLevel >= cap) continue;
                    // 工兵是专业构筑兵力，进度翻倍；等级上限由地形决定（doc §6.3）
                    var gain = g.TypeOf(u).Id == "engineer" ? 2 : 1;
                    u.FortLevel = Math.Min(cap, u.FortLevel + gain);
                    u.Status = UnitStatus.Digging;
                    emit("dig", $"{u.Id} 构筑工事至 {u.FortLevel} 级", new List<string> { side });
                }
                else
                {
                    u.Status = c.Op == CommandOp.Rest ? UnitStatus.Resting : UnitStatus.Ready;
                }
            }
        }

        return moved;
    }

    /// <summary>
    /// 单格机动代价（doc §6.4 地物玩法表）。乘序是有意义的：路→铁路→隧道→渡河，
    /// 先打折后加罚，换序会让路铁叠加格从 0.52 倍变成别的数，所以逐项锁死并对外可测。
    /// </summary>
    public static double StepCost(TerrainDef td, Cell cell, string cls)
    {
        var heavy = cls == "motor" || cls == "mech";
        var step = td.Cost.GetValueOrDefault(cls, 2.0);

        if (cell.Road) step *= 0.65;
        if (cell.Railway && heavy) step *= 0.8;
        if (cell.Tunnel) step *= heavy ? 1.5 : 1.2;
        if (cell.River && !cell.Bridge) step += 2.0;

        return step;
    }

    // ==================== Phase 4b: Encounters ====================

    /// <summary>
    /// 机动落点撞上未知敌情 → 遭遇战，守方占优（doc §8 ④b）。
    /// 一支攻方每回合只与一个接触对象交战：一格多守军时若连续吃多场 CF 裁决，攻方损失没有上界。
    /// 守方选取按 id 稳定序 —— 同一格里的守军顺序不该由单位表的插入历史决定。
    /// </summary>
    private static void Encounters(GameState g, List<Unit> moved, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        foreach (var u in moved)
        {
            if (!u.Alive) continue;
            var foe = g.UnitsAt(u.Pos)
                .Where(e => e.Side != u.Side && !g.TypeOf(e).IsAir)
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (foe == null) continue;

            ApplyClash(g, u, foe, encounter: true, strict, emit, battles);
        }
    }

    // ==================== Phase 5: Combat ====================

    /// <summary>
    /// ⑤ 战斗：突击（近战/直射）与间接火力覆盖共用一份 CF 裁决。
    /// 跨方行动顺序显式化为「目标格 key → 攻方 id」，不再依赖单位命名的巧合（doc §8「顺序不能由 id 命名决定」）。
    /// </summary>
    private static void CombatPhase(GameState g, Dictionary<string, List<Command>> accepted, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        var orders = accepted.Values
            .SelectMany(orders => orders.Where(c => c.Op == CommandOp.Attack || c.Op == CommandOp.Fire))
            .OrderBy(c => TargetKey(g, c), StringComparer.Ordinal)
            .ThenBy(c => c.UnitId, StringComparer.Ordinal)
            .ToList();

        foreach (var c in orders)
        {
            if (c.Op == CommandOp.Fire) FireZone(g, c, strict, emit, battles);
            else Assault(g, c, strict, emit, battles);
        }
    }

    /// <summary>交战目标格：攻击令按守方所在格，火力令按覆盖格；找不到就退回攻方本格。</summary>
    private static string TargetKey(GameState g, Command c)
    {
        if (c.Op == CommandOp.Fire && c.Zone != null) return c.Zone.Value.Key;
        var def = c.Target == null ? null : g.FindUnit(c.Target);
        if (def != null) return def.Pos.Key;
        return g.FindUnit(c.UnitId)?.Pos.Key ?? "";
    }

    private static void Assault(GameState g, Command c, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        var atk = g.FindUnit(c.UnitId);
        var def = g.FindUnit(c.Target ?? "");
        if (atk == null || def == null || !atk.Alive || !def.Alive) return;
        if (atk.Status == UnitStatus.Routed) return;

        // 射程按结算时刻的实测距离复核：机动阶段会改变双方格位，校验时够、现在不够的就打不出去
        var reach = CommandValidator.AttackReach(g.TypeOf(atk));
        if (atk.Pos.DistanceTo(def.Pos) > reach) return;

        ApplyClash(g, atk, def, encounter: false, strict, emit, battles);
    }

    /// <summary>
    /// 间接火力：允许对未观测格盲打（doc §3.4），但精度按上一阶段的暴露档位降级 ——
    /// 判明（E2）以上才按满效率结算，否则就是拿坐标蒙。
    /// </summary>
    private static void FireZone(GameState g, Command c, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        var cf = g.Rules.Combat;
        var att = g.FindUnit(c.UnitId);
        if (att == null || !att.Alive || att.Status == UnitStatus.Routed || c.Zone == null) return;

        var zone = c.Zone.Value;
        var hits = g.UnitsAt(zone)
            .Where(t => t.Side != att.Side && !g.TypeOf(t).IsAir)
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        att.Status = UnitStatus.Fired;

        if (hits.Count == 0)
        {
            att.Ammo = Clamp(att.Ammo - cf.AmmoPerAttack, 0, 1);
            emit("fire_miss", $"{att.Id} 火力覆盖 {zone.Q},{zone.R}：无目标（盲打）", new List<string> { att.Side });
            return;
        }

        var known = g.Tracks.GetValueOrDefault(att.Side)?.GetValueOrDefault(hits[0].Id)?.Level ?? 0;
        var eff = known >= 2 ? cf.FireObservedEfficiency : cf.FireBlindEfficiency;

        foreach (var t in hits)
        {
            var res = CombatChain.ResolveClash(g, att, t, strict);
            var loss = Math.Min(t.Personnel, (int)Math.Round(res.DefLoss * eff));
            t.Personnel -= loss;
            t.Casualties += loss;
            t.Morale = Clamp(t.Morale - 6, 0, 100);

            battles.Add(new BattleRecord
            {
                Turn = g.Turn,
                Kind = "fire",
                Attacker = att.Id,
                Defender = t.Id,
                Losses = loss,
                DefLoss = loss,
                AtkCf = res.AtkCf,
                DefCf = res.DefCf,
                FactorsAtk = res.FactorsAtk,
                FactorsDef = res.FactorsDef,
            });

            emit("arty", $"{att.Id} 炮击 {t.Id} @({zone.Q},{zone.R})，损失 {loss} 人（校射 {(eff * 100).ToString("0")}%）",
                new List<string> { att.Side, t.Side, "spectator" });

            CheckDestroy(g, t, emit);
        }

        att.Ammo = Clamp(att.Ammo - cf.AmmoPerAttack * hits.Count, 0, 1);
    }

    /// <summary>
    /// 一次交战的落状态：CF 链裁决 → 人员/弹药/疲劳/士气 → 溃退与战报 → 要点易手。
    /// 遭遇战守方占优用临时工事级表达（+1 后立刻还原），不为它另开一条损失公式。
    /// </summary>
    private static void ApplyClash(GameState g, Unit atk, Unit def, bool encounter, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        var cf = g.Rules.Combat;
        if (encounter) def.FortLevel += 1;

        var res = CombatChain.ResolveClash(g, atk, def, strict);

        var aLoss = Math.Min(atk.Personnel, res.AtkLoss);
        var dLoss = Math.Min(def.Personnel, res.DefLoss);
        atk.Personnel -= aLoss;
        def.Personnel -= dLoss;
        atk.Casualties += aLoss;
        def.Casualties += dLoss;

        atk.Ammo = Clamp(atk.Ammo - cf.AmmoPerAttack, 0, 1);
        atk.Fatigue = Clamp(atk.Fatigue + g.Rules.FatigueCombat * 4, 0, 100);
        def.Fatigue = Clamp(def.Fatigue + g.Rules.FatigueCombat * 3, 0, 100);
        def.Morale = res.DefMoraleAfter;
        atk.Morale = res.AtkMoraleAfter;
        atk.Status = UnitStatus.Fired;
        if (def.Alive) def.Status = UnitStatus.Fired;

        if (encounter) def.FortLevel = Math.Max(0, def.FortLevel - 1);

        var sides = new List<string> { atk.Side, def.Side, "spectator" };
        var text = encounter
            ? $"遭遇战 @({atk.Pos.Q},{atk.Pos.R})：{atk.Id} ↔ {def.Id}，守方 -{dLoss} / 攻方 -{aLoss}（{CombatChain.CfSummary(res.AtkCf, res.DefCf)}）"
            : $"{atk.Id} 攻击 {def.Id}：守方 -{dLoss} / 攻方 -{aLoss}（{CombatChain.CfSummary(res.AtkCf, res.DefCf)}）";
        emit(encounter ? "encounter" : "combat", text, sides);

        battles.Add(new BattleRecord
        {
            Turn = g.Turn,
            Kind = encounter ? "encounter" : "combat",
            Attacker = atk.Id,
            Defender = def.Id,
            Losses = aLoss + dLoss,
            AtkLoss = aLoss,
            DefLoss = dLoss,
            AtkCf = res.AtkCf,
            DefCf = res.DefCf,
            Routed = res.DefRouted,
            FactorsAtk = res.FactorsAtk,
            FactorsDef = res.FactorsDef,
        });

        CheckDestroy(g, def, emit);
        CheckDestroy(g, atk, emit);

        // 要点易手：守方被歼 → 攻方压上占据该格；守方溃退 → 朝本方纵深后撤一格
        if (!atk.Alive) return;
        if (!def.Alive)
        {
            if (atk.Pos != def.Pos) atk.Pos = def.Pos;
        }
        else if (res.DefRouted)
        {
            def.Status = UnitStatus.Routed;
            var back = RetreatHex(g, def);
            if (back != null)
            {
                def.Pos = back.Value;
                emit("rout", $"{def.Id} 溃退至 {back.Value.Q},{back.Value.R}",
                    new List<string> { def.Side, "spectator" });
            }
            else
            {
                emit("rout", $"{def.Id} 溃退（无退路）", new List<string> { def.Side, "spectator" });
            }
        }
    }

    // ==================== Phase 6: Recovery ====================

    /// <summary>
    /// ⑥ 消耗与恢复（doc §8）：士气重建按补给率与昼夜缩放、疲劳看是否休整、弹药低速回补、溃退逐级归队。
    /// 口粮/油料已在 ③ 补给段按消耗扣，这里不再重复扣一遍。
    /// </summary>
    private static void RecoveryPhase(GameState g, Action<string, string, List<string>> emit)
    {
        var r = g.Rules;
        var day = DayPhase.FromHours(g.HourOfDay);
        var wx = g.WeatherOf();

        foreach (var u in g.Units)
        {
            if (!u.Alive) continue;

            // 休整＝本回合没挪窝也没开火；机动的单位当回合不回血
            var rest = u.Status is UnitStatus.Ready or UnitStatus.Resting or UnitStatus.Digging or UnitStatus.Routed;
            if (rest)
            {
                u.Morale = Clamp(u.Morale + r.MoraleRecover * u.SupplyRate * day.Recon * (1.0 + u.Training / 200), 0, 100);
                u.Fatigue = Clamp(u.Fatigue - r.FatigueRecover * u.SupplyRate, 0, 100);
            }
            else
            {
                u.Fatigue = Clamp(u.Fatigue + r.FatigueIdle * wx.Attrition, 0, 100);
            }

            // 弹药靠后勤回补，交过火的单位当回合补不回来
            u.Ammo = Clamp(u.Ammo + r.AmmoRecover * (u.Status == UnitStatus.Fired ? 0.0 : u.SupplyRate), 0, 1);

            if (u.Status == UnitStatus.Routed)
            {
                if (u.Morale >= r.Combat.RoutRecover)
                {
                    u.Status = UnitStatus.Ready;
                    emit("recover", $"{u.Id} 恢复战斗力", new List<string> { u.Side });
                }
                // 士气触底的残兵也要有微量重建，否则溃退分支不可逆（doc §9 内核必补项）
                else if (u.Morale < r.Combat.RoutMorale)
                {
                    u.Morale = Clamp(u.Morale + r.RoutMoraleNudge, 0, 100);
                }
            }

            // 战场标记是回合内的：机动/交火/构筑/休整在恢复段末归位，跨回合只保留溃退（它有自己的恢复门槛）。
            // 不归位就等于"打过一次火"永久锁在交火状态、再也吃不到士气重建 —— 与内核那条不可达的溃退恢复分支同一种病。
            if (u.Status is UnitStatus.Moved or UnitStatus.Fired or UnitStatus.Digging or UnitStatus.Resting)
                u.Status = UnitStatus.Ready;
        }
    }

    // ==================== Victory Check ====================

    /// <summary>
    /// ⑦ 胜负判定：逐条判定本方是否达成（doc §6.4/§8 的 kind：annihilate / capture / nodes / survive）。
    /// 要点归属复用 MechanicsService 的「争夺中不算占」口径 —— 视图说有 N 处要点而胜利判定另算一套，
    /// 导演部就会当场自相矛盾。
    /// </summary>
    private static void CheckVictory(GameState g, Action<string, string, List<string>> emit)
    {
        if (g.Over) return;

        foreach (var vc in g.VictoryConds)
        {
            var side = string.IsNullOrEmpty(vc.Side) ? "red" : vc.Side;
            var foe = side == "red" ? "blue" : "red";

            switch (vc.Kind)
            {
                case "annihilate":
                    {
                        var foeNow = g.LiveOf(foe).Sum(u => u.Personnel);
                        var foeStart = Math.Max(1, g.Units.Where(u => u.Side == foe)
                            .Sum(u => u.Personnel + u.Casualties));
                        if (foeNow <= foeStart * (1.0 - vc.Amount / 100.0))
                        {
                            Win(g, side, $"{SideLabel(side)}达成歼灭目标（{foe} 方剩余 {foeNow}/{foeStart}）", emit);
                            return;
                        }
                        break;
                    }
                case "capture":
                    {
                        if (string.IsNullOrEmpty(vc.Target)) break;
                        var owner = MechanicsService.HoldersByHex(g).GetValueOrDefault(vc.Target);
                        if (owner == side)
                        {
                            Win(g, side, $"独占要点 {vc.Target}", emit);
                            return;
                        }
                        break;
                    }
                case "nodes":
                    {
                        if (vc.Amount <= 0) break;
                        var held = MechanicsService.NodeHoldersByHex(g, side).Count;
                        if (held >= vc.Amount)
                        {
                            Win(g, side, $"掌握 {held} 处战场要点（目标 {vc.Amount}）", emit);
                            return;
                        }
                        break;
                    }
                case "survive":
                    {
                        if (g.Turn > vc.Turns || g.Turn > g.MaxTurns)
                        {
                            Win(g, side, "坚持到推演结束", emit);
                            return;
                        }
                        break;
                    }
            }
        }

        // 回合耗尽：按剩余兵力判定，避免"两边都没达成条件"时永远不落终局标记
        if (g.Turn > g.MaxTurns)
        {
            var rp = g.LiveOf("red").Sum(u => u.Personnel);
            var bp = g.LiveOf("blue").Sum(u => u.Personnel);
            Win(g, rp >= bp ? "red" : "blue", $"回合耗尽，按剩余兵力判定（红 {rp} / 蓝 {bp}）", emit);
        }
    }

    private static void Win(GameState g, string side, string why, Action<string, string, List<string>> emit)
    {
        g.Over = true;
        g.Winner = side;
        g.EndedTurn = g.Turn;
        emit("game_over", $"推演结束：{SideLabel(side)}胜 —— {why}",
            new List<string> { "red", "blue", "spectator" });
    }

    private static string SideLabel(string side) => side == "red" ? "红方" : side == "blue" ? "蓝方" : side;

    // ==================== Helpers ====================

    /// <summary>
    /// 溃退后撤一格：朝本方纵深（补给源/指挥所的平均位置）最近的可站格。
    /// 候选排除不可通行与水系格（没有桥的河不是退路），也排除敌方地面单位占据的格；
    /// 同距离按格 key 取序首，保证后撤方向可重放。
    /// </summary>
    private static Hex? RetreatHex(GameState g, Unit u)
    {
        var home = HomeOf(g, u.Side);
        var cands = u.Pos.Neighbors()
            .Where(h => g.Cells.ContainsKey(h.Key))
            .Where(h =>
            {
                var td = g.TerrainOf(h);
                return !td.Impassable && !td.Water;
            })
            .Where(h => g.UnitsAt(h).All(o => o.Side == u.Side))
            .OrderBy(h => h.DistanceTo(home))
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .ToList();
        return cands.Count == 0 ? null : cands[0];
    }

    private static Hex HomeOf(GameState g, string side)
    {
        var anchors = g.Units
            .Where(u => u.Alive && u.Side == side && (u.IsHQ || g.TypeOf(u).SupplySource))
            .ToList();
        if (anchors.Count == 0) return new Hex(0, 0);
        return new Hex(
            (int)Math.Round(anchors.Average(u => (double)u.Pos.Q)),
            (int)Math.Round(anchors.Average(u => (double)u.Pos.R)));
    }

    /// <summary>
    /// 减员落状态：人员清零 ⇒ 成建制损失（战报要留下 destroyed），残余则按士气见底判溃退。
    /// 守卫不能写成 <c>Personnel &lt;= 0 &amp;&amp; Alive</c>：Alive 就是 Personnel &gt; 0，
    /// 这条分支永远进不去，全歼只会留下一支"兵力 0 但状态 ready"的幽灵部队。
    /// </summary>
    private static void CheckDestroy(GameState g, Unit u, Action<string, string, List<string>> emit)
    {
        if (u.Personnel <= 0)
        {
            if (u.Status == UnitStatus.Destroyed) return;
            u.Status = UnitStatus.Destroyed;
            emit("destroyed", $"{u.Id} 被歼灭", new List<string> { u.Side, "spectator" });
            return;
        }

        if (u.Morale < g.Rules.Combat.RoutMorale && u.Status != UnitStatus.Routed)
            u.Status = UnitStatus.Routed;
    }

    private static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0) return min;
        if (value.CompareTo(max) > 0) return max;
        return value;
    }
}
