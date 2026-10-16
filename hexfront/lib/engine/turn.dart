// 回合结算：七阶段确定性裁决。唯一入口 resolveTurn(状态, 各方双盲指令)。
import 'dart:math' as math;
import 'defs.dart';
import 'fog.dart';
import 'hex.dart';
import 'mechanics.dart';
import 'commands.dart';
import 'state.dart';

class TurnReport {
  final List<CommandError> rejected;
  final List<WarEvent> events;
  final List<Map<String, dynamic>> battles;
  final String fingerprintBefore, fingerprintAfter;
  TurnReport({
    required this.rejected, required this.events, required this.battles,
    required this.fingerprintBefore, required this.fingerprintAfter,
  });
}

double _clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

TurnReport resolveTurn(GameState g, Map<String, List<Command>> ordersBySide) {
  final before = g.fingerprint();
  final events = <WarEvent>[];
  final battles = <Map<String, dynamic>>[];
  final rejected = <CommandError>[];

  void emit(String code, String text, List<String> sides) {
    final e = WarEvent(g.turn, code, text, sides);
    g.emit(e);
    events.add(e);
  }

  // ── 校验（服务端权威）
  final accepted = <String, List<Command>>{};
  for (final side in ['red', 'blue']) {
    final list = ordersBySide[side] ?? const <Command>[];
    final errs = validateOrders(g, side, list);
    rejected.addAll(errs);
    final badIds = errs.map((e) => e.unitId).toSet();
    accepted[side] = list.where((c) => !badIds.contains(c.unitId)).toList();
    for (final e in errs) {
      emit('order_rejected', '指令被拒：${e.unitId} ${e.message}', [side]);
    }
  }

  final strict = g.flags['strict_supply'] ?? false;

  // ── ① 空中阶段：任务指派 → 空战 → 制空权重算 → 对地打击
  _airPhase(g, accepted, emit, battles);

  // ── ② 侦察与观测更新
  updateObservation(g);

  // ── ③ 补给结算
  final supply = computeSupply(g);
  for (final u in g.units) {
    if (!u.alive) continue;
    final m = supply[u.side];
    u.supplyRate = m != null ? (m[u.pos.key] ?? 0.7) : 0.7;
    final t = g.typeOf(u);
    final drain = t.consumption * 0.06 * (strict ? 1.0 : 0.5) * (1.0 - u.supplyRate) + g.rules.foodPerTurn * 0.4;
    u.food = _clamp(u.food + (u.supplyRate - 1.0) * 0.6 - drain * 0.3, 0, 1);
    u.fuel = _clamp(u.fuel + (u.supplyRate - 1.0) * 0.5 - (t.cls == 'motor' || t.cls == 'mech' ? 0.03 : 0.005), 0, 1);
    if (u.food < 0.2) {
      u.morale = _clamp(u.morale - 4, 0, 100);
      if (g.turn % 3 == 0) emit('hunger', '${u.id} 给养断绝，士气下滑', [u.side]);
    }
    if (u.fuel <= 0.01 && (t.cls == 'motor' || t.cls == 'mech')) {
      u.status = UnitStatus.ready;
      if (u.supplyRate < g.rules.supplyCrit) emit('fuel_out', '${u.id} 油料耗尽，车辆瘫滞', [u.side]);
    }
    if (u.supplyRate < g.rules.supplyCrit && strict) {
      emit('supply_cut', '${u.id} 补给线被切断（R=${u.supplyRate.toStringAsFixed(2)}）', [u.side]);
    }
  }

  // ── ④ 机动（含构筑工事）
  final moved = _movementPhase(g, accepted, emit);

  // ── ④b 遭遇战：移动进入未知敌格 → 防御方占优
  _encounters(g, moved, emit, battles);

  // ── ⑤ 战斗
  _combatPhase(g, accepted, emit, battles);

  // ── ⑥ 消耗与恢复
  _recoveryPhase(g, emit, strict);

  // ── ⑦ 状态推进
  g.hourOfDay = (g.hourOfDay + g.rules.turnHours) % 24;
  if (g.weatherMode != 'director') g.weatherId = evolveWeather(g, g.weatherMode);
  g.turn += 1;
  _checkVictory(g, emit);

  emit('turn_end', '回合 ${g.turn - 1} 结束（${DayPhase.fromHours(g.hourOfDay).label} / ${g.weatherOf().label}）', ['red', 'blue', 'spectator']);
  return TurnReport(
    rejected: rejected, events: events, battles: battles,
    fingerprintBefore: before, fingerprintAfter: g.fingerprint(),
  );
}

