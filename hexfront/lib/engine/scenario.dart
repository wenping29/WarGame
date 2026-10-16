// 剧本 → 副本：加载剧本 JSON、合并创建时的覆盖层、生成初始 GameState。
import 'dart:math' as math;
import 'defs.dart';
import 'hex.dart';
import 'library.dart';
import 'rng.dart';
import 'state.dart';

Map<String, dynamic> _mm(dynamic v) => Map<String, dynamic>.from(v as Map);
List<Map<String, dynamic>> _ml(dynamic v) =>
    (v is List ? v : <dynamic>[]).map((e) => Map<String, dynamic>.from(e as Map)).toList();

/// 深合并（覆盖层语义：只覆盖显式给出的键）
Map<String, dynamic> deepMerge(Map<String, dynamic> base, Map<String, dynamic> over) {
  final out = Map<String, dynamic>.from(base);
  over.forEach((k, v) {
    if (v is Map && out[k] is Map) {
      out[k] = deepMerge(Map<String, dynamic>.from(out[k] as Map), Map<String, dynamic>.from(v));
    } else if (v == null) {
      // null 表示"不覆盖"
    } else {
      out[k] = v;
    }
  });
  return out;
}

class ScenarioError implements Exception {
  final String message;
  ScenarioError(this.message);
  @override
  String toString() => 'ScenarioError: $message';
}

class Scenario {
  final Map<String, dynamic> raw;
  Scenario(this.raw);

  String get id => raw['id'] as String? ?? 'unnamed';
  String get name => raw['name'] as String? ?? id;

  /// 校验剧本可用性，返回问题列表（空即合法）
  List<String> validate(Library lib) {
    final errs = <String>[];
    final sides = raw['sides'];
    if (sides is! Map || sides.isEmpty) errs.add('缺少 sides 阵营定义');
    for (final e in (sides as Map?)?.entries ?? <MapEntry>[]) {
      final side = e.key as String;
      final cfg = _mm(e.value);
      final oob = _ml(cfg['oob']);
      if (oob.isEmpty) errs.add('阵营 $side 的 oob 为空');
      for (final u in oob) {
        final t = u['type'] as String?;
        if (t == null || !lib.types.containsKey(t)) errs.add('阵营 $side 引用未知兵种 ${u['type']}');
        if ((u['at'] as List?) == null) errs.add('阵营 $side 单位 ${u['type']} 缺少坐标 at');
      }
      final budget = (cfg['points_budget'] as num?)?.toDouble();
      if (budget != null && budget > 0) {
        final used = lib.oobPoints(oob);
        if (used > budget + 0.001) errs.add('阵营 $side OOB 点数 ${used.toStringAsFixed(1)} 超出预算 ${budget.toStringAsFixed(1)}');
      }
    }
    final w = raw['weather'];
    if (w is Map && w['initial'] != null && !lib.weather.containsKey(w['initial'])) {
      errs.add('未知天气 ${w['initial']}');
    }
    return errs;
  }
}

/// 创建副本：scenario + overrides → GameState
class InstanceBuilder {
  final Library lib;
  final Map<String, dynamic>? Function(String pkg)? loadGisPackage;

  InstanceBuilder({required this.lib, this.loadGisPackage});

  GameState build({
    required Map<String, dynamic> scenarioRaw,
    Map<String, dynamic>? overrides,
    required String instanceId,
    required String instanceName,
    required int seed,
  }) {
    final merged = overrides == null ? Map<String, dynamic>.from(scenarioRaw) : deepMerge(scenarioRaw, overrides);

    // 定义覆盖层：剧本可增补兵种/地形/天气
    var library = lib;
    library = library.withOverrides(
      terrain: merged['terrain_defs'] == null ? null : _ml(merged['terrain_defs']),
      types: merged['unit_type_defs'] == null ? null : _ml(merged['unit_type_defs']),
      weather: merged['weather_defs'] == null ? null : _ml(merged['weather_defs']),
    );

    final rulesJson = Map<String, dynamic>.from(library.baseRules.toJson());
    if (merged['rules'] is Map) rulesJson.addAll(_mm(merged['rules']));
    final rules = Rules.fromJson(rulesJson);

    final rng = SeededRNG(seed);
    final cells = _buildCells(merged, library, rng);
    final geo = _buildGeo(merged, cells);

    final flags = <String, bool>{
      'fog': true, 'air_superiority': true, 'strict_supply': false, 'night': true,
    };
    if (merged['flags'] is Map) {
      (merged['flags'] as Map).forEach((k, v) => flags[k as String] = v as bool);
    }

    final clock = _mm(merged['clock'] ?? {});
    final weatherCfg = _mm(merged['weather'] ?? {});

    final g = GameState(
      instanceId: instanceId,
      scenarioId: merged['id'] as String? ?? 'unnamed',
      name: instanceName,
      lib: library,
      turn: 1,
      hourOfDay: (clock['start_hour'] as num?)?.toInt() ?? 6,
      season: (clock['season'] as String?) ?? '夏',
      weatherId: (weatherCfg['initial'] as String?) ?? 'clear',
      weatherMode: (weatherCfg['mode'] as String?) ?? 'fixed',
      geo: geo,
      width: _span(cells, true),
      height: _span(cells, false),
      cells: cells,
      units: [],
      rules: rules,
      flags: flags,
      victory: _ml(merged['victory'] ?? []).map((v) => VictoryCond.fromJson(v)).toList(),
      maxTurns: (merged['max_turns'] as num?)?.toInt() ?? 20,
      rng: rng,
    );

    _placeOob(g, merged, library);
    g.rules = rules;
    return g;
  }

