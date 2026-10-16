// 持久化：剧本库、GIS 数据包、副本存档（追加指令日志 + 周期快照）。纯文件，无数据库。
import 'dart:convert';
import 'dart:io';

const jsonUtf = JsonEncoder.withIndent('  ');

class Store {
  final Directory root; // 项目 data 目录
  final Directory saves;
  Store({required this.root, required this.saves}) {
    if (!saves.existsSync()) saves.createSync(recursive: true);
  }

  List<Map<String, dynamic>> listScenarios() {
    final dir = Directory('${root.path}${Platform.pathSeparator}scenarios');
    if (!dir.existsSync()) return [];
    final out = <Map<String, dynamic>>[];
    final files = dir.listSync().whereType<File>().where((f) => f.path.endsWith('.json')).toList()
      ..sort((a, b) => a.path.compareTo(b.path));
    for (final f in files) {
      try {
        final m = jsonDecode(f.readAsStringSync());
        if (m is Map<String, dynamic>) {
          out.add({
            'file': f.uri.pathSegments.last,
            'id': m['id'],
            'name': m['name'],
            'era': m['era'],
            'scale': m['scale'],
            'description': m['description'],
            'gis_package': m['gis_package'],
            'max_turns': m['max_turns'],
            'sides': (m['sides'] as Map?)?.keys.toList(),
            'unit_count': _oobUnitCount(m['sides']),
          });
        }
      } catch (_) {/* 跳过坏剧本 */}
    }
    return out;
  }

  Map<String, dynamic>? loadScenario(String idOrFile) {
    final dir = Directory('${root.path}${Platform.pathSeparator}scenarios');
    if (!dir.existsSync()) return null;
    for (final f in dir.listSync().whereType<File>()) {
      final fname = f.uri.pathSegments.last;
      if (fname == idOrFile || fname == '$idOrFile.json') {
        try {
          final m = jsonDecode(f.readAsStringSync());
          if (m is Map<String, dynamic>) return m;
        } catch (_) {
          return null;
        }
      }
    }
    return null;
  }

  Map<String, dynamic>? loadGisPackage(String pkg) {
    final f = File('${root.path}${Platform.pathSeparator}gis${Platform.pathSeparator}$pkg');
    if (!f.existsSync()) return null;
    try {
      final m = jsonDecode(f.readAsStringSync());
      return m is Map<String, dynamic> ? m : null;
    } catch (_) {
      return null;
    }
  }

  // ───────── 副本存档 ─────────

  Directory instanceDir(String instanceId) {
    final d = Directory('${saves.path}${Platform.pathSeparator}$instanceId');
    if (!d.existsSync()) d.createSync(recursive: true);
    return d;
  }

  void writeInit(Map<String, dynamic> meta, Map<String, dynamic> snapshot) {
    final d = instanceDir(meta['instance'] as String);
    File('${d.path}${Platform.pathSeparator}meta.json').writeAsStringSync(jsonUtf.convert(meta));
    File('${d.path}${Platform.pathSeparator}init.json').writeAsStringSync(jsonEncode(snapshot));
  }

  void appendTurn(String instanceId, Map<String, dynamic> record) {
    final d = instanceDir(instanceId);
    final f = File('${d.path}${Platform.pathSeparator}turns.jsonl');
    f.writeAsStringSync('${jsonEncode(record)}\n', mode: FileMode.append);
  }

  void writeSnapshot(String instanceId, int turn, Map<String, dynamic> snapshot) {
    final d = instanceDir(instanceId);
    File('${d.path}${Platform.pathSeparator}snapshot_t$turn.json').writeAsStringSync(jsonEncode(snapshot));
    final meta = File('${d.path}${Platform.pathSeparator}meta.json');
    if (meta.existsSync()) {
      try {
        final m = Map<String, dynamic>.from(jsonDecode(meta.readAsStringSync()) as Map);
        m['last_turn'] = turn;
        m['updated_at'] = DateTime.now().toIso8601String();
        meta.writeAsStringSync(jsonUtf.convert(m));
      } catch (_) {}
    }
  }

