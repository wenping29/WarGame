// 数据驱动定义：地形、兵种、规则系数。全部可由 JSON 覆盖 → 自定义 OOB 的前提。
import 'hex.dart' show clampd;

Map<String, dynamic> _m(dynamic v) => v is Map<String, dynamic> ? v : Map<String, dynamic>.from(v as Map);
List<dynamic> _l(dynamic v) => v is List ? v : <dynamic>[];

double _d(Map j, String k, double def) => (j[k] as num?)?.toDouble() ?? def;
int _i(Map j, String k, int def) => (j[k] as num?)?.toInt() ?? def;
String _s(Map j, String k, String def) => (j[k] as String?) ?? def;
bool _b(Map j, String k, bool def) => (j[k] as bool?) ?? def;

/// 机动类别：徒步 / 马匹 / 轮式 / 履带
const MOBILITY_CLASSES = ['foot', 'horse', 'motor', 'mech'];

class TerrainDef {
  final String id, label;
  final Map<String, double> cost; // 机动类别 → MP 倍率
  final double defense;           // 防御系数
  final double obsPenalty;        // 目标在此地形时的侦察衰减（1=无衰减）
  final double losBlock;          // 通视阻挡强度（0 不阻挡，1 完全阻挡）
  final int maxFort;              // 工事等级上限
  final bool water;
  final bool impassable;
  TerrainDef({
    required this.id,
    required this.label,
    required this.cost,
    required this.defense,
    required this.obsPenalty,
    required this.losBlock,
    required this.maxFort,
    required this.water,
    required this.impassable,
  });

  static TerrainDef fromJson(Map<String, dynamic> j) {
    final cost = <String, double>{};
    final jc = _m(j['cost'] ?? {});
    for (final c in MOBILITY_CLASSES) {
      cost[c] = _d(jc, c, 1.0);
    }
    return TerrainDef(
      id: _s(j, 'id', 'clear'),
      label: _s(j, 'label', '开阔地'),
      cost: cost,
      defense: _d(j, 'defense', 1.0),
      obsPenalty: _d(j, 'obs_penalty', 1.0),
      losBlock: _d(j, 'los_block', 0.0),
      maxFort: _i(j, 'max_fort', 3),
      water: _b(j, 'water', false),
      impassable: _b(j, 'impassable', false),
    );
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        'label': label,
        'cost': cost,
        'defense': defense,
        'obs_penalty': obsPenalty,
        'los_block': losBlock,
        'max_fort': maxFort,
        'water': water,
        'impassable': impassable,
      };
}

/// 六角格上的地形单元：地形类别 + 真实高程 + 道路/河流/桥梁 + 地物（铁路/电网/机场/隧道）。
class Cell {
  final String terrain; // TerrainDef.id
  final double elev;    // 真实高程（米），无 DEM 时为 0
  final double slope;   // 坡度（度）
  final bool road;
  final bool river;
  final bool bridge;    // 该格有桥（可渡河）
  final int city;       // 城镇等级 0..3
  final bool railway;      // 铁路经过
  final bool railStation;  // 火车站
  final bool tunnel;       // 隧道（限行，重型装备代价翻倍）
  final bool powerline;    // 输电线路
  final bool powerPlant;   // 发电厂（电网枢纽 + 可夺目标）
  final bool substation;   // 变电站（电网枢纽 + 可夺目标）
  final int airport;       // 0 无 / 1 野场 / 2 干线机场（夺下即前进补给节点）
  const Cell({
    required this.terrain,
    this.elev = 0,
    this.slope = 0,
    this.road = false,
    this.river = false,
    this.bridge = false,
    this.city = 0,
    this.railway = false,
    this.railStation = false,
    this.tunnel = false,
    this.powerline = false,
    this.powerPlant = false,
    this.substation = false,
    this.airport = 0,
  });