// ─────────────── 空中 ───────────────

void _airPhase(
  GameState g, Map<String, List<Command>> accepted,
  void Function(String, String, List<String>) emit, List<Map<String, dynamic>> battles,
) {
  for (final side in ['red', 'blue']) {
    for (final c in accepted[side] ?? const <Command>[]) {
      if (c.op != 'air_mission') continue;
      final u = g.findUnit(c.unitId)!;
      u.mission = c.mission;
      u.missionZone = c.mission == 'none' ? null : c.zone;
      u.airborne = c.mission != 'none';
      emit('air_mission', '${u.id} 起飞执行 ${c.mission} 任务', [side]);
    }
  }

  // 空战：同一空域内双方航空兵接战
  final zones = <Hex, List<Unit>>{};
  for (final u in g.units) {
    if (!u.alive || !u.airborne) continue;
    if (g.typeOf(u).airRole != 'fighter') continue;
    zones.putIfAbsent(u.missionZone ?? u.pos, () => []).add(u);
  }
  zones.forEach((zone, fighters) {
    final reds = fighters.where((f) => f.side == 'red').toList();
    final blues = fighters.where((f) => f.side == 'blue').toList();
    if (reds.isEmpty || blues.isEmpty) return;
    for (final a in reds) {
      for (final b in blues) {
        if (!a.alive || !b.alive) continue;
        final res = resolveClash(g, a, b);
        _applyAirLoss(g, a, res.atkLoss, b, res.defLoss, emit);
        battles.add({'turn': g.turn, 'kind': 'air', 'atk': a.id, 'def': b.id, 'res': res});
        emit('air_clash', '空战 @${zone.q},${zone.r}：${a.id} ↔ ${b.id}', ['red', 'blue', 'spectator']);
      }
    }
  });

  // 非战斗机在敌方控制空域被击落的风险
  for (final u in g.units.toList()) {
    if (!u.alive || !u.airborne) continue;
    if (g.typeOf(u).airRole == 'fighter') continue;
    final z = u.missionZone ?? u.pos;
    final ctl = g.airControl[z.key] ?? 0;
    final hostile = (ctl > 0.2 && u.side == 'blue') || (ctl < -0.2 && u.side == 'red');
    if (hostile && g.rng.chance(0.12)) {
      u.personnel = 0;
      u.status = UnitStatus.destroyed;
      emit('air_lost', '${u.id} 在敌控空域被击落', [u.side, 'spectator']);
    }
  }

  // 对地打击
  for (final u in g.units.toList()) {
    if (!u.alive || !u.airborne || g.typeOf(u).airRole != 'strike') continue;
    final z = u.missionZone ?? u.pos;
    if (u.ammo < 0.15) continue;
    u.ammo = _clamp(u.ammo - 0.25, 0, 1);
    final targets = g.unitsAt(z).where((t) => t.side != u.side && !g.typeOf(t).isAir).toList();
    for (final t in targets) {
      final fakeDef = t;
      final res = resolveClash(g, u, fakeDef);
      final loss = math.min(t.personnel, (res.defLoss * 1.4).round());
      t.personnel -= loss;
      t.morale = _clamp(t.morale - 8, 0, 100);
      t.casualties += loss;
      battles.add({'turn': g.turn, 'kind': 'strike', 'atk': u.id, 'def': t.id, 'loss': loss});
      emit('air_strike', '${u.id} 空袭 ${t.id} 于 ${z.q},${z.r}，损失 $loss 人', [t.side, u.side, 'spectator']);
      _checkDestroy(g, t, emit);
    }
  }

  // 制空权重算
  g.airControl
    ..clear()
    ..addAll(computeAirControl(g));
}

