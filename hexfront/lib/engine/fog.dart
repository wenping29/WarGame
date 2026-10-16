// 战争迷雾：观测强度累计 → 通视判定 → 暴露分级 → 阵营视图裁剪。
// 铁律：全量真实态永不出服务端，本文件产出的 side_view 就是网络能看见的上限。
import 'dart:math' as math;
import 'defs.dart';
import 'hex.dart';
import 'mechanics.dart';
import 'state.dart';


/// 侦察半径（格）：兵种 recon + 天气能见度 + 昼夜 + 观察方地形
double reconRadius(GameState g, Unit u) {
  final t = g.typeOf(u);
  final wx = g.weatherOf();
  final day = DayPhase.fromHours(g.hourOfDay);
  final obsTerrain = g.terrainOf(u.pos);
  var r = (1.0 + t.recon * 0.55) * wx.recon * day.recon * (u.fatigue > 60 ? 0.85 : 1.0);
  if (u.status == UnitStatus.routed) r *= 0.5;
  r *= (1.0 - 0.15 * (1.0 - obsTerrain.obsPenalty).clamp(0.0, 1.0)).toDouble();
  return r;
}

/// 通视：真实高程 + 地表遮挡。山脊挡住谷地是迷雾可信度的来源。
bool lineOfSight(GameState g, Hex from, Hex to) {
  if (from == to) return true;
  final path = hexLine(from, to);
  final a = g.cellAt(from).elev;
  final b = g.cellAt(to).elev;
  final eyeA = a + 20.0, eyeB = b + 20.0;
  final n = path.length - 1;
  final step = g.rules.losElevStep;
  var blocking = 0.0;
  for (var i = 1; i < n; i++) {
    final t = i / n;
    final sight = eyeA + (eyeB - eyeA) * t;
    final c = g.cellAt(path[i]);
    if (c.elev > sight + step) blocking += 1.0;
    final td = g.terrainOf(path[i]);
    blocking += td.losBlock * 0.6; // 林地/城镇/山地的视觉遮挡
  }
  if (blocking >= 1.6) return false;
  final targetTd = g.terrainOf(to);
  // 目标在密林中且距离很远 → 目视不可靠
  if (targetTd.obsPenalty < 0.6 && hexDistance(from, to) > 4 && blocking > 0.4) return false;
  return true;
}

/// 暴露等级：观测点 → 0..3
int exposureLevel(GameState g, int obsPoints) {
  final r = g.rules;
  if (obsPoints >= r.exposureE3) return 3;
  if (obsPoints >= r.exposureE2) return 2;
  if (obsPoints >= r.exposureE1) return 1;
  return 0;
}

/// 每回合重算各方观测点，并刷新/衰减"已暴露目标"记忆。
void updateObservation(GameState g) {
  final sides = ['red', 'blue'];
  final fogOn = g.flags['fog'] ?? true;

  if (!fogOn) {
    // 迷雾关闭：双方互为全知（教学/调试用）
    for (final s in sides) {
      g.obs[s] = {for (final k in g.cells.keys) k: 99};
      _refreshTracks(g, s, allSeeing: true);
    }
    return;
  }

  for (final side in sides) {
    final pts = <HexKey, int>{};
    for (final u in g.units) {
      if (!u.alive || u.side != side) continue;
      final r = reconRadius(g, u);
      if (r <= 0) continue;
      final radius = math.max(1, r.round());
      final strengthBase = u.airborne ? 3 : (1 + (g.typeOf(u).recon / 4.0)).floor();
      for (final h in hexRange(u.pos, radius)) {
        if (!g.cells.containsKey(h.key)) continue;
        final d = hexDistance(u.pos, h);
        if (d > 0 && !lineOfSight(g, u.pos, h)) continue;
        final w = d == 0 ? strengthBase + 1 : math.max(1, (strengthBase - (d / radius * 1.5).floor()));
        pts[h.key] = (pts[h.key] ?? 0) + w;
      }
    }
    // 雷达站：定向远距（对空强、对地弱）→ 统一按环形远距离 +1
    for (final u in g.units) {
      if (!u.alive || u.side != side || g.typeOf(u).id != 'radar') continue;
      for (final h in hexRange(u.pos, 9)) {
        if (g.cells.containsKey(h.key)) pts[h.key] = (pts[h.key] ?? 0) + 1;
      }
    }
    // 制空权带来额外监视
    g.airControl.forEach((k, v) {
      final mine = (v > 0 && side == 'red') || (v < 0 && side == 'blue');
      if (mine) pts[k] = (pts[k] ?? 0) + 2;
    });
    g.obs[side] = pts;
    _refreshTracks(g, side, allSeeing: false);
  }
  g.obs['spectator'] = {};
}

