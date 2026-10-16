// HEXFRONT-WEB 服务端：零第三方依赖（只用 dart:io / dart:async / dart:convert）。
// 职责：静态前端托管 + REST（剧本/副本/存档）+ WebSocket 房间分发 + 迷雾裁剪下发。
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:hexfront_web/engine/commands.dart';
import 'package:hexfront_web/engine/fog.dart';
import 'package:hexfront_web/engine/library.dart';
import 'package:hexfront_web/engine/scenario.dart';
import 'package:hexfront_web/engine/state.dart';
import 'package:hexfront_web/server/hexgen.dart';
import 'package:hexfront_web/server/room.dart';
import 'package:hexfront_web/server/store.dart';

final Directory projectRoot = () {
  final script = File.fromUri(Platform.script).parent.parent; // .../wargame/hexfront
  return script.parent; // .../wargame
}();

late final Store store = Store(
  root: Directory('${projectRoot.path}${Platform.pathSeparator}data'),
  saves: Directory('${projectRoot.path}${Platform.pathSeparator}saves'),
);
late final Library library = Library.standard();
late final RoomRegistry registry = RoomRegistry(store);
final InstanceBuilder builder = InstanceBuilder(
  lib: library,
  loadGisPackage: store.loadGisPackage,
);
final GisService gis = GisService(
  repoRoot: projectRoot,
  gisDir: Directory('${projectRoot.path}${Platform.pathSeparator}data${Platform.pathSeparator}gis'),
);

/// 天地图瓦片密钥：优先环境变量 TIANDITU_TK，其次 data/tianditu.json 的 {"tk": "..."}。
/// 不缓存——开发时改完文件刷新页面即可生效。
String? tiandituTk() {
  final env = Platform.environment['TIANDITU_TK']?.trim();
  if (env != null && env.isNotEmpty) return env;
  final f = File('${projectRoot.path}${Platform.pathSeparator}data${Platform.pathSeparator}tianditu.json');
  if (!f.existsSync()) return null;
  try {
    final tk = (jsonDecode(f.readAsStringSync()) as Map)['tk'] as String?;
    return (tk == null || tk.trim().isEmpty) ? null : tk.trim();
  } catch (_) {
    return null; // 手写 json 少个引号不该让整站 500
  }
}

void main(List<String> args) async {
  final port = int.tryParse(args.isNotEmpty ? args.first : (Platform.environment['PORT'] ?? '8770')) ?? 8770;
  final server = await HttpServer.bind(InternetAddress.anyIPv4, port);
  server.autoCompress = true;
  print('HEXFRONT-WEB  http://localhost:$port  (项目根: ${projectRoot.path})');
  print('剧本 ${store.listScenarios().length} 个 · GIS 数据包 ${store.gisPackageSummaries().length} 个 · 存档 ${store.listSaves().length} 个');

  await for (final req in server) {
    try {
      await route(req);
    } catch (e, st) {
      stderr.writeln('[500] $e\n$st');
      req.response.statusCode = 500;
      _json(req, {'error': '服务端内部错误'});
    }
  }
}

Future<void> route(HttpRequest req) async {
  final p = req.uri.path;
  if (p.startsWith('/api/')) return api(req, p);
  if (p == '/ws') return ws(req);
  return serveStatic(req, p);
}

// ───────────────── REST ─────────────────

