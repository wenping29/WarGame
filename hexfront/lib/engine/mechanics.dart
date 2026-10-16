// 战场机制：天气演变、后勤补给线、制空权、战斗裁决。全部系数走 Rules/定义库，可调不改码。
import 'dart:math' as math;
import 'defs.dart';
import 'hex.dart';
import 'state.dart';

double _clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

// ─────────────── 天气 ───────────────

const _WEATHER_CHAIN = <String, List<List<Object>>>{
  'clear': [['clear', 0.62], ['overcast', 0.28], ['rain', 0.07], ['fog', 0.03]],
  'overcast': [['overcast', 0.5], ['clear', 0.22], ['rain', 0.2], ['fog', 0.05], ['storm', 0.03]],
  'rain': [['rain', 0.4], ['overcast', 0.3], ['storm', 0.15], ['clear', 0.15]],
  'storm': [['storm', 0.3], ['rain', 0.45], ['overcast', 0.25]],
  'snow': [['snow', 0.6], ['overcast', 0.3], ['clear', 0.1]],
  'fog': [['fog', 0.35], ['overcast', 0.35], ['clear', 0.3]],
  'sandstorm': [['sandstorm', 0.35], ['clear', 0.45], ['overcast', 0.2]],
};

/// 天气演变：fixed/director 由导演部或剧本锁定；stochastic 逐回合按链转移。
String evolveWeather(GameState g, String mode) {
  if (mode == 'fixed' || mode == 'director') return g.weatherId;
  final row = _WEATHER_CHAIN[g.weatherId] ?? _WEATHER_CHAIN['clear']!;
  var roll = g.rng.nextDouble();
  for (final e in row) {
    roll -= (e[1] as double);
    if (roll <= 0) return e[0] as String;
  }
  return row.last[0] as String;
}

double seasonAttrition(String season) {
  if (season.contains('冬') || season.contains('寒')) return 1.25;
  if (season.contains('秋') || season.contains('雨')) return 1.08;
  return 1.0;
}

// ─────────────── 后勤：补给线 ───────────────

/// 多源 BFS 求每格到最近补给源（辎重/机场/仓库）的通路；敌方占据格阻断补给。
/// 返回 side → hexKey → 距离（跳数）。无补给源的阵营返回空表。
Map<String, Map<HexKey, int>> supplyDistances(GameState g) {
  final occupied = <HexKey, String>{};
  for (final u in g.units) {
    if (u.alive && !g.typeOf(u).isAir) occupied[u.pos.key] = u.side;
  }
  final out = <String, Map<HexKey, int>>{};
  for (final side in ['red', 'blue', 'neutral']) {
    final dist = <HexKey, int>{};
    final frontier = <Hex>[];
    for (final u in g.units) {
      if (!u.alive || u.side != side) continue;
      final t = g.typeOf(u);
      if (t.supplySource || t.cls == 'structure') {
        if (!dist.containsKey(u.pos.key)) {
          dist[u.pos.key] = 0;
          frontier.add(u.pos);
        }
      }
    }
    var i = 0;
    while (i < frontier.length) {
      final cur = frontier[i++];
      final d = dist[cur.key]!;
      for (final n in hexNeighbors(cur)) {
        if (!g.cells.containsKey(n.key)) continue;
        final occ = occupied[n.key];
        if (occ != null && occ != side) continue; // 敌占格断链
        final nd = d + (g.terrainOf(n).water && !_crossable(g, n, side) ? 3 : 1);
        if (dist.containsKey(n.key) && dist[n.key]! <= nd) continue;
        dist[n.key] = nd;
        frontier.add(n);
      }
    }
    out[side] = dist;
  }
  return out;
}

bool _crossable(GameState g, Hex h, String side) {
  final c = g.cellAt(h);
  if (c.bridge) return true;
  for (final u in g.units) {
    if (u.alive && u.side == side && u.pos == h) return true; // 己方已架浮桥/夺取渡口
  }
  return false;
}

/// 补给系数 R = retention^dist（保底 0.05）；无补给源时按宽松默认 0.7
Map<String, Map<HexKey, double>> computeSupply(GameState g) {
  final strict = g.flags['strict_supply'] ?? false;
  final dist = supplyDistances(g);
  final out = <String, Map<HexKey, double>>{};
  final ret = strict ? g.rules.supplyRetention : 0.95;
  dist.forEach((side, m) {
    final r = <HexKey, double>{};
    for (final e in m.entries) {
      r[e.key] = _clamp(math.pow(ret, e.value).toDouble(), 0.05, 1.0);
    }
    out[side] = r;
  });
  return out;
}

