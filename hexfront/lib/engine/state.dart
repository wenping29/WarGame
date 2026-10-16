// 战场状态：单位、地形、迷雾记忆、事件流。含 JSON 编解码 → 支撑副本存档。
import 'defs.dart';
import 'hex.dart';
import 'library.dart';
import 'rng.dart';

Map<String, dynamic> _mj(dynamic v) => Map<String, dynamic>.from(v as Map);
List<dynamic> _jl(dynamic v) => v is List ? v : <dynamic>[];

const UNIT_SIDES = ['red', 'blue', 'neutral'];

/// 单位状态
class UnitStatus {
  static const ready = 'ready';
  static const moved = 'moved';
  static const combat = 'combat';
  static const routed = 'routed';
  static const destroyed = 'destroyed';
}

class Unit {
  String id;
  String side;
  String typeId;
  Hex pos;
  int personnel;
  double morale;      // 0..100
  double fatigue;     // 0..100
  double training;    // 0..100 人员训练度
  double experience;  // 0..100 经验
  double ammo;        // 0..1 相对基数
  double fuel;        // 0..1
  double food;        // 0..1
  String status;
  int fortLevel;
  double supplyRate;  // 0..1 由后勤结算
  int mpLeft;         // 本回合已用 MP（结算期临时量）
  bool airborne;      // 空中单位在执行任务
  String mission;     // none|patrol|strike|recon
  Hex? missionZone;   // 空中任务区
  String? commanderName;
  double commanderQuality; // 0..1
  bool isHQ;
  int casualties;     // 累计损失（战报用）

  Unit({
    required this.id,
    required this.side,
    required this.typeId,
    required this.pos,
    this.personnel = 500,
    this.morale = 70,
    this.fatigue = 0,
    this.training = 60,
    this.experience = 40,
    this.ammo = 1,
    this.fuel = 1,
    this.food = 1,
    this.status = UnitStatus.ready,
    this.fortLevel = 0,
    this.supplyRate = 1,
    this.mpLeft = 0,
    this.airborne = false,
    this.mission = 'none',
    this.missionZone,
    this.commanderName,
    this.commanderQuality = 0.5,
    this.isHQ = false,
    this.casualties = 0,
  });

  bool get alive => status != UnitStatus.destroyed && personnel > 0;

  Unit clone() => Unit(
        id: id, side: side, typeId: typeId, pos: pos, personnel: personnel,
        morale: morale, fatigue: fatigue, training: training, experience: experience,
        ammo: ammo, fuel: fuel, food: food, status: status, fortLevel: fortLevel,
        supplyRate: supplyRate, mpLeft: mpLeft, airborne: airborne, mission: mission,
        missionZone: missionZone, commanderName: commanderName,
        commanderQuality: commanderQuality, isHQ: isHQ, casualties: casualties,
      );

  Map<String, dynamic> toJson() => {
        'id': id, 'side': side, 'type': typeId, 'q': pos.q, 'r': pos.r,
        'personnel': personnel, 'morale': morale, 'fatigue': fatigue,
        'training': training, 'experience': experience,
        'ammo': ammo, 'fuel': fuel, 'food': food,
        'status': status, 'fort': fortLevel, 'supply': supplyRate,
        'airborne': airborne, 'mission': mission,
        'zone': missionZone == null ? null : [missionZone!.q, missionZone!.r],
        'commander': commanderName, 'cq': commanderQuality, 'hq': isHQ,
        'casualties': casualties,
      };

