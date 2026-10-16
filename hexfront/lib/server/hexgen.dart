// GIS 六角格生成服务：把 tools/gis/hexgen.py 包成「能力探测 / 格网方案 / 生成作业」三件事。
// 生成逻辑只有一份（python），服务端只负责调度、校验与进度转发。
import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// 单次生成的格数上限：超过就把前端和结算都拖垮，直接拒收（推荐档在 2400 左右）。
const int gisMaxCells = 30000;
const int gisMaxLogLines = 240;

class GisError implements Exception {
  final String message;
  GisError(this.message);
  @override
  String toString() => message;
}

class GisJobSpec {
  final List<double> bbox; // 西 南 东 北
  final double cellKm;
  final String name;
  final String dem; // auto|local|synthetic
  final String landuse; // auto|proxy|off
  final bool roads;
  final int seed;
  final bool offline;
  GisJobSpec({
    required this.bbox,
    required this.cellKm,
    required this.name,
    required this.dem,
    required this.landuse,
    required this.roads,
    required this.seed,
    required this.offline,
  });

  static GisJobSpec fromJson(Map<String, dynamic> j) {
    final bb = j['bbox'];
    if (bb is! List || bb.length != 4) throw GisError('bbox 需要 [西, 南, 东, 北]');
    final nums = bb.map((e) => (e as num).toDouble()).toList();
    for (final v in nums) {
      if (!v.isFinite) throw GisError('bbox 含非法数值');
    }
    if (!(nums[0] < nums[2] && nums[1] < nums[3])) throw GisError('bbox 需满足 西<东 且 南<北');
    if (nums[1] < -84.5 || nums[3] > 84.5) throw GisError('墨卡托格网在极区无意义，纬度限制在 ±84.5°');
    if (nums[2] - nums[0] > 8.0 || nums[3] - nums[1] > 8.0) throw GisError('范围框单边不得超过 8°（先选小一些的战场）');
    final cell = (j['cell_km'] as num?)?.toDouble();
    if (cell == null || cell <= 0 || cell > 30) throw GisError('对边距需在 0..30 km 之间');
    final dem = j['dem'] as String? ?? 'auto';
    if (!['auto', 'local', 'synthetic'].contains(dem)) throw GisError('未知高程档位 $dem');
    final lu = j['landuse'] as String? ?? 'auto';
    if (!['auto', 'proxy', 'off'].contains(lu)) throw GisError('未知土地覆盖档位 $lu');
    return GisJobSpec(
      bbox: nums,
      cellKm: cell,
      name: uniqueName(j['name'] as String?),
      dem: dem,
      landuse: lu,
      roads: j['roads'] != false,
      seed: (j['seed'] as num?)?.toInt() ?? 7,
      offline: j['offline'] == true,
    );
  }
}

/// 包名只允许安全字符，落在 data/gis/ 里，绝不接受路径分隔符。
String uniqueName(String? raw) {
  final cleaned = (raw ?? '')
      .replaceAll(RegExp(r'[^A-Za-z0-9_.\-]'), '_')
      .replaceAll(RegExp(r'^\.+'), '')
      .replaceAll('.json', '');
  final base = cleaned.isEmpty ? 'gis' : (cleaned.length > 48 ? cleaned.substring(0, 48) : cleaned);
  final stamp = DateTime.now().millisecondsSinceEpoch ~/ 1000;
  return '${base}_$stamp';
}

class GisJob {
  final String id;
  final GisJobSpec spec;
  final String pkgName; // data/gis/<pkgName>.json
  String status = 'queued'; // queued|running|done|failed
  final List<String> log = [];
  Map<String, dynamic>? meta;
  String? error;
  final DateTime createdAt = DateTime.now();
  DateTime? startedAt, finishedAt;
  GisJob(this.id, this.spec, this.pkgName);

  Map<String, dynamic> toJson() => {
        'id': id,
        'status': status,
        'name': '$pkgName.json',
        'spec': {
          'bbox': spec.bbox,
          'cell_km': spec.cellKm,
          'dem': spec.dem,
          'landuse': spec.landuse,
          'roads': spec.roads,
          'seed': spec.seed,
          'offline': spec.offline,
        },
        'log': log,
        'error': error,
        'meta': meta,
        'created_at': createdAt.toIso8601String(),
        'started_at': startedAt?.toIso8601String(),
        'finished_at': finishedAt?.toIso8601String(),
      };
}

class GisService {
  final Directory repoRoot;
  final Directory gisDir;
  GisService({required this.repoRoot, required this.gisDir});