/// 补给系数 → 战力乘子。断供有下限保底，修复内核"断供 CF 归零"缺陷。
double supplyToCF(double rate, bool strict) {
  if (!strict) return _clamp(0.6 + 0.4 * rate, 0.6, 1.0);
  return _clamp(0.25 + 0.75 * rate, 0.25, 1.0);
}

// ─────────────── 制空权 ───────────────

/// 逐格空权分：+1 红控 / -1 蓝控。战斗机巡逻权重最高，防空兵提供区域拒止。
Map<HexKey, double> computeAirControl(GameState g) {
  final acc = <String, Map<HexKey, double>>{
    'red': <HexKey, double>{},
    'blue': <HexKey, double>{},
  };
  void add(String side, Hex h, double v, int radius) {
    if (side != 'red' && side != 'blue') return;
    final m = acc[side]!;
    for (final c in hexRange(h, radius)) {
      if (!g.cells.containsKey(c.key)) continue;
      m[c.key] = (m[c.key] ?? 0) + v;
    }
  }

  for (final u in g.units) {
    if (!u.alive) continue;
    final t = g.typeOf(u);
    final m = <String, double>{'fighter': 1.0, 'strike': 0.45, 'recon': 0.25};
    if (t.isAir) {
      final zone = u.airborne ? (u.missionZone ?? u.pos) : u.pos;
      final w = m[t.airRole] ?? 0.1;
      add(u.side, zone, u.mission == 'patrol' ? w : w * 0.4, t.airRange);
    } else if (t.antiAir > 0) {
      add(u.side, u.pos, t.antiAir * 0.25, 3);
    } else if (t.cls == 'structure') {
      add(u.side, u.pos, 0.2, 4);
    }
  }
  final keys = <HexKey>{...acc['red']!.keys, ...acc['blue']!.keys};
  final out = <HexKey, double>{};
  for (final k in keys) {
    final a = acc['red']![k] ?? 0, b = acc['blue']![k] ?? 0;
    final tot = a + b;
    if (tot < 0.05) continue;
    final v = (a - b) / tot;
    if (v.abs() < 0.2) continue; // 争夺区视为无主
    out[k] = _clamp(v, -1, 1);
  }
  return out;
}

/// 空权对地面单位战力的影响（开阔地、无工事时惩罚最重）
double airPenalty(GameState g, Unit u) {
  if (!(g.flags['air_superiority'] ?? true)) return 1.0;
  final ctl = g.airControl[u.pos.key];
  if (ctl == null || ctl == 0) return 1.0;
  final enemyHolds = (ctl > 0 && u.side == 'blue') || (ctl < 0 && u.side == 'red');
  if (!enemyHolds) return 1.0;
  final td = g.terrainOf(u.pos);
  final exposure = _clamp(1.0 - td.defense * 0.25 - u.fortLevel * 0.12, 0.15, 1.0);
  return _clamp(1.0 - (1.0 - g.rules.airGroundPenalty) * exposure, 0.4, 1.0);
}

// ─────────────── 战斗裁决 ───────────────

/// CF 战力链各因子（战报要能解释"为什么打不动"）
class CombatFactor {
  final double base, terrain, fort, strength, supply, morale, personnel, commander, weather, air, counter, ammo;
  CombatFactor({
    required this.base, required this.terrain, required this.fort, required this.strength,
    required this.supply, required this.morale, required this.personnel, required this.commander,
    required this.weather, required this.air, required this.counter, required this.ammo,
  });

  double get product =>
      base * terrain * fort * strength * supply * morale * personnel * commander * weather * air * counter * ammo;

  Map<String, dynamic> toJson() => {
        'base': _r(base), 'terrain': _r(terrain), 'fort': _r(fort), 'strength': _r(strength),
        'supply': _r(supply), 'morale': _r(morale), 'personnel': _r(personnel),
        'commander': _r(commander), 'weather': _r(weather), 'air': _r(air),
        'counter': _r(counter), 'ammo': _r(ammo), 'cf': _r(product),
      };

  static double _r(double v) => double.parse(v.toStringAsFixed(3));
}

/// 人员水平：训练度与经验进战力
double personnelFactor(Unit u) => 0.75 + 0.0025 * u.training + 0.0015 * u.experience;