void _refreshTracks(GameState g, String side, {required bool allSeeing}) {
  final enemies = g.units.where((u) => u.alive && u.side != side && u.side != 'neutral').toList()
    ..sort((a, b) => a.id.compareTo(b.id));
  final tracks = g.tracks[side]!;
  final pts = g.obs[side]!;
  final seenIds = <String>{};
  for (final e in enemies) {
    final p = pts[e.pos.key] ?? 0;
    final lvl = allSeeing ? 3 : exposureLevel(g, p);
    if (lvl > 0) {
      seenIds.add(e.id);
      final old = tracks[e.id];
      tracks[e.id] = Track(
        e.id,
        old == null ? lvl : math.max(lvl, old.level - (g.turn - old.lastSeenTurn)),
        e.pos,
        g.turn,
      );
      if (allSeeing) tracks[e.id]!.level = 3;
    }
  }
  // 衰减：脱离观测 staleTurns 回合后彻底丢失
  final stale = g.rules.staleTurns;
  for (final id in tracks.keys.toList()) {
    final t = tracks[id]!;
    if (seenIds.contains(id)) continue;
    final age = g.turn - t.lastSeenTurn;
    if (age > stale) {
      tracks.remove(id);
    } else if (age >= 2) {
      t.level = math.max(1, t.level - 1);
    }
  }
}

/// 兵力规模档位：E1 只给档位，E3 才给精确人数
String strengthBand(GameState g, Unit u) {
  final full = g.typeOf(u).personnel;
  final r = u.personnel / math.max(1, full);
  if (r < 0.34) return '弱';
  if (r < 0.7) return '中';
  return '强';
}

String _band(double v) => v < 30 ? '低' : (v < 60 ? '中' : '高');

Map<String, dynamic> _contactOf(GameState g, Track t) {
  final u = g.findUnit(t.unitId);
  final known = g.turn - t.lastSeenTurn;
  final base = <String, dynamic>{
    'id': t.unitId,
    'side': u?.side ?? 'unknown',
    'q': t.lastSeen.q,
    'r': t.lastSeen.r,
    'level': t.level,
    'known_turns_ago': known,
    'stale': known > 0,
  };
  if (u == null || !u.alive) {
    base['status'] = 'gone';
    return base;
  }
  if (t.level >= 1) base['strength_band'] = strengthBand(g, u);
  if (t.level >= 2) {
    final td = g.typeOf(u);
    base['type'] = td.id;
    base['type_label'] = td.label;
    base['cls'] = td.cls;
    base['fort'] = u.fortLevel;
    base['status'] = u.status;
    base['morale_band'] = _band(u.morale);
    base['fatigue_band'] = _band(u.fatigue);
  }
  if (t.level >= 3) {
    final td = g.typeOf(u);
    base['personnel'] = u.personnel;
    base['ammo'] = double.parse(u.ammo.toStringAsFixed(2));
    base['fuel'] = double.parse(u.fuel.toStringAsFixed(2));
    base['food'] = double.parse(u.food.toStringAsFixed(2));
    base['training'] = u.training.round();
    base['experience'] = u.experience.round();
    base['equipment'] = td.equipment.map((e) => e.toJson()).toList();
    if (u.commanderName != null) base['commander'] = {'name': u.commanderName, 'quality': u.commanderQuality};
  }
  return base;
}

/// 阵营视图：己方全知 + 敌方仅已暴露目标。这是下发给红/蓝终端的全部内容。
Map<String, dynamic> sideView(GameState g, String side) {
  final own = g.units.where((u) => u.side == side).toList()
    ..sort((a, b) => a.id.compareTo(b.id));
  final contacts = (g.tracks[side] ?? {}).values.toList()
    ..sort((a, b) => a.unitId.compareTo(b.unitId));
  return {
    'kind': 'side',
    'side': side,
    ..._common(g),
    'own': own.map((u) => _ownUnit(g, u)).toList(),
    'contacts': contacts.map((t) => _contactOf(g, t)).toList(),
    'node_holders': nodeHolders(g, onlySide: side),
  };
}