void _applyAirLoss(
  GameState g, Unit a, int aLoss, Unit b, int bLoss,
  void Function(String, String, List<String>) emit,
) {
  // 航空兵以"架次/编制"为人员基数，损失即平台损毁
  for (final p in [(a, aLoss), (b, bLoss)]) {
    final u = p.$1;
    final loss = p.$2;
    if (loss <= 0) continue;
    u.personnel -= loss;
    u.casualties += loss;
    if (u.personnel <= 0) {
      u.status = UnitStatus.destroyed;
      emit('air_lost', '${u.id} 战损', [u.side, 'spectator']);
    } else {
      u.ammo = _clamp(u.ammo - 0.2, 0, 1);
    }
  }
}

// ─────────────── 机动 ───────────────

/// 单格机动消耗：地形基准值 + 地物层修正。
/// 公路 0.65、铁路再 0.8（仅摩托化/机械化搭得上线，徒步不受惠）；隧道单孔限行，
/// 重装备 1.5、徒步 1.2；无桥渡河按 +2 重罚但不封死（保留强渡选项）。
double stepCost(TerrainDef td, Cell cell, String cls) {
  final heavy = cls == 'motor' || cls == 'mech';
  var step = td.cost[cls] ?? 2.0;
  if (cell.road) step *= 0.65;
  if (cell.railway && heavy) step *= 0.8;
  if (cell.tunnel) step *= heavy ? 1.5 : 1.2;
  if (cell.river && !cell.bridge) step += 2.0;
  return step;
}

List<Unit> _movementPhase(
  GameState g, Map<String, List<Command>> accepted,
  void Function(String, String, List<String>) emit,
) {
  final moved = <Unit>[];
  final wx = g.weatherOf();
  final day = DayPhase.fromHours(g.hourOfDay);
  final plan = (accepted.entries.toList()
        ..sort((a, b) => a.key.compareTo(b.key)))
      .expand((e) => e.value.where((c) => c.op == 'move').toList())
      .toList()
    ..sort((a, b) => a.unitId.compareTo(b.unitId));

  for (final c in plan) {
    final u = g.findUnit(c.unitId);
    if (u == null || !u.alive) continue;
    final t = g.typeOf(u);
    final cls = t.cls == 'air' ? 'mech' : t.cls;
    final hq = commanderFactor(g, u);
    var budget = t.mobility * g.rules.mpBase * t.mobilityPenalty() * wx.mobility * day.recon;
    if (u.fuel <= 0.01 && (cls == 'motor' || cls == 'mech')) budget *= 0.25;
    if (u.fatigue > 70) budget *= 0.7;
    if (u.fortLevel > 0) budget *= 0.8; // 离开工事先弃工
    budget = (budget * _clamp(hq, 0.6, 1.15)).roundToDouble();

    var spent = 0.0;
    Hex cur = u.pos;
    for (final h in c.path.skip(1)) {
      final td = g.terrainOf(h);
      final step = stepCost(td, g.cellAt(h), cls);
      if (td.impassable) break;
      if (spent + step > budget) break;
      spent += step;
      cur = h;
    }
    if (cur != u.pos) {
      u.fatigue = _clamp(u.fatigue + g.rules.fatigueMarch * (spent / math.max(1, budget)) * 6, 0, 100);
      u.morale = _clamp(u.morale - (u.fatigue > 80 ? 3 : 0), 0, 100);
      u.fortLevel = 0;
      u.pos = cur;
      u.status = UnitStatus.moved;
      u.mpLeft = spent.round();
      moved.add(u);
      emit('move', '${u.id} 机动至 ${cur.q},${cur.r}（耗 MP ${spent.round()}）', [u.side]);
    }
  }

  // 构筑工事 / 停止
  for (final side in ['red', 'blue']) {
    for (final c in accepted[side] ?? const <Command>[]) {
      final u = g.findUnit(c.unitId);
      if (u == null || !u.alive) continue;
      if (c.op == 'dig') {
        final td = g.terrainOf(u.pos);
        final cap = td.maxFort;
        if (u.fortLevel < cap) {
          u.fortLevel += g.typeOf(u).id == 'engineer' ? 2 : 1;
          u.fortLevel = math.min(u.fortLevel, cap);
          emit('dig', '${u.id} 构筑工事至 ${u.fortLevel} 级', [side]);
        }
      } else if (c.op == 'halt' || c.op == 'rest') {
        u.fortLevel = u.fortLevel;
        u.status = UnitStatus.ready;
      }
    }
  }
  return moved;
}

