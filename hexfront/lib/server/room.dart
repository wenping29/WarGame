// 房间：角色会话、双盲指令屏障、结算触发、按角色裁剪下发、副本存档落盘。
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import '../engine/commands.dart';
import '../engine/fog.dart';
import '../engine/state.dart';
import '../engine/turn.dart';
import 'store.dart';
import 'ai.dart';

class Session {
  final String token;
  String role; // director|red|blue|spectator
  String name;
  WebSocket? sock;
  Session(this.token, this.role, this.name);
}

class Room {
  final String code;
  final Store store;
  final GameState g;
  final Map<String, dynamic> scenarioRaw;
  final Map<String, dynamic> overrides;
  final DateTime createdAt = DateTime.now();

  final Map<String, List<Command>> orders = {'red': [], 'blue': []};
  final Map<String, bool> ready = {'red': false, 'blue': false};
  final Map<String, List<String>> lastErrors = {};
  final List<Session> sessions = [];
  final List<Map<String, dynamic>> frames = []; // 回放帧（导演部视角 + 各方视图摘要）
  bool autoResolve = true;
  bool aiRed = false, aiBlue = false;
  bool spectatorOmniscient = false; // 观战全知档（教学/赛后）
  int saveEveryTurns = 3;

  Room({
    required this.code,
    required this.store,
    required this.g,
    required this.scenarioRaw,
    required this.overrides,
  });

  String get instanceId => g.instanceId;
  int get turn => g.turn;

  Session? byToken(String token) {
    for (final s in sessions) {
      if (s.token == token) return s;
    }
    return null;
  }

  List<Session> roleSessions(String role, [String? side]) =>
      sessions.where((s) => s.role == role && (side == null || s.role == side)).toList();

  bool sideTaken(String side) => sessions.any((s) => s.role == side);

  void addSession(Session s) => sessions.add(s);

  Map<String, dynamic> viewFor(Session s) {
    switch (s.role) {
      case 'director':
        final v = directorView(g);
        v['orders'] = {
          for (final side in ['red', 'blue'])
            side: orders[side]!.map((c) => c.toJson()).toList()
        };
        v['ready'] = ready;
        v['last_errors'] = lastErrors;
        v['ai'] = {'red': aiRed, 'blue': aiBlue};
        v['spectator_omniscient'] = spectatorOmniscient;
        return v;
      case 'spectator':
        return spectatorOmniscient ? directorView(g) : spectatorView(g);
      default:
        final v = sideView(g, s.role);
        v['draft'] = orders[s.role]!.map((c) => c.toJson()).toList();
        v['ready'] = ready[s.role] ?? false;
        v['errors'] = (lastErrors[s.role] ?? []).map((e) => jsonDecode(e)).toList();
        v['ai_managed'] = s.role == 'red' ? aiRed : aiBlue;
        return v;
    }
  }

  void pushState() {
    for (final s in sessions) {
      if (s.sock == null) continue;
      try {
        s.sock!.add(jsonEncode({'type': 'state', 'view': viewFor(s)}));
      } catch (_) {}
    }
  }

  /// 事件按可见阵营分发（导演部与全知观战看全部）
  void pushEvents(List<WarEvent> events) {
    for (final s in sessions) {
      final visible = events.where((e) {
        if (s.role == 'director') return true;
        if (s.role == 'spectator') return spectatorOmniscient ? true : e.sides.contains('spectator');
        return e.sides.contains(s.role);
      }).toList();
      if (visible.isEmpty || s.sock == null) continue;
      try {
        s.sock!.add(jsonEncode({
          'type': 'events',
          'events': visible.map((e) => e.toJson()).toList(),
        }));
      } catch (_) {}
    }
  }

  String? submitOrders(String role, List<Command> cmds, bool replace) {
    if (role != 'red' && role != 'blue') return '只有对抗方可以提交指令';
    if (g.over) return '推演已结束';
    if (replace) orders[role] = List<Command>.from(cmds);
    else orders[role] = [...orders[role]!, ...cmds];
    ready[role] = false;
    return null;
  }

  String? clearOrders(String role) {
    if (role != 'red' && role != 'blue') return '无权操作';
    orders[role] = [];
    ready[role] = false;
    return null;
  }