  static Unit fromJson(Map<String, dynamic> j) => Unit(
        id: j['id'] as String,
        side: j['side'] as String,
        typeId: j['type'] as String,
        pos: Hex((j['q'] as num).toInt(), (j['r'] as num).toInt()),
        personnel: (j['personnel'] as num?)?.toInt() ?? 500,
        morale: (j['morale'] as num?)?.toDouble() ?? 70,
        fatigue: (j['fatigue'] as num?)?.toDouble() ?? 0,
        training: (j['training'] as num?)?.toDouble() ?? 60,
        experience: (j['experience'] as num?)?.toDouble() ?? 40,
        ammo: (j['ammo'] as num?)?.toDouble() ?? 1,
        fuel: (j['fuel'] as num?)?.toDouble() ?? 1,
        food: (j['food'] as num?)?.toDouble() ?? 1,
        status: (j['status'] as String?) ?? UnitStatus.ready,
        fortLevel: (j['fort'] as num?)?.toInt() ?? 0,
        supplyRate: (j['supply'] as num?)?.toDouble() ?? 1,
        airborne: (j['airborne'] as bool?) ?? false,
        mission: (j['mission'] as String?) ?? 'none',
        missionZone: j['zone'] == null ? null : Hex((j['zone'] as List)[0] as int, (j['zone'] as List)[1] as int),
        commanderName: j['commander'] as String?,
        commanderQuality: (j['cq'] as num?)?.toDouble() ?? 0.5,
        isHQ: (j['hq'] as bool?) ?? false,
        casualties: (j['casualties'] as num?)?.toInt() ?? 0,
      );
}

/// 迷雾记忆：某方对某敌方单位的已知程度。
class Track {
  String unitId;
  int level;        // 0..3 暴露等级
  Hex lastSeen;
  int lastSeenTurn;
  Track(this.unitId, this.level, this.lastSeen, this.lastSeenTurn);

  Map<String, dynamic> toJson() => {
        'unit': unitId, 'level': level,
        'seen': [lastSeen.q, lastSeen.r], 'turn': lastSeenTurn,
      };
  static Track fromJson(Map<String, dynamic> j) => Track(
        j['unit'] as String,
        (j['level'] as num).toInt(),
        Hex((j['seen'] as List)[0] as int, (j['seen'] as List)[1] as int),
        (j['turn'] as num).toInt(),
      );
}

class WarEvent {
  final int turn;
  final String code;
  final String text;
  final List<String> sides; // 可见阵营（导演部/观战按此过滤）
  WarEvent(this.turn, this.code, this.text, this.sides);

  Map<String, dynamic> toJson() => {'turn': turn, 'code': code, 'text': text, 'sides': sides};
  static WarEvent fromJson(Map<String, dynamic> j) => WarEvent(
        (j['turn'] as num).toInt(), j['code'] as String, j['text'] as String,
        (j['sides'] as List? ?? []).cast<String>(),
      );
}

class VictoryCond {
  final String kind;   // annihilate | capture | survive | reach | nodes
  final String? side;  // 达成方
  final String? target;
  final int amount;
  final int turns;
  VictoryCond(this.kind, {this.side, this.target, this.amount = 0, this.turns = 0});

  Map<String, dynamic> toJson() => {'kind': kind, 'side': side, 'target': target, 'amount': amount, 'turns': turns};
  static VictoryCond fromJson(Map<String, dynamic> j) => VictoryCond(
        j['kind'] as String, side: j['side'] as String?, target: j['target'] as String?,
        amount: (j['amount'] as num?)?.toInt() ?? 0, turns: (j['turns'] as num?)?.toInt() ?? 0,
      );
}

class GameState {
  String instanceId;
  String scenarioId;
  String name;
  final Library lib;
  int turn;
  int hourOfDay;
  String season;
  String weatherId;
  String weatherMode;      // fixed | stochastic | director
  HexGeo geo;
  int width, height;
  Map<HexKey, Cell> cells;
  List<Unit> units;
  Rules rules;
  Map<String, bool> flags; // fog / air_superiority / strict_supply / night
  List<VictoryCond> victory;
  int maxTurns;

  // 迷雾记忆：side → hexKey → 观测点；side → unitId → Track
  Map<String, Map<HexKey, int>> obs = {};
  Map<String, Map<String, Track>> tracks = {};
  Map<String, double> airControl = {}; // hexKey → -1(蓝控)..+1(红控)，0  contested