Future<void> api(HttpRequest req, String p) async {
  if (req.method == 'OPTIONS') {
    req.response
      ..headers.set('Access-Control-Allow-Origin', '*')
      ..headers.set('Access-Control-Allow-Headers', 'content-type')
      ..headers.set('Access-Control-Allow-Methods', 'GET,POST,OPTIONS')
      ..statusCode = 204;
    return req.response.close();
  }
  req.response.headers.set('Access-Control-Allow-Origin', '*');

  Future<Map<String, dynamic>> body() async {
    final s = await utf8.decoder.bind(req).join();
    if (s.trim().isEmpty) return <String, dynamic>{};
    return Map<String, dynamic>.from(jsonDecode(s) as Map);
  }

  if (req.method == 'GET' && p == '/api/scenarios') {
    return _json(req, {
      'scenarios': store.listScenarios(),
      'gis': store.gisPackageSummaries(),
      'saves': store.listSaves(),
      'weather': library.weather.values.map((w) => w.toJson()).toList(),
      'unit_types': library.types.values.map((t) => t.toJson()).toList(),
      'terrains': library.terrains.values.map((t) => t.toJson()).toList(),
      'rules_default': library.baseRules.toJson(),
      'tiles': {'provider': 'tianditu', 'tk': tiandituTk()},
    });
  }
  if (req.method == 'GET' && p == '/api/rooms') {
    return _json(req, {'rooms': registry.list()});
  }

  // ── GIS 六角格：能力探测 / 格网方案 / 生成作业 ──
  if (req.method == 'GET' && p == '/api/gis/capabilities') {
    return _json(req, await gis.capabilities(refresh: req.uri.queryParameters['refresh'] == '1'));
  }
  if (req.method == 'GET' && p == '/api/gis/plan') {
    final q = req.uri.queryParameters;
    final bb = _bboxFromQuery(q);
    if (bb == null) return _json(req, {'error': '需要 w/s/e/n 四个数值（西 南 东 北）'}, 400);
    try {
      return _json(req, await gis.plan(bb, cellKm: double.tryParse(q['cell_km'] ?? '')));
    } on GisError catch (e) {
      return _json(req, {'error': e.message}, 400);
    }
  }
  if (p == '/api/gis/jobs' && req.method == 'GET') {
    return _json(req, {'jobs': gis.jobs()});
  }
  if (p == '/api/gis/jobs' && req.method == 'POST') {
    final b = await body();
    try {
      final job = await gis.startJob(GisJobSpec.fromJson(b));
      return _json(req, job.toJson());
    } on GisError catch (e) {
      return _json(req, {'error': e.message}, 400);
    }
  }
  if (req.method == 'GET' && p.startsWith('/api/gis/jobs/')) {
    final job = gis.job(p.substring('/api/gis/jobs/'.length));
    if (job == null) return _json(req, {'error': '作业不存在（服务端重启后作业记录会清空，数据包仍在）'}, 404);
    return _json(req, job.toJson());
  }
  if (req.method == 'POST' && p == '/api/validate') {
    final b = await body();
    final sc = store.loadScenario(b['scenario'] as String);
    if (sc == null) return _json(req, {'errors': ['剧本不存在']});
    final merged = b['overrides'] == null ? sc : deepMerge(sc, Map<String, dynamic>.from(b['overrides'] as Map));
    final eff = _effectiveLibrary(merged);
    final errs = Scenario(merged).validate(eff)..addAll(_validateTypeDefs(merged, library));
    return _json(req, {
      'errors': errs,
      'preview': _oobPreview(merged, eff),
      'type_points': _typePoints(merged, eff),
    });
  }

  if (req.method == 'POST' && p == '/api/instances') {
    final b = await body();
    final scenarioId = b['scenario'] as String;
    final sc = store.loadScenario(scenarioId);
    if (sc == null) return _json(req, {'error': '剧本不存在: $scenarioId'}, 404);
    final overrides = b['overrides'] == null ? <String, dynamic>{} : Map<String, dynamic>.from(b['overrides'] as Map);
    final merged = overrides.isEmpty ? sc : deepMerge(sc, overrides);
    final eff = _effectiveLibrary(merged);
    final errs = Scenario(merged).validate(eff)
      ..addAll(_validateTypeDefs(merged, library))
      ..addAll(_validateOverrides(overrides, eff));
    if (errs.isNotEmpty && b['force'] != true) {
      return _json(req, {'error': '剧本/覆盖校验未通过', 'errors': errs}, 400);
    }
    final seed = (b['seed'] as num?)?.toInt() ?? DateTime.now().millisecondsSinceEpoch ~/ 1000;
    final instanceId = 'I${DateTime.now().millisecondsSinceEpoch ~/ 1000}';
    final g = builder.build(
      scenarioRaw: sc,
      overrides: overrides.isEmpty ? null : overrides,
      instanceId: instanceId,
      instanceName: (b['name'] as String?) ?? sc['name'] as String,
      seed: seed,
    );
    updateObservation(g); // 初始接触：两岸互相可见的部队先建立基线情报
    final meta = {
      'instance': instanceId,
      'scenario': sc['id'],
      'scenario_name': sc['name'],
      'name': g.name,
      'seed': seed,
      'created_at': DateTime.now().toIso8601String(),
      'last_turn': 0,
      'max_turns': g.maxTurns,
      'overrides': overrides,
    };
    store.writeInit(meta, g.toJson());
    final room = registry.create(g: g, scenarioRaw: sc, overrides: overrides);
    if (b['ai_red'] == true) room.aiRed = true;
    if (b['ai_blue'] == true) room.aiBlue = true;
    if (b['fog'] == false) g.flags['fog'] = false;
    return _json(req, {'code': room.code, 'instance': instanceId, 'room': room.publicInfo()});
  }

  if (req.method == 'POST' && p == '/api/saves/load') {
    final b = await body();
    final instanceId = b['instance'] as String;
    final snap = store.readLatestSnapshot(instanceId);
    final meta = store.readMeta(instanceId);
    if (snap == null || meta == null) return _json(req, {'error': '存档不存在或损坏'}, 404);
    final g = GameState.fromJson(snap);
    final room = registry.create(
      g: g,
      scenarioRaw: store.loadScenario(meta['scenario'] as String) ?? <String, dynamic>{},
      overrides: Map<String, dynamic>.from(meta['overrides'] as Map? ?? {}),
    );
    updateObservation(g);
    room.frames.add({
      'turn': g.turn - 1, 'hour': g.hourOfDay, 'weather': g.weatherId,
      'director': directorView(g), 'events': <Map<String, dynamic>>[], 'battles': <Map<String, dynamic>>[],
    });
    return _json(req, {'code': room.code, 'resumed_turn': g.turn, 'room': room.publicInfo()});
  }

  if (req.method == 'GET' && p.startsWith('/api/room/')) {
    final room = registry.byCode(p.split('/').last);
    if (room == null) return _json(req, {'error': '房间不存在'}, 404);
    return _json(req, room.publicInfo());
  }

  if (req.method == 'GET' && p.startsWith('/api/frames/')) {
    final room = registry.byCode(p.split('/').last);
    if (room == null) return _json(req, {'error': '房间不存在（重启后请用存档回放）'}, 404);
    return _json(req, {'code': room.code, 'frames': room.replayFrames()});
  }

  return _json(req, {'error': 'not found: $p'}, 404);
}

