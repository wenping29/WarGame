// 出厂标准定义库（地形 / 兵种 / 天气 / 规则）。剧本与副本可在此之上做覆盖层。
import 'defs.dart';

/// 标准地形库
const STANDARD_TERRAIN_JSON = [
  {'id': 'clear', 'label': '开阔地', 'cost': {'foot': 1.0, 'horse': 0.8, 'motor': 1.4, 'mech': 1.2}, 'defense': 1.0, 'obs_penalty': 1.0, 'los_block': 0.0, 'max_fort': 2},
  {'id': 'rough', 'label': '崎岖地', 'cost': {'foot': 1.3, 'horse': 1.6, 'motor': 2.4, 'mech': 1.8}, 'defense': 1.15, 'obs_penalty': 0.85, 'los_block': 0.25, 'max_fort': 3},
  {'id': 'woods', 'label': '林地', 'cost': {'foot': 1.5, 'horse': 2.0, 'motor': 2.6, 'mech': 2.2}, 'defense': 1.35, 'obs_penalty': 0.55, 'los_block': 0.7, 'max_fort': 3},
  {'id': 'hills', 'label': '丘陵', 'cost': {'foot': 1.6, 'horse': 1.8, 'motor': 2.8, 'mech': 2.2}, 'defense': 1.3, 'obs_penalty': 0.9, 'los_block': 0.35, 'max_fort': 3},
  {'id': 'mountain', 'label': '山地', 'cost': {'foot': 2.2, 'horse': 2.6, 'motor': 4.0, 'mech': 3.2}, 'defense': 1.55, 'obs_penalty': 0.8, 'los_block': 0.75, 'max_fort': 4, 'impassable_motor': true},
  {'id': 'city', 'label': '城镇', 'cost': {'foot': 1.4, 'horse': 1.8, 'motor': 2.0, 'mech': 1.9}, 'defense': 1.7, 'obs_penalty': 0.7, 'los_block': 0.55, 'max_fort': 4},
  {'id': 'swamp', 'label': '沼泽', 'cost': {'foot': 2.0, 'horse': 2.6, 'motor': 3.6, 'mech': 3.0}, 'defense': 0.9, 'obs_penalty': 0.75, 'los_block': 0.4, 'max_fort': 1},
  {'id': 'river', 'label': '河流', 'cost': {'foot': 2.6, 'horse': 3.0, 'motor': 5.0, 'mech': 4.2}, 'defense': 0.8, 'obs_penalty': 1.0, 'los_block': 0.0, 'max_fort': 0, 'water': true},
  {'id': 'water', 'label': '江湖', 'cost': {'foot': 9.0, 'horse': 9.0, 'motor': 9.0, 'mech': 9.0}, 'defense': 0.5, 'obs_penalty': 1.0, 'los_block': 0.0, 'max_fort': 0, 'water': true, 'impassable': true},
  {'id': 'void', 'label': '境外', 'cost': {}, 'defense': 1.0, 'obs_penalty': 1.0, 'los_block': 0.0, 'max_fort': 0, 'impassable': true},
];

