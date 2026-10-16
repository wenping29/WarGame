// AI 副官：严格按本方迷雾视图决策（不用上帝视角），可为缺席阵营代管。
import '../engine/commands.dart';
import '../engine/hex.dart';
import '../engine/state.dart';

/// view 必须是 sideView(g, side) 的产物 → AI 与人类信息对等
List<Command> aiOrders(GameState g, String side, Map<String, dynamic> view) {
  final cmds = <Command>[];
  final own = (view['own'] as List).cast<Map<String, dynamic>>();
  final contacts = (view['contacts'] as List).cast<Map<String, dynamic>>();
  final byId = {for (final o in own) o['id'] as String: o};

  // 目标格：优先胜负要点，其次最近暴露敌军
  Hex? objective;
  for (final v in g.victory) {
    if (v.side == side && v.kind == 'capture' && v.target != null) {
      final p = v.target!.split(',');
      objective = Hex(int.parse(p[0]), int.parse(p[1]));
      break;
    }
  }

  final enemyHexes = <String, Hex>{
    for (final c in contacts) c['id'] as String: Hex((c['q'] as num).toInt(), (c['r'] as num).toInt())
  };

  for (final o in own) {
    final id = o['id'] as String;
    final u = g.findUnit(id);
    if (u == null || !u.alive) continue;
    final t = g.typeOf(u);
    final pos = Hex((o['q'] as num).toInt(), (o['r'] as num).toInt());

    if (t.isAir) {
      final role = t.airRole;
      final zone = _bestAirZone(enemyHexes, pos, t.airRange, g, side);
      final mission = switch (role) {
        'fighter' => 'patrol',
        'strike' => enemyHexes.isNotEmpty ? 'strike' : 'none',
        'recon' => 'recon',
        _ => 'none',
      };
      if (mission != 'none') cmds.add(Command(op: 'air_mission', unitId: id, mission: mission, zone: zone));
      continue;
    }
    if (t.cls == 'structure') continue;
    if ((o['status'] as String?) == UnitStatus.routed) {
      cmds.add(Command(op: 'rest', unitId: id));
      continue;
    }

    // 邻近暴露敌军 → 攻击
    String? targetId;
    var bestD = 99;
    enemyHexes.forEach((eid, eh) {
      final d = hexDistance(pos, eh);
      final reach = t.gunRange > 0 ? t.gunRange + 1 : (t.isAir ? t.airRange : 1);
      if (d <= reach && d < bestD) {
        bestD = d;
        targetId = eid;
      }
    });
    if (targetId != null) {
      if (t.gunRange > 0 && bestD > 1) {
        cmds.add(Command(op: 'fire', unitId: id, zone: enemyHexes[targetId!]));
      } else if (!t.isAir) {
        cmds.add(Command(op: 'attack', unitId: id, target: targetId));
      }
      continue;
    }

    if (t.gunRange > 0) {
      // 炮兵无目标：有暴露敌情的远处格盲打，否则构筑工事
      final far = enemyHexes.values.where((h) => hexDistance(pos, h) <= t.gunRange + 1).toList();
      if (far.isNotEmpty) {
        cmds.add(Command(op: 'fire', unitId: id, zone: far.first));
      } else {
        cmds.add(Command(op: 'dig', unitId: id));
      }
      continue;
    }

    if (u.fortLevel >= 2 && (o['supply'] as num?)?.toDouble() != null && (o['supply'] as num) < 0.5) {
      cmds.add(Command(op: 'rest', unitId: id));
      continue;
    }

    // 机动：向目标/最近暴露敌军推进
    final goal = objective ?? (enemyHexes.isEmpty ? null : _nearest(pos, enemyHexes.values));
    if (goal == null) {
      cmds.add(Command(op: 'dig', unitId: id));
      continue;
    }
    final path = _pathToward(g, side, pos, goal, byId);
    if (path.length > 1) cmds.add(Command(op: 'move', unitId: id, path: path));
    else cmds.add(Command(op: 'dig', unitId: id));
  }
  return cmds;
}

Hex _nearest(Hex from, Iterable<Hex> cands) {
  Hex? best;
  var bd = 1 << 30;
  for (final c in cands) {
    final d = hexDistance(from, c);
    if (d < bd) {
      bd = d;
      best = c;
    }
  }
  return best ?? from;
}

Hex _bestAirZone(Map<String, Hex> enemies, Hex home, int range, GameState g, String side) {
  if (enemies.isEmpty) return home;
  final cands = enemies.values.where((h) => hexDistance(home, h) <= range).toList();
  if (cands.isEmpty) return home;
  return _nearest(home, cands);
}

/// 贪心路径：沿六角直线朝目标推进，避开敌占格与不可通行格，最长 6 格（引擎仍会按 MP 裁剪）
List<Hex> _pathToward(GameState g, String side, Hex from, Hex to, Map<String, Map<String, dynamic>> ownById) {
  if (from == to) return [from];
  final line = hexLine(from, to);
  final path = <Hex>[from];
  final enemySet = <String>{
    for (final c in (g.tracks[side] ?? {}).values) '${c.lastSeen.q},${c.lastSeen.r}'
  };
  for (final h in line.skip(1)) {
    if (!g.cells.containsKey(h.key)) break;
    final td = g.terrainOf(h);
    if (td.impassable) break;
    if (enemySet.contains(h.key)) break;
    final blockers = g.unitsAt(h).where((o) => o.side != side).toList();
    if (blockers.isNotEmpty) break;
    path.add(h);
    if (path.length >= 7) break;
    if (h == to) break;
  }
  if (path.length == 1) {
    // 直线被堵：尝试任一邻格推进
    for (final n in hexNeighbors(from)) {
      if (!g.cells.containsKey(n.key)) continue;
      if (g.terrainOf(n).impassable) continue;
      if (g.unitsAt(n).any((o) => o.side != side)) continue;
      if (hexDistance(n, to) < hexDistance(from, to)) {
        path.add(n);
        break;
      }
    }
  }
  return path;
}