void _encounters(
  GameState g, List<Unit> moved,
  void Function(String, String, List<String>) emit, List<Map<String, dynamic>> battles,
) {
  for (final u in moved) {
    final enemies = g.unitsAt(u.pos).where((e) => e.side != u.side && !g.typeOf(e).isAir).toList();
    if (enemies.isEmpty) continue;
    final def = enemies.first;
    emit('encounter', '遭遇战！${u.id} 突入发现 ${def.id}', [u.side, def.side, 'spectator']);
    _fight(g, u, def, emit, battles, encounter: true);
    if (!def.alive || def.status == UnitStatus.routed) {
      // 守方被冲走 → 攻方占据该格
    } else {
      // 攻方退回出发格由上层简化：原地对峙
    }
  }
}

// ─────────────── 战斗 ───────────────

void _combatPhase(
  GameState g, Map<String, List<Command>> accepted,
  void Function(String, String, List<String>) emit, List<Map<String, dynamic>> battles,
) {
  final attacks = <Command>[];
  for (final side in ['red', 'blue']) {
    attacks.addAll((accepted[side] ?? const <Command>[]).where((c) => c.op == 'attack' || c.op == 'fire'));
  }
  // 显式稳定序：先守方所在格、再攻方 id —— 不再依赖命名巧合
  attacks.sort((a, b) {
    final ta = g.findUnit(a.target ?? '')?.pos ?? g.findUnit(a.unitId)?.pos ?? const Hex(0, 0);
    final tb = g.findUnit(b.target ?? '')?.pos ?? g.findUnit(b.unitId)?.pos ?? const Hex(0, 0);
    final c = ta.key.compareTo(tb.key);
    if (c != 0) return c;
    return a.unitId.compareTo(b.unitId);
  });

  for (final c in attacks) {
    final att = g.findUnit(c.unitId);
    if (att == null || !att.alive || att.status == UnitStatus.routed) continue;
    Unit? def;
    if (c.op == 'attack') {
      def = c.target == null ? null : g.findUnit(c.target!);
      if (def == null || !def.alive) continue;
      final reach = g.typeOf(att).isAir ? g.typeOf(att).airRange : (g.typeOf(att).gunRange > 0 ? g.typeOf(att).gunRange : 1);
      if (hexDistance(att.pos, def.pos) > reach) continue;
    } else {
      final z = c.zone;
      if (z == null) continue;
      final hits = g.unitsAt(z).where((t) => t.side != att.side && !g.typeOf(t).isAir).toList();
      if (hits.isEmpty) {
        emit('fire_miss', '${att.id} 火力覆盖 ${z.q},${z.r}：无目标（盲打）', [att.side]);
        att.ammo = _clamp(att.ammo - g.rules.ammoPerAttack, 0, 1);
        continue;
      }
      // 盲打精度降级
      final known = (g.tracks[att.side] ?? {})[hits.first.id]?.level ?? 0;
      final eff = known >= 2 ? 1.0 : 0.6;
      for (final t in hits) {
        final res = resolveClash(g, att, t);
        final loss = math.min(t.personnel, (res.defLoss * eff).round());
        t.personnel -= loss;
        t.casualties += loss;
        t.morale = _clamp(t.morale - 6, 0, 100);
        battles.add({'turn': g.turn, 'kind': 'fire', 'atk': att.id, 'def': t.id, 'loss': loss, 'eff': eff});
        emit('arty', '${att.id} 炮击 ${t.id} @${z.q},${z.r}，损失 $loss 人', [att.side, t.side, 'spectator']);
        _checkDestroy(g, t, emit);
      }
      att.ammo = _clamp(att.ammo - g.rules.ammoPerAttack * hits.length, 0, 1);
      att.status = UnitStatus.combat;
      continue;
    }
    _fight(g, att, def, emit, battles, encounter: false);
  }
}

