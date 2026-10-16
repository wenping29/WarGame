// Canvas 战场渲染：天地图瓦片层 + 六角叠加层，共用同一"米→屏幕"变换，
// 保证六角落在瓦片上不错位。pan/zoom 同时作用两层。
import { hexCenterMeters, hexCornerOffsetsMeters, metersToHex, hexDistance, hexRange, SQRT3 } from './hex.js';
import { el } from './util.js';

const E = 20037508.342789244; // EPSG:3857 半周长（米）
// 屏幕像素/墨卡托米。上限要容得下 fit() 的结果，否则缩放按钮会被夹在边界上毫无反应。
const SCALE_MIN = 1e-5;
const SCALE_MAX = 0.6; // 1 km 格 ≈ 600 px，再大瓦片级别也超过天地图的 18 级

// 天地图 WMTS。必须用 _w 后缀（球面墨卡托切片方案）：_c 是经纬度直投，
// 与六角格所在的 EPSG:3857 不一致，混用会随纬度漂移。
// 底图与注记是两个独立图层，注记叠在底图之上才有地名。
const TD_HOSTS = ['t0', 't1', 't2', 't3', 't4', 't5', 't6', 't7'];
export const BASEMAPS = {
  vec: { label: '天地图 矢量', layers: ['vec', 'cva'] },
  img: { label: '天地图 影像', layers: ['img', 'cia'] },
  ter: { label: '天地图 地形', layers: ['ter', 'cta'] },
};
export const TD_CREDIT = '© 天地图 · 国家基础地理信息中心';

function buildBasemapControl(map) {
  const sel = el('select', {
    class: 'basemap-sel',
    title: '在线底图（天地图）',
    onchange: (e) => map.setBasemap(e.target.value),
  }, Object.entries(BASEMAPS).map(([k, v]) =>
    el('option', { value: k, selected: k === map.basemap ? 'selected' : null }, v.label)));
  if (!map.tk) { sel.disabled = true; sel.title = '未配置天地图密钥'; }
  return sel;
}

/// 挂到 .mapzoom 控件行末尾；容器不存在时静默跳过。
export function attachBasemapControl(container, map) {
  if (!container) return;
  container.appendChild(buildBasemapControl(map));
}

// 密钥由服务端 /api/scenarios 下发（见 hexfront/bin/server.dart 读 data/tianditu.json），
// 引导页在挂载视图前调用 configureTiles，因此新建的 MapView 一开始就带 tk。
let defaultTk = null;
export function configureTiles(tk) {
  defaultTk = (tk || '').trim() || null;
}

// 军事低饱和地形配色（键对应 library.dart STANDARD_TERRAIN_JSON 的 id）
export const TERRAIN_COLOR = {
  clear: '#8f9058', rough: '#7c834f', woods: '#3f5b39', hills: '#9c8a5b',
  mountain: '#6f6152', city: '#a7997f', swamp: '#5f6b4d', river: '#4d6f86',
  water: '#3b5a70', void: '#2a2f33',
};
const SIDE_COLOR = { red: '#c15b4e', blue: '#4e82b3', neutral: '#7f8c8d' };

