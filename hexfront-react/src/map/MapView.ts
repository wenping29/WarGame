/**
 * Canvas 战场渲染：天地图瓦片层 + 六角叠加层，共用同一「米→屏幕」变换，
 * 保证六角落在瓦片上不错位。pan/zoom 同时作用两层。
 *
 * 本类是命令式渲染器（与 web/js/map.js 同源契约），React 侧只由 components/HexMap.tsx
 * 挂载一次并把服务端视图喂进来：每帧在 RAF 里合并重绘，避免 React 重渲染驱动 canvas。
 */

import { dirsFor, dirsRightFor, hexCenterMeters, hexRange, metersToHex, SQRT3 } from './hex'
import type { AnyView, WireCell, WireContact, WireUnit } from '../room/protocol'

const E = 20037508.342789244 // EPSG:3857 半周长（米）
// 屏幕像素/墨卡托米。上限要容得下 fit() 的结果，否则缩放按钮会被夹在边界上毫无反应。
const SCALE_MIN = 1e-5
const SCALE_MAX = 0.6 // 1 km 格 ≈ 600 px，再大瓦片级别也超过天地图的 18 级

// 天地图 WMTS。必须用 _w 后缀（球面墨卡托切片方案）：_c 是经纬度直投，
// 与六角格所在的 EPSG:3857 不一致，混用会随纬度漂移。
// 底图与注记是两个独立图层，注记叠在底图之上才有地名。
const TD_HOSTS = ['t0', 't1', 't2', 't3', 't4', 't5', 't6', 't7']
export const BASEMAPS: Record<string, { label: string; layers: string[] }> = {
  vec: { label: '天地图 矢量', layers: ['vec', 'cva'] },
  img: { label: '天地图 影像', layers: ['img', 'cia'] },
  ter: { label: '天地图 地形', layers: ['ter', 'cta'] },
}
export const TD_CREDIT = '© 天地图 · 国家基础地理信息中心'
/** 首批请求的错误数门槛：低于它认为「还在途」，不弹降级提示 */
const TILE_ERROR_GATE = 6
/** 请求悬挂折算成失败的时限（部分中间层会吞掉请求，既不 onload 也不 onerror） */
const TILE_STALL_MS = 8000
/** 连续退避这么多轮仍拿不到瓦片就不再自动重试（护住天地图配额） */
const TILE_MAX_RETRY_ROUNDS = 5
const TILE_CACHE_MAX = 512

/** 军事低饱和地形配色（键对应 library STANDARD_TERRAIN 的 id）；void 是棋盘外的境外格 */
export const TERRAIN_COLOR: Record<string, string> = {
  clear: '#8f9058', rough: '#7c834f', woods: '#3f5b39', hills: '#9c8a5b',
  mountain: '#6f6152', city: '#a7997f', swamp: '#5f6b4d', river: '#4d6f86',
  water: '#3b5a70', void: '#2a2f33',
}
export const SIDE_COLOR: Record<string, string> = {
  red: '#c15b4e', blue: '#4e82b3', neutral: '#7f8c8d',
}
/** 地形中文名（与后端 STANDARD_TERRAIN 的 label 同源），供格信息条使用 */
export const TERRAIN_LABEL: Record<string, string> = {
  clear: '开阔地', rough: '崎岖地', woods: '林地', hills: '丘陵', mountain: '山地',
  city: '城镇', swamp: '沼泽', river: '河流', water: '江湖', void: '境外',
}

interface CellRecord extends WireCell {
  key: string
  mx: number
  my: number
}

interface Tile {
  status: 'loading' | 'ok' | 'err'
  img: HTMLImageElement | null
  started: number
}

export interface Pick {
  q: number
  r: number
}

/** 地图图层显示开关（用户可在终端右上角面板逐项切换，localStorage 记忆） */
export interface DisplayOptions {
  showBasemap: boolean // 在线底图（天地图瓦片）
  showTerrain: boolean // 六角格地形填充
  showGrid: boolean // 六角格网描边
  showFeatures: boolean // 地物层（铁路/输电线/机场/电厂/车站等图标与要点归属）
  showUnits: boolean // 兵力块
  showLegend: boolean // 地图图例
  showCoords: boolean // 六角格坐标标注（放大到可读时显示）
}

export const DEFAULT_DISPLAY: DisplayOptions = {
  showBasemap: true,
  showTerrain: true,
  showGrid: true,
  showFeatures: true,
  showUnits: true,
  showLegend: true,
  showCoords: false,
}

export class MapView {
  canvas: HTMLCanvasElement
  private ctx: CanvasRenderingContext2D

  view: AnyView | null = null
  own: WireUnit[] = []
  enemies: WireContact[] = []
  allUnits: WireUnit[] = []
  display: DisplayOptions = { ...DEFAULT_DISPLAY }

  /** 批量更新显示开关；底图开关变化时同步清瓦片缓存 */
  setDisplay(patch: Partial<DisplayOptions>) {
    const prev = this.display.showBasemap
    Object.assign(this.display, patch)
    if (prev !== this.display.showBasemap) this._resetTiles()
    this.requestDraw()
  }

  selection: { q: number; r: number } | null = null
  draftPath: [number, number][] | null = null
  rangeCenter: [number, number] | null = null
  rangeRadius = 0

  private pickHandlers: Array<(p: Pick) => void> = []

  nodeHolders: Record<string, string> = {}
  power: { red: number; blue: number } | null = null
  nodeLabels = new Map<string, string>()

  /** 天地图密钥：由服务端 /api/scenarios 下发（tk 读自 data/tianditu.json，不进版本库），为空则只画六角层 */
  private tk: string | null = null
  private tiles = new Map<string, Tile>()
  private tileErrors = 0
  private tileSuccess = 0
  private tilesEnabled = true
  basemap = 'vec'
  private _gateSince: number | null = null
  private _gateRound = 0
  private _statusTimer = 0

  private _cells: CellRecord[] = []
  private _cellAt = new Map<string, CellRecord>()
  private _cellsKey: string | null = null

  // 相机（墨卡托米 + 屏幕像素）
  private cx = 0
  private cy = 0
  private scale = 0
  w = 0
  h = 0
  private dpr = 1
  private _needFit = true