  Map<HexKey, Cell> _buildCells(Map<String, dynamic> merged, Library library, SeededRNG rng) {
    // ① GIS 数据包（真实 DEM 生成的六角地形）
    final pkg = merged['gis_package'] as String?;
    if (pkg != null && loadGisPackage != null) {
      final data = loadGisPackage!(pkg);
      if (data != null && data['hexes'] is List) {
        final cells = <HexKey, Cell>{};
        for (final h in _ml(data['hexes'])) {
          cells[hexKey((h['q'] as num).toInt(), (h['r'] as num).toInt())] = Cell.fromJson(h);
        }
        return cells;
      }
    }
    // ② 剧本内联地形
    if (merged['terrain'] is List) {
      final cells = <HexKey, Cell>{};
      for (final h in _ml(merged['terrain'])) {
        cells[hexKey((h['q'] as num).toInt(), (h['r'] as num).toInt())] = Cell.fromJson(h);
      }
      if (cells.isNotEmpty) return cells;
    }
    // ③ 程序化地形（无网络/无 DEM 时保证可玩）
    final env = _mm(merged['environment'] ?? {});
    final w = (env['width'] as num?)?.toInt() ?? 26;
    final hgt = (env['height'] as num?)?.toInt() ?? 20;
    final seedH = (env['height_seed'] as num?)?.toInt() ?? 7;
    final cells = <HexKey, Cell>{};
    for (var q = 0; q < w; q++) {
      for (var r = 0; r < hgt; r++) {
        final n = _valueNoise(q, r, seedH);
        final slope = _valueNoise(q + 97, r + 31, seedH + 1);
        String terrain;
        double elev = 0;
        if (env['procedural'] == false) {
          terrain = 'clear';
        } else if (n < 0.18) {
          terrain = 'river';
          elev = 40 + n * 100;
        } else if (n < 0.36) {
          terrain = 'clear';
          elev = 80 + n * 400;
        } else if (n < 0.55) {
          terrain = 'woods';
          elev = 120 + n * 500;
        } else if (n < 0.74) {
          terrain = 'hills';
          elev = 220 + n * 900;
        } else if (n < 0.9) {
          terrain = 'rough';
          elev = 320 + n * 1100;
        } else {
          terrain = 'mountain';
          elev = 500 + n * 1600;
        }
        var city = 0;
        if (slope > 0.86 && n > 0.2 && n < 0.6) city = 2;
        cells[hexKey(q, r)] = Cell(terrain: terrain, elev: (elev / 10).round() * 10, slope: slope * 22, city: city, river: terrain == 'river');
      }
    }
    // 道路：沿一条横向走廊铺（保证有可行军轴线）；过河处即桥
    final roadR = (hgt / 2).round();
    for (var q = 0; q < w; q++) {
      final c = cells[hexKey(q, roadR)]!;
      cells[hexKey(q, roadR)] = Cell(
        terrain: c.terrain, elev: c.elev, slope: c.slope,
        road: true, river: c.river, bridge: c.river, city: c.city,
      );
    }
    return cells;
  }

  HexGeo _buildGeo(Map<String, dynamic> merged, Map<HexKey, Cell> cells) {
    final env = _mm(merged['environment'] ?? {});
    final cellKm = (env['cell_km'] as num?)?.toDouble() ?? 5.0;
    double ox = 4500000, oy = 3200000;
    if (env['mercator'] is List) {
      ox = (env['mercator'][0] as num).toDouble();
      oy = (env['mercator'][1] as num).toDouble();
    } else if (env['center_lonlat'] is List) {
      final p = lonLatToMercator(
        (env['center_lonlat'][0] as num).toDouble(),
        (env['center_lonlat'][1] as num).toDouble(),
      );
      ox = p[0];
      oy = p[1];
    }
    return HexGeo(ox: ox, oy: oy, radius: cellKm * 1000 / math.sqrt(3));
  }