  String get scriptPath => '${repoRoot.path}${Platform.pathSeparator}tools${Platform.pathSeparator}gis${Platform.pathSeparator}hexgen.py';
  String? _python;
  Future<String>? _probing;
  Map<String, dynamic>? _caps;
  DateTime? _capsAt;
  final Map<String, Map<String, dynamic>> _planCache = {};
  final Map<String, GisJob> _jobs = {};
  final List<GisJob> _queue = [];
  GisJob? _running;
  int _seq = 0;

  Map<String, String> get _env => {
        'PYTHONIOENCODING': 'utf-8',
        'PYTHONUTF8': '1',
        'PYTHONUNBUFFERED': '1',
        if (Platform.environment['GDAL_DATA'] != null) 'GDAL_DATA': Platform.environment['GDAL_DATA']!,
      };

  Future<String> _pythonExe() async {
    final fromEnv = Platform.environment['HEXGEN_PYTHON']?.trim();
    if (fromEnv != null && fromEnv.isNotEmpty) return fromEnv;
    if (_python != null) return _python!;
    _probing ??= () async {
      for (final cand in ['python', 'py', 'python3']) {
        try {
          final r = await Process.run(cand, ['--version'], runInShell: Platform.isWindows);
          if (r.exitCode == 0) return cand;
        } catch (_) {}
      }
      throw GisError('未找到 python：GIS 生成需要本机 Python（可用环境变量 HEXGEN_PYTHON 指定）');
    }();
    _python = await _probing!;
    _probing = null;
    return _python!;
  }

  Future<Map<String, dynamic>> capabilities({bool refresh = false}) async {
    if (!refresh && _caps != null && _capsAt != null &&
        DateTime.now().difference(_capsAt!) < const Duration(minutes: 5)) {
      return _caps!;
    }
    String exe;
    try {
      exe = await _pythonExe();
    } on GisError catch (e) {
      _caps = {'available': false, 'python': null, 'rasterio': false, 'reason': e.message};
      _capsAt = DateTime.now();
      return _caps!;
    }
    try {
      final r = await Process.run(exe, [scriptPath, '--capabilities'],
          workingDirectory: repoRoot.path, environment: _env,
          runInShell: Platform.isWindows, stdoutEncoding: utf8, stderrEncoding: utf8);
      final line = _findLine(r.stdout as String, '##CAP## ');
      if (line == null) throw GisError('能力探测无输出：${(r.stderr as String).split('\n').last}');
      final m = Map<String, dynamic>.from(jsonDecode(line) as Map);
      _caps = {
        'available': true,
        'python': m['python'],
        'rasterio': m['rasterio'] == true,
        'gdal_version': m['version'],
        'reason': m['reason'],
        'dem_tif': m['dem_tif'],
        'dem_present': m['dem_present'] == true,
        'dem_info': m['dem_info'],
        'script': scriptPath,
        'landcover_online': m['rasterio'] == true,
      };
    } on GisError catch (e) {
      _caps = {'available': false, 'python': null, 'rasterio': false, 'reason': e.message};
    } catch (e) {
      _caps = {'available': false, 'python': null, 'rasterio': false, 'reason': '能力探测失败：$e'};
    }
    _capsAt = DateTime.now();
    return _caps!;
  }

  /// 格网方案：推荐对边距 + 各档行列数/格数。同一 (bbox, cell) 命中缓存直接回。
  Future<Map<String, dynamic>> plan(List<double> bbox, {double? cellKm}) async {
    final key = '${bbox.join(',')}:${cellKm ?? '-'}';
    final hit = _planCache[key];
    if (hit != null) return hit;
    final exe = await _pythonExe();
    final args = [scriptPath, '--plan', '--bbox', ...bbox.map((v) => _num(v))];
    if (cellKm != null) args.addAll(['--cell-km', _num(cellKm)]);
    final r = await Process.run(exe, args,
        workingDirectory: repoRoot.path, environment: _env, runInShell: Platform.isWindows,
        stdoutEncoding: utf8, stderrEncoding: utf8);
    final line = _findLine(r.stdout as String, '##PLAN## ');
    if (line == null) throw GisError('方案计算失败：${(r.stderr as String).trim().split('\n').last}');
    final m = Map<String, dynamic>.from(jsonDecode(line) as Map);
    if (m['ok'] != true) throw GisError('${m['error'] ?? '方案计算失败'}');
    if (_planCache.length > 64) _planCache.clear();
    _planCache[key] = m;
    return m;
  }