/// /api/gis/plan 的查询串：w/s/e/n = 西/南/东/北（WGS84 度）
List<double>? _bboxFromQuery(Map<String, String> q) {
  final w = double.tryParse(q['w'] ?? '');
  final s = double.tryParse(q['s'] ?? '');
  final e = double.tryParse(q['e'] ?? '');
  final n = double.tryParse(q['n'] ?? '');
  if (w == null || s == null || e == null || n == null) return null;
  return [w, s, e, n];
}

/// 副本创建者能改的兵种属性（JSON 键名）。其余键一律拒绝：
/// cls/is_hq/supply_source/air_role 动的是规则语义，不是"装备性能"。
const TYPE_PATCH_KEYS = {
  'label', 'mobility', 'firepower', 'protection', 'recon', 'load', 'consumption', 'personnel', 'range', 'anti_air', 'points'
};
const TYPE_PATCH_RANGES = {
  'mobility': [0.0, 60.0],
  'firepower': [0.0, 120.0],
  'protection': [0.05, 8.0],
  'recon': [0.0, 20.0],
  'load': [0.05, 8.0],
  'consumption': [0.0, 10.0],
  'personnel': [1.0, 5000.0],
  'range': [0.0, 12.0],
  'anti_air': [0.0, 10.0],
};

/// 剧本 + 覆盖层 → 有效定义库（校验与预算都必须用它，否则覆盖后的兵种按出厂价算）
Library _effectiveLibrary(Map<String, dynamic> merged) => Library.standard().withOverrides(
      terrain: (merged['terrain_defs'] as List?)?.cast<Map<String, dynamic>>(),
      types: (merged['unit_type_defs'] as List?)?.cast<Map<String, dynamic>>(),
      weather: (merged['weather_defs'] as List?)?.cast<Map<String, dynamic>>(),
    );

