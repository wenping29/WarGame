// 多人链路冒烟：四个终端真实接入一个房间，验证双盲、迷雾裁剪、导演部结算。
// 用法：先启动服务端，再 dart run bin/smoke_ws.dart [port]
import 'dart:convert';
import 'dart:io';

class Client {
  final String role;
  final WebSocket sock;
  final List<Map<String, dynamic>> states = [];
  final List<Map<String, dynamic>> events = [];
  final List<String> errors = [];
  Map<String, dynamic>? welcome;
  Map<String, dynamic>? lastSaved;
  Map<String, dynamic>? kicked;
  Client(this.role, this.sock) {
    sock.listen((raw) {
      final m = Map<String, dynamic>.from(jsonDecode(raw as String) as Map);
      switch (m['type']) {
        case 'kicked':
          kicked = m;
          break;
        case 'welcome':
          welcome = m;
          states.add(m['view'] as Map<String, dynamic>);
          break;
        case 'state':
          states.add(m['view'] as Map<String, dynamic>);
          break;
        case 'events':
          for (final e in (m['events'] as List)) {
            events.add(Map<String, dynamic>.from(e as Map));
          }
          break;
        case 'saved':
          lastSaved = m;
          break;
        case 'error':
          errors.add(m['message'] as String);
          break;
      }
    });
  }
  void send(Map<String, dynamic> m) => sock.add(jsonEncode(m));
  Map<String, dynamic> get view => states.last;
  bool? get readyFlag {
    if (role == 'director' || role == 'spectator') return null;
    final r = view['ready'];
    return r is bool ? r : null;
  }
  Future<void> waitFor(bool Function() cond, {int ms = 4000}) async {
    final deadline = DateTime.now().add(Duration(milliseconds: ms));
    while (!cond() && DateTime.now().isBefore(deadline)) {
      await Future.delayed(Duration(milliseconds: 50));
    }
  }
}

int pass = 0, fail = 0;
void check(String name, bool ok, [String d = '']) {
  if (ok) {
    pass++;
    print('  ✓ $name');
  } else {
    fail++;
    print('  ✗ $name ${d.isEmpty ? '' : '—— $d'}');
  }
}

Future<Map<String, dynamic>> _post(int port, String path, Map<String, dynamic> body) async {
  final cli = HttpClient();
  final req = await cli.postUrl(Uri.parse('http://127.0.0.1:$port$path'));
  req.headers.contentType = ContentType.json;
  req.write(jsonEncode(body));
  final res = await req.close();
  final txt = await res.transform(utf8.decoder).join();
  cli.close();
  return Map<String, dynamic>.from(jsonDecode(txt) as Map);
}

Future<WebSocket> _open(int port, String code, String role, [Map<String, String>? extra]) {
  final q = <String>['room=$code', 'role=$role'];
  extra?.forEach((k, v) => q.add('$k=$v'));
  return WebSocket.connect('ws://127.0.0.1:$port/ws?' + q.join('&'));
}