  int _span(Map<HexKey, Cell> cells, bool onQ) {
    var lo = 1 << 30, hi = -(1 << 30);
    for (final k in cells.keys) {
      final h = Hex.parse(k);
      final v = onQ ? h.q : h.r;
      if (v < lo) lo = v;
      if (v > hi) hi = v;
    }
    return hi - lo + 1;
  }

  void _placeOob(GameState g, Map<String, dynamic> merged, Library library) {
    final sides = _mm(merged['sides'] ?? {});
    var counter = 0;
    final taken = <String>{};
    final prefix = {'red': 'R', 'blue': 'B', 'neutral': 'N'};
    sides.forEach((sideKey, sideVal) {
      final cfg = _mm(sideVal);
      final oob = _ml(cfg['oob']);
      for (final entry in oob) {
        final t = library.types[entry['type']];
        if (t == null) continue;
        final qty = (entry['qty'] as num?)?.toInt() ?? 1;
        final ratio = (entry['personnel_ratio'] as num?)?.toDouble() ?? 1.0;
        final at = (entry['at'] as List?) ?? const [0, 0];
        for (var i = 0; i < qty; i++) {
          counter++;
          final q = (at[0] as num).toInt() + (i % 3);
          final r = (at[1] as num).toInt() + (i ~/ 3) % 4;
          final pos = _spawnAt(g, library, Hex(q, r), taken);
          taken.add(pos.key);
          final u = Unit(
            id: '${prefix[sideKey] ?? 'X'}$counter',
            side: sideKey,
            typeId: t.id,
            pos: pos,
            personnel: (t.personnel * ratio).round(),
            morale: (entry['morale'] as num?)?.toDouble() ?? 70,
            training: (entry['training'] as num?)?.toDouble() ?? t.trainingDefault,
            experience: (entry['experience'] as num?)?.toDouble() ?? t.expDefault,
            fatigue: (entry['fatigue'] as num?)?.toDouble() ?? 0,
            ammo: (entry['ammo'] as num?)?.toDouble() ?? 1,
            fuel: (entry['fuel'] as num?)?.toDouble() ?? 1,
            food: (entry['food'] as num?)?.toDouble() ?? 1,
            fortLevel: (entry['fort'] as num?)?.toInt() ?? 0,
            isHQ: t.isHQ || (entry['hq'] as bool?) == true,
            commanderName: entry['commander'] is Map ? (entry['commander']['name'] as String?) : (entry['commander'] as String?),
            commanderQuality: entry['commander'] is Map
                ? ((entry['commander']['quality'] as num?)?.toDouble() ?? 0.5)
                : 0.5,
          );
          if (t.isAir) u.mission = 'none';
          g.units.add(u);
        }
      }
    });
    // 同格堆叠：结构/机场等允许共存；其余保持同格（原型简化：不做堆叠惩罚）
  }

  /// 剧本坐标是按理想地图写的，换成真实 DEM 格网后可能落在河湖/境外格 ⇒ 就近找可通行格
  Hex _spawnAt(GameState g, Library lib, Hex want, Set<String> taken) {
    bool ok(Hex h) {
      final c = g.cells[h.key];
      if (c == null) return false;
      final t = lib.terrains[c.terrain];
      if (t == null || t.impassable || t.water) return false;
      return !taken.contains(h.key);
    }

    if (ok(want)) return want;
    for (final h in hexRange(want, 4)) {
      if (h != want && ok(h)) return h;
    }
    return want;
  }
}

/// 轻量确定性值噪声（可复现，用于程序化地形）
double _valueNoise(int x, int y, int seed) {
  double v(int ix, int iy) {
    var h = (ix * 374761393 + iy * 668265263 + seed * 2147483647) & 0xFFFFFFFF;
    h = (h ^ (h >> 13)) & 0xFFFFFFFF;
    h = (h * 1274126177) & 0xFFFFFFFF;
    return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216.0;
  }

  final xf = x / 4.0, yf = y / 4.0;
  final x0 = xf.floor(), y0 = yf.floor();
  final tx = xf - x0, ty = yf - y0;
  double sx(double t) => t * t * (3 - 2 * t);
  final a = v(x0, y0), b = v(x0 + 1, y0), c = v(x0, y0 + 1), d = v(x0 + 1, y0 + 1);
  final u = sx(tx), w = sx(ty);
  return (a * (1 - u) + b * u) * (1 - w) + (c * (1 - u) + d * u) * w;
}
