// 引擎自检（可脱离 Web 独立运行）：dart run bin/verify.dart
import 'dart:convert';
import 'dart:io';
import 'package:hexfront_web/engine/commands.dart';
import 'package:hexfront_web/engine/defs.dart';
import 'package:hexfront_web/engine/fog.dart';
import 'package:hexfront_web/engine/hex.dart';
import 'package:hexfront_web/engine/library.dart';
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

  print('\n合计：$_pass 通过 / $_fail 失败');
  exit(_fail == 0 ? 0 : 1);
}