void _fight(
  GameState g, Unit att, Unit def,
  void Function(String, String, List<String>) emit, List<Map<String, dynamic>> battles, {
  required bool encounter,
}) {
  if (encounter) {
    // 遭遇战守方占优：临时给守方工事级 +1
    def.fortLevel += 1;
  }
  final res = resolveClash(g, att, def);
  final aLoss = math.min(att.personnel, res.atkLoss);
  final dLoss = math.min(def.personnel, res.defLoss);
  att.personnel -= aLoss;
  def.personnel -= dLoss;
  att.casualties += aLoss;
  def.casualties += dLoss;
  att.ammo = _clamp(att.ammo - g.rules.ammoPerAttack, 0, 1);
  att.fatigue = _clamp(att.fatigue + 4, 0, 100);
  def.fatigue = _clamp(def.fatigue + 3, 0, 100);
  att.status = UnitStatus.combat;
  def.status = UnitStatus.combat;

  final text = '${att.id} 攻击 ${def.id}：守方 -$dLoss / 攻方 -$aLoss'
      '（CF ${res.atkCF.toStringAsFixed(1)} : ${res.defCF.toStringAsFixed(1)}）';
  emit('combat', text, [att.side, def.side, 'spectator']);
  battles.add({
    'turn': g.turn, 'kind': 'assault', 'atk': att.id, 'def': def.id,
    'atk_loss': aLoss, 'def_loss': dLoss, 'atk_cf': res.atkCF, 'def_cf': res.defCF,
    'factors_atk': res.factorsAtk, 'factors_def': res.factorsDef, 'routed': res.defRouted,
  });

  _checkDestroy(g, def, emit);
  _checkDestroy(g, att, emit);
  if (encounter) def.fortLevel = math.max(0, def.fortLevel - 1);

  // 要点易手：守方溃退或被歼 → 攻方进入该格
  if ((!def.alive || res.defRouted) && att.alive) {
    if (!def.alive) {
      att.pos = def.pos;
    } else {
      // 守方溃退后撤一格（朝己方纵深方向）
      final back = _retreatHex(g, def);
      if (back != null) {
        def.pos = back;
        def.status = UnitStatus.routed;
        emit('rout', '${def.id} 溃退至 ${back.q},${back.r}', [def.side, 'spectator']);
      } else {
        def.status = UnitStatus.routed;
        emit('rout', '${def.id} 溃退（无退路）', [def.side, 'spectator']);
      }
      att.pos = def.pos == att.pos ? att.pos : att.pos;
    }
  }
}

Hex? _retreatHex(GameState g, Unit u) {
  final cands = hexNeighbors(u.pos)
      .where((h) => g.cells.containsKey(h.key) && !g.terrainOf(h).impassable && !g.terrainOf(h).water)
      .where((h) => g.unitsAt(h).every((o) => o.side == u.side || !g.typeOf(o).isAir))
      .toList()
    ..sort((a, b) {
      final da = hexDistance(a, _homeOf(g, u.side));
      final db = hexDistance(b, _homeOf(g, u.side));
      return da - db;
    });
  return cands.isEmpty ? null : cands.first;
}

Hex _homeOf(GameState g, String side) {
  // 本方纵深方向：己方补给源/指挥所的平均位置
  final anchors = g.units.where((u) => u.alive && u.side == side && (g.typeOf(u).supplySource || u.isHQ)).toList();
  if (anchors.isEmpty) return const Hex(0, 0);
  var q = 0, r = 0;
  for (final a in anchors) {
    q += a.pos.q;
    r += a.pos.r;
  }
  return Hex((q / anchors.length).round(), (r / anchors.length).round());
}

void _checkDestroy(GameState g, Unit u, void Function(String, String, List<String>) emit) {
  if (u.personnel <= 0) {
    u.personnel = 0;
    u.status = UnitStatus.destroyed;
    emit('destroyed', '${u.id}（${g.typeOf(u).label}）被歼灭', [u.side, 'spectator']);
  } else if (u.morale < g.rules.routMorale && u.status != UnitStatus.routed) {
    u.status = UnitStatus.routed;
  }
}

// ─────────────── 恢复 ───────────────

