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
  print('剧本 ${store.listScenarios().length} 个 · GIS 数据包 ${store.listGisPackages().length} 个 · 存档 ${store.listSaves().length} 个');

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
      'gis': store.listGisPackages().map((k, v) => MapEntry(k, v)),
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
  if (req.method == 'POST' && p == '/api/validate') {
    final b = await body();
    final sc = store.loadScenario(b['scenario'] as String);
    if (sc == null) return _json(req, {'errors': ['剧本不存在']});
    final merged = b['overrides'] == null ? sc : deepMerge(sc, Map<String, dynamic>.from(b['overrides'] as Map));
    final errs = Scenario(sc).validate(Library.standard().withOverrides(
          terrain: (merged['terrain_defs'] as List?)?.cast<Map<String, dynamic>>(),
          types: (merged['unit_type_defs'] as List?)?.cast<Map<String, dynamic>>(),
          weather: (merged['weather_defs'] as List?)?.cast<Map<String, dynamic>>(),
        ));
    return _json(req, {'errors': errs, 'preview': _oobPreview(merged)});
  }

  if (req.method == 'POST' && p == '/api/instances') {
    final b = await body();
    final scenarioId = b['scenario'] as String;
    final sc = store.loadScenario(scenarioId);
    if (sc == null) return _json(req, {'error': '剧本不存在: $scenarioId'}, 404);
    final overrides = b['overrides'] == null ? <String, dynamic>{} : Map<String, dynamic>.from(b['overrides'] as Map);
    final merged = overrides.isEmpty ? sc : deepMerge(sc, overrides);
    final errs = Scenario(sc).validate(Library.standard().withOverrides(
          terrain: (merged['terrain_defs'] as List?)?.cast<Map<String, dynamic>>(),
          types: (merged['unit_type_defs'] as List?)?.cast<Map<String, dynamic>>(),
          weather: (merged['weather_defs'] as List?)?.cast<Map<String, dynamic>>(),
        ))
      ..addAll(_validateOverrides(overrides));
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

List<String> _validateOverrides(Map<String, dynamic> ov) {
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
          if (!library.types.containsKey(u['type'])) errs.add('未知兵种 ${u['type']}');
        }
        final budget = cfg['points_budget'];
        if (budget is num && budget > 0) {
          final used = library.oobPoints(oob.cast<Map<String, dynamic>>());
          if (used > budget + 0.001) {
            errs.add('阵营 ${e.key} 点数 ${used.toStringAsFixed(0)} 超出预算 ${budget.toStringAsFixed(0)}');
          }
        }
      }
    }
  }
  final wx = ov['weather'];
  if (wx is Map && wx['initial'] != null && !library.weather.containsKey(wx['initial'])) {
    errs.add('未知天气 ${wx['initial']}');
  }
  return errs;
}

Map<String, dynamic> _oobPreview(Map<String, dynamic> merged) {
  final sides = merged['sides'] as Map? ?? {};
  final out = <String, dynamic>{};
  sides.forEach((k, v) {
    final cfg = v as Map;
    final oob = (cfg['oob'] as List? ?? []).cast<Map<String, dynamic>>();
    out[k as String] = {
      'points': library.oobPoints(oob),
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