  List<WarEvent> log = [];
  SeededRNG rng;
  bool over = false;
  String? winner;
  int? endedTurn;

  GameState({
    required this.instanceId,
    required this.scenarioId,
    required this.name,
    required this.lib,
    required this.turn,
    required this.hourOfDay,
    required this.season,
    required this.weatherId,
    required this.weatherMode,
    required this.geo,
    required this.width,
    required this.height,
    required this.cells,
    required this.units,
    required this.rules,
    required this.flags,
    required this.victory,
    required this.maxTurns,
    required this.rng,
  }) {
    for (final s in ['red', 'blue', 'spectator']) {
      obs[s] ??= {};
      tracks[s] ??= {};
    }
  }

  Unit? findUnit(String id) {
    for (final u in units) {
      if (u.id == id) return u;
    }
    return null;
  }

  List<Unit> unitsAt(Hex h) => units.where((u) => u.alive && u.pos == h).toList();
  Iterable<Unit> liveOf(String side) => units.where((u) => u.alive && u.side == side);

  static const Cell voidCell = Cell(terrain: 'void');
  Cell cellAt(Hex h) => cells[h.key] ?? voidCell;
  bool hasCell(Hex h) => cells.containsKey(h.key);
  Set<Hex> hexes() => cells.keys.map(Hex.parse).toSet();

  /// 运行时定义访问：状态自带定义库，存档/回放不依赖外部版本
  UnitTypeDef typeOf(Unit u) => lib.type(u.typeId);
  TerrainDef terrainOf(Hex h) => lib.terrain(cellAt(h).terrain);
  WeatherDef weatherOf() => lib.wx(weatherId);
  Cell cell(HexKey k) => cells[k] ?? voidCell;

  void emit(WarEvent e) {
    log.add(e);
    if (log.length > 400) log.removeRange(0, log.length - 400);
  }

  GameState deepClone() {
    final g = GameState(
      instanceId: instanceId, scenarioId: scenarioId, name: name, lib: lib, turn: turn,
      hourOfDay: hourOfDay, season: season, weatherId: weatherId, weatherMode: weatherMode,
      geo: geo, width: width, height: height,
      cells: Map<HexKey, Cell>.from(cells),
      units: units.map((u) => u.clone()).toList(),
      rules: Rules.fromJson(rules.toJson()),
      flags: Map<String, bool>.from(flags),
      victory: victory.map((v) => VictoryCond.fromJson(v.toJson())).toList(),
      maxTurns: maxTurns,
      rng: SeededRNG(0)..state = rng.state,
    );
    g.over = over;
    g.winner = winner;
    g.endedTurn = endedTurn;
    for (final e in log) g.log.add(e);
    g.obs = obs.map((k, v) => MapEntry(k, Map<HexKey, int>.from(v)));
    g.tracks = tracks.map((k, v) => MapEntry(k, v.map((uk, t) => MapEntry(uk, Track.fromJson(t.toJson())))));
    g.airControl = Map<String, double>.from(airControl);
    return g;
  }

  Map<String, dynamic> toJson() => {
        'instance': instanceId, 'scenario': scenarioId, 'name': name,
        'defs': lib.toJson(),
        'turn': turn, 'hour': hourOfDay, 'season': season,
        'weather': weatherId, 'weather_mode': weatherMode,
        'geo': geo.toJson(), 'width': width, 'height': height,
        'cells': cells.entries.map((e) {
          final h = Hex.parse(e.key);
          final c = e.value.toJson();
          c['q'] = h.q;
          c['r'] = h.r;
          return c;
        }).toList(),
        'units': units.map((u) => u.toJson()).toList(),
        'rules': rules.toJson(),
        'flags': flags,
        'victory': victory.map((v) => v.toJson()).toList(),
        'max_turns': maxTurns,
        'rng_state': rng.state,
        'over': over, 'winner': winner, 'ended_turn': endedTurn,
        'obs': obs.map((k, v) => MapEntry(k, v.map((hk, pv) => MapEntry(hk, pv)))),
        'tracks': tracks.map((k, v) => MapEntry(k, v.values.map((t) => t.toJson()).toList())),
        'air': airControl,
        'log': log.map((e) => e.toJson()).toList(),
      };

