// 指令模型与服务端校验：客户端只提交指令，一切合法性以服务端为准。
import 'fog.dart' show exposureLevel;
import 'hex.dart';
import 'state.dart';

class CommandError {
  final String unitId, reason, message;
  CommandError(this.unitId, this.reason, this.message);
  Map<String, dynamic> toJson() => {'unit': unitId, 'reason': reason, 'message': message};
}

class Command {
  final String op;      // move|attack|fire|dig|rest|air_mission|halt
  final String unitId;
  final List<Hex> path; // move 用：含起点或不含起点的连续相邻路径
  final String? target; // attack 用：敌方单位 id
  final Hex? zone;      // fire / air_mission 用：目标格
  final String mission; // air_mission: patrol|strike|recon|none

  Command({
    required this.op, required this.unitId,
    this.path = const [], this.target, this.zone, this.mission = 'none',
  });

  static Command fromJson(Map<String, dynamic> j) {
    List<Hex> parsePath(dynamic v) =>
        (v as List? ?? []).map((e) => Hex((e[0] as num).toInt(), (e[1] as num).toInt())).toList();
    Hex? parseHex(dynamic v) => v == null ? null : Hex((v[0] as num).toInt(), (v[1] as num).toInt());
    return Command(
      op: j['op'] as String,
      unitId: j['unit'] as String,
      path: parsePath(j['path']),
      target: j['target'] as String?,
      zone: parseHex(j['zone']),
      mission: (j['mission'] as String?) ?? 'none',
    );
  }

  Map<String, dynamic> toJson() => {
        'op': op, 'unit': unitId,
        'path': path.isEmpty ? null : path.map((h) => [h.q, h.r]).toList(),
        'target': target,
        'zone': zone == null ? null : [zone!.q, zone!.r],
        'mission': mission,
      };
}

/// 服务端校验：返回被拒绝的指令列表（附原因），通过的指令进入结算。
List<CommandError> validateOrders(GameState g, String side, List<Command> orders) {
  final errs = <CommandError>[];
  final fogOn = g.flags['fog'] ?? true;
  final observed = <String>{
    // 迷雾下只有已暴露（E1+）目标可被下令攻击
    for (final e in (g.tracks[side] ?? {}).entries)
      if (e.value.level >= 1) e.key
  };
  final usedUnits = <String>{};

  for (final c in orders) {
    final u = g.findUnit(c.unitId);
    if (u == null || !u.alive) {
      errs.add(CommandError(c.unitId, 'no_unit', '单位不存在或已损失'));
      continue;
    }
    if (u.side != side) {
      errs.add(CommandError(c.unitId, 'not_yours', '不能指挥他方单位'));
      continue;
    }
    if (usedUnits.contains(c.unitId)) {
      errs.add(CommandError(c.unitId, 'duplicate', '本回合该单位已受领其他指令'));
      continue;
    }
    if (u.status == UnitStatus.routed && c.op != 'rest') {
      errs.add(CommandError(c.unitId, 'routed', '溃退中只能休整'));
      continue;
    }
    usedUnits.add(c.unitId);

    switch (c.op) {
      case 'move':
        if (g.typeOf(u).cls == 'structure') {
          errs.add(CommandError(c.unitId, 'static', '固定设施不可机动'));
          break;
        }
        if (c.path.length < 2) {
          errs.add(CommandError(c.unitId, 'bad_path', '路径至少需含起点与一个目标格'));
          break;
        }
        if (!isAdjacentPath(c.path)) {
          errs.add(CommandError(c.unitId, 'not_adjacent', '路径存在跳格（非相邻移动）'));
          break;
        }
        if (c.path.first != u.pos) {
          errs.add(CommandError(c.unitId, 'path_start', '路径起点必须为单位当前格'));
          break;
        }
        for (final h in c.path.skip(1)) {
          if (!g.cells.containsKey(h.key)) {
            errs.add(CommandError(c.unitId, 'out_of_map', '路径越界'));
            break;
          }
          if (g.terrainOf(h).impassable) {
            errs.add(CommandError(c.unitId, 'impassable', '路径经过不可通行地形'));
            break;
          }
          final here = g.units.where((o) => o.alive && o.pos == h && o.side != side).toList();
          if (here.isNotEmpty) {
            errs.add(CommandError(c.unitId, 'blocked', '路径穿过敌方占据格，需先交战'));
            break;
          }
        }
        break;
      case 'attack':
        final t = c.target == null ? null : g.findUnit(c.target!);
        if (t == null || !t.alive) {
          errs.add(CommandError(c.unitId, 'no_target', '目标不存在'));
          break;
        }
        if (t.side == side) {
          errs.add(CommandError(c.unitId, 'friendly', '不可攻击己方'));
          break;
        }
        if (fogOn && !observed.contains(t.id)) {
          errs.add(CommandError(c.unitId, 'not_observed', '目标未被侦察到，不可下令攻击'));
          break;
        }
        final d = hexDistance(u.pos, t.pos);
        final reach = g.typeOf(u).isAir ? g.typeOf(u).airRange : (g.typeOf(u).gunRange > 0 ? g.typeOf(u).gunRange : 1);
        if (d > reach) {
          errs.add(CommandError(c.unitId, 'out_of_range', '目标超出射程（$d > $reach）'));
          break;
        }
        break;
      case 'fire':
        final t = g.typeOf(u);
        if (t.gunRange <= 0) {
          errs.add(CommandError(c.unitId, 'not_artillery', '非炮兵单位不可间接火力覆盖'));
          break;
        }
        final z = c.zone;
        if (z == null || !g.cells.containsKey(z.key)) {
          errs.add(CommandError(c.unitId, 'bad_zone', '火力目标格无效'));
          break;
        }
        if (hexDistance(u.pos, z) > t.gunRange + 1) {
          errs.add(CommandError(c.unitId, 'out_of_range', '超出射程'));
          break;
        }
        // 盲打允许：不要求目视，但精度降级在结算处理
        break;
      case 'dig':
      case 'rest':
      case 'halt':
        break;
      case 'air_mission':
        if (!g.typeOf(u).isAir) {
          errs.add(CommandError(c.unitId, 'not_air', '仅空中单位可指派任务'));
          break;
        }
        if (c.mission != 'none') {
          final z = c.zone;
          if (z == null || !g.cells.containsKey(z.key)) {
            errs.add(CommandError(c.unitId, 'bad_zone', '任务区无效'));
            break;
          }
          if (hexDistance(u.pos, z) > g.typeOf(u).airRange) {
            errs.add(CommandError(c.unitId, 'out_of_range', '任务区超出作战半径'));
            break;
          }
        }
        break;
      default:
        errs.add(CommandError(c.unitId, 'unknown_op', '未知指令 ${c.op}'));
    }
  }
  return errs;
}

/// 观测点是否足以判明该格敌情（供 UI 提示与炮兵盲打降级用）
int observationAt(GameState g, String side, Hex h) => (g.obs[side] ?? {})[h.key] ?? 0;
int exposureAt(GameState g, String side, Hex h) => exposureLevel(g, observationAt(g, side, h));
