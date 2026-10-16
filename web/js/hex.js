// 六角几何：严格对齐服务端 HexGeo（lib/engine/hex.dart）。
// 服务端：center = [ox + sqrt(3)*R*(q + r/2), oy - 1.5*R*r]，单位是 EPSG:3857 米。
// 注意 r 增大时 mercator y 减小（向南）。所有像素计算都走"米→屏幕"，保证与瓦片不错位。

export const SQRT3 = Math.sqrt(3);

export function hexCenterMeters(geo, q, r) {
  return [geo.ox + SQRT3 * geo.radius * (q + r / 2.0),
          geo.oy - 1.5 * geo.radius * r];
}

/// 6 邻方向（轴向，与 hex.dart AXIAL_DIRS 一致）
export const DIRS = [[1, 0], [1, -1], [0, -1], [-1, 0], [-1, 1], [0, 1]];

export function hexDistance(aq, ar, bq, br) {
  const dq = aq - bq, dr = ar - br, ds = (-aq - ar) - (-bq - br);
  return Math.max(Math.abs(dq), Math.abs(dr), Math.abs(ds));
}

function cubeRound(fq, fr) {
  const fs = -fq - fr;
  let rq = Math.round(fq), rr = Math.round(fr), rs = Math.round(fs);
  const dq = Math.abs(rq - fq), dr = Math.abs(rr - fr), ds = Math.abs(rs - fs);
  if (dq > dr && dq > ds) rq = -rr - rs;
  else if (dr > ds) rr = -rq - rs;
  return [rq, rr];
}

/// 最近格：复刻 HexGeo.nearest（浮点轴向反解 + cube 取整）
export function metersToHex(geo, x, y) {
  const px = (x - geo.ox) / (SQRT3 * geo.radius);
  const py = (geo.oy - y) / (1.5 * geo.radius);
  return cubeRound(px - py / 2.0, py);
}

/// 六角直线（cube 插值），复刻 hex.dart hexLine；用于生成相邻步进路径
export function hexLine(aq, ar, bq, br) {
  const n = hexDistance(aq, ar, bq, br);
  if (n === 0) return [[aq, ar]];
  const out = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n;
    out.push(cubeRound(aq + (bq - aq) * t, ar + (br - ar) * t));
  }
  return dedupe(out);
}

/// hexLine 取整可能产生重复点（相邻性要求每步恰为 1），去掉连续重复
export function dedupe(path) {
  const out = [];
  for (const h of path) {
    const last = out[out.length - 1];
    if (!last || last[0] !== h[0] || last[1] !== h[1]) out.push(h);
  }
  return out;
}

/// 半径内的所有格（含自身），复刻 hexRange，用于射程/任务区预览
export function hexRange(aq, ar, radius) {
  const out = [];
  for (let dq = -radius; dq <= radius; dq++) {
    const r1 = Math.max(-radius, -dq - radius), r2 = Math.min(radius, -dq + radius);
    for (let dr = r1; dr <= r2; dr++) out.push([aq + dq, ar + dr]);
  }
  return out;
}

/// 尖顶六角形的 6 个顶点在"米"坐标系中的偏移（相对中心），x 向东 y 向北
export function hexCornerOffsetsMeters(geo) {
  const R = geo.radius;
  const pts = [];
  for (let i = 0; i < 6; i++) {
    // 屏幕(mercator) 尖顶：角度以 y 向下为正，与 canvas 约定一致
    const a = Math.PI / 180 * (60 * i - 30);
    pts.push([R * Math.cos(a), -R * Math.sin(a)]);
  }
  return pts;
}
