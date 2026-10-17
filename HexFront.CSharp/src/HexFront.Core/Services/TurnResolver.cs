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

    /// <summary>Battle record for combat logging.</summary>
    public class BattleRecord
    {
        public int Turn { get; init; }
        public string Kind { get; init; } = ""; // air/strike/combat/encounter
        public string Attacker { get; init; } = "";
        public string Defender { get; init; } = "";
        public int Losses { get; init; }
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

        var strictFlag = g.Flags.GetValueOrDefault("strict_supply");
        var strict = strictFlag is bool strictBool && strictBool;

        // Phase 1: Air phase (missions → air combat → air control → ground strike)
        AirPhase(g, accepted, Emit, battles);

        // Phase 2: Observation update
        FogService.UpdateObservation(g);

        // Phase 3: Supply resolution
        ResolveSupply(g, strict, Emit);

        // Phase 4: Movement (including fortification)
        var moved = MovementPhase(g, accepted, Emit);

        // Phase 4b: Encounters (moving into unknown enemy hex)
        Encounters(g, moved, Emit, battles);

        // Phase 5: Combat
        CombatPhase(g, accepted, Emit, battles);

        // Phase 6: Recovery & attrition
        RecoveryPhase(g, Emit, strict);

        // Phase 7: State advancement
        g.HourOfDay = (g.HourOfDay + g.Rules.TurnHours) % 24;
        if (g.WeatherMode != "director")
            g.Weather = EvolveWeather(g);
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

            if (hostile && new Random().NextDouble() < 0.12)
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
        var supplyBySide = new Dictionary<string, Dictionary<string, double>>
        {
            ["red"] = MechanicsService.ComputeSupply(g, "red"),
            ["blue"] = MechanicsService.ComputeSupply(g, "blue"),
        };

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
            var hq = CommanderFactor(g, u);

            var budget = t.Mobility * g.Rules.MpBase * t.MobilityPenalty() * wx.Mobility * day.Recon;
            if (u.Fuel <= 0.01 && (cls == "motor" || cls == "mech")) budget *= 0.25;
            if (u.Fatigue > 70) budget *= 0.7;
            if (u.FortLevel > 0) budget *= 0.8; // Abandon fortifications
            // 指挥官系数只在 [0.6,1.15] 内缩放，预算本身按 Dart 语义取整（不是把预算夹到 1.15）
            budget = Math.Round(budget * Clamp(hq, 0.6, 1.15));

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

    private static void Encounters(GameState g, List<Unit> moved,
        Action<string, string, List<string>> emit, List<BattleRecord> battles)
    {
        foreach (var u in moved)
        {
            var enemies = g.UnitsAt(u.Pos).Where(e => e.Side != u.Side && e.Alive).ToList();
            if (enemies.Count == 0) continue;

            // Encounter combat: defender advantage
            foreach (var e in enemies)
            {
                var res = ResolveCombat(g, u, e, isEncounter: true);
                ApplyCombatLoss(g, u, res.DefLoss, e, res.AtkLoss, emit);

                battles.Add(new BattleRecord
                {
                    Turn = g.Turn,
                    Kind = "encounter",
                    Attacker = u.Id,
                    Defender = e.Id,
                    Losses = res.AtkLoss + res.DefLoss,
                });

                emit("encounter", $"遭遇战 @({u.Pos.Q},{u.Pos.R})：{u.Id} ↔ {e.Id}",
                    new List<string> { u.Side, e.Side, "spectator" });
            }
        }
    }

    // ==================== Phase 5: Combat ====================

    private static void CombatPhase(GameState g, Dictionary<string, List<Command>> accepted,
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

            var res = ResolveCombat(g, atk, def);
            ApplyCombatLoss(g, atk, res.DefLoss, def, res.AtkLoss, emit);

            battles.Add(new BattleRecord
            {
                Turn = g.Turn,
                Kind = "combat",
                Attacker = atk.Id,
                Defender = def.Id,
                Losses = res.AtkLoss + res.DefLoss,
            });

            emit("combat", $"战斗：{atk.Id} → {def.Id}",
                new List<string> { atk.Side, def.Side, "spectator" });
        }
    }

    private static (int AtkLoss, int DefLoss) ResolveCombat(GameState g, Unit atk, Unit def, bool isEncounter = false)
    {
        var atkType = g.TypeOf(atk);
        var defType = g.TypeOf(def);
        var defTerrain = g.TerrainOf(def.Pos);

        var atkPower = atkType.Firepower * atk.Morale / 100.0 * atk.Personnel * atk.Ammo;
        var defPower = defType.Protection * def.Morale / 100.0 * def.Personnel * (1.0 + defTerrain.Defense * 0.2);

        if (isEncounter) defPower *= 1.3; // Defender advantage

        var ratio = atkPower / Math.Max(1, defPower);

        int atkLoss, defLoss;
        if (ratio > 2.0)
        {
            atkLoss = (int)(atk.Personnel * 0.05);
            defLoss = (int)(def.Personnel * 0.25);
        }
        else if (ratio > 1.0)
        {
            atkLoss = (int)(atk.Personnel * 0.1);
            defLoss = (int)(def.Personnel * 0.2);
        }
        else
        {
            atkLoss = (int)(atk.Personnel * 0.15);
            defLoss = (int)(def.Personnel * 0.1);
        }

        return (atkLoss, defLoss);
    }

    private static void ApplyCombatLoss(GameState g, Unit atk, int atkLoss, Unit def, int defLoss,
        Action<string, string, List<string>> emit)
    {
        atk.Personnel -= atkLoss;
        atk.Casualties += atkLoss;
        atk.Morale = Clamp(atk.Morale - atkLoss * 0.1, 0, 100);

        def.Personnel -= defLoss;
        def.Casualties += defLoss;
        def.Morale = Clamp(def.Morale - defLoss * 0.15, 0, 100);

        if (def.Morale < 20)
        {
            def.Status = UnitStatus.Routed;
            emit("rout", $"{def.Id} 溃退", new List<string> { def.Side, "spectator" });
        }

        CheckDestroy(g, atk, emit);
        CheckDestroy(g, def, emit);
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
            if (u.Status == UnitStatus.Routed && u.Morale > 30)
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
        var holders = MechanicsService.NodeHolders(g);
        var redNodes = holders.GetValueOrDefault("red", new List<string>()).Count;
        var blueNodes = holders.GetValueOrDefault("blue", new List<string>()).Count;

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
        var holders = MechanicsService.NodeHolders(g);
        var redNodes = holders.GetValueOrDefault("red", new List<string>()).Count;
        var blueNodes = holders.GetValueOrDefault("blue", new List<string>()).Count;

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

    private static double CommanderFactor(GameState g, Unit u)
    {
        // Simplified: base 1.0, could be enhanced with commander quality
        return 1.0;
    }

    private static string EvolveWeather(GameState g)
    {
        // Simple weather evolution: stay the same or shift one step
        var weathers = new[] { "clear", "cloudy", "rain", "storm", "fog", "snow" };
        var currentIdx = Array.IndexOf(weathers, g.Weather ?? "clear");
        if (currentIdx < 0) currentIdx = 0;

        var rng = new Random();
        var change = rng.Next(-1, 2); // -1, 0, or +1
        var newIdx = Clamp(currentIdx + change, 0, weathers.Length - 1);

        return weathers[newIdx];
    }

    private static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0) return min;
        if (value.CompareTo(max) > 0) return max;
        return value;
    }
}