void _recoveryPhase(GameState g, void Function(String, String, List<String>) emit, bool strict) {
  final day = DayPhase.fromHours(g.hourOfDay);
  final wx = g.weatherOf();
  for (final u in g.units) {
    if (!u.alive) continue;
    final rest = u.status == UnitStatus.ready || u.status == UnitStatus.routed;
    final supplyK = u.supplyRate;
    // 士气恢复
    if (rest) {
      u.morale = _clamp(u.morale + 4 * supplyK * day.recon * (1.0 + u.training / 200), 0, 100);
    }
    // 疲劳：休整回血；高疲劳无补给继续恶化
    if (rest) {
      u.fatigue = _clamp(u.fatigue - 8 * supplyK, 0, 100);
    } else {
      u.fatigue = _clamp(u.fatigue + 2 * wx.attrition, 0, 100);
    }
    // 弹药低速回补（有补给）
    u.ammo = _clamp(u.ammo + 0.05 * supplyK - (u.status == UnitStatus.combat ? 0.05 : 0), 0, 1);
    // 溃退恢复：修复内核不可达分支
    if (u.status == UnitStatus.routed && u.morale >= g.rules.routRecover) {
      u.status = UnitStatus.ready;
      emit('recover', '${u.id} 恢复战斗力', [u.side]);
    } else if (u.status == UnitStatus.routed && u.morale < g.rules.routMorale) {
      u.morale = _clamp(u.morale + 2, 0, 100);
    }
    if (u.status != UnitStatus.combat && u.status != UnitStatus.routed) u.status = UnitStatus.ready;
  }
}

// ─────────────── 胜负 ───────────────

void _checkVictory(GameState g, void Function(String, String, List<String>) emit) {
  if (g.over) return;
  for (final v in g.victory) {
    final side = v.side ?? 'red';
    final foe = side == 'red' ? 'blue' : 'red';
    switch (v.kind) {
      case 'annihilate':
        final foeTotal0 = g.liveOf(foe).fold<int>(0, (s, u) => s + u.personnel);
        final all = g.units.where((u) => u.side == foe).fold<int>(0, (s, u) => s + u.personnel);
        final startEst = math.max(
            1, all + g.units.where((u) => u.side == foe).fold<int>(0, (s, u) => s + u.casualties));
        if (foeTotal0 <= startEst * (1 - v.amount / 100.0)) {
          _win(g, side, '${v.side} 达成歼灭目标', emit);
          return;
        }
        break;
      case 'capture':
        final target = v.target;
        if (target == null) break;
        final parts = target.split(',');
        final h = Hex(int.parse(parts[0]), int.parse(parts[1]));
        final holders = g.unitsAt(h).where((u) => u.alive).toList();
        if (holders.length == 1 && holders.first.side == side) {
          _win(g, side, '控制要点 $target', emit);
          return;
        }
        break;
      case 'nodes':
        // 同时掌握 amount 处战场要点（城镇/车站/机场/电厂/变电站）⇒ 达成；争夺中不算占
        final holders = nodeHolders(g, onlySide: side);
        if (v.amount > 0 && holders.length >= v.amount) {
          _win(g, side, '掌握 ${holders.length} 处战场要点（目标 ${v.amount}）', emit);
          return;
        }
        break;
      case 'survive':
        if (g.turn > v.turns || g.turn > g.maxTurns) {
          _win(g, side, '坚持到推演结束', emit);
          return;
        }
        break;
    }
  }
  if (g.turn > g.maxTurns) {
    final rp = g.liveOf('red').fold<int>(0, (s, u) => s + u.personnel);
    final bp = g.liveOf('blue').fold<int>(0, (s, u) => s + u.personnel);
    _win(g, rp >= bp ? 'red' : 'blue', '回合耗尽，按剩余兵力判定（红 $rp / 蓝 $bp）', emit);
  }
}

void _win(
  GameState g, String side, String why,
  void Function(String, String, List<String>) emit,
) {
  g.over = true;
  g.winner = side;
  g.endedTurn = g.turn;
  emit('game_over', '推演结束：${side == 'red' ? '红方' : '蓝方'}胜 —— $why', ['red', 'blue', 'spectator']);
}