/// 标准兵种库。points = 每建制单元点数预算价。
const STANDARD_UNIT_TYPES_JSON = [
  {'id': 'rifle', 'label': '步兵', 'cls': 'foot', 'mobility': 5, 'firepower': 10, 'protection': 1.0, 'recon': 2, 'load': 1.0, 'consumption': 1.0, 'personnel': 600, 'points': 10, 'equipment': [{'id': 'rifle', 'label': '步枪', 'count': 520}, {'id': 'mg', 'label': '机枪', 'count': 18}, {'id': 'mortar', 'label': '迫击炮', 'count': 6}]},
  {'id': 'light_inf', 'label': '轻步兵', 'cls': 'foot', 'mobility': 6, 'firepower': 8, 'protection': 0.85, 'recon': 3, 'load': 0.7, 'consumption': 0.8, 'personnel': 450, 'points': 8, 'counters': {'armor': 0.6}},
  {'id': 'rifle_auto', 'label': '汽车步兵', 'cls': 'motor', 'mobility': 9, 'firepower': 11, 'protection': 0.9, 'recon': 2, 'load': 1.4, 'consumption': 1.6, 'personnel': 650, 'points': 13, 'equipment': [{'id': 'truck', 'label': '载重卡车', 'count': 60}]},
  {'id': 'mec_inf', 'label': '机械化步兵', 'cls': 'mech', 'mobility': 10, 'firepower': 14, 'protection': 1.35, 'recon': 2.5, 'load': 1.8, 'consumption': 2.0, 'personnel': 600, 'points': 19, 'equipment': [{'id': 'apc', 'label': '装甲输送车', 'count': 70}]},
  {'id': 'armor', 'label': '装甲侦察', 'cls': 'mech', 'mobility': 12, 'firepower': 9, 'protection': 1.2, 'recon': 5, 'load': 1.5, 'consumption': 2.2, 'personnel': 260, 'points': 14, 'equipment': [{'id': 'armcar', 'label': '装甲侦察车', 'count': 30}]},
  {'id': 'tank', 'label': '坦克', 'cls': 'mech', 'mobility': 11, 'firepower': 24, 'protection': 2.4, 'recon': 3, 'load': 2.6, 'consumption': 3.0, 'personnel': 220, 'points': 26, 'counters': {'rifle': 2.2, 'light_inf': 2.0, 'tank': 0.9}, 'equipment': [{'id': 'tank', 'label': '主战坦克', 'count': 22}]},
  {'id': 'gun', 'label': '反坦克炮', 'cls': 'motor', 'mobility': 7, 'firepower': 18, 'protection': 0.9, 'recon': 2, 'load': 1.9, 'consumption': 1.6, 'personnel': 180, 'points': 13, 'counters': {'tank': 2.0, 'armor': 1.8}, 'equipment': [{'id': 'atgun', 'label': '反坦克炮', 'count': 12}]},
  {'id': 'arty', 'label': '炮兵', 'cls': 'motor', 'mobility': 6, 'firepower': 30, 'protection': 0.7, 'recon': 1.5, 'load': 2.8, 'consumption': 2.4, 'personnel': 240, 'points': 20, 'range': 3, 'equipment': [{'id': 'howitzer', 'label': '榴弹炮', 'count': 12}]},
  {'id': 'engineer', 'label': '工兵', 'cls': 'foot', 'mobility': 4, 'firepower': 6, 'protection': 1.0, 'recon': 2, 'load': 1.6, 'consumption': 1.0, 'personnel': 200, 'points': 9},
  {'id': 'signals', 'label': '通讯兵', 'cls': 'motor', 'mobility': 8, 'firepower': 2, 'protection': 0.6, 'recon': 4, 'load': 1.2, 'consumption': 1.0, 'personnel': 90, 'points': 7},
  {'id': 'aa', 'label': '防空', 'cls': 'motor', 'mobility': 8, 'firepower': 8, 'protection': 0.8, 'recon': 3, 'load': 2.0, 'consumption': 1.8, 'personnel': 150, 'points': 12, 'anti_air': 3.0},
  {'id': 'recy', 'label': '辎重', 'cls': 'motor', 'mobility': 6, 'firepower': 1, 'protection': 0.5, 'recon': 1, 'load': 3.2, 'consumption': 0.4, 'personnel': 120, 'points': 8, 'supply_source': true},
  {'id': 'hq', 'label': '指挥所', 'cls': 'foot', 'mobility': 4, 'firepower': 3, 'protection': 0.8, 'recon': 2, 'load': 1.0, 'consumption': 0.6, 'personnel': 80, 'points': 6, 'is_hq': true},
  {'id': 'radar', 'label': '雷达站', 'cls': 'structure', 'mobility': 0, 'firepower': 0, 'protection': 0.4, 'recon': 9, 'load': 1.0, 'consumption': 0.5, 'personnel': 40, 'points': 9},
  {'id': 'airbase', 'label': '机场', 'cls': 'structure', 'mobility': 0, 'firepower': 2, 'protection': 0.6, 'recon': 3, 'load': 1.0, 'consumption': 1.0, 'personnel': 150, 'points': 10},
  {'id': 'fighter', 'label': '战斗机', 'cls': 'air', 'mobility': 30, 'firepower': 16, 'protection': 0.8, 'recon': 8, 'load': 1.0, 'consumption': 2.0, 'personnel': 4, 'points': 22, 'air_role': 'fighter', 'air_range': 10},
  {'id': 'strike_air', 'label': '强击机', 'cls': 'air', 'mobility': 26, 'firepower': 26, 'protection': 0.7, 'recon': 5, 'load': 1.2, 'consumption': 2.4, 'personnel': 4, 'points': 24, 'air_role': 'strike', 'air_range': 8},
  {'id': 'recon_air', 'label': '侦察机', 'cls': 'air', 'mobility': 28, 'firepower': 1, 'protection': 0.5, 'recon': 14, 'load': 0.8, 'consumption': 1.6, 'personnel': 3, 'points': 16, 'air_role': 'recon', 'air_range': 12},
];