export class MapView {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.view = null;
    this.own = [];
    this.enemies = [];
    this.allUnits = [];
    this.selection = null;      // {q,r,id}
    this.draftPath = null;      // [[q,r],...]
    this.rangeCenter = null;    // [q,r]
    this.rangeRadius = 0;
    this.pickHandlers = [];
    this.tiles = new Map();     // "layer/z/x/y" -> {img,status}
    this.tileErrors = 0;
    this.tileSuccess = 0;
    this._gateSince = null;     // 瓦片全失败时的退避计时
    this._gateRound = 0;
    this.tilesEnabled = true;
    this.tk = defaultTk;        // 天地图密钥；为空则不请求瓦片，只画六角层
    this.disposed = false;      // 视图销毁后拒掉一切自重绘，否则退避定时器会吊着脱离的 canvas 空转
    this._statusTimer = 0;
    this._idleTimer = 0;
    this.basemap = 'vec';
    this._cellsKey = null;
    this._cells = [];           // 解析缓存：每帧不再重解析 500 格
    this._cellAt = new Map();
    this._needFit = true;
    this._raf = 0;
    this._mouse = null;
    this._bind();
  }

  // ── 相机（米） ──
  // screen = toScreen(mx,my)
  toScreen(mx, my) {
    return [(mx - this.cx) * this.scale + this.w / 2, (this.cy - my) * this.scale + this.h / 2];
  }
  toMeters(sx, sy) {
    return [this.cx + (sx - this.w / 2) / this.scale, this.cy - (sy - this.h / 2) / this.scale];
  }

  _bind() {
    const c = this.canvas;
    let drag = null, moved = 0;
    c.addEventListener('pointerdown', (e) => {
      drag = { x: e.clientX, y: e.clientY, cx: this.cx, cy: this.cy };
      moved = 0;
      c.setPointerCapture(e.pointerId);
    });
    c.addEventListener('pointermove', (e) => {
      const rect = c.getBoundingClientRect();
      this._mouse = { sx: e.clientX - rect.left, sy: e.clientY - rect.top };
      if (drag) {
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
        moved = Math.max(moved, Math.abs(dx) + Math.abs(dy));
        this.cx = drag.cx - dx / this.scale;
        this.cy = drag.cy + dy / this.scale;
      }
      this.requestDraw();
    });
    const up = (e) => {
      if (drag && moved < 5) {
        const rect = c.getBoundingClientRect();
        this._pick(e.clientX - rect.left, e.clientY - rect.top);
      }
      drag = null;
    };
    c.addEventListener('pointerup', up);
    c.addEventListener('pointercancel', () => { drag = null; });
    c.addEventListener('wheel', (e) => {
      e.preventDefault();
      const rect = c.getBoundingClientRect();
      const sx = e.clientX - rect.left, sy = e.clientY - rect.top;
      this._zoomAt(e.deltaY < 0 ? 1.15 : 1 / 1.15, sx, sy);
    }, { passive: false });
    c.addEventListener('dblclick', (e) => {
      const rect = c.getBoundingClientRect();
      this._zoomAt(1.6, e.clientX - rect.left, e.clientY - rect.top);
    });
  }

  _zoomAt(factor, sx, sy) {
    const [mx, my] = this.toMeters(sx, sy);
    this.scale = Math.max(SCALE_MIN, Math.min(SCALE_MAX, this.scale * factor));
    // 保持光标下的米坐标不动
    this.cx = mx - (sx - this.w / 2) / this.scale;
    this.cy = my + (sy - this.h / 2) / this.scale;
    this.requestDraw();
  }

  zoomBy(factor) { this._zoomAt(factor, this.w / 2, this.h / 2); }

  _pick(sx, sy) {
    const [mx, my] = this.toMeters(sx, sy);
    const geo = this.view && this.view.geo;
    if (!geo) return;
    const [q, r] = metersToHex(geo, mx, my);
    this.pickHandlers.forEach((f) => f({ q, r, sx, sy }));
  }
  onPick(fn) { this.pickHandlers.push(fn); return this; }

  hexAtScreen(sx, sy) {
    const geo = this.view && this.view.geo;
    if (!geo) return null;
    const [mx, my] = this.toMeters(sx, sy);
    const [q, r] = metersToHex(geo, mx, my);
    return { q, r };
  }

  resize() {
    const c = this.canvas;
    const rect = c.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    c.width = Math.max(1, Math.floor(rect.width * dpr));
    c.height = Math.max(1, Math.floor(rect.height * dpr));
    this.w = rect.width; this.h = rect.height; this.dpr = dpr;
    if (this._needFit && this._cells.length) this.fit();
    this.requestDraw();
  }

  // ── 数据 ──
  setView(view) {
    this.view = view;
    const geo = view.geo;
    if (!geo) return;
    const key = [geo.ox, geo.oy, geo.radius, view.width, view.height, (view.cells || []).length].join('|');
    if (key !== this._cellsKey) this._parseCells(view, key);
    this._normalize(view);
    if (this._needFit && this.w) { this.fit(); this._needFit = false; }
    this.requestDraw();
  }

  _normalize(view) {
    // UI 必须按 view.kind 适配，而不是假设观战永远是非全知
    const k = view.kind;
    if (k === 'director') {
      this.allUnits = view.units || [];
      this.own = [];
      this.enemies = [];
    } else if (k === 'side') {
      this.allUnits = view.own || [];
      this.own = view.own || [];
      this.enemies = view.contacts || [];
    } else { // spectator
      this.allUnits = [];
      this.own = [];
      this.enemies = view.contacts || [];
    }
  }

  _parseCells(view, key) {
    const geo = view.geo;
    const out = [];
    const at = new Map();
    for (const c of view.cells || []) {
      const [mx, my] = hexCenterMeters(geo, c.q, c.r);
      const rec = {
        q: c.q, r: c.r, terrain: c.terrain || 'clear',
        elev: c.elev || 0, slope: c.slope || 0,
        road: !!c.road, river: !!c.river, bridge: !!c.bridge, city: c.city || 0,
        mx, my,
      };
      out.push(rec);
      at.set(c.q + ',' + c.r, rec);
    }
    this._cells = out;
    this._cellAt = at;
    this._cellsKey = key;
  }

  cellAt(q, r) { return this._cellAt.get(q + ',' + r); }

  fit() {
    if (!this._cells.length) return;
    const geo = this.view.geo;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const c of this._cells) {
      if (c.mx < minX) minX = c.mx;
      if (c.mx > maxX) maxX = c.mx;
      if (c.my < minY) minY = c.my;
      if (c.my > maxY) maxY = c.my;
    }
    const padM = SQRT3 * geo.radius;
    const spanX = (maxX - minX) + padM * 2;
    const spanY = (maxY - minY) + padM * 2;
    this.cx = (minX + maxX) / 2;
    this.cy = (minY + maxY) / 2;
    const availX = Math.max(1, this.w || 600), availY = Math.max(1, this.h || 400);
    const k = Math.min(availX / spanX, availY / spanY) * 0.92;
    this.scale = Math.max(SCALE_MIN, Math.min(SCALE_MAX, k));
  }

  // ── 绘制 ──
  dispose() {
    this.disposed = true;
    if (this._statusTimer) { clearTimeout(this._statusTimer); this._statusTimer = 0; }
    if (this._idleTimer) { clearTimeout(this._idleTimer); this._idleTimer = 0; }
    if (this._raf) { cancelAnimationFrame(this._raf); this._raf = 0; }
  }

  requestDraw() {
    if (this.disposed || this._raf) return;
    this._raf = requestAnimationFrame(() => { this._raf = 0; this.draw(); });
  }

  /// 尚未就绪（无尺寸/无视图）时不能静默丢弃重绘请求，否则后续事件再也画不出来
  _scheduleEmptyFrame() {
    if (this.disposed || this._idleTimer) return;
    this._idleTimer = setTimeout(() => { this._idleTimer = 0; this.requestDraw(); }, 300);
  }

  _tileZoom() {
    const worldPx = 2 * E * this.scale;
    return worldPx / 256; // 2^z
  }

  draw() {
    const ctx = this.ctx, c = this.canvas;
    const w = this.w, h = this.h;
    if (!w || !this.scale) { this._scheduleEmptyFrame(); return; }
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#141a1e';
    ctx.fillRect(0, 0, w, h);
    if (!this.view) {
      ctx.fillStyle = 'rgba(255,255,255,.55)';
      ctx.font = '12px sans-serif';
      ctx.fillText('连接中…', 10, 20);
      return;
    }
    if (this.tk && this.tilesEnabled) this._tileStatus(ctx, w, h);
    const showTiles = this._tilesAllowed();
    if (showTiles) this._drawTiles(ctx);
    this._drawHexes(ctx, showTiles);
    this._drawOverlays(ctx);
    this._drawUnits(ctx);
    this._drawCursorHex(ctx);
    this._drawTileHint(ctx);
  }

  /// 有密钥但一张瓦片都没成功（key 错/被 WAF 拦/断网）：指数退避后重试，并在画布上常驻状态条。
  /// Canvas 按需重绘，所以失败态/在途态都要自维持一个 1 s 心跳（见 _reapStalled），
  /// 否则退避计时器永远不会再被检查，瓦片再也起不来。
  _tileStatus(ctx, w, h) {
    if (!this.tk || !this.tilesEnabled) return;
    if (this.tileSuccess > 0) return;
    this._reapStalled();
    // 1 s 心跳：瓦片拿不到时既可能在途也可能已被闸门阻断且无在途请求，两种状态都得有人再叫一次 draw()，
    // 否则 Canvas 按需重绘 → 没人重绘 → 退避计时器永不推进 → 底图永久熄火。放弃后停止心跳。
    if (!this._tileGiveUp() && !this._statusTimer) {
      this._statusTimer = setTimeout(() => { this._statusTimer = 0; this.requestDraw(); }, 1000);
    }
    if (this._gateRound > 0) {
      const backoff = Math.min(60000, 3000 * Math.pow(2, this._gateRound - 1));
      if (Date.now() - (this._gateSince || 0) < backoff) return; // 本窗口只重试一次，避免每帧刷请求
    }
    if (this.tileErrors < 6) return; // 首批请求还在途
    ctx.fillStyle = 'rgba(255,255,255,.55)';
    ctx.font = '11px sans-serif';
    ctx.textAlign = 'left';
    if (this._tileGiveUp()) {
      ctx.fillText('在线底图不可用（密钥或网络问题）· 六角地形仍按 GIS 数据上色 · 修正 data/tianditu.json 后刷新', 8, h - 26);
      return;
    }
    this._gateRound = (this._gateRound || 0) + 1;
    this._gateSince = Date.now();
    this.tiles.clear();
    this.tileErrors = 0;
    ctx.fillText('在线底图连接失败（密钥或网络问题），第 ' + this._gateRound + ' 次重试…', 8, h - 26);
  }

  /// 请求悬挂（被中间层吞掉，既不 onload 也不 onerror——本机实测浏览器直连天地图就是这种）
  /// 超过 8 s 一律折算成失败，否则 tileErrors 永远是 0，闸门与状态条都不会出现。
  _reapStalled() {
    const now = Date.now();
    for (const t of this.tiles.values()) {
      if (t.status === 'loading' && t.started && now - t.started > 8000) { t.status = 'err'; this.tileErrors++; }
    }
  }

  /// 连续退避 5 轮仍拿不到瓦片就不再自动重试（约 6 批请求，护住天地图配额），
  /// 用户切底图或刷新才会再试一次。
  _tileGiveUp() {
    return this.tileSuccess === 0 && (this._gateRound || 0) >= 5;
  }

  _tilesAllowed() {
    if (!this.tk || !this.tilesEnabled || this._tileGiveUp()) return false;
    return this.tileErrors < 6 || this.tileSuccess > 0;
  }

  /// 没密钥时把原因写在图上，否则用户只会以为底图坏了
  _drawTileHint(ctx) {
    if (this.tk) return;
    ctx.fillStyle = 'rgba(255,255,255,.62)';
    ctx.font = '11px sans-serif';
    ctx.textAlign = 'left';
    ctx.fillText('在线底图未配置：将天地图 tk 写入 data/tianditu.json 后刷新（六角格仍按 GIS 地形数据上色）', 8, this.h - 10);
  }

  setBasemap(id) {
    if (!BASEMAPS[id] || id === this.basemap) return;
    this.basemap = id;
    this.tiles.clear();
    // 换底图即重新给网络一次机会：否则一次断网把瓦片永久关掉（闸门看的是累计错误数）
    this.tileErrors = 0;
    this.tileSuccess = 0;
    this._gateSince = null;
    this._gateRound = 0;
    this.requestDraw();
  }

  _drawTiles(ctx) {
    const bm = BASEMAPS[this.basemap] || BASEMAPS.vec;
    const zfac = this._tileZoom();
    // 天地图只切 1–18 级，取 0 级或 19 级会拿到空瓦片
    const zi = Math.max(1, Math.min(18, Math.round(Math.log2(zfac))));
    const worldPx = 2 * E * this.scale;
    const tilePx = worldPx / Math.pow(2, zi);
    if (tilePx < 64 || tilePx > 1024) return; // 极端缩放不画瓦片
    const [mleft, mtop] = this.toMeters(0, 0);
    const [mright, mbottom] = this.toMeters(this.w, this.h);
    const n = Math.pow(2, zi);
    const tx0 = Math.floor(((mleft + E) / (2 * E)) * n);
    const tx1 = Math.floor(((mright + E) / (2 * E)) * n);
    const ty0 = Math.floor(((E - mtop) / (2 * E)) * n);
    const ty1 = Math.floor(((E - mbottom) / (2 * E)) * n);
    for (const layer of bm.layers) {
      for (let tx = tx0; tx <= tx1; tx++) {
        if (tx < 0 || tx >= n) continue;
        for (let ty = ty0; ty <= ty1; ty++) {
          if (ty < 0 || ty >= n) continue;
          const key = layer + '/' + zi + '/' + tx + '/' + ty;
          let t = this.tiles.get(key);
          if (!t) t = this._loadTile(key, layer, zi, tx, ty);
          if (t.status !== 'ok') continue;
          const m0x = -E + tx * (2 * E / n);
          const m0y = E - ty * (2 * E / n);
          const [sx, sy] = this.toScreen(m0x, m0y);
          ctx.drawImage(t.img, sx, sy, tilePx + 1, tilePx + 1);
        }
      }
    }
    // 版权注记（天地图要求署名）
    ctx.fillStyle = 'rgba(255,255,255,.75)';
    ctx.font = '10px sans-serif';
    ctx.textAlign = 'right';
    ctx.fillText(TD_CREDIT, this.w - 4, this.h - 4);
    ctx.textAlign = 'left';
  }

  _loadTile(key, layer, zi, tx, ty) {
    if (this.tiles.size > 512) this.tiles.clear();
    const t = { status: 'loading', img: null, started: Date.now() };
    const img = new Image();
    // 不设 crossOrigin：天地图瓦片服务不保证回 CORS 头，带 anonymous 会直接加载失败。
    // 本画布只做展示、从不 getImageData 回读像素，污染画布无影响。
    img.onload = () => { t.status = 'ok'; t.img = img; this.tileSuccess++; this.requestDraw(); };
    img.onerror = () => { t.status = 'err'; this.tileErrors++; this.requestDraw(); };
    img.src = this._tdUrl(layer, zi, tx, ty);
    this.tiles.set(key, t);
    return t;
  }

  _tdUrl(layer, zi, tx, ty) {
    const host = TD_HOSTS[(tx + ty) % TD_HOSTS.length]; // 子域轮询，绕开单域名并发上限
    const q = new URLSearchParams({
      SERVICE: 'WMTS', REQUEST: 'GetTile', VERSION: '1.0.0',
      LAYER: layer, STYLE: 'default', TILEMATRIXSET: 'w', FORMAT: 'tiles',
      TILEMATRIX: String(zi), TILEROW: String(ty), TILECOL: String(tx), tk: this.tk,
    });
    return `https://${host}.tianditu.gov.cn/${layer}_w/wmts?${q}`;
  }

  _hexPath(ctx, sx, sy, rp) {
    ctx.beginPath();
    for (let i = 0; i < 6; i++) {
      const a = Math.PI / 180 * (60 * i - 90);
      const px = sx + rp * Math.cos(a), py = sy + rp * Math.sin(a);
      if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
    }
    ctx.closePath();
  }

  _drawHexes(ctx, showTiles) {
    const geo = this.view.geo;
    const rp = geo.radius * this.scale; // 外接圆半径（像素）
    if (rp < 2) return; // 缩得太小，交给瓦片/底色
    const alpha = showTiles ? 0.28 : 0.96;
    const [m0x, m0y] = this.toMeters(0, 0);
    const [m1x, m1y] = this.toMeters(this.w, this.h);
    const x0 = Math.min(m0x, m1x), x1 = Math.max(m0x, m1x);
    const y0 = Math.min(m0y, m1y), y1 = Math.max(m0y, m1y);
    ctx.globalAlpha = alpha;
    for (const c of this._cells) {
      if (c.mx < x0 - rp * 2 || c.mx > x1 + rp * 2 || c.my < y0 - rp * 2 || c.my > y1 + rp * 2) continue;
      const [sx, sy] = this.toScreen(c.mx, c.my);
      ctx.fillStyle = TERRAIN_COLOR[c.terrain] || '#555';
      this._hexPath(ctx, sx, sy, rp);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    // 道路 / 河流（仅在能看清时画）
    if (rp > 6) this._drawLines(ctx, rp);
    // 格网描边（迷雾底图时更明显）
    if (!showTiles || rp > 14) {
      ctx.globalAlpha = showTiles ? 0.14 : 0.3;
      ctx.strokeStyle = '#0c1013';
      ctx.lineWidth = 1;
      for (const c of this._cells) {
        if (c.mx < x0 - rp * 2 || c.mx > x1 + rp * 2 || c.my < y0 - rp * 2 || c.my > y1 + rp * 2) continue;
        if (c.terrain === 'void') continue;
        const [sx, sy] = this.toScreen(c.mx, c.my);
        this._hexPath(ctx, sx, sy, rp);
        ctx.stroke();
      }
      ctx.globalAlpha = 1;
    }
  }

  _drawLines(ctx, rp) {
    const dirsRight = [[1, 0], [1, -1], [0, 1]]; // 覆盖每条共享边一次
    for (const c of this._cells) {
      if (!c.road && !c.river && !c.bridge) continue;
      for (const [dq, dr] of dirsRight) {
        const nb = this.cellAt(c.q + dq, c.r + dr);
        if (!nb) continue;
        const link = (c.road && nb.road) || (c.river && nb.river) || (c.bridge && (nb.road || nb.bridge));
        if (!link) continue;
        const [ax, ay] = this.toScreen(c.mx, c.my);
        const [bx, by] = this.toScreen(nb.mx, nb.my);
        if (c.river || nb.river) {
          ctx.strokeStyle = '#3a6b86'; ctx.lineWidth = Math.max(1.5, rp * 0.28);
        } else {
          ctx.strokeStyle = '#c9b98d'; ctx.lineWidth = Math.max(1, rp * 0.12);
        }
        ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
      }
      // 桥梁标记
      if (c.bridge && rp > 10) {
        const [sx, sy] = this.toScreen(c.mx, c.my);
        ctx.fillStyle = '#d8d2bf';
        ctx.fillRect(sx - rp * 0.25, sy - rp * 0.12, rp * 0.5, rp * 0.24);
      }
      // 城镇
      if (c.city > 0 && rp > 8) {
        const [sx, sy] = this.toScreen(c.mx, c.my);
        const n = Math.min(3, c.city);
        ctx.fillStyle = '#413a30';
        for (let i = 0; i < n * 2; i++) {
          const a = (i / (n * 2)) * Math.PI * 2;
          ctx.fillRect(sx + Math.cos(a) * rp * 0.4 - rp * 0.1, sy + Math.sin(a) * rp * 0.4 - rp * 0.1, rp * 0.2, rp * 0.2);
        }
      }
    }
  }

  _drawOverlays(ctx) {
    const geo = this.view.geo;
    const rp = geo.radius * this.scale;
    // 射程/任务区预览
    if (this.rangeCenter && this.rangeRadius > 0) {
      const set = new Set(hexRange(this.rangeCenter[0], this.rangeCenter[1], this.rangeRadius).map((h) => h.join(',')));
      ctx.globalAlpha = 0.22;
      ctx.fillStyle = '#d8a24a';
      for (const c of this._cells) {
        if (!set.has(c.q + ',' + c.r)) continue;
        const [sx, sy] = this.toScreen(c.mx, c.my);
        this._hexPath(ctx, sx, sy, rp);
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    }
    // 草稿路径
    if (this.draftPath && this.draftPath.length >= 2) {
      ctx.strokeStyle = '#cfe9d6';
      ctx.lineWidth = Math.max(2, rp * 0.25);
      ctx.beginPath();
      this.draftPath.forEach(([q, r], i) => {
        const c = this.cellAt(q, r) || { mx: hexCenterMeters(geo, q, r)[0], my: hexCenterMeters(geo, q, r)[1] };
        const [sx, sy] = this.toScreen(c.mx, c.my);
        if (i === 0) ctx.moveTo(sx, sy); else ctx.lineTo(sx, sy);
      });
      ctx.stroke();
    }
    // 选中格
    if (this.selection) {
      const [mx, my] = hexCenterMeters(geo, this.selection.q, this.selection.r);
      const [sx, sy] = this.toScreen(mx, my);
      ctx.strokeStyle = '#7fb069';
      ctx.lineWidth = 2.5;
      this._hexPath(ctx, sx, sy, rp * 0.95);
      ctx.stroke();
    }
  }

  _drawCursorHex(ctx) {
    if (!this._mouse || !this.view) return;
    const geo = this.view.geo;
    const rp = geo.radius * this.scale;
    const hex = this.hexAtScreen(this._mouse.sx, this._mouse.sy);
    if (!hex) return;
    const c = this.cellAt(hex.q, hex.r);
    if (!c) return;
    ctx.strokeStyle = 'rgba(207,233,214,.6)';
    ctx.lineWidth = 1.5;
    this._hexPath(ctx, ...this.toScreen(c.mx, c.my), rp * 0.95);
    ctx.stroke();
  }

  _drawUnits(ctx) {
    const geo = this.view.geo;
    const rp = geo.radius * this.scale;
    if (rp < 4) { this._drawUnitsAsDots(ctx); return; }
    for (const u of this.own) this._drawUnit(ctx, u, rp, false);
    for (const u of this.enemies) this._drawUnit(ctx, u, rp, true);
    if (this.view.kind === 'director') for (const u of this.allUnits) this._drawUnit(ctx, u, rp, false);
  }

  _drawUnit(ctx, u, rp, isContact) {
    const [mx, my] = hexCenterMeters(this.view.geo, u.q, u.r);
    const [sx, sy] = this.toScreen(mx, my);
    const side = u.side || (isContact ? 'unknown' : 'neutral');
    const color = SIDE_COLOR[side] || '#888';
    const level = isContact ? (u.level || 0) : 3;
    const alpha = isContact ? 0.45 + 0.18 * level : 1;
    ctx.globalAlpha = alpha;
    // 兵力块
    const bw = rp * 0.9, bh = rp * 0.7;
    ctx.fillStyle = color;
    ctx.strokeStyle = '#0d1114';
    ctx.lineWidth = 1.5;
    this._roundRect(ctx, sx - bw / 2, sy - bh / 2, bw, bh, 3);
    ctx.fill(); ctx.stroke();
    // 陈旧（脱离观测）用虚线外圈
    if (isContact && u.stale) {
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = '#d8a24a';
      this._hexPath(ctx, sx, sy, rp * 0.92);
      ctx.stroke();
      ctx.setLineDash([]);
    }
    // 指挥所 / 任务
    if (u.hq) { ctx.strokeStyle = '#f0d9a5'; ctx.lineWidth = 2; this._roundRect(ctx, sx - bw / 2 - 3, sy - bh / 2 - 3, bw + 6, bh + 6, 4); ctx.stroke(); }
    if (u.airborne) { ctx.fillStyle = '#0d1114'; ctx.beginPath(); ctx.moveTo(sx - rp * 0.3, sy + bh / 2); ctx.lineTo(sx + rp * 0.3, sy + bh / 2); ctx.lineTo(sx, sy + bh / 2 + rp * 0.3); ctx.fill(); }
    // 文字
    ctx.fillStyle = '#0d1114';
    ctx.font = `bold ${Math.max(8, rp * 0.42)}px sans-serif`;
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    let label = (u.id || '').slice(-2);
    if (isContact && level < 2) label = '?';
    else if (isContact && u.type_label) label = u.type_label.slice(0, 2);
    else if (u.type_label) label = u.type_label.slice(0, 2);
    ctx.fillText(label, sx, sy);
    if (isContact && level >= 1) { ctx.fillStyle = '#0d1114'; ctx.font = `bold ${Math.max(7, rp * 0.3)}px sans-serif`; ctx.fillText('E' + level, sx + bw / 2 - rp * 0.15, sy - bh / 2 + rp * 0.15); }
    ctx.globalAlpha = 1;
    ctx.textBaseline = 'alphabetic'; ctx.textAlign = 'left';
  }

  _drawUnitsAsDots(ctx) {
    for (const u of [...this.own, ...this.enemies, ...this.allUnits]) {
      const [mx, my] = hexCenterMeters(this.view.geo, u.q, u.r);
      const [sx, sy] = this.toScreen(mx, my);
      ctx.fillStyle = SIDE_COLOR[u.side] || (u.level ? '#c15b4e' : '#7f8c8d');
      ctx.beginPath(); ctx.arc(sx, sy, 3, 0, Math.PI * 2); ctx.fill();
    }
  }

  _roundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }
}