  Map<String, dynamic> toJson() => {
        'terrain': terrain,
        'elev': elev,
        'slope': slope,
        'road': road,
        'river': river,
        'bridge': bridge,
        'city': city,
        if (railway) 'railway': railway,
        if (railStation) 'rail_station': railStation,
        if (tunnel) 'tunnel': tunnel,
        if (powerline) 'powerline': powerline,
        if (powerPlant) 'power_plant': powerPlant,
        if (substation) 'substation': substation,
        if (airport > 0) 'airport': airport,
      };

  static Cell fromJson(Map<String, dynamic> j) => Cell(
        terrain: _s(j, 'terrain', 'clear'),
        elev: _d(j, 'elev', 0),
        slope: _d(j, 'slope', 0),
        road: _b(j, 'road', false),
        river: _b(j, 'river', false),
        bridge: _b(j, 'bridge', false),
        city: _i(j, 'city', 0),
        railway: _b(j, 'railway', false),
        railStation: _b(j, 'rail_station', false),
        tunnel: _b(j, 'tunnel', false),
        powerline: _b(j, 'powerline', false),
        powerPlant: _b(j, 'power_plant', false),
        substation: _b(j, 'substation', false),
        airport: _i(j, 'airport', 0),
      );

  Cell copyWith({String? terrain, bool? bridge, int? city, double? elev}) => Cell(
        terrain: terrain ?? this.terrain,
        elev: elev ?? this.elev,
        slope: slope,
        road: road,
        river: river,
        bridge: bridge ?? this.bridge,
        city: city ?? this.city,
        railway: railway,
        railStation: railStation,
        tunnel: tunnel,
        powerline: powerline,
        powerPlant: powerPlant,
        substation: substation,
        airport: airport,
      );
}

class EquipmentItem {
  final String id, label;
  final int count;
  EquipmentItem(this.id, this.label, this.count);
  Map<String, dynamic> toJson() => {'id': id, 'label': label, 'count': count};
  static EquipmentItem fromJson(Map<String, dynamic> j) =>
      EquipmentItem(_s(j, 'id', ''), _s(j, 'label', ''), _i(j, 'count', 0));
}

/// 兵种/装备模板：自定义 OOB 的最小单元。
class UnitTypeDef {
  final String id, label, cls; // cls: foot|horse|motor|mech|air|structure
  final double mobility;       // 基础 MP
  final double firepower;      // 单位火力基准
  final double protection;     // 防护（减损）
  final double recon;          // 侦察强度（决定观测半径与观测点）
  final double load;           // 装备负荷（越高越慢）
  final double consumption;    // 每回合补给消耗
  final int personnel;         // 编制人数（1 个建制单元）
  final double points;         // 点数预算（每建制单元）
  final String airRole;        // none|fighter|strike|recon
  final int airRange;          // 空中单位作用半径（格）
  final int gunRange;          // 火炮间接火力射程（格），0=直射
  final double antiAir;        // 对空火力强度
  final double trainingDefault;
  final double expDefault;
  final Map<String, double> counters; // 相克：目标兵种 → 系数
  final List<EquipmentItem> equipment; // E3 暴露时展示的装备明细
  final bool isHQ;
  final bool supplySource;

  UnitTypeDef({
    required this.id,
    required this.label,
    required this.cls,
    required this.mobility,
    required this.firepower,
    required this.protection,
    required this.recon,
    required this.load,
    required this.consumption,
    required this.personnel,
    required this.points,
    required this.airRole,
    required this.airRange,
    required this.counters,
    required this.equipment,
    required this.isHQ,
    required this.supplySource,
    this.gunRange = 0,
    this.antiAir = 0,
    this.trainingDefault = 60,
    this.expDefault = 40,
  });

  bool get isAir => cls == 'air';
  bool get isStructure => cls == 'structure';