/// 标准天气库
const STANDARD_WEATHER_JSON = [
  {'id': 'clear', 'label': '晴', 'mobility': 1.0, 'recon': 1.05, 'combat': 1.0, 'attrition': 1.0, 'supply': 1.0, 'visibility': 12},
  {'id': 'overcast', 'label': '多云', 'mobility': 1.0, 'recon': 1.0, 'combat': 1.0, 'attrition': 1.0, 'supply': 1.0, 'visibility': 10},
  {'id': 'rain', 'label': '降雨', 'mobility': 0.85, 'recon': 0.8, 'combat': 0.92, 'attrition': 1.15, 'supply': 0.85, 'visibility': 6},
  {'id': 'storm', 'label': '暴雨', 'mobility': 0.65, 'recon': 0.6, 'combat': 0.8, 'attrition': 1.4, 'supply': 0.6, 'visibility': 3},
  {'id': 'snow', 'label': '降雪', 'mobility': 0.6, 'recon': 0.75, 'combat': 0.85, 'attrition': 1.6, 'supply': 0.7, 'visibility': 4},
  {'id': 'fog', 'label': '浓雾', 'mobility': 0.9, 'recon': 0.35, 'combat': 0.75, 'attrition': 1.0, 'supply': 0.95, 'visibility': 1},
  {'id': 'sandstorm', 'label': '沙尘', 'mobility': 0.8, 'recon': 0.5, 'combat': 0.9, 'attrition': 1.2, 'supply': 0.9, 'visibility': 2},
];

/// 兵种属性 → 点数权重（相对基准值的偏离度）。
/// 战力与防护最贵，机动次之；负荷与消耗是"负担"，调高反而便宜——
/// 因为它们在结算里真的会扣 MP 和补给，不该按涨价处理。
const TYPE_PRICE_WEIGHTS = {
  'firepower': 0.55,
  'protection': 0.45,
  'mobility': 0.35,
  'recon': 0.25,
  'personnel': 0.30,
  'load': -0.12,
  'consumption': -0.18,
};
/// 射程类是整数格，按加数量计（每格 / 每点强度）
const TYPE_PRICE_ADDITIVE = {'range': 0.08, 'anti_air': 0.06};
const TYPE_PRICE_MIN = 0.35;
const TYPE_PRICE_MAX = 2.6;

/// 属性覆盖的自动计价：补丁没显式给 points 就按偏离度收钱，
/// 否则"把坦克火力从 24 调到 48"就能绕过点数预算。
double pricedPoints(UnitTypeDef base, Map<String, dynamic> patch) {
  final cur = base.toJson();
  var mult = 1.0;
  TYPE_PRICE_WEIGHTS.forEach((key, w) {
    final raw = patch[key];
    if (raw is! num) return;
    final b = (cur[key] as num?)?.toDouble() ?? 0.0;
    final denom = b.abs() < 0.5 ? 1.0 : b.abs(); // 基准过小时用统一标尺，避免 0.05→0.1 记成 +100%
    mult += w * ((raw.toDouble() - b) / denom);
  });
  TYPE_PRICE_ADDITIVE.forEach((key, w) {
    final raw = patch[key];
    if (raw is! num) return;
    final b = (cur[key] as num?)?.toDouble() ?? 0.0;
    mult += w * (raw.toDouble() - b);
  });
  final k = mult.clamp(TYPE_PRICE_MIN, TYPE_PRICE_MAX);
  return double.parse((base.points * k).toStringAsFixed(1));
}