Map<String, dynamic> _ownUnit(GameState g, Unit u) {
  final t = g.typeOf(u);
  return {
    'id': u.id, 'side': u.side, 'type': t.id, 'type_label': t.label, 'cls': t.cls,
    'q': u.pos.q, 'r': u.pos.r, 'personnel': u.personnel, 'morale': u.morale.round(),
    'fatigue': u.fatigue.round(), 'training': u.training.round(), 'experience': u.experience.round(),
    'ammo': double.parse(u.ammo.toStringAsFixed(2)), 'fuel': double.parse(u.fuel.toStringAsFixed(2)),
    'food': double.parse(u.food.toStringAsFixed(2)), 'status': u.status, 'fort': u.fortLevel,
    'supply': double.parse(u.supplyRate.toStringAsFixed(2)), 'hq': u.isHQ,
    'airborne': u.airborne, 'mission': u.mission,
    'zone': u.missionZone == null ? null : [u.missionZone!.q, u.missionZone!.r],
    'commander': u.commanderName, 'equipment': t.equipment.map((e) => e.toJson()).toList(),
    'mobility': t.mobility, 'firepower': t.firepower, 'recon': t.recon,
  };
}

/// 导演部视图：全局真实态 + 双方已受领指令（由服务端附加）
Map<String, dynamic> directorView(GameState g) {
  final all = g.units.toList()..sort((a, b) => a.id.compareTo(b.id));
  return {
    'kind': 'director',
    ..._common(g),
    'units': all.map((u) => _ownUnit(g, u)).toList(),
    'node_holders': nodeHolders(g),
    'power': {for (final s in ['red', 'blue']) s: double.parse(powerFactor(g, s).toStringAsFixed(3))},
    'air_control': g.airControl.entries.map((e) {
      final h = Hex.parse(e.key);
      return {'q': h.q, 'r': h.r, 'v': double.parse(e.value.toStringAsFixed(2))};
    }).toList(),
  };
}

/// 观战视图：仅公开情报层（双方已暴露目标的并集 + 己方未知），无未暴露单位。
Map<String, dynamic> spectatorView(GameState g) {
  final merged = <String, Map<String, dynamic>>{};
  for (final side in ['red', 'blue']) {
    (g.tracks[side] ?? {}).forEach((id, t) {
      final c = _contactOf(g, t);
      final old = merged[id];
      if (old == null || (old['level'] as int) < t.level) merged[id] = c;
    });
  }
  final contacts = merged.values.toList()..sort((a, b) => (a['id'] as String).compareTo(b['id'] as String));
  return {
    'kind': 'spectator',
    ..._common(g),
    'contacts': contacts,
    'summary': {
      'red_units': g.liveOf('red').length,
      'blue_units': g.liveOf('blue').length,
      'red_personnel': g.liveOf('red').fold<int>(0, (s, u) => s + u.personnel),
      'blue_personnel': g.liveOf('blue').fold<int>(0, (s, u) => s + u.personnel),
    },
  };
}

Map<String, dynamic> _common(GameState g) => {
      'instance': g.instanceId, 'scenario': g.scenarioId, 'name': g.name,
      'turn': g.turn, 'max_turns': g.maxTurns, 'hour': g.hourOfDay,
      'day': DayPhase.fromHours(g.hourOfDay).toJson(), 'season': g.season,
      'weather': g.weatherOf().toJson(), 'flags': g.flags,
      'geo': g.geo.toJson(), 'width': g.width, 'height': g.height,
      'cells': g.cells.entries.map((e) {
        final h = Hex.parse(e.key);
        final c = e.value.toJson();
        c['q'] = h.q;
        c['r'] = h.r;
        return c;
      }).toList(),
      'victory': g.victory.map((v) => v.toJson()).toList(),
      'nodes': objectiveNodes(g),          // 要点地理（城镇/车站/机场/电厂/变电站），不含归属
      'over': g.over, 'winner': g.winner,
      'fog': g.flags['fog'] ?? true,
    };
