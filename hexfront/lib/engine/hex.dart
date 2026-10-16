// 六角格几何：尖顶(pointy-top)轴向坐标 q,r。
// 坐标主键用字符串 "q,r"，避免整数编码在负坐标上出错。
import 'dart:math' as math;

typedef HexKey = String;

HexKey hexKey(int q, int r) => '$q,$r';

class Hex {
  final int q;
  final int r;
  const Hex(this.q, this.r);

  HexKey get key => hexKey(q, r);
  int get s => -q - r;

  static Hex parse(HexKey key) {
    final i = key.indexOf(',');
    return Hex(int.parse(key.substring(0, i)), int.parse(key.substring(i + 1)));
  }

  static Hex of(int q, int r) => Hex(q, r);

  @override
  bool operator ==(Object o) => o is Hex && o.q == q && o.r == r;
  @override
  int get hashCode => Object.hash(q, r);
  @override
  String toString() => key;
}

/// 6 邻方向（轴向）。索引与 cube 方向一致，便于按方位取扇区。
const List<Hex> AXIAL_DIRS = [
  Hex(1, 0), Hex(1, -1), Hex(0, -1),
  Hex(-1, 0), Hex(-1, 1), Hex(0, 1),
];

List<Hex> hexNeighbors(Hex h) => AXIAL_DIRS.map((d) => Hex(h.q + d.q, h.r + d.r)).toList();

int hexDistance(Hex a, Hex b) {
  final dq = a.q - b.q, dr = a.r - b.r, ds = a.s - b.s;
  return math.max(math.max((dq).abs(), (dr).abs()), (ds).abs());
}

/// 距离 h 中心恰好 dist 的环。
List<Hex> hexRing(Hex center, int dist) {
  if (dist == 0) return [center];
  final out = <Hex>[];
  var h = Hex(center.q + AXIAL_DIRS[4].q * dist, center.r + AXIAL_DIRS[4].r * dist);
  for (var i = 0; i < 6; i++) {
    for (var j = 0; j < dist; j++) {
      out.add(h);
      h = Hex(h.q + AXIAL_DIRS[i].q, h.r + AXIAL_DIRS[i].r);
    }
  }
  return out;
}

/// 半径 radius 内的所有格（含自身），按 (距离, q, r) 稳定排序 → 保证结算确定性。
List<Hex> hexRange(Hex center, int radius) {
  final out = <Hex>[];
  for (var dq = -radius; dq <= radius; dq++) {
    for (var dr = math.max(-radius, -dq - radius); dr <= math.min(radius, -dq + radius); dr++) {
      out.add(Hex(center.q + dq, center.r + dr));
    }
  }
  out.sort((a, b) {
    final d = hexDistance(center, a) - hexDistance(center, b);
    if (d != 0) return d;
    if (a.q != b.q) return a.q - b.q;
    return a.r - b.r;
  });
  return out;
}

/// 六角直线（cube 插值 + 取整），用于通视判定。
List<Hex> hexLine(Hex a, Hex b) {
  final n = hexDistance(a, b);
  if (n == 0) return [a];
  final out = <Hex>[];
  for (var i = 0; i <= n; i++) {
    final t = i / n;
    out.add(_cubeRound(
      a.q + (b.q - a.q) * t,
      a.r + (b.r - a.r) * t,
    ));
  }
  return out;
}

Hex _cubeRound(double fq, double fr) {
  final fs = -fq - fr;
  var rq = fq.round(), rr = fr.round(), rs = fs.round();
  final dq = (rq - fq).abs(), dr = (rr - fr).abs(), ds = (rs - fs).abs();
  if (dq > dr && dq > ds) {
    rq = -rr - rs;
  } else if (dr > ds) {
    rr = -rq - rs;
  }
  return Hex(rq, rr);
}

/// 格 → 墨卡托米（平面直角）。EPSG:3857 下用仿射即可与在线瓦片严格对齐。
/// origin = q=0,r=0 格中心对应的墨卡托坐标；R = 格外接圆半径（米）。
class HexGeo {
  final double ox, oy, radius;
  final int dir; // 0..5 起始方位，用于旋转（本版本不旋转）
  final double cellGroundKm; // 实地对边距（km）；0 = 旧存档未知
  final double scale; // 墨卡托放大 1/cos(lat)；0 = 未知（按 1 处理）
  HexGeo({
    required this.ox,
    required this.oy,
    required this.radius,
    this.dir = 0,
    this.cellGroundKm = 0,
    this.scale = 0,
  });

  double get hexW => math.sqrt(3) * radius; // 尖顶格宽
  double get hexVStep => 1.5 * radius;      // 行距

  List<double> center(int q, int r) => [
        ox + hexW * (q + r / 2.0),
        oy - hexVStep * r,
      ];

  /// 墨卡托米 → 最近格（浮点轴向反解 + 取整）
  Hex nearest(double x, double y) {
    final px = (x - ox) / hexW;
    final py = (oy - y) / hexVStep; // r 轴向下为正
    return _cubeRound(px - py / 2.0, py);
  }

  Map<String, dynamic> toJson() => {
        'ox': ox, 'oy': oy, 'radius': radius,
        if (cellGroundKm > 0) 'cell_ground_km': cellGroundKm,
        if (scale > 0) 'mercator_scale': scale,
      };
  static HexGeo fromJson(Map<String, dynamic> j) => HexGeo(
        ox: (j['ox'] as num).toDouble(),
        oy: (j['oy'] as num).toDouble(),
        radius: (j['radius'] as num).toDouble(),
        cellGroundKm: (j['cell_ground_km'] as num?)?.toDouble() ?? 0,
        scale: (j['mercator_scale'] as num?)?.toDouble() ?? 0,
      );
}

/// 经纬度(WGS84) → EPSG:3857 墨卡托米
List<double> lonLatToMercator(double lon, double lat) {
  const e = 20037508.342789244;
  final x = lon * e / 180.0;
  var y = math.log(math.tan((90 + lat) * math.pi / 360.0)) / (math.pi / 180.0);
  y = y * e / 180.0;
  return [x, y];
}

/// 墨卡托米 → 经纬度
List<double> mercatorToLonLat(double x, double y) {
  const e = 20037508.342789244;
  final lon = x * 180.0 / e;
  var lat = y * 180.0 / e;
  lat = 180.0 / math.pi * (2 * math.atan(math.exp(lat * math.pi / 180.0)) - math.pi / 2);
  return [lon, lat];
}

/// 路径相邻性校验：每步必须是 6 邻之一，且首格为起点。
bool isAdjacentPath(List<Hex> path) {
  for (var i = 1; i < path.length; i++) {
    if (hexDistance(path[i - 1], path[i]) != 1) return false;
  }
  return true;
}

double clampd(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