/// 兵种属性覆盖的合法性（对出厂库检查，覆盖后的库只用来计价）
List<String> _validateTypeDefs(Map<String, dynamic> merged, Library base) {
  final errs = <String>[];
  final defs = merged['unit_type_defs'];
  if (defs is! List) return errs;
  for (final e in defs) {
    if (e is! Map) {
      errs.add('unit_type_defs 的每一项必须是对象');
      continue;
    }
    final id = e['id'];
    if (id is! String || !base.types.containsKey(id)) {
      errs.add('兵种属性覆盖只允许改既有兵种，未知兵种 $id');
      continue;
    }
    for (final k in e.keys) {
      if (k is! String || k == 'id') continue;
      if (!TYPE_PATCH_KEYS.contains(k)) errs.add('兵种 $id 不允许覆盖字段 $k');
    }
    TYPE_PATCH_RANGES.forEach((k, bound) {
      final v = e[k];
      if (v == null) return;
      if (v is! num) {
        errs.add('兵种 $id 的 $k 必须是数值');
        return;
      }
      if (v < bound[0] || v > bound[1]) {
        errs.add('兵种 $id 的 $k=$v 超出允许区间 ${bound[0]}–${bound[1]}');
      }
    });
    if (e['points'] is num && (e['points'] as num) <= 0) errs.add('兵种 $id 的显式点数必须大于 0');
  }
  return errs;
}

/// 覆盖后各兵种的成交价（含自动计价结果），供向导展示——前端不自己算钱
Map<String, dynamic> _typePoints(Map<String, dynamic> merged, Library eff) {
  final out = <String, dynamic>{};
  final defs = merged['unit_type_defs'];
  if (defs is! List) return out;
  for (final e in defs) {
    if (e is! Map) continue;
    final id = e['id'];
    final t = id is String ? eff.types[id] : null;
    if (t != null) out[id] = t.points;
  }
  return out;
}

List<String> _validateOverrides(Map<String, dynamic> ov, Library eff) {
  final errs = <String>[];
  final sides = ov['sides'];
  if (sides is Map) {
    for (final e in sides.entries) {
      final cfg = e.value;
      if (cfg is! Map) continue;
      final oob = cfg['oob'];
      if (oob is List && oob.isEmpty) errs.add('阵营 ${e.key} 的覆盖 OOB 为空（若要保留剧本默认，请不要提交该字段）');
      if (oob is List) {
        for (final u in oob) {
          if (u is! Map) continue;
          if (!eff.types.containsKey(u['type'])) errs.add('未知兵种 ${u['type']}');
        }
        final budget = cfg['points_budget'];
        if (budget is num && budget > 0) {
          final used = eff.oobPoints(oob.cast<Map<String, dynamic>>());
          if (used > budget + 0.001) {
            errs.add('阵营 ${e.key} 点数 ${used.toStringAsFixed(0)} 超出预算 ${budget.toStringAsFixed(0)}');
          }
        }
      }
    }
  }
  final wx = ov['weather'];
  if (wx is Map && wx['initial'] != null && !eff.weather.containsKey(wx['initial'])) {
    errs.add('未知天气 ${wx['initial']}');
  }
  return errs;
}

Map<String, dynamic> _oobPreview(Map<String, dynamic> merged, Library eff) {
  final sides = merged['sides'] as Map? ?? {};
  final out = <String, dynamic>{};
  sides.forEach((k, v) {
    final cfg = v as Map;
    final oob = (cfg['oob'] as List? ?? []).cast<Map<String, dynamic>>();
    out[k as String] = {
      'points': eff.oobPoints(oob),
      'budget': cfg['points_budget'],
      'units': oob.fold<int>(0, (s, e) => s + ((e['qty'] as num?)?.toInt() ?? 1)),
      'name': cfg['name'],
    };
  });
  return {'sides': out};
}

void _json(HttpRequest req, Object data, [int status = 200]) {
  req.response
    ..statusCode = status
    ..headers.contentType = ContentType.json
    ..write(jsonEncode(data));
  req.response.close();
}

// ───────────────── WebSocket ─────────────────

