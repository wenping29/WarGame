// 六角几何：odd-r 偏移坐标（q=列、r=行，奇数行右移半格），整盘铺成矩形。
// 与服务端 HexGrid 严格一致：center = [ox + sqrt(3)*R*(q + (r&1)/2), oy - 1.5*R*r]，
// 单位是 EPSG:3857 米。r 增大时 mercator y 减小（向南）。
// 距离/直线/半径等立方体运算先转轴向坐标（aq = q - (r - (r&1))/2, ar = r）再回写偏移坐标。

export const SQRT3 = Math.sqrt(3);

export function toAxial(q, r) { return [q - (r - (r & 1)) / 2, r]; }
export function fromAxial(aq, ar) { return [aq + (ar - (ar & 1)) / 2, ar]; }

export function hexCenterMeters(geo, q, r) {
  return [geo.ox + SQRT3 * geo.radius * (q + (r & 1) / 2.0),
          geo.oy - 1.5 * geo.radius * r];
}

/// 6 邻方向（按行奇偶，顺时针自 E 起）
const DIRS_EVEN = [[1, 0], [0, -1], [-1, -1], [-1, 0], [-1, 1], [0, 1]];
const DIRS_ODD = [[1, 0], [1, -1], [0, -1], [-1, 0], [0, 1], [1, 1]];
export function dirsFor(r) { return (r & 1) === 0 ? DIRS_EVEN : DIRS_ODD; }

export function neighbors(q, r) {
  return dirsFor(r).map(([dq, dr]) => [q + dq, r + dr]);
}

/// 半边邻方向（E / NE / SE）：连笔线状地物时每对相邻格只走一次
export function dirsRightFor(r) {
  const d = dirsFor(r);
  return [d[0], d[1], d[5]];
}

export function hexDistance(aq, ar, bq, br) {
  const [ax, ay] = toAxial(aq, ar);
  const [bx, by] = toAxial(bq, br);
  const dq = ax - bx, dr = ay - by, ds = (-ax - ay) - (-bx - by);
  return Math.max(Math.abs(dq), Math.abs(dr), Math.abs(ds));
}

function axialRound(faq, far) {
  const fs = -faq - far;
  let rq = Math.round(faq), rr = Math.round(far), rs = Math.round(fs);
  const dq = Math.abs(rq - faq), dr = Math.abs(rr - far), ds = Math.abs(rs - fs);
  if (dq > dr && dq > ds) rq = -rr - rs;
  else if (dr > ds) rr = -rq - rs;
  return fromAxial(rq, rr);
}

/// 最近格：hexCenterMeters 的解析逆（在轴向空间取整）
export function metersToHex(geo, x, y) {
  const far = (geo.oy - y) / (1.5 * geo.radius);
  const faq = (x - geo.ox) / (SQRT3 * geo.radius) - far / 2.0;
  return axialRound(faq, far);
}

/// 六角直线（cube 插值），用于生成相邻步进路径
export function hexLine(aq, ar, bq, br) {
  const n = hexDistance(aq, ar, bq, br);
  if (n === 0) return [[aq, ar]];
  const [ax, ay] = toAxial(aq, ar);
  const [bx, by] = toAxial(bq, br);
  const out = [];
  for (let i = 0; i <= n; i++) {
    const t = i / n;
    out.push(axialRound(ax + (bx - ax) * t, ay + (by - ay) * t));
  }
  return dedupe(out);
}

/// 取整可能产生重复点（相邻性要求每步恰为 1），去掉连续重复
export function dedupe(path) {
  const out = [];
  for (const h of path) {
    const last = out[out.length - 1];
    if (!last || last[0] !== h[0] || last[1] !== h[1]) out.push(h);
  }
  return out;
}

/// 半径内的所有格（含自身），用于射程/任务区预览
export function hexRange(aq, ar, radius) {
  const [ax, ay] = toAxial(aq, ar);
  const out = [];
  for (let dq = -radius; dq <= radius; dq++) {
    const r1 = Math.max(-radius, -dq - radius), r2 = Math.min(radius, -dq + radius);
    for (let dr = r1; dr <= r2; dr++) out.push(fromAxial(ax + dq, ay + dr));
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