  List<Map<String, dynamic>> listSaves() {
    if (!saves.existsSync()) return [];
    final out = <Map<String, dynamic>>[];
    for (final d in saves.listSync().whereType<Directory>()) {
      final mf = File('${d.path}${Platform.pathSeparator}meta.json');
      if (!mf.existsSync()) continue;
      try {
        final m = Map<String, dynamic>.from(jsonDecode(mf.readAsStringSync()) as Map);
        final turns = File('${d.path}${Platform.pathSeparator}turns.jsonl');
        m['turns_recorded'] = turns.existsSync() ? turns.readAsLinesSync().where((l) => l.trim().isNotEmpty).length : 0;
        out.add(m);
      } catch (_) {}
    }
    out.sort((a, b) => ((b['created_at'] as String?) ?? '').compareTo((a['created_at'] as String?) ?? ''));
    return out;
  }

  Map<String, dynamic>? readMeta(String instanceId) {
    final f = File('${instanceDir(instanceId).path}${Platform.pathSeparator}meta.json');
    if (!f.existsSync()) return null;
    try {
      return Map<String, dynamic>.from(jsonDecode(f.readAsStringSync()) as Map);
    } catch (_) {
      return null;
    }
  }

  /// 载入副本：优先最新快照 + 重放其后的指令记录（由 room 层完成重放）
  Map<String, dynamic>? readLatestSnapshot(String instanceId) {
    final d = instanceDir(instanceId);
    if (!d.existsSync()) return null;
    final snaps = d.listSync().whereType<File>().where((f) => f.path.contains('snapshot_t')).toList()
      ..sort((a, b) {
        int t(File f) => int.tryParse(f.path.split('snapshot_t').last.split('.').first) ?? 0;
        return t(a).compareTo(t(b));
      });
    if (snaps.isNotEmpty) {
      try {
        return Map<String, dynamic>.from(jsonDecode(snaps.last.readAsStringSync()) as Map);
      } catch (_) {}
    }
    final init = File('${d.path}${Platform.pathSeparator}init.json');
    if (init.existsSync()) {
      try {
        return Map<String, dynamic>.from(jsonDecode(init.readAsStringSync()) as Map);
      } catch (_) {}
    }
    return null;
  }

  List<Map<String, dynamic>> readTurnRecords(String instanceId) {
    final f = File('${instanceDir(instanceId).path}${Platform.pathSeparator}turns.jsonl');
    if (!f.existsSync()) return [];
    final out = <Map<String, dynamic>>[];
    for (final line in f.readAsLinesSync()) {
      if (line.trim().isEmpty) continue;
      try {
        final m = jsonDecode(line);
        if (m is Map<String, dynamic>) out.add(m);
      } catch (_) {}
    }
    return out;
  }

  Map<String, String> listGisPackages() {
    final dir = Directory('${root.path}${Platform.pathSeparator}gis');
    if (!dir.existsSync()) return {};
    final out = <String, String>{};
    for (final f in dir.listSync().whereType<File>()) {
      final name = f.uri.pathSegments.last;
      if (!name.endsWith('.json')) continue;
      try {
        final m = Map<String, dynamic>.from(jsonDecode(f.readAsStringSync()) as Map);
        final meta = m['meta'] as Map?;
        out[name] = '(${meta?['source'] ?? '?'}) ${meta?['cell_km'] ?? '?'}km/格 · ${meta?['width'] ?? '?'}×${meta?['height'] ?? '?'}';
      } catch (_) {
        out[name] = '(解析失败)';
      }
    }
    return out;
  }
}

int _oobUnitCount(dynamic sides) {
  if (sides is! Map) return 0;
  var n = 0;
  for (final v in sides.values) {
    if (v is! Map) continue;
    final oob = v['oob'];
    if (oob is! List) continue;
    for (final u in oob) {
      if (u is Map) n += ((u['qty'] as num?)?.toInt() ?? 1);
    }
  }
  return n;
}

String randomCode(int len) {
  const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  final rnd = DateTime.now().microsecondsSinceEpoch;
  final b = StringBuffer();
  var v = rnd;
  for (var i = 0; i < len; i++) {
    v = (v * 1103515245 + 12345) & 0x7FFFFFFF;
    b.write(chars[v % chars.length]);
  }
  return b.toString();
}