/// 指挥链：本方指挥所（isHQ）在半径内 → 增益；全失联 → 扣减
double commanderFactor(GameState g, Unit u) {
  final radius = g.rules.commanderRadius.round();
  var best = 0.0;
  for (final hq in g.units) {
    if (!hq.alive || hq.side != u.side || !hq.isHQ) continue;
    final d = hexDistance(hq.pos, u.pos);
    if (d > radius) continue;
    final v = (1.0 - d / (radius + 1)) * (0.6 + 0.8 * hq.commanderQuality);
    if (v > best) best = v;
  }
  if (u.isHQ) best = math.max(best, 0.5 + 0.5 * u.commanderQuality);
  if (best <= 0.001) return 0.8; // 失联
  return 0.9 + 0.25 * best;
}

/// 攻方对守方的 CF。守方地形/工事计入，攻方弹药/补给/士气计入。
CombatFactor computeCF(GameState g, Unit att, Unit def) {
  final attType = g.typeOf(att);
  final defType = g.typeOf(def);
  final td = g.terrainOf(def.pos);
  final wx = g.weatherOf();
  final day = DayPhase.fromHours(g.hourOfDay);
  final strict = g.flags['strict_supply'] ?? false;
  final ammoMinCf = (g.rules.raw['ammo_min_cf'] as num?)?.toDouble() ?? 0.3;
  final ammo = _clamp(att.ammo <= 0 ? ammoMinCf : 0.5 + 0.5 * att.ammo, ammoMinCf, 1.0);
  final counter = attType.counters[defType.id] ?? 1.0;
  return CombatFactor(
    base: attType.firepower,
    terrain: 1.0 / td.defense,
    fort: 1.0 / (1.0 + g.rules.fortBonus * def.fortLevel),
    strength: att.personnel / math.max(1, attType.personnel),
    supply: supplyToCF(att.supplyRate, strict),
    morale: _clamp(0.5 + att.morale / 100 * 0.7, 0.4, 1.2),
    personnel: personnelFactor(att),
    commander: commanderFactor(g, att),
    weather: wx.combat * day.combat,
    air: airPenalty(g, att),
    counter: counter,
    ammo: ammo,
  );
}

class ClashResult {
  final int atkLoss, defLoss;
  final double atkCF, defCF;
  final bool defRouted;
  final bool attackerCaptured;
  final Map<String, dynamic> factorsAtk, factorsDef;
  ClashResult({
    required this.atkLoss, required this.defLoss, required this.atkCF, required this.defCF,
    required this.defRouted, required this.attackerCaptured,
    required this.factorsAtk, required this.factorsDef,
  });
}

/// 交战裁决：CF 比 → 损失份额 + 士气跌落 + 可能的溃退/易手。
ClashResult resolveClash(GameState g, Unit att, Unit def) {
  final fa = computeCF(g, att, def);
  final fd = computeCF(g, def, att);
  final atkCF = fa.product;
  final defCF = math.max(0.01, fd.product);
  final ratio = atkCF / defCF;
  final noiseA = g.rng.jitter(g.rules.noiseAmp);
  final noiseD = g.rng.jitter(g.rules.noiseAmp);
  final wx = g.weatherOf();
  final day = DayPhase.fromHours(g.hourOfDay);
  final seasonK = seasonAttrition(g.season);

  final defLossFrac = _clamp(0.055 * ratio * noiseA * wx.attrition * day.combat * seasonK, 0.0, 0.34);
  final atkLossFrac = _clamp(0.03 / ratio * noiseD * wx.attrition * seasonK, 0.0, 0.2);

  final defLoss = math.max(defLossFrac >= 0.005 ? 1 : 0, (defLossFrac * def.personnel).round());
  final atkLoss = math.max(atkLossFrac >= 0.005 ? 1 : 0, (atkLossFrac * att.personnel).round());

  // 守方士气跌落（含工事与兵力比安慰项）
  final moraleHit = _clamp(6 + 18 * (defLossFrac * 4) - 4 * def.fortLevel, 2, 40);
  def.morale = _clamp(def.morale - moraleHit, 0, 100);
  att.morale = _clamp(att.morale - (atkLoss > 0 ? 3 : 0) + (defLoss > atkLoss * 2 ? 2 : 0), 0, 100);

  final defRouted = def.morale < g.rules.routMorale;
  final captured = defRouted || def.personnel - defLoss <= 0;

  return ClashResult(
    atkLoss: atkLoss, defLoss: defLoss, atkCF: atkCF, defCF: defCF,
    defRouted: defRouted, attackerCaptured: captured,
    factorsAtk: fa.toJson(), factorsDef: fd.toJson(),
  );
}