Future<void> main(List<String> args) async {
  final port = args.isEmpty ? 8770 : int.parse(args.first);

  print('HEXFRONT-WEB 多人链路冒烟');
  // ① 建副本（蓝方交给 AI 副官代管，便于单端测试）
  final created = await _post(port, '/api/instances', {
    'scenario': 'rhine_corridor', 'name': '冒烟房间', 'seed': 555, 'ai_blue': true
  });
  if (created['code'] == null) {
    print('创建副本失败：$created');
    exit(1);
  }
  final code = created['code'] as String;
  print('房间 $code 已创建');

  final red = Client('red', await _open(port, code, 'red'));
  await red.waitFor(() => red.welcome != null);
  final blue = Client('blue', await _open(port, code, 'blue'));
  await blue.waitFor(() => blue.welcome != null);
  final dir = Client('director', await _open(port, code, 'director'));
  await dir.waitFor(() => dir.welcome != null);
  final spec = Client('spectator', await _open(port, code, 'spectator'));
  await spec.waitFor(() => spec.welcome != null);

  print('\n[1] 角色接入与视图隔离');
  check('红方收到己方视图', red.view['kind'] == 'side' && (red.view['own'] as List).isNotEmpty);
  check('蓝方收到己方视图', blue.view['kind'] == 'side' && (blue.view['own'] as List).isNotEmpty);
  check('导演部为全知视图', dir.view['kind'] == 'director' && (dir.view['units'] as List).length > (red.view['own'] as List).length);
  check('观战非全知：无未暴露敌方明细', spec.view['kind'] == 'spectator' && !(spec.view as Map).containsKey('units'));
  final redOwnIds = (red.view['own'] as List).cast<Map<String, dynamic>>().map((o) => o['id'] as String).toSet();
  final blueIds = (dir.view['units'] as List).cast<Map<String, dynamic>>().where((u) => u['side'] == 'blue').map((u) => u['id'] as String).toSet();
  check('红蓝单位集合不交叉', redOwnIds.intersection(blueIds).isEmpty);

  print('\n[2] 阵营占用');
  var rejected = false;
  try {
    final second = await _open(port, code, 'red').timeout(Duration(seconds: 3));
    await second.close();
  } catch (_) {
    rejected = true;
  }
  check('红位已被占用时第二次加入被拒', rejected);

  print('\n[3] 双盲指令与结算');
  final mine = (red.view['own'] as List).cast<Map<String, dynamic>>();
  final mover = mine.firstWhere(
      (u) => u['cls'] == 'foot' || u['cls'] == 'motor' || u['cls'] == 'mech',
      orElse: () => mine.first);
  final q = (mover['q'] as num).toInt(), r = (mover['r'] as num).toInt();
  red.send({
    'type': 'orders',
    'replace': true,
    'commands': [
      {
        'op': 'move', 'unit': mover['id'], 'path': [
        [q, r], [q + 1, r]
      ]
      }
    ]
  });
  await red.waitFor(() => (red.view['draft'] as List?)?.isNotEmpty == true, ms: 3000);
  check('指令草案回传本方', (red.view['draft'] as List?)?.isNotEmpty == true);
  check('蓝方看不到红方指令草案', (blue.view['draft'] as List?)?.isEmpty != false);
  final dirOrders = (dir.view['orders'] as Map?)?['red'] as List?;
  check('导演部可见双方已受领指令', dirOrders != null && dirOrders.isNotEmpty);

  // 非法指令：跳格 → ready 时被服务端拦下
  red.send({
    'type': 'orders',
    'replace': true,
    'commands': [
      {
        'op': 'move', 'unit': mover['id'], 'path': [
        [q, r], [q + 4, r]
      ]
      }
    ]
  });
  await red.waitFor(() => (red.view['draft'] as List?)?.isNotEmpty == true, ms: 2000);
  red.send({'type': 'ready', 'value': true});
  await red.waitFor(
      () => red.errors.isNotEmpty || (red.view['errors'] as List?)?.isNotEmpty == true,
      ms: 3000);
  check('非法指令被服务端拒绝并回传原因',
      red.errors.isNotEmpty || (red.view['errors'] as List?)?.isNotEmpty == true,
      red.errors.join(';'));
  check('非法指令下 ready 不生效（不污染对局）', red.readyFlag != true);

  // 改为合法指令并确认就绪
  red.send({
    'type': 'orders',
    'replace': true,
    'commands': [
      {
        'op': 'move', 'unit': mover['id'], 'path': [
        [q, r], [q + 1, r]
      ]
      }
    ]
  });
  await red.waitFor(() => (red.view['draft'] as List?)?.isNotEmpty == true, ms: 2000);
  red.send({'type': 'ready', 'value': true});
  await red.waitFor(() => red.readyFlag == true, ms: 2000);
  check('合法指令后就绪成功', red.readyFlag == true);

  // 导演部强制结算
  final turnBefore = red.view['turn'] as int;
  dir.send({'type': 'resolve'});
  await red.waitFor(() => (red.view['turn'] as int) > turnBefore, ms: 8000);
  check('导演部触发结算，回合推进', (red.view['turn'] as int) == turnBefore + 1);
  check('红方收到本方事件', red.events.isNotEmpty);
  check('蓝方看不到红方私有机动事件',
      !blue.events.any((e) => e['code'] == 'move' && (e['text'] as String).startsWith('R')));

  print('\n[4] 导演部导调');
  dir.send({'type': 'director_cmd', 'action': 'weather', 'id': 'fog'});
  await dir.waitFor(() => (dir.view['weather'] as Map?)?['id'] == 'fog', ms: 3000);
  check('导演部改天气生效', (dir.view['weather'] as Map?)?['id'] == 'fog');
  dir.send({'type': 'director_cmd', 'action': 'ai', 'side': 'blue', 'on': true});
  await dir.waitFor(() => (dir.view['ai'] as Map?)?['blue'] == true, ms: 3000);
  check('AI 代管可切换', (dir.view['ai'] as Map?)?['blue'] == true);
  dir.send({'type': 'director_cmd', 'action': 'fog', 'on': false});
  await dir.waitFor(() => (dir.view['flags'] as Map?)?['fog'] == false, ms: 3000);
  check('导演部可关闭迷雾（教学模式）', (dir.view['flags'] as Map?)?['fog'] == false);
  final openSpec = Client('spectator', await _open(port, code, 'spectator'));
  await openSpec.waitFor(() => openSpec.welcome != null, ms: 3000);
  dir.send({'type': 'director_cmd', 'action': 'spectator_omniscient', 'on': true});
  await openSpec.waitFor(() => openSpec.view['kind'] == 'director', ms: 4000);
  check('观战全知档可切换', openSpec.view['kind'] == 'director');
  dir.send({'type': 'director_cmd', 'action': 'fog', 'on': true});

  print('\n[5] 存档与载入');
  dir.send({'type': 'save'});
  await dir.waitFor(() => dir.lastSaved != null, ms: 3000);
  final scenarios = await _get(port, '/api/scenarios');
  final saves = (scenarios['saves'] as List).cast<Map<String, dynamic>>();
  check('存档列表可见本局', saves.any((s) => s['instance'] == created['instance']),
      '${saves.length} 个存档');

  final loaded = await _post(port, '/api/saves/load', {'instance': created['instance']});
  check('存档可载入为新房间', loaded['code'] != null, '${loaded['error']}');
  if (loaded['code'] != null) {
    final back = Client('director', await _open(port, loaded['code'] as String, 'director'));
    await back.waitFor(() => back.welcome != null, ms: 3000);
    check('载入后回合数一致', (back.view['turn'] as int) == (dir.view['turn'] as int),
        '${back.view['turn']} vs ${dir.view['turn']}');
  }

  print('\n[6] 断线重连与会话释放');
  final blueTok = blue.welcome!['token'] as String;
  await blue.sock.close();
  Map<String, dynamic>? info;
  for (var i = 0; i < 20; i++) {
    info = await _get(port, '/api/room/$code');
    if (info['side_taken']?['blue'] != true) break;
    await Future.delayed(Duration(milliseconds: 100));
  }
  check('对抗方断开后阵营位立刻释放', info?['side_taken']?['blue'] == false,
      '${info?['side_taken']}');
  final blue2 = Client('blue', await _open(port, code, 'blue', {'token': blueTok}));
  await blue2.waitFor(() => blue2.welcome != null, ms: 3000);
  check('同 token 重连可接管本方视图',
      blue2.welcome != null && (blue2.view['own'] as List).isNotEmpty);
  final t6 = blue2.view['turn'] as int;
  dir.send({'type': 'resolve'});
  await blue2.waitFor(() => blue2.view['turn'] != t6, ms: 8000);
  check('重连终端持续收到服务端推送（会话未成为孤儿）',
      blue2.view['turn'] == t6 + 1, '${blue2.view['turn']} vs $t6');

  final blueTok2 = blue2.welcome!['token'] as String;
  final taker = Client('blue', await _open(port, code, 'blue', {'token': blueTok2}));
  await taker.waitFor(() => taker.welcome != null, ms: 3000);
  await blue2.waitFor(() => blue2.kicked != null, ms: 3000);
  check('同角色双开：旧终端收到 kicked（不会静默失联）', blue2.kicked != null);

  print('\n[7] AI 代管自动推进');
  final t7 = red.view['turn'] as int;
  red.send({'type': 'ready', 'value': true});
  await red.waitFor(() => red.view['turn'] != t7, ms: 6000);
  check('对面 AI 代管时，人类就绪即自动结算（不再空等）',
      red.view['turn'] == t7 + 1, '${red.view['turn']} vs $t7');

  print('\n合计：$pass 通过 / $fail 失败');
  exit(fail == 0 ? 0 : 1);
}

Future<Map<String, dynamic>> _get(int port, String path) async {
  final cli = HttpClient();
  final res = await cli.getUrl(Uri.parse('http://127.0.0.1:$port$path')).then((r) => r.close());
  final txt = await res.transform(utf8.decoder).join();
  cli.close();
  return Map<String, dynamic>.from(jsonDecode(txt) as Map);
}