Future<void> ws(HttpRequest req) async {
  final q = req.uri.queryParameters;
  final room = registry.byCode(q['room'] ?? '');
  if (room == null) {
    req.response.statusCode = 404;
    return req.response.close();
  }
  final role = (q['role'] ?? 'spectator').toLowerCase();
  if (!['director', 'red', 'blue', 'spectator'].contains(role)) {
    req.response.statusCode = 400;
    return req.response.close();
  }
  final token = q['token'] ?? randomCode(10);
  // 断线重连：同一 token 复用原会话对象，否则新 socket 不在 sessions 里，pushState 永远推不到它
  final old = room.byToken(token);
  final reuse = old != null && old.role == role;
  final session = reuse ? old : Session(randomCode(10), role, q['name'] ?? _roleLabel(role));
  // 同一 token 重连（旧连接尚未 onDone）不算占用；他人抢位才 409
  final takenByOther = room.sessions.any((s) => s.role == role && s.token != session.token);
  if ((role == 'red' || role == 'blue') && takenByOther && q['force'] != '1') {
    req.response.statusCode = 409;
    return req.response.close();
  }
  if (!reuse) room.addSession(session);
  session.name = q['name'] ?? _roleLabel(role);
  // 顶号：同角色的其他终端（force 接管时）踢掉；绝不碰自己这条会话
  final other = room.sessions.where((s) => !identical(s, session) && s.role == role).toList();
  for (final dup in other) {
    try {
      dup.sock?.add(jsonEncode({'type': 'kicked', 'reason': '同一角色在其他终端加入'}));
      dup.sock?.close();
    } catch (_) {}
    room.sessions.remove(dup);
  }

  final socket = await WebSocketTransformer.upgrade(req);
  // 同 token 的另一个标签页接管：必须显式踢掉旧连接，否则旧页面以为自己仍在线
  final stale = session.sock;
  if (stale != null && stale.readyState == WebSocket.open) {
    try {
      stale.add(jsonEncode({'type': 'kicked', 'reason': '同一角色在其他终端加入'}));
      stale.close();
    } catch (_) {}
  }
  session.sock = socket;
  socket.add(jsonEncode({
    'type': 'welcome',
    'token': session.token,
    'role': role,
    'room': room.publicInfo(),
    'view': room.viewFor(session),
  }));
  room.pushState();

  socket.listen(
    (raw) {
      Map<String, dynamic> msg;
      try {
        msg = Map<String, dynamic>.from(jsonDecode(raw as String) as Map);
      } catch (_) {
        return;
      }
      handle(room, session, msg);
    },
    onDone: () => _detach(room, session, socket),
    onError: (_) => _detach(room, session, socket),
  );
}

/// 终端关闭即释放会话：对抗位必须立刻腾出，否则关一个标签页就永久锁死红/蓝方。
/// 指令草案存在 Room 上（按阵营），不随会话消失，重连后照旧带回。
void _detach(Room room, Session s, WebSocket socket) {
  if (!identical(s.sock, socket)) return; // 已被更新的连接接管
  s.sock = null;
  room.sessions.remove(s);
}

String _roleLabel(String role) => switch (role) {
      'director' => '导演部',
      'red' => '红方指挥',
      'blue' => '蓝方指挥',
      _ => '观战席'
    };

void handle(Room room, Session s, Map<String, dynamic> msg) {
  void err(String m) {
    try {
      s.sock?.add(jsonEncode({'type': 'error', 'message': m}));
    } catch (_) {}
  }

  switch (msg['type'] as String?) {
    case 'ping':
      s.sock?.add(jsonEncode({'type': 'pong', 'turn': room.turn}));
      break;

    case 'orders':
      final list = (msg['commands'] as List? ?? []).map((e) => Command.fromJson(Map<String, dynamic>.from(e as Map))).toList();
      final e = room.submitOrders(s.role, list, msg['replace'] != false);
      if (e != null) return err(e);
      room.pushState();
      break;

    case 'clear_orders':
      final e = room.clearOrders(s.role);
      if (e != null) return err(e);
      room.pushState();
      break;

    case 'ready':
      final e = room.setReady(s.role, msg['value'] != false);
      if (e != null) return err(e);
      room.pushState();
      // AI 代管的一方视为已就绪，否则人类玩家点就绪后会永久等待
      if (_sideReady(room, 'red') && _sideReady(room, 'blue')) {
        Timer(Duration(milliseconds: 300), () => room.resolve(byRole: 'auto'));
      }
      break;

    case 'resolve':
      if (s.role != 'director') return err('只有导演部可以强制结算');
      room.resolve(byRole: 'director', force: true);
      break;

    case 'save':
      store.writeSnapshot(room.instanceId, room.turn, room.g.toJson());
      s.sock?.add(jsonEncode({'type': 'saved', 'instance': room.instanceId, 'turn': room.turn}));
      break;

    case 'frames':
      if (s.role != 'director' && s.role != 'spectator') return err('无权获取回放帧');
      s.sock?.add(jsonEncode({'type': 'frames', 'frames': room.replayFrames()}));
      break;

    case 'director_cmd':
      if (s.role != 'director') return err('只有导演部可以导调');
      _director(room, msg, s);
      break;

    case 'state':
      room.pushState();
      break;

    default:
      err('未知消息 ${msg['type']}');
  }
}