  static UnitTypeDef fromJson(Map<String, dynamic> j) {
    final counters = <String, double>{};
    _m(j['counters'] ?? {}).forEach((k, v) => counters[k] = (v as num).toDouble());
    return UnitTypeDef(
      id: _s(j, 'id', 'rifle'),
      label: _s(j, 'label', '步兵'),
      cls: _s(j, 'cls', 'foot'),
      mobility: _d(j, 'mobility', 4),
      firepower: _d(j, 'firepower', 10),
      protection: _d(j, 'protection', 1),
      recon: _d(j, 'recon', 2),
      load: _d(j, 'load', 1),
      consumption: _d(j, 'consumption', 1),
      personnel: _i(j, 'personnel', 500),
      points: _d(j, 'points', 10),
      airRole: _s(j, 'air_role', 'none'),
      airRange: _i(j, 'air_range', 0),
      gunRange: _i(j, 'range', 0),
      antiAir: _d(j, 'anti_air', 0),
      trainingDefault: _d(j, 'training', 60),
      expDefault: _d(j, 'experience', 40),
      counters: counters,
      equipment: _l(j['equipment']).map((e) => EquipmentItem.fromJson(_m(e))).toList(),
      isHQ: _b(j, 'is_hq', false),
      supplySource: _b(j, 'supply_source', false),
    );
  }

  Map<String, dynamic> toJson() => {
        'id': id, 'label': label, 'cls': cls, 'mobility': mobility,
        'firepower': firepower, 'protection': protection, 'recon': recon,
        'load': load, 'consumption': consumption, 'personnel': personnel,
        'points': points, 'air_role': airRole, 'air_range': airRange,
        'range': gunRange, 'anti_air': antiAir,
        'training': trainingDefault, 'experience': expDefault,
        'counters': counters, 'equipment': equipment.map((e) => e.toJson()).toList(),
        'is_hq': isHQ, 'supply_source': supplySource,
      };

  /// 负荷惩罚：装备越重越慢
  double mobilityPenalty() => clampd(1.0 - 0.06 * (load - 1.0), 0.6, 1.0);
}

/// 规则参数库：全部外置可调（调参不改码）。
class Rules {
  double mpBase;
  int turnHours;
  double supplyRetention;  // R = R0 * retention^dist
  double supplyCrit;        // 低于此值判定断供惩罚
  double ammoPerAttack;
  double foodPerTurn;
  double fatigueMarch;
  double routMorale;        // 士气低于此值溃退
  double routRecover;       // 恢复到该值重新可用
  double losElevStep;       // 通视：每格高差门槛（米）
  double fortBonus;         // 每级工事防御加成
  double airGroundPenalty;  // 敌方掌握制空权时对地惩罚
  double commanderRadius;
  double noiseAmp;
  int exposureE1, exposureE2, exposureE3; // 观测点阈值
  int staleTurns;            // 记忆保留回合数
  Map<String, dynamic> raw;

  Rules({
    this.mpBase = 1.0,
    this.turnHours = 6,
    this.supplyRetention = 0.85,
    this.supplyCrit = 0.4,
    this.ammoPerAttack = 0.12,
    this.foodPerTurn = 0.05,
    this.fatigueMarch = 2.0,
    this.routMorale = 25,
    this.routRecover = 45,
    this.losElevStep = 60,
    this.fortBonus = 0.15,
    this.airGroundPenalty = 0.75,
    this.commanderRadius = 8,
    this.noiseAmp = 0.15,
    this.exposureE1 = 1,
    this.exposureE2 = 3,
    this.exposureE3 = 6,
    this.staleTurns = 4,
    Map<String, dynamic>? raw,
  }) : raw = raw ?? {};