class Library {
  final Map<String, TerrainDef> terrains;
  final Map<String, UnitTypeDef> types;
  final Map<String, WeatherDef> weather;
  final Rules baseRules;

  Library({required this.terrains, required this.types, required this.weather, required this.baseRules});

  factory Library.standard() {
    return Library(
      terrains: {for (final t in STANDARD_TERRAIN_JSON) t['id'] as String: TerrainDef.fromJson(t)},
      types: {for (final t in STANDARD_UNIT_TYPES_JSON) t['id'] as String: UnitTypeDef.fromJson(t)},
      weather: {for (final w in STANDARD_WEATHER_JSON) w['id'] as String: WeatherDef.fromJson(w)},
      baseRules: Rules(),
    );
  }

  /// 应用剧本/副本的覆盖层：新增或替换定义。返回新库（不改自身）。
  Library withOverrides({
    List<Map<String, dynamic>>? terrain,
    List<Map<String, dynamic>>? types,
    List<Map<String, dynamic>>? weather,
  }) {
    final t = Map<String, TerrainDef>.from(terrains);
    final u = Map<String, UnitTypeDef>.from(this.types);
    final w = Map<String, WeatherDef>.from(weatherDefs);
    for (final e in terrain ?? const []) {
      final d = TerrainDef.fromJson(e);
      t[d.id] = d;
    }
    for (final e in types ?? const []) {
      final base = u[e['id']];
      final merged = Map<String, dynamic>.from(base?.toJson() ?? {});
      merged.addAll(e);
      // 补丁未显式标价 ⇒ 按属性偏离度自动计价（存档快照带 points，不会被二次计价）
      if (base != null && e['points'] == null) merged['points'] = pricedPoints(base, e);
      final d = UnitTypeDef.fromJson(merged);
      u[d.id] = d;
    }
    for (final e in weather ?? const []) {
      final d = WeatherDef.fromJson(e);
      w[d.id] = d;
    }
    return Library(terrains: t, types: u, weather: w, baseRules: baseRules);
  }

  Map<String, WeatherDef> get weatherDefs => weather;

  TerrainDef terrain(String id) => terrains[id] ?? terrains['void']!;
  UnitTypeDef type(String id) => types[id] ?? types['rifle']!;
  WeatherDef wx(String id) => weather[id] ?? weather['clear']!;

  /// 定义快照：让副本存档自包含（载入不依赖外部库版本）
  Map<String, dynamic> toJson() => {
        'terrain': terrains.values.map((t) => t.toJson()).toList(),
        'unit_types': types.values.map((t) => t.toJson()).toList(),
        'weather': weather.values.map((w) => w.toJson()).toList(),
      };

  static Library fromJson(Map<String, dynamic> j) {
    final std = Library.standard();
    return std.withOverrides(
      terrain: (j['terrain'] as List? ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList(),
      types: (j['unit_types'] as List? ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList(),
      weather: (j['weather'] as List? ?? []).map((e) => Map<String, dynamic>.from(e as Map)).toList(),
    );
  }

  /// 阵营点数合计：用于校验自定义 OOB 是否超预算
  double oobPoints(List<Map<String, dynamic>> oob) {
    var total = 0.0;
    for (final e in oob) {
      final t = types[e['type']];
      final qty = (e['qty'] as num?)?.toInt() ?? 1;
      if (t == null) continue;
      var p = t.points * qty;
      // 人员水平溢价：训练/经验高于基准 60/40 时按点数收附加费
      final tr = (e['training'] as num?)?.toDouble() ?? t.trainingDefault;
      final ex = (e['experience'] as num?)?.toDouble() ?? t.expDefault;
      p += qty * ((tr - 60).clamp(-30, 40) + (ex - 40).clamp(-30, 40)) * 0.06;
      total += p;
    }
    return total;
  }
}