  Future<GisJob> startJob(GisJobSpec spec) async {
    // 用同一个 python 算法预估格数，避免客户端算错把服务器拖死
    final p = await plan(spec.bbox, cellKm: spec.cellKm);
    final cur = Map<String, dynamic>.from((p['current'] as Map?) ?? {});
    final cells = (cur['cells'] as num?)?.toInt() ?? 0;
    if (cells > gisMaxCells) {
      throw GisError('该范围会有 $cells 格，超过上限 $gisMaxCells；请扩大对边距或缩小范围框');
    }
    final id = 'G${(++_seq).toString().padLeft(4, '0')}';
    var pkg = spec.name;
    for (var n = 1; File('${gisDir.path}${Platform.pathSeparator}$pkg.json').existsSync(); n++) {
      pkg = '${spec.name}_$n';
    }
    final job = GisJob(id, spec, pkg);
    _jobs[id] = job;
    _queue.add(job);
    _drain();
    return job;
  }

  GisJob? job(String id) => _jobs[id];

  List<Map<String, dynamic>> jobs() {
    final list = _jobs.values.toList()
      ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
    return list.take(30).map((j) => j.toJson()).toList();
  }

  void _drain() {
    if (_running != null || _queue.isEmpty) return;
    final job = _queue.removeAt(0);
    _running = job;
    unawaited(_execute(job));
  }

  Future<void> _execute(GisJob job) async {
    job.status = 'running';
    job.startedAt = DateTime.now();
    _log(job, '作业开始：范围 ${job.spec.bbox.map(_num).join(', ')}° · 对边距 ${_num(job.spec.cellKm)} km');
    try {
      final exe = await _pythonExe();
      final args = [
        scriptPath,
        '--bbox', ...job.spec.bbox.map((v) => _num(v)),
        '--cell-km', _num(job.spec.cellKm),
        '--out', 'data/gis/${job.pkgName}.json',
        '--dem', job.spec.dem,
        '--landuse', job.spec.landuse,
        '--roads', job.spec.roads ? 'on' : 'off',
        '--seed', '${job.spec.seed}',
        '--print-meta',
        if (job.spec.offline) '--offline',
      ];
      final proc = await Process.start(exe, args,
          workingDirectory: repoRoot.path, environment: _env, runInShell: Platform.isWindows);
      final done = Completer<int>();
      var exitCode = 0;
      proc.exitCode.then((c) { if (!done.isCompleted) done.complete(c); });
      proc.stdout.transform(utf8.decoder).transform(const LineSplitter()).listen((l) {
        final t = l.trimRight();
        if (t.isEmpty) return;
        if (t.startsWith('##META## ')) {
          try {
            job.meta = Map<String, dynamic>.from(jsonDecode(t.substring(9)) as Map);
          } catch (_) {/* 元信息坏掉只是显示问题 */}
          return;
        }
        _log(job, t);
      }, onError: (_) {});
      proc.stderr.transform(utf8.decoder).transform(const LineSplitter()).listen((l) {
        final t = l.trimRight();
        if (t.isNotEmpty) _log(job, '! $t');
      }, onError: (_) {});
      exitCode = await done.future.timeout(const Duration(minutes: 20), onTimeout: () {
        proc.kill(ProcessSignal.sigterm);
        return -1;
      });
      final ok = job.meta?['ok'] == true;
      if (exitCode != 0 || !ok) {
        job.status = 'failed';
        final fails = (job.meta?['fails'] as num?)?.toInt();
        job.error = exitCode == -1
            ? '生成超时（20 分钟）已终止'
            : fails != null && fails > 0
                ? '自校验 ${fails} 项未通过，未产出数据包'
                : '生成进程退出码 $exitCode';
        _log(job, '失败：${job.error}');
      } else {
        job.status = 'done';
        _log(job, '完成：data/gis/${job.pkgName}.json（${job.meta?['width']}×${job.meta?['height']} 格）');
      }
    } catch (e) {
      job.status = 'failed';
      job.error = '$e';
      _log(job, '失败：$e');
    }
    job.finishedAt = DateTime.now();
    _running = null;
    _drain();
  }

  void _log(GisJob job, String line) {
    job.log.add(line);
    if (job.log.length > gisMaxLogLines) job.log.removeAt(0);
  }
}

String _num(double v) => v == v.roundToDouble() ? v.toStringAsFixed(0) : '$v';

String? _findLine(String out, String prefix) {
  String? hit;
  for (final l in const LineSplitter().convert(out)) {
    if (l.startsWith(prefix)) hit = l;
  }
  return hit == null ? null : hit.substring(prefix.length);
}
