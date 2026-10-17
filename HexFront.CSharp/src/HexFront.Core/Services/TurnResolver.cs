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
        public string Kind { get; init; } = ""; // air/strike/combat/encounter
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
        AirPhase(g, accepted, Emit, battles);

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
        RecoveryPhase(g, Emit, strict);

        // Phase 7: State advancement
        g.HourOfDay = (g.HourOfDay + g.Rules.TurnHours) % 24;
        g.Weather = MechanicsService.EvolveWeather(g);
        g.Turn += 1;

        CheckVictory(g, Emit);

        Emit("turn_end", $"回合 {g.Turn - 1} 结束", new List<string> { "red", "blue", "spectator" });

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

    private static void AirPhase(GameState g, Dictionary<string, List<Command>> accepted,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        // Assign air missions
        foreach (var side in new[] { "red", "blue" })
        {
            foreach (var c in accepted.GetValueOrDefault(side, new List<Command>()))
            {
                if (c.Op != CommandOp.AirMission) continue;
                var u = g.FindUnit(c.UnitId);
                if (u == null) continue;

                u.Mission = c.Mission ?? "none";
                u.MissionZone = c.Zone;
                u.Airborne = c.Mission != "none";
                emit("air_mission", $"{u.Id} 起飞执行 {c.Mission} 任务", new List<string> { side });
            }
        }

        // Air combat: fighters in same zone engage
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

        foreach (var (zone, fighters) in zones)
        {
            var reds = fighters.Where(f => f.Side == "red").ToList();
            var blues = fighters.Where(f => f.Side == "blue").ToList();
            if (reds.Count == 0 || blues.Count == 0) continue;

            foreach (var a in reds)
            {
                foreach (var b in blues)
                {
                    if (!a.Alive || !b.Alive) continue;
                    // Simplified air combat resolution
                    var aLoss = ResolveAirCombat(a, b);
                    var bLoss = ResolveAirCombat(b, a);

                    ApplyAirLoss(g, a, aLoss, emit);
                    ApplyAirLoss(g, b, bLoss, emit);

                    battles.Add(new BattleRecord
                    {
                        Turn = g.Turn,
                        Kind = "air",
                        Attacker = a.Id,
                        Defender = b.Id,
                        Losses = aLoss + bLoss,
                    });

                    emit("air_clash", $"空战 @{zone.Q},{zone.R}：{a.Id} ↔ {b.Id}",
                        new List<string> { "red", "blue", "spectator" });
                }
            }
        }

        // Non-fighters risk being shot down in hostile airspace
        foreach (var u in g.Units.ToList())
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

        // Ground strike by attack aircraft
        foreach (var u in g.Units.ToList())
        {
            if (!u.Alive || !u.Airborne) continue;
            var t = g.TypeOf(u);
            if (t.AirRole != "strike") continue;
            if (u.Ammo < 0.15) continue;

            u.Ammo = Clamp(u.Ammo - 0.25, 0, 1);

            var z = u.MissionZone ?? u.Pos;
            var targets = g.UnitsAt(z).Where(tgt => tgt.Side != u.Side && !g.TypeOf(tgt).IsAir).ToList();

            foreach (var tgt in targets)
            {
                var loss = Math.Min(tgt.Personnel, (int)(tgt.Personnel * 0.15));
                tgt.Personnel -= loss;
                tgt.Morale = Clamp(tgt.Morale - 8, 0, 100);
                tgt.Casualties += loss;

                battles.Add(new BattleRecord
                {
                    Turn = g.Turn,
                    Kind = "strike",
                    Attacker = u.Id,
                    Defender = tgt.Id,
                    Losses = loss,
                });

                emit("air_strike", $"{u.Id} 空袭 {tgt.Id}，损失 {loss} 人",
                    new List<string> { tgt.Side, u.Side, "spectator" });

                CheckDestroy(g, tgt, emit);
            }
        }

        // Recalculate air control
        g.AirControl.Clear();
        var newControl = MechanicsService.ComputeAirControl(g);
        foreach (var (k, v) in newControl)
            g.AirControl[k] = v;
    }

    private static int ResolveAirCombat(Unit attacker, Unit defender)
    {
        var atkPower = attacker.Morale / 100.0 * attacker.Personnel;
        var defPower = defender.Morale / 100.0 * defender.Personnel;
        var ratio = atkPower / Math.Max(1, defPower);

        if (ratio > 1.5) return (int)(defender.Personnel * 0.3);
        if (ratio > 1.0) return (int)(defender.Personnel * 0.2);
        return (int)(defender.Personnel * 0.1);
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
            u.SupplyRate = m != null && m.TryGetValue(u.Pos.Key, out var s) ? s : 0.7;

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
            .OrderBy(c => c.UnitId)
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
            }

            if (!moved_any || cur == u.Pos) continue;

            // Execute movement
            var oldPos = u.Pos;
            u.Fatigue = Clamp(u.Fatigue + g.Rules.FatigueMarch * (spent / Math.Max(1, budget)) * 6, 0, 100);
            u.Morale = Clamp(u.Morale - (u.Fatigue > 80 ? 3 : 0), 0, 100);
            u.FortLevel = 0;
            u.Pos = cur;
            u.Status = UnitStatus.Moved;
            u.HasMoved = true;
            moved.Add(u);

            emit("move", $"{u.Id} 机动至 {cur.Q},{cur.R}（耗 MP {Math.Round(spent)}）", new List<string> { u.Side });

            // Check if entering enemy-occupied hex
            var enemies = g.UnitsAt(cur).Where(e => e.Side != u.Side && e.Alive).ToList();
            if (enemies.Count > 0)
            {
                emit("contact", $"{u.Id} 接触敌军", new List<string> { u.Side, "spectator" });
            }
        }

        return moved;
    }

    private static double StepCost(TerrainDef td, Cell cell, string cls)
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

    private static void Encounters(GameState g, List<Unit> moved, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        foreach (var u in moved)
        {
            var enemies = g.UnitsAt(u.Pos).Where(e => e.Side != u.Side && e.Alive).ToList();
            if (enemies.Count == 0) continue;

            foreach (var e in enemies)
            {
                if (!u.Alive || !e.Alive) continue;
                ApplyClash(g, u, e, encounter: true, strict, emit, battles);
            }
        }
    }

    // ==================== Phase 5: Combat ====================

    private static void CombatPhase(GameState g, Dictionary<string, List<Command>> accepted, bool strict,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        var attacks = accepted.Values
            .SelectMany(orders => orders.Where(c => c.Op == CommandOp.Attack))
            .OrderBy(c => c.UnitId)
            .ToList();

        foreach (var c in attacks)
        {
            var atk = g.FindUnit(c.UnitId);
            var def = g.FindUnit(c.Target ?? "");
            if (atk == null || def == null || !atk.Alive || !def.Alive) continue;

            ApplyClash(g, atk, def, encounter: false, strict, emit, battles);
        }
    }

    /// <summary>
    /// 一次交战的落状态：CF 链裁决 → 人员/弹药/疲劳/士气 → 溃退与战报。
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
        atk.HasFired = true;

        if (encounter) def.FortLevel = Math.Max(0, def.FortLevel - 1);

        var sides = new List<string> { atk.Side, def.Side, "spectator" };
        var text = encounter
            ? $"遭遇战 @({atk.Pos.Q},{atk.Pos.R})：{atk.Id} ↔ {def.Id}，守方 -{dLoss} / 攻方 -{aLoss}（{CombatChain.CfSummary(res.AtkCf, res.DefCf)}）"
            : $"{atk.Id} 攻击 {def.Id}：守方 -{dLoss} / 攻方 -{aLoss}（{CombatChain.CfSummary(res.AtkCf, res.DefCf)}）";
        emit(encounter ? "encounter" : "combat", text, sides);

        if (res.DefRouted && def.Alive)
            emit("rout", $"{def.Id} 溃退", new List<string> { def.Side, "spectator" });

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
    }

    // ==================== Phase 6: Recovery ====================

    private static void RecoveryPhase(GameState g, Action<string, string, List<string>> emit, bool strict)
    {
        foreach (var u in g.Units)
        {
            if (!u.Alive) continue;

            // Rest recovery
            if (u.Status == UnitStatus.Resting)
            {
                u.Fatigue = Clamp(u.Fatigue - 15, 0, 100);
                u.Morale = Clamp(u.Morale + 5, 0, 100);
                u.Status = UnitStatus.Ready;
            }

            // Natural recovery
            if (u.SupplyRate > 0.8)
            {
                u.Morale = Clamp(u.Morale + 2, 0, 100);
                u.Fatigue = Clamp(u.Fatigue - 3, 0, 100);
            }

            // Routed units may rally
            if (u.Status == UnitStatus.Routed && u.Morale > g.Rules.Combat.RoutRecover)
            {
                u.Status = UnitStatus.Ready;
                emit("rally", $"{u.Id} 重新集结", new List<string> { u.Side });
            }
        }
    }

    // ==================== Victory Check ====================

    private static void CheckVictory(GameState g, Action<string, string, List<string>> emit)
    {
        foreach (var vc in g.VictoryConds)
        {
            switch (vc.Type)
            {
                case VictoryType.Attrition:
                    CheckAttritionVictory(g, vc, emit);
                    break;
                case VictoryType.HoldObjective:
                    CheckHoldVictory(g, vc, emit);
                    break;
                case VictoryType.ControlNodes:
                    CheckNodeVictory(g, vc, emit);
                    break;
            }
        }
    }

    private static void CheckAttritionVictory(GameState g, VictoryCond vc, Action<string, string, List<string>> emit)
    {
        var redPersonnel = g.LiveOf("red").Sum(u => u.Personnel);
        var bluePersonnel = g.LiveOf("blue").Sum(u => u.Personnel);

        if (redPersonnel < vc.RedThreshold)
        {
            g.Over = true;
            g.Winner = "blue";
            emit("victory", "蓝方达成消耗胜利", new List<string> { "red", "blue", "spectator" });
        }
        else if (bluePersonnel < vc.BlueThreshold)
        {
            g.Over = true;
            g.Winner = "red";
            emit("victory", "红方达成消耗胜利", new List<string> { "red", "blue", "spectator" });
        }
    }

    private static void CheckHoldVictory(GameState g, VictoryCond vc, Action<string, string, List<string>> emit)
    {
        var holders = MechanicsService.NodeHoldersByHex(g);
        var redNodes = holders.Count(h => h.Value == "red");
        var blueNodes = holders.Count(h => h.Value == "blue");

        if (redNodes >= vc.RedHoldCount)
        {
            g.Over = true;
            g.Winner = "red";
            emit("victory", "红方守住要点", new List<string> { "red", "blue", "spectator" });
        }
        else if (blueNodes >= vc.BlueHoldCount)
        {
            g.Over = true;
            g.Winner = "blue";
            emit("victory", "蓝方守住要点", new List<string> { "red", "blue", "spectator" });
        }
    }

    private static void CheckNodeVictory(GameState g, VictoryCond vc, Action<string, string, List<string>> emit)
    {
        var holders = MechanicsService.NodeHoldersByHex(g);
        var redNodes = holders.Count(h => h.Value == "red");
        var blueNodes = holders.Count(h => h.Value == "blue");

        if (redNodes > blueNodes)
        {
            g.Over = true;
            g.Winner = "red";
            emit("victory", "红方控制更多要点", new List<string> { "red", "blue", "spectator" });
        }
        else if (blueNodes > redNodes)
        {
            g.Over = true;
            g.Winner = "blue";
            emit("victory", "蓝方控制更多要点", new List<string> { "red", "blue", "spectator" });
        }
    }

    // ==================== Helpers ====================

    private static void CheckDestroy(GameState g, Unit u, Action<string, string, List<string>> emit)
    {
        if (u.Personnel <= 0 && u.Alive)
        {
            u.Status = UnitStatus.Destroyed;
            emit("destroyed", $"{u.Id} 被歼灭", new List<string> { u.Side, "spectator" });
        }
    }

    private static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0) return min;
        if (value.CompareTo(max) > 0) return max;
        return value;
    }
}