  static GameState fromJson(Map<String, dynamic> j, {Library? library}) {
    final lib = library ??
        (j['defs'] is Map ? Library.fromJson(Map<String, dynamic>.from(j['defs'] as Map)) : Library.standard());
    final cells = <HexKey, Cell>{};
    for (final c in _jl(j['cells'])) {
      final m = _mj(c);
      cells[hexKey((m['q'] as num).toInt(), (m['r'] as num).toInt())] = Cell.fromJson(m);
    }
    final g = GameState(
      instanceId: j['instance'] as String,
      scenarioId: j['scenario'] as String,
      name: j['name'] as String,
      lib: lib,
      turn: (j['turn'] as num).toInt(),
      hourOfDay: (j['hour'] as num).toInt(),
      season: j['season'] as String,
      weatherId: j['weather'] as String,
      weatherMode: (j['weather_mode'] as String?) ?? 'fixed',
      geo: HexGeo.fromJson(_mj(j['geo'])),
      width: (j['width'] as num).toInt(),
      height: (j['height'] as num).toInt(),
      cells: cells,
      units: _jl(j['units']).map((u) => Unit.fromJson(_mj(u))).toList(),
      rules: Rules.fromJson(_mj(j['rules'] ?? {})),
      flags: (_mj(j['flags'] ?? {})).map((k, v) => MapEntry(k, v as bool)),
      victory: _jl(j['victory']).map((v) => VictoryCond.fromJson(_mj(v))).toList(),
      maxTurns: (j['max_turns'] as num?)?.toInt() ?? 20,
      rng: SeededRNG(0)..state = (j['rng_state'] as num?)?.toInt() ?? 12345,
    );
    g.over = (j['over'] as bool?) ?? false;
    g.winner = j['winner'] as String?;
    g.endedTurn = (j['ended_turn'] as num?)?.toInt();
    final ob = j['obs'];
    if (ob is Map) {
      ob.forEach((k, v) {
        final m = <HexKey, int>{};
        (v as Map).forEach((hk, pv) => m[hk as String] = (pv as num).toInt());
        g.obs[k as String] = m;
      });
    }
    final tr = j['tracks'];
    if (tr is Map) {
      tr.forEach((k, v) {
        final m = <String, Track>{};
        for (final e in (v as List)) {
          final t = Track.fromJson(_mj(e));
          m[t.unitId] = t;
        }
        g.tracks[k as String] = m;
      });
    }
    final ac = j['air'];
    if (ac is Map) ac.forEach((k, v) => g.airControl[k as String] = (v as num).toDouble());
    for (final e in _jl(j['log'])) g.log.add(WarEvent.fromJson(_mj(e)));
    return g;
  }

  /// 紧凑指纹：确定性对拍 + 存档完整性校验
  String fingerprint() {
    final parts = <String>[
      '$turn', '$hourOfDay', '$weatherId', '${rng.state}',
      units.map((u) => '${u.id}:${u.pos.q},${u.pos.r}:${u.personnel}:${u.status}:${u.fortLevel}').join('|'),
      airControl.entries.map((e) => '${e.key}=${e.value.toStringAsFixed(3)}').join('|'),
    ];
    final s = parts.join('#');
    var h = 2166136261;
    for (final c in s.codeUnits) {
      h = (h ^ c) & 0xFFFFFFFF;
      h = (h * 16777619) & 0xFFFFFFFF;
    }
    return h.toRadixString(16).padLeft(8, '0');
  }
}
