import { hexCenterMeters, metersToHex, neighbors, hexDistance, hexLine, hexRange, dirsRightFor } from '../web/js/hex.js';

const geo = { ox: 1000, oy: 2000, radius: 500 };
const W = 9, H = 6;
const cells = [];
for (let r = 0; r < H; r++) for (let q = 0; q < W; q++) cells.push([q, r]);
const key = (q, r) => `${q},${r}`;
const byKey = new Map(cells.map(([q, r]) => [key(q, r), [q, r]]));

const fail = [];
// 1. rectangular layout: shift only depends on row parity
for (let r = 0; r < H; r++) {
  const shift = hexCenterMeters(geo, 0, r)[0] - hexCenterMeters(geo, 0, 0)[0];
  const want = (r & 1) * Math.sqrt(3) * geo.radius / 2;
  if (Math.abs(shift - want) > 1e-6) fail.push(`row ${r} shift ${shift} != ${want}`);
}
// 2. center -> nearest round trip
for (const [q, r] of cells) {
  const [x, y] = hexCenterMeters(geo, q, r);
  const [nq, nr] = metersToHex(geo, x, y);
  if (nq !== q || nr !== r) fail.push(`nearest(${q},${r}) -> ${nq},${nr}`);
  const [nq2, nr2] = metersToHex(geo, x + 100, y - 100);
  if (nq2 !== q || nr2 !== r) fail.push(`nearest offset(${q},${r}) -> ${nq2},${nr2}`);
}
// 3. logical adjacency == pixel adjacency
const FTF = Math.sqrt(3) * geo.radius;
for (const [q, r] of cells) {
  const [hx, hy] = hexCenterMeters(geo, q, r);
  const byPixel = cells.filter(([oq, or_]) => {
    if (oq === q && or_ === r) return false;
    const [ox, oy] = hexCenterMeters(geo, oq, or_);
    return Math.abs(Math.hypot(ox - hx, oy - hy) - FTF) < 1e-6;
  }).map(([oq, or_]) => key(oq, or_)).sort();
  const logical = neighbors(q, r).filter(([oq, or_]) => byKey.has(key(oq, or_)))
    .map(([oq, or_]) => key(oq, or_)).sort();
  if (byPixel.join('|') !== logical.join('|')) fail.push(`adjacency ${q},${r}: ${byPixel} vs ${logical}`);
}
// 4. distance / line / range
if (hexDistance(0, 0, 3, 0) !== 3) fail.push('dist east 3');
if (hexDistance(0, 0, 0, 4) !== 4) fail.push('dist south 4');
for (const [bq, br] of [[8, 0], [0, 5], [7, 3], [4, 4]]) {
  const line = hexLine(0, 0, bq, br);
  if (line[line.length - 1].join(',') !== key(bq, br)) fail.push(`line end ${bq},${br} -> ${line[line.length - 1]}`);
  for (let i = 1; i < line.length; i++) {
    const [aq, ar] = line[i - 1];
    const [cqq, crr] = line[i];
    if (hexDistance(aq, ar, cqq, crr) !== 1) fail.push(`line step ${aq},${ar}->${cqq},${crr}`);
  }
}
const rng = hexRange(4, 3, 2);
if (rng.length !== 19) fail.push(`range count ${rng.length}`);
if (new Set(rng.map(([q, r]) => key(q, r))).size !== 19) fail.push('range duplicates');
for (const [q, r] of rng) if (hexDistance(4, 3, q, r) > 2) fail.push(`range far ${q},${r}`);
// 5. right-half dirs cover each edge once
let undirected = 0;
for (const [q, r] of cells) for (const [dq, dr] of dirsRightFor(r)) if (byKey.has(key(q + dq, r + dr))) undirected++;
const directed = cells.reduce((s, [q, r]) => s + neighbors(q, r).filter(([oq, or_]) => byKey.has(key(oq, or_))).length, 0);
if (undirected * 2 !== directed) fail.push(`edge cover ${undirected} vs ${directed / 2}`);

console.log(fail.length ? 'FAIL:\n' + fail.join('\n') : `ALL PASS (${undirected} undirected edges, ${W}x${H} board)`);
process.exit(fail.length ? 1 : 0);