void _director(Room room, Map<String, dynamic> msg, Session s) {
  final action = msg['action'] as String?;
  final g = room.g;
  switch (action) {
    case 'weather':
      final id = msg['id'] as String?;
      if (id == null || !library.weather.containsKey(id)) return _errSock(s, '未知天气');
      g.weatherId = id;
      g.weatherMode = 'director';
      g.emit(WarEvent(g.turn, 'weather_set', '导演部将天气设为 ${g.weatherOf().label}', ['red', 'blue', 'spectator']));
      updateObservation(g);
      break;
    case 'ai':
      final side = msg['side'] as String?;
      final on = msg['on'] == true;
      if (side == 'red') room.aiRed = on;
      if (side == 'blue') room.aiBlue = on;
      // 导调开启代管后立即检查屏障，避免人类玩家还要再点一次就绪
      if (_sideReady(room, 'red') && _sideReady(room, 'blue')) {
        Timer(Duration(milliseconds: 300), () => room.resolve(byRole: 'director-ai'));
      }
      break;
    case 'spectator_omniscient':
      room.spectatorOmniscient = msg['on'] == true;
      break;
    case 'fog':
      g.flags['fog'] = msg['on'] == true;
      updateObservation(g);
      g.emit(WarEvent(g.turn, 'fog', '导演部${g.flags['fog'] == true ? '开启' : '关闭'}战争迷雾', ['director']));
      break;
    case 'inject_intel':
      final side = msg['side'] as String?;
      final unitId = msg['unit'] as String?;
      final target = g.findUnit(unitId ?? '');
      if (side == null || target == null || (side != 'red' && side != 'blue')) return _errSock(s, '参数错误');
      room.g.tracks[side]![target.id] = Track(target.id, 3, target.pos, g.turn);
      g.emit(WarEvent(g.turn, 'intel', '导演部向${side == 'red' ? '红方' : '蓝方'}通报敌情：${target.id}', [side]));
      break;
    case 'jump_turn':
      final t = (msg['turn'] as num?)?.toInt();
      if (t == null || t < 1 || t > g.turn) return _errSock(s, '只能回跳到已推演回合（回放由前端帧完成）');
      break;
    default:
      _errSock(s, '未知导调动作 $action');
  }
  room.pushState();
}

/// AI 代管的一方视为已就绪（否则人类玩家点就绪后永远等不到对面）
bool _sideReady(Room room, String side) =>
    room.ready[side] == true || (side == 'red' ? room.aiRed : room.aiBlue);

void _errSock(Session s, String m) {
  try {
    s.sock?.add(jsonEncode({'type': 'error', 'message': m}));
  } catch (_) {}
}

// ───────────────── 静态前端 ─────────────────

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

Future<void> serveStatic(HttpRequest req, String p) async {
  final web = Directory('${projectRoot.path}${Platform.pathSeparator}web');
  var rel = p == '/' ? 'index.html' : p.substring(1);
  rel = Uri.decodeComponent(rel);
  final f = File('${web.path}${Platform.pathSeparator}${rel.replaceAll('/', Platform.pathSeparator)}');
  if (!f.existsSync() || !f.path.startsWith(web.path)) {
    req.response.statusCode = 404;
    return req.response.write('not found: $rel');
  }
  final ext = f.path.substring(f.path.lastIndexOf('.'));
  req.response.headers.set('Content-Type', MIME[ext] ?? 'application/octet-stream');
  req.response.headers.set('Cache-Control', 'no-store');
  await req.response.addStream(f.openRead());
  await req.response.close();
}