  private _raf = 0
  private _idleTimer = 0
  private _mouse: { sx: number; sy: number } | null = null
  private disposed = false

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('canvas 2d 上下文不可用')
    this.ctx = ctx
    this._bind()
  }

  // ── 相机（米） ──

  toScreen(mx: number, my: number): [number, number] {
    return [(mx - this.cx) * this.scale + this.w / 2, (this.cy - my) * this.scale + this.h / 2]
  }

  toMeters(sx: number, sy: number): [number, number] {
    return [this.cx + (sx - this.w / 2) / this.scale, this.cy - (sy - this.h / 2) / this.scale]
  }

  private _bind() {
    const c = this.canvas
    let drag: { x: number; y: number; cx: number; cy: number } | null = null
    let moved = 0
    c.addEventListener('pointerdown', (e) => {
      drag = { x: e.clientX, y: e.clientY, cx: this.cx, cy: this.cy }
      moved = 0
      c.setPointerCapture(e.pointerId)
    })
    c.addEventListener('pointermove', (e) => {
      const rect = c.getBoundingClientRect()
      this._mouse = { sx: e.clientX - rect.left, sy: e.clientY - rect.top }
      if (drag) {
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y
        moved = Math.max(moved, Math.abs(dx) + Math.abs(dy))
        this.cx = drag.cx - dx / this.scale
        this.cy = drag.cy + dy / this.scale
      }
      this.requestDraw()
    })
    const up = (e: PointerEvent) => {
      // 拖过 5 px 视为平移，不触发选格
      if (drag && moved < 5) {
        const rect = c.getBoundingClientRect()
        this._pick(e.clientX - rect.left, e.clientY - rect.top)
      }
      drag = null
    }
    c.addEventListener('pointerup', up)
    c.addEventListener('pointercancel', () => { drag = null })
    c.addEventListener('wheel', (e) => {
      e.preventDefault()
      const rect = c.getBoundingClientRect()
      this._zoomAt(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.clientX - rect.left, e.clientY - rect.top)
    }, { passive: false })
    c.addEventListener('dblclick', (e) => {
      const rect = c.getBoundingClientRect()
      this._zoomAt(1.6, e.clientX - rect.left, e.clientY - rect.top)
    })
  }

  private _zoomAt(factor: number, sx: number, sy: number) {
    const [mx, my] = this.toMeters(sx, sy)
    this.scale = Math.max(SCALE_MIN, Math.min(SCALE_MAX, this.scale * factor))
    // 保持光标下的米坐标不动
    this.cx = mx - (sx - this.w / 2) / this.scale
    this.cy = my + (sy - this.h / 2) / this.scale
    this.requestDraw()
  }

  zoomBy(factor: number) {
    this._zoomAt(factor, this.w / 2, this.h / 2)
  }

  private _pick(sx: number, sy: number) {
    const hex = this.hexAtScreen(sx, sy)
    if (!hex) return
    this.pickHandlers.forEach((f) => f(hex))
  }

  onPick(fn: (p: Pick) => void): this {
    this.pickHandlers.push(fn)
    return this
  }

  hexAtScreen(sx: number, sy: number): Pick | null {
    const geo = this.view?.geo
    if (!geo) return null
    const [mx, my] = this.toMeters(sx, sy)
    const [q, r] = metersToHex(geo, mx, my)
    return { q, r }
  }

  resize() {
    const c = this.canvas
    const rect = c.getBoundingClientRect()
    const dpr = window.devicePixelRatio || 1
    c.width = Math.max(1, Math.floor(rect.width * dpr))
    c.height = Math.max(1, Math.floor(rect.height * dpr))
    this.w = rect.width
    this.h = rect.height
    this.dpr = dpr
    if (this._needFit && this._cells.length) this.fit()
    this.requestDraw()
  }

  // ── 数据 ──

  setView(view: AnyView | null) {
    this.view = view
    if (!view) { this.requestDraw(); return }
    // 归属是情报：观战视图不下发 node_holders，缺字段即按「无人掌握」渲染
    this.nodeHolders = ('node_holders' in view ? view.node_holders : null) || {}
    this.power = 'power' in view ? view.power || null : null
    this.nodeLabels = new Map((view.nodes || []).map((n) => [n.q + ',' + n.r, n.label]))
    const geo = view.geo
    if (!geo) return
    const key = [geo.ox, geo.oy, geo.radius, view.width, view.height, (view.cells || []).length].join('|')
    if (key !== this._cellsKey) this._parseCells(view, key)
    this._normalize(view)
    if (this._needFit && this.w) { this.fit(); this._needFit = false }
    this.requestDraw()
  }

  /// UI 按 view.kind 适配，而不是假设观战永远是非全知
  private _normalize(view: AnyView) {
    if (view.kind === 'director') {
      this.allUnits = view.units || []
      this.own = []
      this.enemies = []
    } else if (view.kind === 'side') {
      this.allUnits = view.own || []
      this.own = view.own || []
      this.enemies = view.contacts || []
    } else {
      this.allUnits = []
      this.own = []
      this.enemies = view.contacts || []
    }
  }

  private _parseCells(view: AnyView, key: string) {
    const geo = view.geo
    const out: CellRecord[] = []
    const at = new Map<string, CellRecord>()
    for (const c of view.cells || []) {
      const [mx, my] = hexCenterMeters(geo, c.q, c.r)
      const rec: CellRecord = { ...c, key: c.q + ',' + c.r, mx, my }
      out.push(rec)
      at.set(rec.key, rec)
    }
    this._cells = out
    this._cellAt = at
    this._cellsKey = key
  }

  cellAt(q: number, r: number): CellRecord | undefined {
    return this._cellAt.get(q + ',' + r)
  }

  fit() {
    if (!this._cells.length || !this.view) return
    const geo = this.view.geo
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity
    for (const c of this._cells) {
      if (c.mx < minX) minX = c.mx
      if (c.mx > maxX) maxX = c.mx
      if (c.my < minY) minY = c.my
      if (c.my > maxY) maxY = c.my
    }
    const padM = SQRT3 * geo.radius
    const spanX = maxX - minX + padM * 2
    const spanY = maxY - minY + padM * 2
    this.cx = (minX + maxX) / 2
    this.cy = (minY + maxY) / 2
    const availX = Math.max(1, this.w || 600)
    const availY = Math.max(1, this.h || 400)
    this.scale = Math.max(SCALE_MIN, Math.min(SCALE_MAX, Math.min(availX / spanX, availY / spanY) * 0.92))
  }

  // ── 在线底图（天地图 WMTS） ──

  /** 密钥经 /api/scenarios 下发，前端不落盘：data/tianditu.json 是访问凭证，不进版本库 */
  setTk(tk: string | null) {
    const next = (tk || '').trim() || null
    if (next === this.tk) return
    this.tk = next
    this._resetTiles()
    this.requestDraw()
  }

  setBasemap(id: string) {
    if (!BASEMAPS[id] || id === this.basemap) return
    this.basemap = id
    // 换底图即重新给网络一次机会：否则一次断网把瓦片永久关掉（闸门看的是累计错误数）
    this._resetTiles()
    this.requestDraw()
  }

  private _resetTiles() {
    this.tiles.clear()
    this.tileErrors = 0
    this.tileSuccess = 0
    this._gateSince = null
    this._gateRound = 0
  }

  private _tileZoom() {
    return (2 * E * this.scale) / 256 // 2^z
  }

  /// 连续退避 5 轮仍拿不到瓦片就不再自动重试（约 6 批请求，护住天地图配额），
  /// 用户切底图或刷新才会再试一次。
  private _tileGiveUp() {
    return this.tileSuccess === 0 && this._gateRound >= TILE_MAX_RETRY_ROUNDS
  }

  private _tilesAllowed() {
    if (!this.tk || !this.tilesEnabled || this._tileGiveUp()) return false
    return this.tileErrors < TILE_ERROR_GATE || this.tileSuccess > 0
  }

  /// 请求悬挂（被中间层吞掉，既不 onload 也不 onerror——本机实测浏览器直连天地图就是这种）
  /// 超过 8 s 一律折算成失败，否则 tileErrors 永远是 0，闸门与状态条都不会出现。
  private _reapStalled() {
    const now = Date.now()
    for (const t of this.tiles.values()) {
      if (t.status === 'loading' && t.started && now - t.started > TILE_STALL_MS) {
        t.status = 'err'
        this.tileErrors++
      }
    }
  }

  /// 有密钥但一张瓦片都没成功（key 错/被 WAF 拦/断网）：指数退避后重试，并在画布上常驻状态条。
  /// Canvas 按需重绘，所以失败态/在途态都要自维持一个 1 s 心跳，
  /// 否则退避计时器永远不会再被检查，瓦片再也起不来。
  private _tileStatus() {
    if (!this.tk || !this.tilesEnabled || this.tileSuccess > 0) return
    const ctx = this.ctx, h = this.h
    this._reapStalled()
    if (!this._tileGiveUp() && !this._statusTimer) {
      this._statusTimer = window.setTimeout(() => { this._statusTimer = 0; this.requestDraw() }, 1000)
    }
    if (this._gateRound > 0) {
      const backoff = Math.min(60000, 3000 * Math.pow(2, this._gateRound - 1))
      if (Date.now() - (this._gateSince || 0) < backoff) return // 本窗口只重试一次，避免每帧刷请求
    }
    if (this.tileErrors < TILE_ERROR_GATE) return // 首批请求还在途
    ctx.fillStyle = 'rgba(255,255,255,.55)'
    ctx.font = '11px sans-serif'
    ctx.textAlign = 'left'
    if (this._tileGiveUp()) {
      ctx.fillText('在线底图不可用（密钥或网络问题）· 六角地形仍按 GIS 数据上色 · 修正 data/tianditu.json 后刷新', 8, h - 26)
      return
    }
    this._gateRound++
    this._gateSince = Date.now()
    this.tiles.clear()
    this.tileErrors = 0
    ctx.fillText('在线底图连接失败（密钥或网络问题），第 ' + this._gateRound + ' 次重试…', 8, h - 26)
  }

  /// 没密钥时把原因写在图上，否则用户只会以为底图坏了
  private _drawTileHint() {
    if (this.tk) return
    const ctx = this.ctx
    ctx.fillStyle = 'rgba(255,255,255,.62)'
    ctx.font = '11px sans-serif'
    ctx.textAlign = 'left'
    ctx.fillText('在线底图未配置：将天地图 tk 写入 data/tianditu.json 后刷新（六角格仍按 GIS 地形数据上色）', 8, this.h - 10)
  }

  private _drawTiles() {
    const ctx = this.ctx
    const bm = BASEMAPS[this.basemap] || BASEMAPS.vec
    const zfac = this._tileZoom()
    // 天地图只切 1–18 级，取 0 级或 19 级会拿到空瓦片
    const zi = Math.max(1, Math.min(18, Math.round(Math.log2(zfac))))
    const worldPx = 2 * E * this.scale
    const tilePx = worldPx / Math.pow(2, zi)
    if (tilePx < 64 || tilePx > 1024) return // 极端缩放不画瓦片
    const [mleft, mtop] = this.toMeters(0, 0)
    const [mright, mbottom] = this.toMeters(this.w, this.h)
    const n = Math.pow(2, zi)
    const tx0 = Math.floor(((mleft + E) / (2 * E)) * n)
    const tx1 = Math.floor(((mright + E) / (2 * E)) * n)
    const ty0 = Math.floor(((E - mtop) / (2 * E)) * n)
    const ty1 = Math.floor(((E - mbottom) / (2 * E)) * n)
    for (const layer of bm.layers) {
      for (let tx = tx0; tx <= tx1; tx++) {
        if (tx < 0 || tx >= n) continue
        for (let ty = ty0; ty <= ty1; ty++) {
          if (ty < 0 || ty >= n) continue
          const key = layer + '/' + zi + '/' + tx + '/' + ty
          let t = this.tiles.get(key)
          if (!t) t = this._loadTile(key, layer, zi, tx, ty)
          if (t.status !== 'ok' || !t.img) continue
          const m0x = -E + tx * (2 * E / n)
          const m0y = E - ty * (2 * E / n)
          const [sx, sy] = this.toScreen(m0x, m0y)
          ctx.drawImage(t.img, sx, sy, tilePx + 1, tilePx + 1)
        }
      }
    }
    // 版权注记（天地图要求署名）
    ctx.fillStyle = 'rgba(255,255,255,.75)'
    ctx.font = '10px sans-serif'
    ctx.textAlign = 'right'
    ctx.fillText(TD_CREDIT, this.w - 4, this.h - 4)
    ctx.textAlign = 'left'
  }

  private _loadTile(key: string, layer: string, zi: number, tx: number, ty: number): Tile {
    if (this.tiles.size > TILE_CACHE_MAX) this.tiles.clear()
    const t: Tile = { status: 'loading', img: null, started: Date.now() }
    const img = new Image()
    // 不设 crossOrigin：天地图瓦片服务不保证回 CORS 头，带 anonymous 会直接加载失败。
    // 本画布只做展示、从不 getImageData 回读像素，污染画布无影响。
    img.onload = () => { t.status = 'ok'; t.img = img; this.tileSuccess++; this.requestDraw() }
    img.onerror = () => { t.status = 'err'; this.tileErrors++; this.requestDraw() }
    img.src = this._tdUrl(layer, zi, tx, ty)
    this.tiles.set(key, t)
    return t
  }

  private _tdUrl(layer: string, zi: number, tx: number, ty: number): string {
    const host = TD_HOSTS[(tx + ty) % TD_HOSTS.length] // 子域轮询，绕开单域名并发上限
    const q = new URLSearchParams({
      SERVICE: 'WMTS', REQUEST: 'GetTile', VERSION: '1.0.0',
      LAYER: layer, STYLE: 'default', TILEMATRIXSET: 'w', FORMAT: 'tiles',
      TILEMATRIX: String(zi), TILEROW: String(ty), TILECOL: String(tx), tk: this.tk || '',
    })
    return `https://${host}.tianditu.gov.cn/${layer}_w/wmts?${q}`
  }

  // ── 绘制 ──

  dispose() {
    this.disposed = true
    if (this._statusTimer) { clearTimeout(this._statusTimer); this._statusTimer = 0 }
    if (this._idleTimer) { clearTimeout(this._idleTimer); this._idleTimer = 0 }
    if (this._raf) { cancelAnimationFrame(this._raf); this._raf = 0 }
  }

  /// 单 RAF 合并：一帧内的多次数据变更只重绘一次
  requestDraw() {
    if (this.disposed || this._raf) return
    this._raf = requestAnimationFrame(() => { this._raf = 0; this.draw() })
  }

  /// 尚未就绪（无尺寸/无视图）时不能静默丢弃重绘请求，否则后续事件再也画不出来
  private _scheduleEmptyFrame() {
    if (this.disposed || this._idleTimer) return
    this._idleTimer = setTimeout(() => { this._idleTimer = 0; this.requestDraw() }, 300)
  }

  draw() {
    const ctx = this.ctx
    if (!this.w || !this.scale) { this._scheduleEmptyFrame(); return }
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0)
    ctx.clearRect(0, 0, this.w, this.h)
    ctx.fillStyle = '#141a1e'
    ctx.fillRect(0, 0, this.w, this.h)
    if (!this.view) {
      ctx.fillStyle = 'rgba(255,255,255,.55)'
      ctx.font = '12px sans-serif'
      ctx.fillText('连接中…', 10, 20)
      return
    }
    if (this.tk && this.tilesEnabled) this._tileStatus()
    const showTiles = this._tilesAllowed() && this.display.showBasemap
    if (showTiles) this._drawTiles()
    this._drawHexes(ctx, showTiles)
    this._drawOverlays(ctx)
    if (this.display.showUnits) this._drawUnits(ctx)
    this._drawCursorHex(ctx)
    this._drawTileHint()
    if (this.display.showLegend) this._drawFeatureLegend(this.view.geo ? this.view.geo.radius * this.scale : 0)
  }

  private _hexPath(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    ctx.beginPath()
    for (let i = 0; i < 6; i++) {
      const a = Math.PI / 180 * (60 * i - 90)
      const px = sx + rp * Math.cos(a), py = sy + rp * Math.sin(a)
      if (i === 0) ctx.moveTo(px, py)
      else ctx.lineTo(px, py)
    }
    ctx.closePath()
  }

  /** 可见范围（米）：视口裁剪用，500+ 格不必每格都算屏幕坐标 */
  private _viewport(): [number, number, number, number] {
    const [m0x, m0y] = this.toMeters(0, 0)
    const [m1x, m1y] = this.toMeters(this.w, this.h)
    return [Math.min(m0x, m1x), Math.min(m0y, m1y), Math.max(m0x, m1x), Math.max(m0y, m1y)]
  }

  private _drawHexes(ctx: CanvasRenderingContext2D, showTiles: boolean) {
    if (!this.view) return
    const rp = this.view.geo.radius * this.scale // 外接圆半径（像素）
    if (rp < 2) return // 缩得太小，交给瓦片/底色
    const alpha = showTiles ? 0.28 : 0.96
    const [x0, y0, x1, y1] = this._viewport()
    const visible = (c: CellRecord) =>
      c.mx >= x0 - rp * 2 && c.mx <= x1 + rp * 2 && c.my >= y0 - rp * 2 && c.my <= y1 + rp * 2

    ctx.globalAlpha = alpha
    if (this.display.showTerrain) {
      for (const c of this._cells) {
        if (!visible(c)) continue
        const [sx, sy] = this.toScreen(c.mx, c.my)
        ctx.fillStyle = TERRAIN_COLOR[c.terrain] || '#555'
        this._hexPath(ctx, sx, sy, rp)
        ctx.fill()
      }
    }
    ctx.globalAlpha = 1
    // 道路 / 河流（仅在能看清时画）
    if (rp > 6) this._drawLines(ctx, rp)
    // 地物层：线状（铁路/输电线）→ 点状（机场/电厂/变电站/车站/隧道）→ 要点归属
    if (this.display.showFeatures) {
      if (rp > 5) this._drawFeatureLinks(rp)
      if (rp > 9) {
        this._drawFeatureGlyphs(ctx, rp)
        this._drawNodeRings(ctx, rp)
      }
    }
    // 格网描边（无底图时更明显，有底图只在放大到能看清时画；纯格网模式下加亮）
    if (this.display.showGrid && (!showTiles || rp > 14)) {
      ctx.globalAlpha = showTiles ? 0.14 : (this.display.showTerrain ? 0.3 : 0.55)
      ctx.strokeStyle = '#0c1013'
      ctx.lineWidth = 1
      for (const c of this._cells) {
        if (!visible(c) || c.terrain === 'void') continue
        const [sx, sy] = this.toScreen(c.mx, c.my)
        this._hexPath(ctx, sx, sy, rp)
        ctx.stroke()
      }
      ctx.globalAlpha = 1
    }
    // 格坐标标注：放大到单格半径可容纳小字时显示
    if (this.display.showCoords && rp > 22) {
      ctx.fillStyle = 'rgba(220,230,240,.6)'
      ctx.font = `${Math.min(11, Math.round(rp * 0.38))}px monospace`
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      for (const c of this._cells) {
        if (!visible(c) || c.terrain === 'void') continue
        const [sx, sy] = this.toScreen(c.mx, c.my)
        ctx.fillText(`${c.q},${c.r}`, sx, sy)
      }
      ctx.textAlign = 'start'
      ctx.textBaseline = 'alphabetic'
    }
  }

  // ── 线状地物：公路 / 河流 / 桥梁 / 城镇 ──

  /// 沿「共享边」连笔：dirsRightFor 只取半边方向，保证每对相邻格只连一次
  private _linkPairs(key: 'railway' | 'powerline'): CellRecord[][] {
    const pairs: CellRecord[][] = []
    for (const c of this._cells) {
      if (!c[key]) continue
      for (const [dq, dr] of dirsRightFor(c.r)) {
        const nb = this.cellAt(c.q + dq, c.r + dr)
        if (nb && nb[key]) pairs.push([c, nb])
      }
    }
    return pairs
  }

  private _strokePairs(pairs: CellRecord[][], color: string, width: number, dash?: number[]) {
    const ctx = this.ctx
    ctx.save()
    ctx.strokeStyle = color
    ctx.lineWidth = width
    if (dash) ctx.setLineDash(dash)
    ctx.beginPath()
    for (const [a, b] of pairs) {
      const [ax, ay] = this.toScreen(a.mx, a.my)
      const [bx, by] = this.toScreen(b.mx, b.my)
      ctx.moveTo(ax, ay)
      ctx.lineTo(bx, by)
    }
    ctx.stroke()
    ctx.restore()
  }

  private _drawLines(ctx: CanvasRenderingContext2D, rp: number) {
    for (const c of this._cells) {
      if (!c.road && !c.river && !c.bridge) continue
      for (const [dq, dr] of dirsRightFor(c.r)) {
        const nb = this.cellAt(c.q + dq, c.r + dr)
        if (!nb) continue
        const link = (c.road && nb.road) || (c.river && nb.river) || (c.bridge && (nb.road || nb.bridge))
        if (!link) continue
        const [ax, ay] = this.toScreen(c.mx, c.my)
        const [bx, by] = this.toScreen(nb.mx, nb.my)
        if (c.river || nb.river) {
          ctx.strokeStyle = '#3a6b86'
          ctx.lineWidth = Math.max(1.5, rp * 0.28)
        } else {
          ctx.strokeStyle = '#c9b98d'
          ctx.lineWidth = Math.max(1, rp * 0.12)
        }
        ctx.beginPath()
        ctx.moveTo(ax, ay)
        ctx.lineTo(bx, by)
        ctx.stroke()
      }
      // 桥梁标记
      if (c.bridge && rp > 10) {
        const [sx, sy] = this.toScreen(c.mx, c.my)
        ctx.fillStyle = '#d8d2bf'
        ctx.fillRect(sx - rp * 0.25, sy - rp * 0.12, rp * 0.5, rp * 0.24)
      }
      // 城镇：绕心的小方块簇，与兵力块一眼可分
      if (c.city > 0 && rp > 8) {
        const [sx, sy] = this.toScreen(c.mx, c.my)
        const n = Math.min(3, c.city)
        ctx.fillStyle = '#413a30'
        for (let i = 0; i < n * 2; i++) {
          const a = (i / (n * 2)) * Math.PI * 2
          ctx.fillRect(sx + Math.cos(a) * rp * 0.4 - rp * 0.1, sy + Math.sin(a) * rp * 0.4 - rp * 0.1, rp * 0.2, rp * 0.2)
        }
      }
    }
  }

  // ── 地物层：铁路 / 输电网络 / 机场 / 电厂 / 变电站 / 车站 / 隧道 / 要点归属 ──

  private _drawFeatureLinks(rp: number) {
    const rail = this._linkPairs('railway')
    if (rail.length) {
      // 铁路：深色钢轨底 + 浅色枕木断点，和公路的连续实线一眼可分
      this._strokePairs(rail, '#2b3136', Math.max(1.6, rp * 0.2))
      this._strokePairs(rail, '#cfd3d6', Math.max(0.9, rp * 0.1), [Math.max(1, rp * 0.12), Math.max(1.6, rp * 0.28)])
    }
    const wire = this._linkPairs('powerline')
    if (wire.length) this._strokePairs(wire, '#c9b24a', Math.max(1, rp * 0.075), [Math.max(1.5, rp * 0.34), Math.max(1.2, rp * 0.26)])
  }

  /// 杆塔只画在线路的端点/分叉上（每个格都画会糊成一片）
  private _pylonCells(): CellRecord[] {
    const out: CellRecord[] = []
    for (const c of this._cells) {
      if (!c.powerline) continue
      let deg = 0
      for (const [dq, dr] of dirsFor(c.r)) {
        const nb = this.cellAt(c.q + dq, c.r + dr)
        if (nb && nb.powerline) deg++
      }
      if (deg !== 2) out.push(c)
    }
    return out
  }

  private _drawFeatureGlyphs(ctx: CanvasRenderingContext2D, rp: number) {
    for (const c of this._cells) {
      const [sx, sy] = this.toScreen(c.mx, c.my)
      if (sx < -rp || sy < -rp || sx > this.w + rp || sy > this.h + rp) continue
      if (c.tunnel) this._glyphTunnel(ctx, sx, sy, rp)
      if (c.power_plant) this._glyphPlant(ctx, sx, sy, rp)
      if (c.substation) this._glyphSubstation(ctx, sx, sy, rp)
      if ((c.airport ?? 0) > 0) this._glyphAirport(ctx, sx, sy, rp, c.airport ?? 0)
      if (c.rail_station) this._glyphStation(ctx, sx, sy, rp)
      if (c.fuel_station) this._glyphFuel(ctx, sx, sy, rp)
    }
    for (const c of this._pylonCells()) {
      const [sx, sy] = this.toScreen(c.mx, c.my)
      if (sx < -rp || sy < -rp || sx > this.w + rp || sy > this.h + rp) continue
      this._glyphPylon(ctx, sx, sy, rp)
    }
  }

  _glyphTunnel(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    const r = rp * 0.32
    ctx.beginPath()
    ctx.moveTo(sx - r, sy + r * 0.75)
    ctx.lineTo(sx - r, sy - r * 0.1)
    ctx.arc(sx, sy - r * 0.1, r, Math.PI, 0)
    ctx.lineTo(sx + r, sy + r * 0.75)
    ctx.closePath()
    ctx.fillStyle = '#1c2226'
    ctx.fill()
    ctx.strokeStyle = '#cfd3d6'
    ctx.lineWidth = 1
    ctx.stroke()
  }

  _glyphPlant(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    ctx.beginPath()
    ctx.arc(sx, sy, rp * 0.4, 0, Math.PI * 2)
    ctx.fillStyle = '#3a2f16'
    ctx.fill()
    ctx.strokeStyle = '#e8b53f'
    ctx.lineWidth = 1.2
    ctx.stroke()
    const b: Array<[number, number]> = [[-.09, -.3], [.16, -.3], [.02, -.05], [.16, -.05], [-.1, .32], [-.01, .03], [-.16, .03]]
    ctx.beginPath()
    b.forEach(([x, y], i) => {
      const px = sx + x * rp, py = sy + y * rp
      if (i) ctx.lineTo(px, py)
      else ctx.moveTo(px, py)
    })
    ctx.closePath()
    ctx.fillStyle = '#e8b53f'
    ctx.fill()
  }

  _glyphSubstation(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    const r = rp * 0.34
    ctx.beginPath()
    ctx.moveTo(sx, sy - r)
    ctx.lineTo(sx + r, sy)
    ctx.lineTo(sx, sy + r)
    ctx.lineTo(sx - r, sy)
    ctx.closePath()
    ctx.fillStyle = '#123330'
    ctx.fill()
    ctx.strokeStyle = '#5fc9b8'
    ctx.lineWidth = 1.2
    ctx.stroke()
    ctx.fillStyle = '#5fc9b8'
    ctx.fillRect(sx - r * 0.45, sy - r * 0.14, r * 0.9, r * 0.28)
  }

  _glyphPylon(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    ctx.strokeStyle = '#c9b24a'
    ctx.lineWidth = 1
    ctx.beginPath()
    ctx.moveTo(sx - rp * 0.14, sy + rp * 0.2)
    ctx.lineTo(sx, sy - rp * 0.22)
    ctx.lineTo(sx + rp * 0.14, sy + rp * 0.2)
    ctx.moveTo(sx - rp * 0.2, sy - rp * 0.14)
    ctx.lineTo(sx + rp * 0.2, sy - rp * 0.14)
    ctx.stroke()
  }

  _glyphAirport(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number, grade: number) {
    const len = rp * (grade >= 2 ? 1.1 : 0.8), wid = rp * 0.22
    ctx.save()
    ctx.translate(sx, sy)
    ctx.rotate(-Math.PI / 5)
    ctx.fillStyle = '#e2e6e9'
    ctx.fillRect(-len / 2, -wid / 2, len, wid)
    ctx.strokeStyle = '#3a4046'
    ctx.lineWidth = 0.8
    ctx.strokeRect(-len / 2, -wid / 2, len, wid)
    ctx.setLineDash([rp * 0.12, rp * 0.12])
    ctx.beginPath()
    ctx.moveTo(-len / 2, 0)
    ctx.lineTo(len / 2, 0)
    ctx.strokeStyle = '#a8742a'
    ctx.lineWidth = 1
    ctx.stroke()
    ctx.restore()
    if (grade >= 2) { // 干线机场：塔台角标
      ctx.beginPath()
      ctx.arc(sx + rp * 0.44, sy - rp * 0.44, rp * 0.13, 0, Math.PI * 2)
      ctx.fillStyle = '#f0d9a5'
      ctx.fill()
    }
  }

  /// 加油站：油泵造型（机身 + 油管弯钩），橙色系与机场/车站区分
  _glyphFuel(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    const w = rp * 0.3, h = rp * 0.36, x = sx - w / 2 - rp * 0.05, y = sy - h / 2
    ctx.fillStyle = '#33241a'
    ctx.fillRect(x, y, w, h)
    ctx.strokeStyle = '#e0913f'
    ctx.lineWidth = 1
    ctx.strokeRect(x, y, w, h)
    ctx.fillStyle = '#e0913f'
    ctx.fillRect(x + w * 0.22, y + h * 0.18, w * 0.56, h * 0.26) // 仪表窗
    ctx.beginPath()                                              // 油管弯钩
    ctx.moveTo(x + w, y + h * 0.3)
    ctx.lineTo(x + w + rp * 0.12, y + h * 0.3)
    ctx.lineTo(x + w + rp * 0.12, y + h * 0.75)
    ctx.stroke()
  }

  _glyphStation(ctx: CanvasRenderingContext2D, sx: number, sy: number, rp: number) {
    const w = rp * 0.46, h = rp * 0.3
    ctx.fillStyle = '#252a2f'
    ctx.fillRect(sx - w / 2, sy - h / 2, w, h)
    ctx.strokeStyle = '#e6e2d6'
    ctx.lineWidth = 1
    ctx.strokeRect(sx - w / 2, sy - h / 2, w, h)
    ctx.fillStyle = '#e6e2d6'
    ctx.fillRect(sx - w / 2, sy - h * 0.1, w, h * 0.2) // 雨棚横线，与兵力的圆角块区分
  }

  /// 要点归属：视图里给了谁就画谁的虚线圈（双盲视图只含己方，观战视图为空）
  private _drawNodeRings(ctx: CanvasRenderingContext2D, rp: number) {
    const entries = Object.entries(this.nodeHolders || {})
    if (!entries.length || rp < 7) return
    ctx.save()
    ctx.lineWidth = 1.8
    ctx.setLineDash([rp * 0.3, rp * 0.22])
    for (const [key, side] of entries) {
      const c = this._cellAt.get(key)
      if (!c) continue
      const [sx, sy] = this.toScreen(c.mx, c.my)
      if (sx < -rp || sy < -rp || sx > this.w + rp || sy > this.h + rp) continue
      ctx.strokeStyle = SIDE_COLOR[side] || '#7f8c8d'
      this._hexPath(ctx, sx, sy, rp * 0.88)
      ctx.stroke()
    }
    ctx.restore()
  }

  private _hasFeatures() {
    return this._cells.some((c) => c.railway || c.powerline || c.tunnel || (c.airport ?? 0) > 0
      || c.power_plant || c.substation || c.rail_station || c.fuel_station)
  }

  /// 图例：竖排在右上（让开缩放控件与底部瓦片提示），缩略时隐藏避免遮挡兵力
  private _drawFeatureLegend(rp: number) {
    if (!this._hasFeatures() || rp < 9 || this.w < 320) return
    const ctx = this.ctx
    const has = (k: 'railway' | 'powerline' | 'tunnel' | 'rail_station' | 'power_plant' | 'substation' | 'airport' | 'fuel_station') =>
      k === 'airport' ? this._cells.some((c) => (c.airport ?? 0) > 0) : this._cells.some((c) => !!c[k])
    const items: Array<[string, string, string]> = []
    if (has('railway')) items.push(['rail', '铁路', '#cfd3d6'])
    if (has('powerline')) items.push(['wire', '输电线', '#c9b24a'])
    if (has('power_plant')) items.push(['plant', '发电厂', '#e8b53f'])
    if (has('substation')) items.push(['sub', '变电站', '#5fc9b8'])
    if (has('airport')) items.push(['air', '机场', '#e2e6e9'])
    if (has('rail_station')) items.push(['station', '火车站', '#e6e2d6'])
    if (has('fuel_station')) items.push(['fuel', '加油站', '#e0913f'])
    if (has('tunnel')) items.push(['tunnel', '隧道', '#cfd3d6'])
    if (Object.keys(this.nodeHolders || {}).length) items.push(['ring', '掌握要点', ''])
    const power = this.power ? `电网 红×${this.power.red} 蓝×${this.power.blue}` : null
    const rows = items.length + (power ? 1 : 0)
    const rowH = 15, padX = 7, padY = 6
    ctx.save()
    ctx.font = '11px sans-serif'
    ctx.textBaseline = 'middle'
    ctx.textAlign = 'left'
    let boxW = 0
    for (const [, label] of items) boxW = Math.max(boxW, 18 + ctx.measureText(label).width)
    if (power) boxW = Math.max(boxW, ctx.measureText(power).width)
    boxW += padX * 2
    const boxH = rows * rowH + padY * 2
    const x0 = this.w - boxW - 8, y0 = 128
    ctx.fillStyle = 'rgba(15,19,23,.8)'
    ctx.strokeStyle = 'rgba(255,255,255,.18)'
    ctx.lineWidth = 1
    this._roundRect(ctx, x0, y0, boxW, boxH, 4)
    ctx.fill()
    ctx.stroke()
    items.forEach(([kind, label, color], i) => {
      const cy = y0 + padY + rowH * i + rowH / 2
      this._legendMark(ctx, kind, x0 + padX, cy, color)
      ctx.fillStyle = '#cdd6de'
      ctx.fillText(label, x0 + padX + 18, cy)
    })
    if (power) {
      ctx.fillStyle = '#e8b53f'
      ctx.fillText(power, x0 + padX, y0 + padY + rowH * items.length + rowH / 2)
    }
    ctx.restore()
  }

  private _legendMark(ctx: CanvasRenderingContext2D, kind: string, x: number, cy: number, color: string) {
    const cx = x + 8
    if (kind === 'rail' || kind === 'wire') {
      if (kind === 'rail') {
        ctx.strokeStyle = '#2b3136'
        ctx.lineWidth = 3
        ctx.beginPath()
        ctx.moveTo(x, cy)
        ctx.lineTo(x + 16, cy)
        ctx.stroke()
      }
      ctx.save()
      ctx.strokeStyle = color
      ctx.lineWidth = 1.4
      ctx.setLineDash(kind === 'wire' ? [3, 2] : [2, 3])
      ctx.beginPath()
      ctx.moveTo(x, cy)
      ctx.lineTo(x + 16, cy)
      ctx.stroke()
      ctx.restore()
      return
    }
    if (kind === 'plant') {
      ctx.beginPath()
      ctx.arc(cx, cy, 5, 0, Math.PI * 2)
      ctx.fillStyle = '#3a2f16'
      ctx.fill()
      ctx.strokeStyle = color
      ctx.lineWidth = 1.2
      ctx.stroke()
      return
    }
    if (kind === 'sub') {
      ctx.beginPath()
      ctx.moveTo(cx, cy - 5)
      ctx.lineTo(cx + 5, cy)
      ctx.lineTo(cx, cy + 5)
      ctx.lineTo(cx - 5, cy)
      ctx.closePath()
      ctx.fillStyle = '#123330'
      ctx.fill()
      ctx.strokeStyle = color
      ctx.lineWidth = 1.2
      ctx.stroke()
      return
    }
    if (kind === 'air') {
      ctx.save()
      ctx.translate(cx, cy)
      ctx.rotate(-Math.PI / 5)
      ctx.fillStyle = color
      ctx.fillRect(-8, -1.4, 16, 2.8)
      ctx.restore()
      return
    }
    if (kind === 'station') {
      ctx.fillStyle = '#252a2f'
      ctx.fillRect(cx - 5, cy - 3.5, 10, 7)
      ctx.strokeStyle = color
      ctx.lineWidth = 1
      ctx.strokeRect(cx - 5, cy - 3.5, 10, 7)
      return
    }
    if (kind === 'fuel') {
      ctx.fillStyle = '#33241a'
      ctx.fillRect(cx - 3, cy - 4, 6, 8)
      ctx.strokeStyle = color
      ctx.lineWidth = 1
      ctx.strokeRect(cx - 3, cy - 4, 6, 8)
      ctx.beginPath()
      ctx.moveTo(cx + 3, cy - 1)
      ctx.lineTo(cx + 6, cy - 1)
      ctx.lineTo(cx + 6, cy + 3)
      ctx.stroke()
      return
    }
    if (kind === 'tunnel') {
      ctx.beginPath()
      ctx.moveTo(cx - 5, cy + 4)
      ctx.lineTo(cx - 5, cy - 1)
      ctx.arc(cx, cy - 1, 5, Math.PI, 0)
      ctx.lineTo(cx + 5, cy + 4)
      ctx.closePath()
      ctx.fillStyle = '#1c2226'
      ctx.fill()
      ctx.strokeStyle = color
      ctx.lineWidth = 1
      ctx.stroke()
      return
    }
    if (kind === 'ring') {
      ctx.save()
      ctx.setLineDash([3, 2])
      ctx.strokeStyle = '#c15b4e'
      ctx.lineWidth = 1.4
      ctx.beginPath()
      ctx.arc(cx, cy, 5.5, 0, Math.PI * 2)
      ctx.stroke()
      ctx.restore()
    }
  }

  _roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
    ctx.beginPath()
    ctx.moveTo(x + r, y)
    ctx.arcTo(x + w, y, x + w, y + h, r)
    ctx.arcTo(x + w, y + h, x, y + h, r)
    ctx.arcTo(x, y + h, x, y, r)
    ctx.arcTo(x, y, x + w, y, r)
    ctx.closePath()
  }

  // ── 兵力层 ──

  private _drawUnits(ctx: CanvasRenderingContext2D) {
    if (!this.view) return
    const rp = this.view.geo.radius * this.scale
    if (rp < 4) { this._drawUnitsAsDots(ctx); return }
    for (const u of this.own) this._drawUnit(ctx, u, rp, false)
    for (const u of this.enemies) this._drawUnit(ctx, u, rp, true)
    if (this.view.kind === 'director') for (const u of this.allUnits) this._drawUnit(ctx, u, rp, false)
  }

  private _drawUnit(ctx: CanvasRenderingContext2D, u: WireUnit | WireContact, rp: number, isContact: boolean) {
    if (!this.view) return
    const [mx, my] = hexCenterMeters(this.view.geo, u.q, u.r)
    const [sx, sy] = this.toScreen(mx, my)
    const side = u.side || (isContact ? 'unknown' : 'neutral')
    const color = SIDE_COLOR[side] || '#888'
    const level = isContact ? ((u as WireContact).level || 0) : 3
    // 暴露分级即情报置信度：E0 只是「有过迹象」，E3 才是完整番号
    const alpha = isContact ? 0.45 + 0.18 * level : 1
    ctx.globalAlpha = alpha
    const bw = rp * 0.9, bh = rp * 0.7
    ctx.fillStyle = color
    ctx.strokeStyle = '#0d1114'
    ctx.lineWidth = 1.5
    this._roundRect(ctx, sx - bw / 2, sy - bh / 2, bw, bh, 3)
    ctx.fill()
    ctx.stroke()
    // 陈旧（脱离观测）用虚线外圈
    if (isContact && (u as WireContact).stale) {
      ctx.setLineDash([3, 3])
      ctx.strokeStyle = '#d8a24a'
      this._hexPath(ctx, sx, sy, rp * 0.92)
      ctx.stroke()
      ctx.setLineDash([])
    }
    // 指挥所 / 机降
    if ((u as WireUnit).hq) {
      ctx.strokeStyle = '#f0d9a5'
      ctx.lineWidth = 2
      this._roundRect(ctx, sx - bw / 2 - 3, sy - bh / 2 - 3, bw + 6, bh + 6, 4)
      ctx.stroke()
    }
    if ((u as WireUnit).airborne) {
      ctx.fillStyle = '#0d1114'
      ctx.beginPath()
      ctx.moveTo(sx - rp * 0.3, sy + bh / 2)
      ctx.lineTo(sx + rp * 0.3, sy + bh / 2)
      ctx.lineTo(sx, sy + bh / 2 + rp * 0.3)
      ctx.fill()
    }
    ctx.fillStyle = '#0d1114'
    ctx.font = `bold ${Math.max(8, rp * 0.42)}px sans-serif`
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    let label = (u.id || '').slice(-2)
    if (isContact && level < 2) label = '?'
    else if (u.type_label) label = u.type_label.slice(0, 2)
    ctx.fillText(label, sx, sy)
    if (isContact && level >= 1) {
      ctx.fillStyle = '#0d1114'
      ctx.font = `bold ${Math.max(7, rp * 0.3)}px sans-serif`
      ctx.fillText('E' + level, sx + bw / 2 - rp * 0.15, sy - bh / 2 + rp * 0.15)
    }
    ctx.globalAlpha = 1
    ctx.textBaseline = 'alphabetic'
    ctx.textAlign = 'left'
  }

  /// 极小缩放降级为色点：兵力块在这个尺寸只会糊成一片噪声
  private _drawUnitsAsDots(ctx: CanvasRenderingContext2D) {
    if (!this.view) return
    for (const u of [...this.own, ...this.enemies, ...this.allUnits]) {
      const [mx, my] = hexCenterMeters(this.view.geo, u.q, u.r)
      const [sx, sy] = this.toScreen(mx, my)
      ctx.fillStyle = SIDE_COLOR[u.side] || ((u as WireContact).level ? '#c15b4e' : '#7f8c8d')
      ctx.beginPath()
      ctx.arc(sx, sy, 3, 0, Math.PI * 2)
      ctx.fill()
    }
  }

  private _drawOverlays(ctx: CanvasRenderingContext2D) {
    if (!this.view) return
    const geo = this.view.geo
    const rp = geo.radius * this.scale
    // 射程/任务区预览
    if (this.rangeCenter && this.rangeRadius > 0) {
      const set = new Set(hexRange(this.rangeCenter[0], this.rangeCenter[1], this.rangeRadius).map((h) => h.join(',')))
      ctx.globalAlpha = 0.22
      ctx.fillStyle = '#d8a24a'
      for (const c of this._cells) {
        if (!set.has(c.q + ',' + c.r)) continue
        const [sx, sy] = this.toScreen(c.mx, c.my)
        this._hexPath(ctx, sx, sy, rp)
        ctx.fill()
      }
      ctx.globalAlpha = 1
    }
    // 选中格
    if (this.selection) {
      const [mx, my] = hexCenterMeters(geo, this.selection.q, this.selection.r)
      const [sx, sy] = this.toScreen(mx, my)
      ctx.strokeStyle = '#7fb069'
      ctx.lineWidth = 2.5
      this._hexPath(ctx, sx, sy, rp * 0.95)
      ctx.stroke()
    }
    // 草稿路径
    if (this.draftPath && this.draftPath.length >= 2) {
      ctx.strokeStyle = '#cfe9d6'
      ctx.lineWidth = Math.max(2, rp * 0.25)
      ctx.beginPath()
      this.draftPath.forEach(([q, r], i) => {
        const c = this.cellAt(q, r)
        const [mx, my] = c ? [c.mx, c.my] : hexCenterMeters(geo, q, r)
        const [sx, sy] = this.toScreen(mx, my)
        if (i === 0) ctx.moveTo(sx, sy)
        else ctx.lineTo(sx, sy)
      })
      ctx.stroke()
    }
  }

  private _drawCursorHex(ctx: CanvasRenderingContext2D) {
    if (!this._mouse || !this.view) return
    const rp = this.view.geo.radius * this.scale
    const hex = this.hexAtScreen(this._mouse.sx, this._mouse.sy)
    if (!hex) return
    const c = this.cellAt(hex.q, hex.r)
    if (!c) return
    ctx.strokeStyle = 'rgba(207,233,214,.6)'
    ctx.lineWidth = 1.5
    const [sx, sy] = this.toScreen(c.mx, c.my)
    this._hexPath(ctx, sx, sy, rp * 0.95)
    ctx.stroke()
    if (rp > 8) this._drawCellInfo(ctx, c, rp)
  }

  /// 格内地物摘要（悬停即读，不用点）：公路/铁路/桥隧/电网/机场 + 要点归属
  private _cellTags(c: CellRecord): string[] {
    const t: string[] = []
    if (c.road) t.push('公路')
    if (c.railway) t.push('铁路')
    if (c.rail_station) t.push('火车站')
    if (c.fuel_station) t.push('加油站')
    if (c.tunnel) t.push('隧道')
    if (c.power_plant) t.push('发电厂')
    if (c.substation) t.push('变电站')
    else if (c.powerline) t.push('输电线')
    if (c.airport === 2) t.push('干线机场')
    else if (c.airport === 1) t.push('野战机场')
    if (c.bridge) t.push('桥梁')
    else if (c.river) t.push('无桥渡口')
    return t
  }

  private _drawCellInfo(ctx: CanvasRenderingContext2D, c: CellRecord, rp: number) {
    const key = c.q + ',' + c.r
    const tags = this._cellTags(c)
    const node = this.nodeLabels.get(key)
    const holder = (this.nodeHolders || {})[key]
    if (!tags.length && !node) return
    const l1 = `(${c.q},${c.r}) ${TERRAIN_LABEL[c.terrain] || c.terrain} · 海拔 ${Math.round(c.elev)}m`
    const l2 = [node ? '要点：' + node + (holder ? '（' + (holder === 'red' ? '红方' : '蓝方') + '掌握）' : '') : null, ...tags]
      .filter(Boolean).join(' · ')
    ctx.save()
    ctx.font = '11px sans-serif'
    ctx.textBaseline = 'middle'
    ctx.textAlign = 'left'
    const lines = l2 ? [l1, l2] : [l1]
    const wTxt = Math.max(...lines.map((s) => ctx.measureText(s).width))
    const boxW = wTxt + 14, boxH = lines.length * 15 + 10
    const [sx, sy] = this.toScreen(c.mx, c.my)
    let x0 = sx - boxW / 2
    let y0 = sy + rp + 8
    x0 = Math.max(4, Math.min(this.w - boxW - 4, x0))
    if (y0 + boxH > this.h - 4) y0 = Math.max(4, sy - rp - boxH - 8) // 贴底时翻到格上方
    ctx.fillStyle = 'rgba(15,19,23,.86)'
    ctx.strokeStyle = 'rgba(255,255,255,.2)'
    ctx.lineWidth = 1
    this._roundRect(ctx, x0, y0, boxW, boxH, 4)
    ctx.fill()
    ctx.stroke()
    ctx.fillStyle = '#dfe6ec'
    lines.forEach((s, i) => ctx.fillText(s, x0 + 7, y0 + 12 + i * 15))
    ctx.restore()
  }
}