  static Rules fromJson(Map<String, dynamic> j) => Rules(
        mpBase: _d(j, 'mp_base', 1.0),
        turnHours: _i(j, 'turn_hours', 6),
        supplyRetention: _d(j, 'supply_retention', 0.85),
        supplyCrit: _d(j, 'supply_crit', 0.4),
        ammoPerAttack: _d(j, 'ammo_per_attack', 0.12),
        foodPerTurn: _d(j, 'food_per_turn', 0.05),
        fatigueMarch: _d(j, 'fatigue_march', 2.0),
        routMorale: _d(j, 'rout_morale', 25),
        routRecover: _d(j, 'rout_recover', 45),
        losElevStep: _d(j, 'los_elev_step', 60),
        fortBonus: _d(j, 'fort_bonus', 0.15),
        airGroundPenalty: _d(j, 'air_ground_penalty', 0.75),
        commanderRadius: _d(j, 'commander_radius', 8),
        noiseAmp: _d(j, 'noise_amp', 0.15),
        exposureE1: _i(j, 'exposure_e1', 1),
        exposureE2: _i(j, 'exposure_e2', 3),
        exposureE3: _i(j, 'exposure_e3', 6),
        staleTurns: _i(j, 'stale_turns', 4),
        raw: j,
      );

  Map<String, dynamic> toJson() => Map<String, dynamic>.from(raw.isEmpty
      ? {
          'mp_base': mpBase, 'turn_hours': turnHours, 'supply_retention': supplyRetention,
          'supply_crit': supplyCrit, 'ammo_per_attack': ammoPerAttack, 'food_per_turn': foodPerTurn,
          'fatigue_march': fatigueMarch, 'rout_morale': routMorale, 'rout_recover': routRecover,
          'los_elev_step': losElevStep, 'fort_bonus': fortBonus, 'air_ground_penalty': airGroundPenalty,
          'commander_radius': commanderRadius, 'noise_amp': noiseAmp,
          'exposure_e1': exposureE1, 'exposure_e2': exposureE2, 'exposure_e3': exposureE3,
          'stale_turns': staleTurns,
        }
      : raw);

  Rules mergedWith(Map<String, dynamic> override) {
    final j = toJson();
    j.addAll(override);
    return Rules.fromJson(j);
  }
}

/// 天气定义：对机动/侦察/战斗/补给/损耗的乘子。
class WeatherDef {
  final String id, label;
  final double mobility;
  final double recon;
  final double combat;
  final double attrition;
  final double supply;
  final int visibility; // 用于日志展示
  WeatherDef({
    required this.id, required this.label, required this.mobility,
    required this.recon, required this.combat, required this.attrition,
    required this.supply, required this.visibility,
  });

  static WeatherDef fromJson(Map<String, dynamic> j) => WeatherDef(
        id: _s(j, 'id', 'clear'),
        label: _s(j, 'label', '晴'),
        mobility: _d(j, 'mobility', 1.0),
        recon: _d(j, 'recon', 1.0),
        combat: _d(j, 'combat', 1.0),
        attrition: _d(j, 'attrition', 1.0),
        supply: _d(j, 'supply', 1.0),
        visibility: _i(j, 'visibility', 10),
      );

  Map<String, dynamic> toJson() => {
        'id': id, 'label': label, 'mobility': mobility, 'recon': recon,
        'combat': combat, 'attrition': attrition, 'supply': supply, 'visibility': visibility,
      };
}

/// 昼夜：影响侦察与交战。
class DayPhase {
  final String id, label;
  final double recon;
  final double combat;
  DayPhase(this.id, this.label, this.recon, this.combat);

  static DayPhase fromHours(int hourOfDay) {
    final h = hourOfDay % 24;
    if (h >= 6 && h < 9) return DayPhase('dawn', '拂晓', 0.8, 0.95);
    if (h >= 9 && h < 17) return DayPhase('day', '昼间', 1.0, 1.0);
    if (h >= 17 && h < 20) return DayPhase('dusk', '黄昏', 0.8, 0.95);
    return DayPhase('night', '夜间', 0.45, 0.75);
  }

  Map<String, dynamic> toJson() => {'id': id, 'label': label, 'recon': recon, 'combat': combat};
}
