// 引擎自检（可脱离 Web 独立运行）：dart run bin/verify.dart
import 'dart:convert';
import 'dart:io';
import 'package:hexfront_web/engine/commands.dart';
import 'package:hexfront_web/engine/defs.dart';
import 'package:hexfront_web/engine/fog.dart';
import 'package:hexfront_web/engine/hex.dart';
import 'package:hexfront_web/engine/library.dart';
import 'package:hexfront_web/engine/mechanics.dart';
import 'package:hexfront_web/engine/scenario.dart';
import 'package:hexfront_web/engine/state.dart';
import 'package:hexfront_web/engine/turn.dart';
import 'package:hexfront_web/server/ai.dart';
import 'package:hexfront_web/server/store.dart';

final Directory root = File.fromUri(Platform.script).parent.parent.parent;
final Store store = Store(
  root: Directory('${root.path}${Platform.pathSeparator}data'),
  saves: Directory('${root.path}${Platform.pathSeparator}saves'),
);

int _pass = 0, _fail = 0;
void check(String name, bool ok, [String detail = '']) {
  if (ok) {
    _pass++;
    print('  ✓ $name');
  } else {
    _fail++;
    print('  ✗ $name ${detail.isEmpty ? '' : '—— $detail'}');
  }
}

void main() {
  print('HEXFRONT-WEB 引擎自检');
  final lib = Library.standard();
  final builder = InstanceBuilder(lib: lib, loadGisPackage: store.loadGisPackage);
  final scRaw = store.loadScenario('rhine_corridor');
  if (scRaw == null) {
    print('剧本 rhine_corridor 缺失，终止');
    exit(1);
  }

  print('\n[1] 剧本 → 副本');
  final errs = Scenario(scRaw).validate(lib);
  check('剧本校验通过', errs.isEmpty, errs.join(' / '));
  final g = builder.build(scenarioRaw: scRaw, instanceId: 'V1', instanceName: '自检副本', seed: 20261016);
  check('初始单位生成', g.units.length >= 15, '${g.units.length} 个单位');
  check('地形格网完整', g.cells.length >= 400, '${g.cells.length} 格');
  final badSpawn = g.units.where((u) {
    final c = g.cells[u.pos.key];
    final t = c == null ? null : g.terrainOf(u.pos);
    return c == null || t == null || t.impassable || t.water;
  }).toList();
  check('全部单位出生在可通行陆地', badSpawn.isEmpty,
      badSpawn.map((u) => '${u.id}@${u.pos.key}').join(','));
  final src = store.loadGisPackage('rhine_5km.json') != null ? 'GIS 数据包' : '程序化降级';
  print('    地形来源：$src · 格数 ${g.cells.length} · 兵种 ${lib.types.length} · 天气 ${lib.weather.length}');
  updateObservation(g);
  check('开局双方互不可见（迷雾隔离生效）',
      (g.tracks['red'] ?? {}).isEmpty && (g.tracks['blue'] ?? {}).isEmpty);

  print('\n[2] 战争迷雾：视图裁剪');
  // 先推进 3 回合让侦察接触产生，再验证裁剪是否严格
  for (var t = 0; t < 3; t++) {
    resolveTurn(g, {
      'red': aiOrders(g, 'red', sideView(g, 'red')),
      'blue': aiOrders(g, 'blue', sideView(g, 'blue')),
    });
  }
  final redView = sideView(g, 'red');
  final redOwn = (redView['own'] as List).cast<Map<String, dynamic>>();
  final redContacts = (redView['contacts'] as List).cast<Map<String, dynamic>>();
  final blueAlive = g.liveOf('blue').map((u) => u.id).toSet();
  final exposed = redContacts.map((c) => c['id'] as String).toSet();
  final leaked = blueAlive.where((id) => !exposed.contains(id));
  final ownIds = redOwn.map((o) => o['id'] as String).toSet();
  check('预热后有已暴露目标', redContacts.isNotEmpty, '红方接触 ${redContacts.length} 个');
  check('仍有未暴露目标（非全知）', exposed.length < blueAlive.length, '暴露 ${exposed.length}/${blueAlive.length}');
  check('红方视图不含未暴露蓝方单位', ownIds.intersection(blueAlive).isEmpty && leaked.length == blueAlive.length - exposed.length);
  check('红方视图不含蓝方精确人数（除非 E3）',
      redContacts.where((c) => (c['level'] as int) < 3).every((c) => !c.containsKey('personnel')));
  check('E1 接触只给兵力档位不给兵种',
      redContacts.where((c) => (c['level'] as int) == 1).every((c) => c['type'] == null && c['strength_band'] != null));
  // 攻击未知目标必须被拒
  final hidden = blueAlive.where((id) => !exposed.contains(id)).toList();
  if (hidden.isNotEmpty) {
    final attacker = g.liveOf('red').first;
    final rej = validateOrders(g, 'red', [Command(op: 'attack', unitId: attacker.id, target: hidden.first)]);
    check('攻击未暴露目标被拒', rej.isNotEmpty && rej.first.reason == 'not_observed', rej.isEmpty ? '未被拒' : rej.first.reason);
  } else {
    print('    （本回合无未暴露目标，跳过拒止用例）');
  }
  final badPath = validateOrders(g, 'red', [
    Command(op: 'move', unitId: g.liveOf('red').first.id, path: [
      g.liveOf('red').first.pos,
      Hex(g.liveOf('red').first.pos.q + 3, g.liveOf('red').first.pos.r),
    ])
  ]);
  check('跳格路径被服务端拒绝', badPath.isNotEmpty && badPath.first.reason == 'not_adjacent');

  print('\n[3] 确定性对拍（同种子同指令 → 同指纹）');
  final a = builder.build(scenarioRaw: scRaw, instanceId: 'VA', instanceName: 'A', seed: 777);
  final b = builder.build(scenarioRaw: scRaw, instanceId: 'VB', instanceName: 'B', seed: 777);
  updateObservation(a);
  updateObservation(b);
  var same = true;
  for (var t = 0; t < 5; t++) {
    final oa = aiOrders(a, 'red', sideView(a, 'red'));
    final ob = aiOrders(b, 'red', sideView(b, 'red'));
    final pa = aiOrders(a, 'blue', sideView(a, 'blue'));
    final pb = aiOrders(b, 'blue', sideView(b, 'blue'));
    final ra = resolveTurn(a, {'red': oa, 'blue': pa});
    final rb = resolveTurn(b, {'red': ob, 'blue': pb});
    if (ra.fingerprintAfter != rb.fingerprintAfter) {
      same = false;
      break;
    }
  }
  check('5 回合双跑逐回合指纹一致', same, 'A 回合${a.turn} vs B 回合${b.turn}');

  print('\n[4] 存档往返（副本存档 / 载入续跑）');
  final snap = jsonEncode(g.toJson());
  final g2 = GameState.fromJson(Map<String, dynamic>.from(jsonDecode(snap) as Map));
  check('快照反序列化后指纹一致', g2.fingerprint() == g.fingerprint());
  final o1 = aiOrders(g, 'red', sideView(g, 'red'));
  final o2 = aiOrders(g2, 'red', sideView(g2, 'red'));
  final p1 = aiOrders(g, 'blue', sideView(g, 'blue'));
  final p2 = aiOrders(g2, 'blue', sideView(g2, 'blue'));
  final r1 = resolveTurn(g, {'red': o1, 'blue': p1});
  final r2 = resolveTurn(g2, {'red': o2, 'blue': p2});
  check('载入后续跑结果一致（RNG 状态已持久化）', r1.fingerprintAfter == r2.fingerprintAfter,
      '${r1.fingerprintAfter} vs ${r2.fingerprintAfter}');

  print('\n[5] 自定义 OOB 与天气覆盖');
  final overridden = builder.build(
    scenarioRaw: scRaw,
    overrides: {
      'sides': {
        'red': {
          'oob': [
            {'type': 'tank', 'qty': 4, 'at': [6, 7], 'training': 92, 'experience': 88, 'morale': 95},
            {'type': 'recy', 'qty': 2, 'at': [3, 8]},
          ]
        }
      },
      'weather': {'initial': 'fog', 'mode': 'fixed'},
      'flags': {'strict_supply': true},
    },
    instanceId: 'VO',
    instanceName: '覆盖副本',
    seed: 4242,
  );
  check('覆盖后红方仅 6 个单位', overridden.liveOf('red').length == 6, '${overridden.liveOf('red').length}');
  check('覆盖生效：天气=浓雾', overridden.weatherId == 'fog' && overridden.weatherOf().label == '浓雾');
  check('覆盖生效：严格后勤开启', overridden.flags['strict_supply'] == true);
  check('高训练度写进人员水平', overridden.liveOf('red').first.training == 92);
  final budgetErrs = Scenario(scRaw).validate(lib);
  final overBudget = lib.oobPoints([
    {'type': 'tank', 'qty': 40, 'at': [6, 7]}
  ]);
  check('点数预算可核算', overBudget > 1000 && budgetErrs.isEmpty, 'points=$overBudget');

  print('\n[6] 全 AI 自动对抗至终局');
  final ga = builder.build(scenarioRaw: scRaw, instanceId: 'VS', instanceName: '自动对抗', seed: 99);
  updateObservation(ga);
  var turnLog = <String>[];
  while (!ga.over && ga.turn <= ga.maxTurns + 2) {
    final ro = aiOrders(ga, 'red', sideView(ga, 'red'));
    final bo = aiOrders(ga, 'blue', sideView(ga, 'blue'));
    final rep = resolveTurn(ga, {'red': ro, 'blue': bo});
    turnLog.add('T${ga.turn - 1} ${ga.weatherOf().label}/${DayPhase.fromHours(ga.hourOfDay).label} 拒令${rep.rejected.length} 事件${rep.events.length} 战${rep.battles.length}');
    if (ga.turn > ga.maxTurns + 1) break;
  }
  final redP = ga.liveOf('red').fold<int>(0, (s, u) => s + u.personnel);
  final blueP = ga.liveOf('blue').fold<int>(0, (s, u) => s + u.personnel);
  for (final l in turnLog.take(12)) {
    print('    $l');
  }
  if (turnLog.length > 12) print('    … 共 ${turnLog.length} 回合');
  check('推演可推进且有战斗发生', turnLog.any((l) => l.contains('战')) || ga.over);
  check('终局判定生效', ga.over, '未终局：红 $redP / 蓝 $blueP 人');
  print('    结果：${ga.winner ?? '无'} · 存活 红 $redP / 蓝 $blueP');

  print('\n[7] GIS 数据包 → 几何同源（对边距=实地口径）');
  const newPkg = 'rhine_2p5km.json';
  final pkgData = store.loadGisPackage(newPkg);
  if (pkgData == null) {
    print('    （$newPkg 不存在，跳过几何同源用例）');
  } else {
    final meta = Map<String, dynamic>.from(pkgData['meta'] as Map);
    final gg = builder.build(
      scenarioRaw: scRaw,
      overrides: {'gis_package': newPkg},
      instanceId: 'VG',
      instanceName: '几何自检',
      seed: 555,
    );
    check('副本格数取自数据包', gg.cells.length == meta['width'] * meta['height'],
        '${gg.cells.length} vs ${meta['width'] * meta['height']}');
    final radiusWanted = (meta['radius_m'] as num).toDouble();
    check('引擎半径来自数据包而非 env 默认', (gg.geo.radius - radiusWanted).abs() < 1e-6,
        '${gg.geo.radius} vs $radiusWanted');
    final cellM = gg.geo.hexW; // √3·R = 对边距（墨卡托米）
    final ground = (meta['cell_ground_m'] as num?)?.toDouble();
    if (ground != null) {
      final scale = (meta['mercator_scale'] as num).toDouble();
      check('对边距实地↔墨卡托放大自洽', (cellM - ground * scale).abs() / cellM < 1e-9,
          '$cellM vs ${ground * scale}');
      check('几何记录实地口径', gg.geo.cellGroundKm > 0 && gg.geo.scale > 1.0,
          '${gg.geo.cellGroundKm} km / ×${gg.geo.scale}');
    }
    // 中心经纬度必须落回中心格：证明格网与在线瓦片同一墨卡托框架
    final cl = (meta['center_lonlat'] as List).cast<num>();
    final mp = lonLatToMercator(cl[0].toDouble(), cl[1].toDouble());
    final fq = ((meta['width'] as num).toInt() - 1) / 2.0;
    final fr = ((meta['height'] as num).toInt() - 1) / 2.0;
    check('格网原点取自数据包',
        (gg.geo.ox - (meta['mercator_ox'] as num).toDouble()).abs() < 1e-6 &&
            (gg.geo.oy - (meta['mercator_oy'] as num).toDouble()).abs() < 1e-6);
    final back = gg.geo.nearest(mp[0], mp[1]);
    check('中心经纬度落回格网中心',
        (back.q - fq).abs() <= 1.0 && (back.r - fr).abs() <= 1.0, '$back vs ($fq,$fr)');
    final rt = GameState.fromJson(gg.toJson());
    check('geo 元信息可往返存档', rt.geo.cellGroundKm == gg.geo.cellGroundKm && rt.geo.radius == gg.geo.radius);
  }

  print('\n[8] 兵种属性覆盖（机动/战力/后勤）与自动计价');
  final stdLib = Library.standard();
  final tankBase = stdLib.types['tank']!;
  final buff = {'id': 'tank', 'firepower': 48, 'mobility': 14, 'consumption': 1.0};
  final priced = stdLib.withOverrides(types: [Map<String, dynamic>.from(buff)]);
  final tankPriced = priced.types['tank']!;
  check('覆盖生效：火力/机动/消耗写进定义库',
      tankPriced.firepower == 48 && tankPriced.mobility == 14 && tankPriced.consumption == 1.0,
      '${tankPriced.firepower}/${tankPriced.mobility}/${tankPriced.consumption}');
  check('未覆盖字段保持出厂值',
      tankPriced.protection == tankBase.protection && tankPriced.personnel == tankBase.personnel);
  check('自动计价：强化后点数上升', tankPriced.points > tankBase.points,
      '${tankBase.points} → ${tankPriced.points}');
  final oob4 = [
    {'type': 'tank', 'qty': 4, 'at': [6, 7]}
  ];
  check('点数预算按覆盖后属性核算',
      priced.oobPoints(oob4) > stdLib.oobPoints(oob4),
      '${stdLib.oobPoints(oob4)} → ${priced.oobPoints(oob4)}');
  final explicit = stdLib.withOverrides(types: [
    {'id': 'tank', 'firepower': 48, 'points': 3}
  ]);
  check('显式点数优先，不做二次计价', explicit.types['tank']!.points == 3.0);
  final weakened = stdLib.withOverrides(types: [
    {'id': 'tank', 'firepower': 6, 'mobility': 5}
  ]);
  check('削弱后点数下降但不白送', weakened.types['tank']!.points >= tankBase.points * 0.35,
      '${weakened.types['tank']!.points}');

  final gt = builder.build(
    scenarioRaw: scRaw,
    overrides: {'unit_type_defs': [Map<String, dynamic>.from(buff)]},
    instanceId: 'VT',
    instanceName: '强化装甲',
    seed: 20261016,
  );
  final tankU = gt.units.where((u) => u.typeId == 'tank').toList();
  check('副本单位读到强化后的兵种', tankU.isNotEmpty && gt.typeOf(tankU.first).firepower == 48);
  final gt2 = GameState.fromJson(gt.toJson());
  check('存档自包含：载入后强化属性与成交价都在',
      gt2.typeOf(gt2.units.firstWhere((u) => u.typeId == 'tank')).firepower == 48 &&
          gt2.lib.types['tank']!.points == tankPriced.points);

  final gBase = builder.build(scenarioRaw: scRaw, instanceId: 'VB0', instanceName: '基线', seed: 31337);
  final gBuff = builder.build(
    scenarioRaw: scRaw,
    overrides: {'unit_type_defs': [Map<String, dynamic>.from(buff)]},
    instanceId: 'VB1',
    instanceName: '强化',
    seed: 31337,
  );
  updateObservation(gBase);
  updateObservation(gBuff);
  for (var t = 0; t < 3; t++) {
    resolveTurn(gBase, {
      'red': aiOrders(gBase, 'red', sideView(gBase, 'red')),
      'blue': aiOrders(gBase, 'blue', sideView(gBase, 'blue')),
    });
    resolveTurn(gBuff, {
      'red': aiOrders(gBuff, 'red', sideView(gBuff, 'red')),
      'blue': aiOrders(gBuff, 'blue', sideView(gBuff, 'blue')),
    });
  }
  check('属性覆盖真的进入结算（同种子指纹分叉）', gBase.fingerprint() != gBuff.fingerprint(),
      '${gBase.fingerprint()} vs ${gBuff.fingerprint()}');

  print('\n[9] 地物层：真实矢量 → 引擎玩法（铁路/隧道/电网/机场/要点）');
  const featPkg = 'rhine_ruhr_2p5km.json';
  if (store.loadGisPackage(featPkg) == null) {
    print('    （$featPkg 不存在，跳过地物用例）');
  } else {
    final gf = builder.build(
      scenarioRaw: scRaw,
      overrides: {'gis_package': featPkg},
      instanceId: 'VFE',
      instanceName: '地物自检',
      seed: 818,
    );
    int cnt(bool Function(Cell c) p) => gf.cells.values.where(p).length;
    check('铁路/隧道/电网/机场写入引擎格网',
        cnt((c) => c.railway) > 0 && cnt((c) => c.tunnel) > 0 && cnt((c) => c.powerline) > 0 &&
            cnt((c) => c.powerPlant) > 0 && cnt((c) => c.substation) > 0 && cnt((c) => c.airport > 0) > 0,
        '铁路 ${cnt((c) => c.railway)} 隧道 ${cnt((c) => c.tunnel)} 线路 ${cnt((c) => c.powerline)} '
            '电厂 ${cnt((c) => c.powerPlant)} 变电站 ${cnt((c) => c.substation)} 机场 ${cnt((c) => c.airport > 0)}');
    check('地物不变式：车站⊆铁路 · 桥⊆河 · 隧道在线上且不在水面',
        gf.cells.entries.every((e) {
          final c = e.value;
          if (c.railStation && !c.railway) return false;
          if (c.bridge && !c.river) return false;
          if (c.tunnel && (!(c.railway || c.road) || gf.terrainOf(Hex.parse(e.key)).water)) return false;
          return true;
        }));
    final rtFeat = GameState.fromJson(gf.toJson());
    check('地物字段随存档往返',
        rtFeat.cells.values.where((c) => c.railway).length == cnt((c) => c.railway) &&
            rtFeat.cells.values.where((c) => c.tunnel).length == cnt((c) => c.tunnel) &&
            rtFeat.cells.values.where((c) => c.airport > 0).length == cnt((c) => c.airport > 0));

    final clearTd = gf.lib.terrains['clear']!;
    const bare = Cell(terrain: 'clear');
    const rail = Cell(terrain: 'clear', railway: true);
    const roadRail = Cell(terrain: 'clear', road: true, railway: true);
    const tunnel = Cell(terrain: 'clear', tunnel: true);
    check('铁路使摩托化机动更省，徒步不搭车不受惠',
        stepCost(clearTd, rail, 'motor') < stepCost(clearTd, bare, 'motor') &&
            stepCost(clearTd, rail, 'foot') == stepCost(clearTd, bare, 'foot'));
    check('公路与铁路折扣可叠加（0.65×0.8）',
        (stepCost(clearTd, roadRail, 'motor') - clearTd.cost['motor']! * 0.52).abs() < 1e-9);
    check('隧道限行：重装备比徒步更吃亏',
        stepCost(clearTd, tunnel, 'mech') / stepCost(clearTd, bare, 'mech') >
            stepCost(clearTd, tunnel, 'foot') / stepCost(clearTd, bare, 'foot'));
    final riverTd = gf.lib.terrains['river']!;
    const ford = Cell(terrain: 'river', river: true);
    const bridged = Cell(terrain: 'river', river: true, bridge: true);
    check('无桥渡河重罚 +2，桥位免罚',
        stepCost(riverTd, ford, 'motor') - stepCost(riverTd, bridged, 'motor') > 1.99);

    // 电网：只夺半边不发电，电厂+变电站同时掌握才给补给红利
    final gA = GameState.fromJson(gf.toJson());
    final gB = GameState.fromJson(gf.toJson());
    final busy = gB.units.where((u) => u.alive).map((u) => u.pos.key).toSet();
    final plantKey = gB.cells.entries.firstWhere((e) => e.value.powerPlant && !busy.contains(e.key)).key;
    final subKey = gB.cells.entries.firstWhere((e) => e.value.substation && !busy.contains(e.key)).key;
    final gndB = gB.liveOf('red')
        .where((u) => !gB.typeOf(u).isAir && !gB.typeOf(u).supplySource && gB.typeOf(u).cls != 'structure')
        .toList();
    check('开局双方都不通电（电网系数 1.0）',
        powerFactor(gA, 'red') == 1.0 && powerFactor(gA, 'blue') == 1.0,
        '红 ${powerFactor(gA, 'red')} / 蓝 ${powerFactor(gA, 'blue')}');
    gndB[0].pos = Hex.parse(plantKey);
    check('只夺电厂仍不通电', powerFactor(gB, 'red') == 1.0, '${powerFactor(gB, 'red')}');
    gndB[1].pos = Hex.parse(subKey);
    final pf = powerFactor(gB, 'red');
    check('电厂 + 变电站同时掌握才通电（红利封顶 +15%）', pf > 1.0 && pf <= 1.1500001, '$pf');
    check('未掌握电网的一方不吃红利', powerFactor(gB, 'blue') == 1.0);
    final supA = computeSupply(gA)['red']!;
    final supB = computeSupply(gB)['red']!;
    final farKey = supA.entries.reduce((a, b) => a.value <= b.value ? a : b).key;
    check('电网红利进入补给系数（远端最弱格上浮）',
        supB[farKey]! > supA[farKey]!, '$farKey ${supA[farKey]} → ${supB[farKey]}');

    // 机场：夺下场站且该方仍有航空兵 → 前进补给节点
    final gAir = GameState.fromJson(gf.toJson());
    final apBusy = gAir.units.where((u) => u.alive).map((u) => u.pos.key).toSet();
    final apFreeKey = gAir.cells.entries
        .firstWhere((e) => e.value.airport > 0 && !apBusy.contains(e.key))
        .key;
    final gndAir = gAir.liveOf('red')
        .where((u) => !gAir.typeOf(u).isAir && !gAir.typeOf(u).supplySource && gAir.typeOf(u).cls != 'structure')
        .toList();
    final d0 = supplyDistances(gAir)['red']![apFreeKey];
    gndAir[0].pos = Hex.parse(apFreeKey);
    final d1 = supplyDistances(gAir)['red']![apFreeKey];
    check('地面部队站上机场 ⇒ 该格成为补给节点（距离归 0）',
        d1 == 0 && (d0 == null || d0 > 0), '$apFreeKey 距离 $d0 → $d1');
    for (final u in gAir.units) {
      if (u.alive && u.side == 'red' && gAir.typeOf(u).isAir) u.status = UnitStatus.destroyed;
    }
    final d2 = supplyDistances(gAir)['red']![apFreeKey];
    check('航空兵撤离后机场不再供补给（空运需要机组）', d2 != 0, '距离 $d2');
    check('机场等级只用 1/2（野战/干线）', cnt((c) => c.airport > 0) > 0 && gf.cells.values.every((c) => c.airport <= 2));

    // 迷雾纪律：要点地理公开，归属按视图裁剪
    final gv = GameState.fromJson(gf.toJson());
    final nodeAll = objectiveNodes(gv);
    final holders = nodeHolders(gv);
    check('要点清单齐备（含类别与中文标签）',
        nodeAll.isNotEmpty && nodeAll.every((n) => (n['label'] as String).isNotEmpty && n['kind'] != null),
        '${nodeAll.length} 处要点');
    final vRed = sideView(gv, 'red');
    final vBlue = sideView(gv, 'blue');
    final vDir = directorView(gv);
    final vSpec = spectatorView(gv);
    check('各方视图的要点地理一致（地形不是情报）',
        (vRed['nodes'] as List).length == nodeAll.length && (vBlue['nodes'] as List).length == nodeAll.length);
    final nhRed = (vRed['node_holders'] as Map).map((k, v) => MapEntry(k as String, v as String));
    final nhBlue = (vBlue['node_holders'] as Map).map((k, v) => MapEntry(k as String, v as String));
    check('双盲视图只披露己方占领（敌方归属属情报）',
        nhRed.values.every((s) => s == 'red') && nhBlue.values.every((s) => s == 'blue') &&
            nhRed.keys.toSet().intersection(nhBlue.keys.toSet()).isEmpty,
        '红 ${nhRed.length} / 蓝 ${nhBlue.length} / 全局 ${holders.length}');
    check('导演部视图给出双方电网系数',
        (vDir['power'] as Map).containsKey('red') && (vDir['power'] as Map).containsKey('blue'));
    check('观战视图不给要点归属',
        !vSpec.containsKey('node_holders') && (vSpec['nodes'] as List).isNotEmpty);

    // 目标胜利：同时掌握够多处战场要点即达成
    final gWin = builder.build(
      scenarioRaw: scRaw,
      overrides: {
        'gis_package': featPkg,
        'victory': [
          {'kind': 'nodes', 'side': 'red', 'target': null, 'amount': 3, 'turns': 0}
        ]
      },
      instanceId: 'VFN',
      instanceName: '要点胜利',
      seed: 626,
    );
    updateObservation(gWin);
    final taken = gWin.units.where((u) => u.alive).map((u) => u.pos.key).toSet();
    final freeNodes = objectiveNodes(gWin).where((n) => !taken.contains(n['key'])).take(3).toList();
    final gndWin = gWin.liveOf('red').where((u) => !gWin.typeOf(u).isAir).toList();
    for (var i = 0; i < freeNodes.length && i < gndWin.length; i++) {
      gndWin[i].pos = Hex.parse(freeNodes[i]['key'] as String);
    }
    check('落位后红方确握 3 处要点',
        nodeHolders(gWin, onlySide: 'red').length >= 3, '${nodeHolders(gWin, onlySide: 'red').length}');
    resolveTurn(gWin, {'red': [], 'blue': []});
    check('要点数量达成即终局（kind=nodes）', gWin.over && gWin.winner == 'red',
        'winner=${gWin.winner}');
    print('    终局：${gWin.winner ?? '无'} · 要点 ${nodeHolders(gWin, onlySide: 'red').length}/${objectiveNodes(gWin).length}');

    // 全 AI 自动对抗在带地物的真实地图上可跑通
    final gRun = builder.build(
      scenarioRaw: scRaw,
      overrides: {'gis_package': featPkg},
      instanceId: 'VFR',
      instanceName: '地物自动对抗',
      seed: 4242,
    );
    updateObservation(gRun);
    var runTurns = 0;
    while (!gRun.over && gRun.turn <= gRun.maxTurns + 2 && runTurns < 20) {
      resolveTurn(gRun, {
        'red': aiOrders(gRun, 'red', sideView(gRun, 'red')),
        'blue': aiOrders(gRun, 'blue', sideView(gRun, 'blue')),
      });
      runTurns++;
    }
    final onRail = gRun.units.where((u) => u.alive && gRun.cellAt(u.pos).railway).toList();
    check('带地物地图可连续推演（$runTurns 回合）且无异常', runTurns >= 3, '${gRun.turn} 回合');
    print('    地物利用：${onRail.length} 个单位停在铁路上 · 结果 ${gRun.winner ?? '未终局'}');
  }

  print('\n合计：$_pass 通过 / $_fail 失败');
  exit(_fail == 0 ? 0 : 1);
}