  String? setReady(String role, bool v) {
    if (role != 'red' && role != 'blue') return '无权操作';
    if (v) {
      final errs = validateOrders(g, role, orders[role]!);
      lastErrors[role] = errs.map((e) => jsonEncode(e.toJson())).toList();
      if (errs.isNotEmpty) return '存在 ${errs.length} 条非法指令，已剔除后需重新确认';
    }
    ready[role] = v;
    return null;
  }

  /// 结算一回合：双盲指令 → 引擎裁决 → 落盘 → 推送。
  FutureOr<TurnReport?> resolve({String? byRole, bool force = false}) async {
    if (g.over) return null;
    // AI 副官按本方迷雾视图补全未下令单位
    if (aiRed) _aiFill('red');
    if (aiBlue) _aiFill('blue');

    final t = g.turn;
    final redOrders = List<Command>.from(orders['red']!);
    final blueOrders = List<Command>.from(orders['blue']!);
    final rep = resolveTurn(g, {'red': redOrders, 'blue': blueOrders});

    for (final s in ['red', 'blue']) {
      lastErrors[s] = rep.rejected.where((e) => _unitSide(e.unitId) == s).map((e) => jsonEncode(e.toJson())).toList();
    }
    orders['red'] = [];
    orders['blue'] = [];
    ready['red'] = false;
    ready['blue'] = false;

    // 落盘：追加指令记录 + 周期快照 + 回放帧
    store.appendTurn(instanceId, {
      'turn': t,
      'at': DateTime.now().toIso8601String(),
      'resolved_by': byRole ?? 'auto',
      'red': redOrders.map((c) => c.toJson()).toList(),
      'blue': blueOrders.map((c) => c.toJson()).toList(),
      'rejected': rep.rejected.map((e) => e.toJson()).toList(),
      'fp_before': rep.fingerprintBefore,
      'fp_after': rep.fingerprintAfter,
    });
    final frame = {
      'turn': t,
      'hour': g.hourOfDay,
      'weather': g.weatherId,
      'director': directorView(g),
      'events': rep.events.map((e) => e.toJson()).toList(),
      'battles': rep.battles,
    };
    frames.add(frame);
    if (frames.length > 200) frames.removeRange(0, frames.length - 200);
    if ((g.turn - 1) % saveEveryTurns == 0 || g.over) {
      store.writeSnapshot(instanceId, g.turn - 1, g.toJson());
    }

    pushEvents(rep.events);
    pushState();
    return rep;
  }

  String? _unitSide(String id) => g.findUnit(id)?.side;

  void _aiFill(String side) {
    final mine = (g.tracks[side] ?? {}).isEmpty
        ? sideView(g, side)
        : sideView(g, side);
    final cmds = aiOrders(g, side, mine);
    final have = orders[side]!.map((c) => c.unitId).toSet();
    orders[side] = [...orders[side]!, ...cmds.where((c) => !have.contains(c.unitId))];
  }

  Map<String, dynamic> publicInfo() => {
        'code': code,
        'instance': instanceId,
        'name': g.name,
        'scenario': g.scenarioId,
        'turn': g.turn,
        'max_turns': g.maxTurns,
        'weather': g.weatherId,
        'over': g.over,
        'winner': g.winner,
        'roles': {
          for (final s in ['director', 'red', 'blue', 'spectator']) s: roleSessions(s).length
        },
        'side_taken': {'red': sideTaken('red'), 'blue': sideTaken('blue')},
        'ai': {'red': aiRed, 'blue': aiBlue},
        'created_at': createdAt.toIso8601String(),
        'fog': g.flags['fog'],
        'width': g.width,
        'height': g.height,
      };

  /// 复盘：回放帧列表（导演部/存档模式用）
  List<Map<String, dynamic>> replayFrames() => frames;
}

/// 房间注册表
class RoomRegistry {
  final Map<String, Room> rooms = {};
  final Store store;
  RoomRegistry(this.store);

  Room? byCode(String code) => rooms[code.toUpperCase()];

  Room create({
    required GameState g,
    required Map<String, dynamic> scenarioRaw,
    required Map<String, dynamic> overrides,
  }) {
    var code = randomCode(6);
    while (rooms.containsKey(code)) {
      code = randomCode(6);
    }
    final r = Room(code: code, store: store, g: g, scenarioRaw: scenarioRaw, overrides: overrides);
    rooms[code] = r;
    return r;
  }

  List<Map<String, dynamic>> list() => rooms.values.map((r) => r.publicInfo()).toList();
}
