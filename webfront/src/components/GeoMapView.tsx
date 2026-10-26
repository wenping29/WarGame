/**
 * 在线地图 + 六角格网叠加视图（六角格/存档/剧本共用的地图引擎）。
 * 天地图 WMTS（EPSG:3857）为底图，六角格以墨卡托米计（与主地图约定一致），
 * 支持浏览/框选范围/拾取坐标/涂刷挖格补格/拖动单位落位（move）等模式。
 *
 * 性能要点（基准：8816 格 fill≈1ms，路径构建≈390ms，构建是唯一瓶颈）：
 * - 底层格网路径缓存为世界坐标（相对网格锚点）Path2D，平移/缩放零重建，仅几何变化时
 *   按 8ms/帧时间分片构建，超大网格（200×200）不冻结页面；
 * - 涂装层/挖洞层只含相关格子（极小），按引用失效，涂刷/挖格不动底层；
 *   挖洞用 destination-out 抠穿露出底图；
 * - 悬停高亮走 ref 不触发 React 重渲染；瓦片限次重试、上限淘汰，避免重试风暴。
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { loadMeta } from '../room/useUnitFull'
import { resolveTiandituTk, TIANDITU_ENV_TK } from '../services/tianditu'
import type { GisData } from '../gis/hexgen'

const E = 20037508.342789244 // EPSG:3857 半周长（米）
const SQRT3 = Math.sqrt(3)
const SIN60 = 0.8660254037844386
const TD_HOSTS = ['t0', 't1', 't2', 't3', 't4', 't5', 't6', 't7']
const LAYERS: Record<string, { label: string; layers: string[] }> = {
  vec: { label: '矢量', layers: ['vec', 'cva'] },
  img: { label: '影像', layers: ['img', 'cia'] },
  ter: { label: '地形', layers: ['ter', 'cta'] },
}
const SCALE_MIN = (256 * Math.pow(2, 2)) / (2 * E)
const SCALE_MAX = (256 * Math.pow(2, 17)) / (2 * E)
const TILE_STALL_MS = 8000
const TILE_MAX_TRIES = 3
const TILE_CACHE_MAX = 600
const HEX_BUILD_BUDGET_MS = 8

/** 六边形子路径（pointy-top，坐标原点任意） */
function hexPath(p: Path2D, cx: number, cy: number, r: number) {
  const dx = SIN60 * r
  const dy = r * 0.5
  p.moveTo(cx - dx, cy - dy)
  p.lineTo(cx, cy - r)
  p.lineTo(cx + dx, cy - dy)
  p.lineTo(cx + dx, cy + dy)
  p.lineTo(cx, cy + r)
  p.lineTo(cx - dx, cy + dy)
  p.closePath()
}

export const TERRAINS: Array<[string, string, string]> = [
  ['plain', '平原', '#5d7a4a'],
  ['forest', '森林', '#38573c'],
  ['city', '城镇', '#8a8072'],
  ['water', '水域', '#33587a'],
  ['mountain', '山地', '#6e675c'],
]
const TERRAIN_COLOR = Object.fromEntries(TERRAINS.map(([k, , c]) => [k, c])) as Record<string, string>

export interface BBox {
  west: number
  south: number
  east: number
  north: number
}
export type MapMode = 'pan' | 'paint' | 'dig' | 'fill' | 'frame' | 'pick' | 'move'

/** 六角格网叠加：SW 锚点（经纬度）+ 行列 + 外接圆半径（墨卡托米） */
export interface HexOverlay {
  west: number
  south: number
  rows: number
  cols: number
  size: number
  holes?: string[]
  terrains?: Record<string, string>
  /** true = 创建中预览（青色细线），false = 正式网格（地形涂装） */
  preview?: boolean
  /**
   * true = 引擎口径：行 r 增大向南（行 0 在北缘），锚点 = hex(0,0) 中心（与服务端 HexGrid 一致）。
   * 缺省 false = 旧预览口径：行增大向北，锚点 = 包文档 west/south。
   */
  southward?: boolean
  /** 单位标记：按格坐标画圆点（随 southward 口径定位）；dirty 画高亮外圈 */
  markers?: Array<{ q: number; r: number; color: string; dirty?: boolean }>
}

interface Props {
  mode: MapMode
  overlay: HexOverlay | null
  onFrame?: (b: BBox) => void
  onPick?: (lng: number, lat: number) => void
  onCellClick?: (k: string | null) => void
  /** mode='move'：拖起 overlay.markers 里的某个标记（按下即触发，便于选中联动） */
  onMarkerClick?: (index: number) => void
  /** mode='move'：拖动标记落到新格（q=列、r=行，按网面吸附，越界/挖除由调用方判定） */
  onMarkerMove?: (index: number, q: number, r: number) => void
  /** 变更 nonce 时飞到 bbox 范围 */
  fit?: { bbox: BBox; nonce: number } | null
  /** 常驻显示的框选范围矩形（随视图缩放平移） */
  range?: BBox | null
  /** GIS 生成数据（道路/铁路/河流线要素） */
  gis?: GisData | null
  /** 纯 canvas 模式：不加载天地图瓦片，以经纬网格线为底（避免在线底图卡顿） */
  canvasOnly?: boolean
}

/* ---------- 墨卡托 <-> 经纬度 ---------- */
export const mxToLng = (mx: number) => (mx * 180) / E
export const myToLat = (my: number) => (2 * Math.atan(Math.exp((my * Math.PI) / E)) - Math.PI / 2) * (180 / Math.PI)
export const lngToMx = (lng: number) => (lng * E) / 180
export const latToMy = (lat: number) => (E / Math.PI) * Math.log(Math.tan(Math.PI / 4 + ((lat * Math.PI) / 180) / 2))
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
const KM_PER_DEG = (Math.PI * 6378137) / 180 / 1000

interface View { cx: number; cy: number; scale: number }
interface Tile { img: HTMLImageElement | null; ok: boolean; started: number; tries: number }
const EMPTY_TERR: Record<string, string> = {}

/** 底层格网缓存：世界坐标（相对 SW 锚点），只随几何变化重建 */
interface BaseCache {
  rows: number
  cols: number
  size: number
  path: Path2D
  builtRows: number
  done: boolean
}
/** 涂装/挖洞小层缓存：按引用失效（数组/对象不可变更新） */
interface SmallCache { ref: object; path: Path2D }
/** 涂装层按地形分组的路径集 */
type TerrGroups = Map<string, Path2D>
/** GIS 线要素缓存：按 gis 对象引用失效 */
interface GisCache { ref: GisData; roads: Map<string, Path2D>; rails: Path2D; rivers: Path2D }

/** 道路等级 → [颜色, 屏幕线宽px] */
function roadStyle(cls: string): [string, number] {
  if (cls === 'motorway') return ['#e8912d', 3.2]
  if (cls === 'trunk') return ['#e8a33d', 3]
  if (cls === 'primary') return ['#e6c34d', 2.6]
  if (cls === 'secondary') return ['#d8c26a', 2.2]
  if (cls === 'road') return ['#e0b54d', 2.2]
  return ['#c9bd80', 2]
}
/** 扁平 [lng,lat,...] 折线 → 世界坐标 Path2D（逐段 moveTo/lineTo） */
function buildLine(pts: number[], path: Path2D) {
  for (let i = 0; i + 3 < pts.length; i += 2) {
    path.moveTo(lngToMx(pts[i]), latToMy(pts[i + 1]))
    path.lineTo(lngToMx(pts[i + 2]), latToMy(pts[i + 3]))
  }
}

const ckey = (col: number, row: number) => `${col},${row}`

export default function GeoMapView({ mode, overlay, onFrame, onPick, onCellClick, onMarkerClick, onMarkerMove, fit, range, gis, canvasOnly }: Props) {
  const wrapRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const viewRef = useRef<View>({ cx: lngToMx(107), cy: latToMy(34), scale: (256 * Math.pow(2, 5)) / (2 * E) })
  const tilesRef = useRef<Map<string, Tile>>(new Map())
  const rafRef = useRef(0)
  const dragRef = useRef<{ x: number; y: number; cx: number; cy: number } | null>(null)
  const bandRef = useRef<{ ax: number; ay: number; bx: number; by: number } | null>(null)
  const paintingRef = useRef(false)
  const lastCellRef = useRef<string | null>(null)
  const hoverRef = useRef<string | null>(null)
  const markerDragRef = useRef<{ idx: number; last: string | null } | null>(null)
  const deadRef = useRef(0)
  const baseRef = useRef<BaseCache | null>(null)
  const terrCacheRef = useRef<SmallCache | null>(null)
  const holeCacheRef = useRef<SmallCache | null>(null)
  const gisCacheRef = useRef<GisCache | null>(null)

  const [tk, setTk] = useState<string | null>(TIANDITU_ENV_TK)
  const [basemap, setBasemap] = useState<keyof typeof LAYERS>('vec')
  const [zoomLabel, setZoomLabel] = useState(5)
  const [tileFail, setTileFail] = useState(false)

  const modeRef = useRef(mode)
  const overlayRef = useRef(overlay)
  const rangeRef = useRef(range)
  const gisRef = useRef(gis)
  const bmRef = useRef(basemap)
  const tkRef = useRef(tk)
  const coRef = useRef(canvasOnly)
  const cbRef = useRef({ onFrame, onPick, onCellClick, onMarkerClick, onMarkerMove })
  modeRef.current = mode
  overlayRef.current = overlay
  rangeRef.current = range
  gisRef.current = gis
  bmRef.current = basemap
  tkRef.current = tk
  coRef.current = canvasOnly
  cbRef.current = { onFrame, onPick, onCellClick, onMarkerClick, onMarkerMove }

  const schedule = useCallback(() => {
    if (rafRef.current) return
    rafRef.current = requestAnimationFrame(() => {
      rafRef.current = 0
      drawRef.current?.()
    })
  }, [])
  const drawRef = useRef<(() => void) | null>(null)

  useEffect(() => {
    // 后端不可达时也要能用前端配置的密钥（.env 里的 VITE_TIANDITU_TK）
    loadMeta()
      .then((m) => setTk(resolveTiandituTk((m.tiles as { tk?: string } | undefined)?.tk)))
      .catch(() => setTk(TIANDITU_ENV_TK))
  }, [])

  // 密钥异步到达：清掉带空 tk 失败的瓦片，换钥匙重载
  useEffect(() => {
    if (!tk) return
    tilesRef.current.clear()
    schedule()
  }, [tk, schedule])

  // 周期性兜底：还有未就绪瓦片时触发重绘（到点的失败瓦片会在 draw 中重试）
  useEffect(() => {
    const iv = setInterval(() => {
      let pending = 0
      let dead = 0
      for (const t of tilesRef.current.values()) {
        if (t.ok) continue
        if (t.tries >= TILE_MAX_TRIES) dead++
        else pending++
      }
      if (dead !== deadRef.current) {
        deadRef.current = dead
        setTileFail(dead > 0)
      }
      if (pending > 0) schedule()
    }, 3000)
    return () => clearInterval(iv)
  }, [schedule])

  // overlay/range/gis 变化（表单输入/涂刷/点列表/框选回显/GIS 生成）→ 立即重绘
  useEffect(() => {
    schedule()
  }, [overlay, range, gis, schedule])

  /* ---------- 瓦片 ---------- */
  const tileUrl = (layer: string, zi: number, tx: number, ty: number) => {
    const host = TD_HOSTS[(tx + ty) % TD_HOSTS.length]
    const q = new URLSearchParams({
      SERVICE: 'WMTS', REQUEST: 'GetTile', VERSION: '1.0.0',
      LAYER: layer, STYLE: 'default', TILEMATRIXSET: 'w', FORMAT: 'tiles',
      TILEMATRIX: String(zi), TILEROW: String(ty), TILECOL: String(tx), tk: tkRef.current || '',
    })
    return `https://${host}.tianditu.gov.cn/${layer}_w/wmts?${q}`
  }

  const getTile = (key: string, layer: string, zi: number, tx: number, ty: number): Tile => {
    let t = tilesRef.current.get(key)
    if (t) {
      if (t.ok) return t
      if (t.tries >= TILE_MAX_TRIES) return t // 已判死：不再重试
      if (Date.now() - t.started <= TILE_STALL_MS) return t // 等待中
      tilesRef.current.delete(key)
      t = { img: null, ok: false, started: Date.now(), tries: t.tries + 1 }
    } else {
      // 缓存超限：先淘汰最早入库的已加载瓦片
      if (tilesRef.current.size >= TILE_CACHE_MAX) {
        for (const [k, v] of tilesRef.current) {
          if (v.ok) tilesRef.current.delete(k)
          if (tilesRef.current.size < TILE_CACHE_MAX * 0.8) break
        }
      }
      t = { img: null, ok: false, started: Date.now(), tries: 1 }
    }
    const img = new Image()
    img.onload = () => { t!.img = img; t!.ok = true; schedule() }
    img.onerror = () => { /* 保持 !ok，由卡时逻辑到点重试，超次数判死 */ }
    img.src = tileUrl(layer, zi, tx, ty)
    tilesRef.current.set(key, t)
    return t
  }

  /* ---------- 坐标换算 ---------- */
  const toScreen = (mx: number, my: number): [number, number] => {
    const v = viewRef.current
    const W = wrapRef.current?.clientWidth ?? 0
    const H = wrapRef.current?.clientHeight ?? 0
    return [(mx - v.cx) * v.scale + W / 2, H / 2 - (my - v.cy) * v.scale]
  }
  const toMeters = (px: number, py: number): [number, number] => {
    const v = viewRef.current
    const W = wrapRef.current?.clientWidth ?? 0
    const H = wrapRef.current?.clientHeight ?? 0
    return [v.cx + (px - W / 2) / v.scale, v.cy - (py - H / 2) / v.scale]
  }

  /** 屏幕点 → 覆盖层格键（锚点邻域解析定位，避免全网格扫描） */
  const hitCell = (px: number, py: number): string | null => {
    const o = overlayRef.current
    if (!o || o.rows < 1 || o.cols < 1) return null
    const [mx, my] = toMeters(px, py)
    const westM = lngToMx(o.west)
    const southM = latToMy(o.south)
    const lim = o.size * o.size
    const dir = o.southward ? -1.5 : 1.5
    const row0 = Math.floor((dir > 0 ? my - southM : southM - my) / (1.5 * o.size))
    const col0 = Math.floor((mx - westM) / (SQRT3 * o.size))
    let best: string | null = null
    let bestD = Infinity
    for (let row = row0 - 1; row <= row0 + 2; row++) {
      if (row < 0 || row >= o.rows) continue
      for (let col = col0 - 2; col <= col0 + 1; col++) {
        if (col < 0 || col >= o.cols) continue
        const gx = westM + SQRT3 * o.size * (col + 0.5 * (row & 1))
        const gy = southM + dir * o.size * row
        const d = (mx - gx) ** 2 + (my - gy) ** 2
        if (d < bestD && d <= lim) { bestD = d; best = ckey(col, row) }
      }
    }
    return best
  }

  /** 格 (col,row) 中心的墨卡托坐标（与 draw 里悬停/标记的定位同式） */
  const cellCenterM = (o: HexOverlay, col: number, row: number): [number, number] => [
    lngToMx(o.west) + SQRT3 * o.size * (col + 0.5 * (row & 1)),
    latToMy(o.south) + (o.southward ? -1.5 : 1.5) * o.size * row,
  ]

  /** 屏幕点 → 命中的标记序号（后画的在上层，故倒序找；容差与绘制半径同式再 +4px） */
  const hitMarker = (px: number, py: number): number | null => {
    const o = overlayRef.current
    const marks = o?.markers
    if (!o || !marks?.length) return null
    const [mx, my] = toMeters(px, py)
    const v = viewRef.current
    const grabPx = Math.max(3, Math.min(8, o.size * v.scale * 0.3)) + 4
    const grab = grabPx / v.scale
    for (let i = marks.length - 1; i >= 0; i--) {
      const m = marks[i]
      if (!Number.isInteger(m.q) || !Number.isInteger(m.r) || m.q < 0 || m.q >= o.cols || m.r < 0 || m.r >= o.rows) continue
      const [gx, gy] = cellCenterM(o, m.q, m.r)
      if ((mx - gx) ** 2 + (my - gy) ** 2 <= grab * grab) return i
    }
    return null
  }

  /* ---------- 绘制 ---------- */
  const draw = useCallback(() => {
    const canvas = canvasRef.current
    const wrap = wrapRef.current
    if (!canvas || !wrap) return
    const W = wrap.clientWidth
    const H = wrap.clientHeight
    const dpr = window.devicePixelRatio || 1
    if (canvas.width !== W * dpr || canvas.height !== H * dpr) {
      canvas.width = W * dpr
      canvas.height = H * dpr
    }
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.fillStyle = '#1b2228'
    ctx.fillRect(0, 0, W, H)

    const v = viewRef.current
    // 底图层：纯 canvas 模式画经纬网格线；否则加载天地图瓦片
    if (coRef.current) {
      const [mxL, myT] = toMeters(0, 0)
      const [mxR, myB] = toMeters(W, H)
      const lngL = mxToLng(mxL)
      const lngR = mxToLng(mxR)
      const latT = myToLat(myT)
      const latB = myToLat(myB)
      const span = Math.max(lngR - lngL, latT - latB, 0.0001)
      const step = span > 30 ? 10 : span > 12 ? 5 : span > 6 ? 2 : span > 3 ? 1
        : span > 1.2 ? 0.5 : span > 0.6 ? 0.25 : span > 0.25 ? 0.1
        : span > 0.1 ? 0.05 : span > 0.05 ? 0.02 : 0.01
      ctx.font = '10px sans-serif'
      ctx.lineWidth = 1
      // 经线（垂直）
      ctx.strokeStyle = 'rgba(255,255,255,.06)'
      ctx.fillStyle = 'rgba(255,255,255,.3)'
      ctx.textAlign = 'left'
      for (let lng = Math.ceil(lngL / step) * step; lng <= lngR; lng += step) {
        const x = toScreen(lngToMx(lng), 0)[0]
        ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, H); ctx.stroke()
        ctx.fillText(`${lng.toFixed(step < 1 ? 2 : 0)}°E`, x + 3, H - 5)
      }
      // 纬线（水平）
      for (let lat = Math.ceil(latB / step) * step; lat <= latT; lat += step) {
        const y = toScreen(0, latToMy(lat))[1]
        ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(W, y); ctx.stroke()
        ctx.fillText(`${lat.toFixed(step < 1 ? 2 : 0)}°N`, 4, y - 4)
      }
    } else {
      const zfac = (v.scale * 2 * E) / 256
      const ziLog = Math.round(Math.log2(zfac))
      const zi = clamp(ziLog, 1, 18)
      if (ziLog !== zoomLabel) setZoomLabel(ziLog)
      const worldPx = 2 * E * v.scale
      const tilePx = worldPx / Math.pow(2, zi)
      if (tilePx >= 64 && tilePx <= 1024) {
        const n = Math.pow(2, zi)
        const [mxL, myT] = toMeters(0, 0)
        const [mxR, myB] = toMeters(W, H)
        const tx0 = Math.floor(((mxL + E) / (2 * E)) * n)
        const tx1 = Math.floor(((mxR + E) / (2 * E)) * n)
        const ty0 = Math.floor(((E - myT) / (2 * E)) * n)
        const ty1 = Math.floor(((E - myB) / (2 * E)) * n)
        for (const layer of LAYERS[bmRef.current].layers) {
          for (let tx = tx0; tx <= tx1; tx++) {
            if (tx < 0 || tx >= n) continue
            for (let ty = ty0; ty <= ty1; ty++) {
              if (ty < 0 || ty >= n) continue
              const t = getTile(`${layer}/${zi}/${tx}/${ty}`, layer, zi, tx, ty)
              if (!t.ok || !t.img) continue
              const m0x = -E + tx * ((2 * E) / n)
              const m0y = E - ty * ((2 * E) / n)
              const [sx, sy] = toScreen(m0x, m0y)
              ctx.drawImage(t.img, sx, sy, tilePx + 1, tilePx + 1)
            }
          }
        }
        ctx.fillStyle = 'rgba(255,255,255,.75)'
        ctx.font = '10px sans-serif'
        ctx.textAlign = 'right'
        ctx.fillText('© 天地图 · 国家基础地理信息中心', W - 6, H - 6)
        ctx.textAlign = 'left'
      }
    }

    // 六角格网叠加（世界坐标缓存路径 + translate 绘制）
    const o = overlayRef.current
    if (o && o.rows >= 1 && o.cols >= 1 && o.size > 0) {
      const westM = lngToMx(o.west)
      const southM = latToMy(o.south)
      const r = o.size * v.scale
      if (r > 0) {
        if (r < 2.2) {
          // 极小格：整体矩形填充，不逐格绘制
          let x0: number, y0: number, x1: number, y1: number
          if (o.southward) {
            // 锚点 = hex(0,0) 中心，行 r 增大向南
            x0 = (westM - SQRT3 / 2 * o.size - v.cx) * v.scale + W / 2
            x1 = (westM + SQRT3 * o.size * o.cols - v.cx) * v.scale + W / 2
            y0 = H / 2 - (southM + o.size - v.cy) * v.scale                          // 北缘
            y1 = H / 2 - (southM - o.size * (1.5 * o.rows - 0.5) - v.cy) * v.scale   // 南缘
          } else {
            x0 = (westM - v.cx) * v.scale + W / 2
            y0 = H / 2 - (southM + o.size * (1.5 * o.rows + 0.5) - v.cy) * v.scale
            x1 = (westM + SQRT3 * o.size * (o.cols + 0.5) - v.cx) * v.scale + W / 2
            y1 = H / 2 - (southM - v.cy) * v.scale
          }
          ctx.globalAlpha = o.preview ? 0.25 : 0.82
          ctx.fillStyle = '#5d7a4a'
          ctx.fillRect(x0, y0, x1 - x0, y1 - y0)
          ctx.globalAlpha = 1
          ctx.strokeStyle = o.preview ? 'rgba(120,220,255,.7)' : 'rgba(12,16,20,.55)'
          ctx.lineWidth = 1
          if (o.preview) ctx.setLineDash([4, 3])
          ctx.strokeRect(x0, y0, x1 - x0, y1 - y0)
          ctx.setLineDash([])
        } else {
          // 底层格网缓存（几何变化才重建；按时间分片构建）
          let cs = baseRef.current
          if (!cs || cs.rows !== o.rows || cs.cols !== o.cols || cs.size !== o.size) {
            cs = { rows: o.rows, cols: o.cols, size: o.size, path: new Path2D(), builtRows: 0, done: false }
            baseRef.current = cs
          }
          if (!cs.done) {
            const t0 = performance.now()
            const rowDir = o.southward ? -1.5 : 1.5
            while (cs.builtRows < cs.rows && performance.now() - t0 < HEX_BUILD_BUDGET_MS) {
              const row = cs.builtRows++
              const off = 0.5 * (row & 1)
              const wy = rowDir * o.size * row
              for (let col = 0; col < cs.cols; col++) {
                hexPath(cs.path, SQRT3 * o.size * (col + off), wy, o.size)
              }
            }
            cs.done = cs.builtRows >= cs.rows
          }
          // 涂装层缓存（只含已涂格，按引用失效，分地形组）
          const terr = o.terrains ?? EMPTY_TERR
          let terrGroups: TerrGroups | null = null
          if (terr !== EMPTY_TERR) {
            if (terrCacheRef.current?.ref !== terr) {
              const g: TerrGroups = new Map()
              const rowDir = o.southward ? -1.5 : 1.5
              for (const [k, t] of Object.entries(terr)) {
                if (!TERRAIN_COLOR[t]) continue
                const [col, row] = k.split(',').map(Number)
                if (!Number.isInteger(col) || !Number.isInteger(row) || col < 0 || col >= o.cols || row < 0 || row >= o.rows) continue
                let p = g.get(t)
                if (!p) { p = new Path2D(); g.set(t, p) }
                hexPath(p, SQRT3 * o.size * (col + 0.5 * (row & 1)), rowDir * o.size * row, o.size)
              }
              terrCacheRef.current = { ref: terr, path: g as unknown as Path2D }
            }
            terrGroups = terrCacheRef.current!.path as unknown as TerrGroups
          }
          // 挖洞层缓存
          const holes = o.holes ?? []
          if (holeCacheRef.current?.ref !== holes) {
            const p = new Path2D()
            const rowDir = o.southward ? -1.5 : 1.5
            for (const k of holes) {
              const [col, row] = k.split(',').map(Number)
              if (!Number.isInteger(col) || !Number.isInteger(row) || col < 0 || col >= o.cols || row < 0 || row >= o.rows) continue
              hexPath(p, SQRT3 * o.size * (col + 0.5 * (row & 1)), rowDir * o.size * row, o.size)
            }
            holeCacheRef.current = { ref: holes, path: p }
          }
          // 绘制：translate 到网格锚点的屏幕位置，再按视图比例缩放并翻转 Y（世界坐标 Y 向上）
          const ox = (westM - v.cx) * v.scale + W / 2
          const oy = H / 2 - (southM - v.cy) * v.scale
          ctx.save()
          ctx.translate(ox, oy)
          ctx.scale(v.scale, -v.scale)
          const lw = 1 / v.scale
          if (o.preview) {
            ctx.strokeStyle = 'rgba(120,220,255,.7)'
            ctx.lineWidth = lw
            ctx.setLineDash([4 / v.scale, 3 / v.scale])
            ctx.stroke(cs.path)
            ctx.setLineDash([])
          } else {
            ctx.globalAlpha = 0.82
            ctx.fillStyle = TERRAIN_COLOR.plain
            ctx.fill(cs.path)
            ctx.globalAlpha = 1
            ctx.strokeStyle = 'rgba(12,16,20,.55)'
            ctx.lineWidth = lw
            ctx.stroke(cs.path)
            // 涂装层（不透明覆盖底色）
            if (terrGroups) {
              for (const [t, g] of terrGroups) {
                ctx.fillStyle = TERRAIN_COLOR[t] ?? TERRAIN_COLOR.plain
                ctx.fill(g)
                ctx.stroke(g)
              }
            }
            // 挖洞：抠穿底层与涂装，露出底图
            if (holes.length) {
              const hp = holeCacheRef.current!.path
              ctx.globalCompositeOperation = 'destination-out'
              ctx.fill(hp)
              ctx.globalCompositeOperation = 'source-over'
              ctx.strokeStyle = 'rgba(150,160,170,.35)'
              ctx.lineWidth = lw
              ctx.setLineDash([4 / v.scale, 3 / v.scale])
              ctx.stroke(hp)
              ctx.setLineDash([])
            }
          }
          ctx.restore()

          // 细部标记：只遍历已涂装的格子（不随总格数增长）
          if (r >= 8 && !o.preview && terr !== EMPTY_TERR) {
            const [vL, vT] = toMeters(0, 0)
            const [vR, vB] = toMeters(W, H)
            const rowDir = o.southward ? -1.5 : 1.5
            for (const [k, t] of Object.entries(terr)) {
              if (t !== 'city' && t !== 'water') continue
              const [col, row] = k.split(',').map(Number)
              const gx = westM + SQRT3 * o.size * (col + 0.5 * (row & 1))
              if (gx < vL - o.size || gx > vR + o.size) continue
              const gy = southM + rowDir * o.size * row
              if (gy < vT - o.size || gy > vB + o.size) continue
              const cx = (gx - v.cx) * v.scale + W / 2
              const cy = H / 2 - (gy - v.cy) * v.scale
              if (t === 'city') {
                ctx.fillStyle = 'rgba(20,20,20,.5)'
                ctx.fillRect(cx - 2, cy - 2, 4, 4)
              } else {
                ctx.strokeStyle = 'rgba(190,215,235,.5)'
                ctx.beginPath()
                ctx.moveTo(cx - 6, cy + 2)
                ctx.quadraticCurveTo(cx, cy - 3, cx + 6, cy + 2)
                ctx.stroke()
              }
            }
          }
        }
        // 悬停高亮（ref，不触发重渲染）
        const hv = hoverRef.current
        if (hv && (modeRef.current === 'paint' || modeRef.current === 'dig' || modeRef.current === 'fill' || modeRef.current === 'move')) {
          const [hc, hr] = hv.split(',').map(Number)
          if (hc < o.cols && hr < o.rows) {
            const gx = westM + SQRT3 * o.size * (hc + 0.5 * (hr & 1))
            const gy = southM + (o.southward ? -1.5 : 1.5) * o.size * hr
            const cx = (gx - v.cx) * v.scale + W / 2
            const cy = H / 2 - (gy - v.cy) * v.scale
            const hp = new Path2D()
            hexPath(hp, cx, cy, r)
            ctx.strokeStyle = 'rgba(240,220,140,.95)'
            ctx.lineWidth = 2.5
            ctx.stroke(hp)
          }
        }

        // 单位标记（屏幕空间圆点；overlay.markers 每次引用变化由 effect 重绘）
        const marks = o.markers
        if (marks?.length && r >= 2.2) {
          const rowDir = o.southward ? -1.5 : 1.5
          for (const m of marks) {
            if (!Number.isInteger(m.q) || !Number.isInteger(m.r)
              || m.q < 0 || m.q >= o.cols || m.r < 0 || m.r >= o.rows) continue
            const gx = westM + SQRT3 * o.size * (m.q + 0.5 * (m.r & 1))
            const gy = southM + rowDir * o.size * m.r
            const cx = (gx - v.cx) * v.scale + W / 2
            const cy = H / 2 - (gy - v.cy) * v.scale
            const rad = Math.max(3, Math.min(8, r * 0.3))
            if (m.dirty) {
              ctx.beginPath()
              ctx.arc(cx, cy, rad + 2.5, 0, Math.PI * 2)
              ctx.strokeStyle = 'rgba(240,220,140,.95)'
              ctx.lineWidth = 2
              ctx.stroke()
            }
            ctx.beginPath()
            ctx.arc(cx, cy, rad, 0, Math.PI * 2)
            ctx.globalAlpha = 0.92
            ctx.fillStyle = m.color
            ctx.fill()
            ctx.globalAlpha = 1
            ctx.lineWidth = 1.2
            ctx.strokeStyle = 'rgba(10,14,18,.8)'
            ctx.stroke()
          }
        }
      }
    }

    // GIS 线要素：河流 / 道路 / 铁路（世界坐标绝对定位，缩放平移跟随）
    const gd = gisRef.current
    if (gd) {
      let gc = gisCacheRef.current
      if (gc?.ref !== gd) {
        const rmap = new Map<string, Path2D>()
        for (const w of gd.ways.r) {
          const c = w.cls ?? ''
          let p = rmap.get(c)
          if (!p) {
            p = new Path2D()
            rmap.set(c, p)
          }
          buildLine(w.p, p)
        }
        const lp = new Path2D()
        for (const w of gd.ways.l) buildLine(w.p, lp)
        const wp = new Path2D()
        for (const w of gd.ways.w) buildLine(w.p, wp)
        gc = { ref: gd, roads: rmap, rails: lp, rivers: wp }
        gisCacheRef.current = gc
      }
      ctx.save()
      ctx.translate(W / 2 - v.cx * v.scale, H / 2 + v.cy * v.scale)
      ctx.scale(v.scale, -v.scale)
      const lw = 1 / v.scale
      ctx.lineCap = 'round'
      ctx.lineJoin = 'round'
      // 河流
      ctx.strokeStyle = 'rgba(90,150,210,.85)'
      ctx.lineWidth = 2.6 * lw
      ctx.stroke(gc.rivers)
      // 道路按等级
      for (const [cls, p] of gc.roads) {
        const [c, wd] = roadStyle(cls)
        ctx.strokeStyle = c
        ctx.lineWidth = wd * lw
        ctx.stroke(p)
      }
      // 铁路：深色底线 + 白色短划
      ctx.strokeStyle = '#2e3338'
      ctx.lineWidth = 2.6 * lw
      ctx.stroke(gc.rails)
      ctx.strokeStyle = '#e8e8e8'
      ctx.lineWidth = 1.1 * lw
      ctx.setLineDash([6 * lw, 6 * lw])
      ctx.stroke(gc.rails)
      ctx.setLineDash([])
      ctx.restore()
    }

    // 框选橡皮筋
    if (bandRef.current) {
      const b = bandRef.current
      const x0 = Math.min(b.ax, b.bx)
      const y0 = Math.min(b.ay, b.by)
      const [smx, smy] = toMeters(b.ax, b.ay)
      const [emx, emy] = toMeters(b.bx, b.by)
      const bb: BBox = { west: mxToLng(Math.min(smx, emx)), east: mxToLng(Math.max(smx, emx)), south: myToLat(Math.min(smy, emy)), north: myToLat(Math.max(smy, emy)) }
      const wKm = (bb.east - bb.west) * KM_PER_DEG * Math.cos(((bb.north + bb.south) / 2) * (Math.PI / 180))
      const hKm = (bb.north - bb.south) * KM_PER_DEG
      ctx.strokeStyle = 'rgba(120,220,255,.95)'
      ctx.lineWidth = 2
      ctx.strokeRect(x0, y0, Math.abs(b.bx - b.ax), Math.abs(b.by - b.ay))
      ctx.fillStyle = 'rgba(120,220,255,.12)'
      ctx.fillRect(x0, y0, Math.abs(b.bx - b.ax), Math.abs(b.by - b.ay))
      const label = `宽 ${wKm.toFixed(1)} km × 高 ${hKm.toFixed(1)} km`
      ctx.font = '12px sans-serif'
      const tw = ctx.measureText(label).width + 14
      ctx.fillStyle = 'rgba(10,16,22,.8)'
      ctx.fillRect(x0, y0 - 22, tw, 19)
      ctx.fillStyle = '#bfe9ff'
      ctx.fillText(label, x0 + 7, y0 - 8)
    }

    // 常驻框选范围矩形（框选完成后保持显示，随视图缩放平移）
    const rb = rangeRef.current
    if (rb) {
      const [rx0, ry0] = toScreen(lngToMx(rb.west), latToMy(rb.north))
      const [rx1, ry1] = toScreen(lngToMx(rb.east), latToMy(rb.south))
      const wKm = (rb.east - rb.west) * KM_PER_DEG * Math.cos(((rb.north + rb.south) / 2) * (Math.PI / 180))
      const hKm = (rb.north - rb.south) * KM_PER_DEG
      ctx.fillStyle = 'rgba(120,220,255,.10)'
      ctx.fillRect(rx0, ry0, rx1 - rx0, ry1 - ry0)
      ctx.strokeStyle = 'rgba(120,220,255,.9)'
      ctx.lineWidth = 2
      ctx.setLineDash([6, 4])
      ctx.strokeRect(rx0, ry0, rx1 - rx0, ry1 - ry0)
      ctx.setLineDash([])
      const label = `范围 ${wKm.toFixed(1)} × ${hKm.toFixed(1)} km`
      ctx.font = '12px sans-serif'
      const tw = ctx.measureText(label).width + 14
      ctx.fillStyle = 'rgba(10,16,22,.8)'
      ctx.fillRect(rx0, ry0 - 22, tw, 19)
      ctx.fillStyle = '#bfe9ff'
      ctx.fillText(label, rx0 + 7, ry0 - 8)
    }

    // 底层还在分片构建：继续下一帧
    if (o && baseRef.current && !baseRef.current.done) schedule()
  }, [zoomLabel, schedule])
  drawRef.current = draw

  /* ---------- 视角飞行 ---------- */
  useEffect(() => {
    if (!fit?.bbox) return
    const wrap = wrapRef.current
    if (!wrap) return
    const b = fit.bbox
    const westM = lngToMx(b.west)
    const eastM = lngToMx(b.east)
    const southM = latToMy(b.south)
    const northM = latToMy(b.north)
    const w = Math.max(eastM - westM, 1)
    const h = Math.max(northM - southM, 1)
    const W = wrap.clientWidth || 800
    const H = wrap.clientHeight || 500
    const scale = clamp(Math.min(W / w, H / h) * 0.85, SCALE_MIN, SCALE_MAX)
    viewRef.current = { cx: (westM + eastM) / 2, cy: (southM + northM) / 2, scale }
    schedule()
  }, [fit?.nonce, fit?.bbox, schedule])

  useEffect(() => {
    schedule()
    const wrap = wrapRef.current
    if (!wrap) return
    const ro = new ResizeObserver(() => schedule())
    ro.observe(wrap)
    return () => ro.disconnect()
  }, [schedule])

  // overlay / markers 引用变化即重绘（refs 已在渲染期更新）
  useEffect(() => {
    schedule()
  }, [overlay, schedule])

  /* ---------- 交互 ---------- */
  const onWheel = useCallback((e: WheelEvent) => {
    e.preventDefault()
    const v = viewRef.current
    const wrap = wrapRef.current
    if (!wrap) return
    const rect = wrap.getBoundingClientRect()
    const px = e.clientX - rect.left - wrap.clientWidth / 2
    const py = e.clientY - rect.top - wrap.clientHeight / 2
    const factor = e.deltaY < 0 ? 1.25 : 0.8
    const next = clamp(v.scale * factor, SCALE_MIN, SCALE_MAX)
    v.cx += px / v.scale - px / next
    v.cy -= py / v.scale - py / next
    v.scale = next
    schedule()
  }, [schedule])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    canvas.addEventListener('wheel', onWheel, { passive: false })
    return () => canvas.removeEventListener('wheel', onWheel)
  }, [onWheel])

  const applyCellAt = (e: React.MouseEvent) => {
    const wrap = wrapRef.current
    if (!wrap) return
    const rect = wrap.getBoundingClientRect()
    const k = hitCell(e.clientX - rect.left, e.clientY - rect.top)
    if (k && k !== lastCellRef.current) {
      lastCellRef.current = k
      cbRef.current.onCellClick?.(k)
    }
  }

  const onDown = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const wrap = wrapRef.current
    if (!wrap) return
    const rect = wrap.getBoundingClientRect()
    const px = e.clientX - rect.left
    const py = e.clientY - rect.top
    const m = modeRef.current
    if (m === 'move') {
      const idx = hitMarker(px, py)
      if (idx !== null) {
        markerDragRef.current = { idx, last: null }
        hoverRef.current = hitCell(px, py)
        cbRef.current.onMarkerClick?.(idx)
        schedule()
        const moveMk = (ev: MouseEvent) => {
          const k = hitCell(ev.clientX - rect.left, ev.clientY - rect.top)
          if (k !== hoverRef.current) { hoverRef.current = k; schedule() }
          const d = markerDragRef.current
          if (!k || !d || k === d.last) return
          d.last = k
          const [c, r] = k.split(',').map(Number)
          cbRef.current.onMarkerMove?.(idx, c, r)
        }
        const upMk = () => {
          window.removeEventListener('mousemove', moveMk)
          window.removeEventListener('mouseup', upMk)
          markerDragRef.current = null
          schedule()
        }
        window.addEventListener('mousemove', moveMk)
        window.addEventListener('mouseup', upMk)
        return
      }
      // 空白处按下：不吞掉，继续走下方平移分支
    }
    if (m === 'frame') {
      bandRef.current = { ax: px, ay: py, bx: px, by: py }
    } else if (m === 'pick') {
      const [mx, my] = toMeters(px, py)
      cbRef.current.onPick?.(mxToLng(mx), myToLat(my))
      return
    } else if (m === 'paint' || m === 'dig' || m === 'fill') {
      paintingRef.current = true
      lastCellRef.current = null
      applyCellAt(e)
      return // 涂刷优先，不做平移
    }
    // pan 及兜底：拖动平移
    const v = viewRef.current
    dragRef.current = { x: px, y: py, cx: v.cx, cy: v.cy }
    const move = (ev: MouseEvent) => {
      const mx2 = ev.clientX - rect.left
      const my2 = ev.clientY - rect.top
      if (bandRef.current) {
        bandRef.current = { ...bandRef.current, bx: mx2, by: my2 }
      } else if (dragRef.current) {
        const vv = viewRef.current
        vv.cx = dragRef.current.cx - (mx2 - dragRef.current.x) / vv.scale
        vv.cy = dragRef.current.cy + (my2 - dragRef.current.y) / vv.scale
      }
      schedule()
    }
    const up = () => {
      window.removeEventListener('mousemove', move)
      window.removeEventListener('mouseup', up)
      if (bandRef.current) {
        const b = bandRef.current
        bandRef.current = null
        if (Math.abs(b.bx - b.ax) > 10 && Math.abs(b.by - b.ay) > 10) {
          const [smx, smy] = toMeters(b.ax, b.ay)
          const [emx, emy] = toMeters(b.bx, b.by)
          cbRef.current.onFrame?.({
            west: mxToLng(Math.min(smx, emx)), east: mxToLng(Math.max(smx, emx)),
            south: myToLat(Math.min(smy, emy)), north: myToLat(Math.max(smy, emy)),
          })
        }
      }
      dragRef.current = null
      schedule()
    }
    window.addEventListener('mousemove', move)
    window.addEventListener('mouseup', up)
  }

  /** 涂刷拖动 + 悬停（ref 驱动，无 React 重渲染） */
  const onMouseMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const m = modeRef.current
    if (m === 'paint' || m === 'dig' || m === 'fill' || m === 'move') {
      if (paintingRef.current) applyCellAt(e)
      const wrap = wrapRef.current
      if (wrap) {
        const rect = wrap.getBoundingClientRect()
        const k = hitCell(e.clientX - rect.left, e.clientY - rect.top)
        if (k !== hoverRef.current) {
          hoverRef.current = k
          schedule()
        }
      }
    } else if (hoverRef.current) {
      hoverRef.current = null
      schedule()
    }
  }

  const cursor = mode === 'pan' ? 'grab'
    : mode === 'pick' ? 'copy'
    : mode === 'frame' ? 'crosshair'
    : 'crosshair'

  return (
    <div ref={wrapRef} className="gmv-wrap">
      <canvas ref={canvasRef} onMouseDown={onDown} onMouseMove={onMouseMove} onMouseUp={() => { paintingRef.current = false; lastCellRef.current = null }} onMouseLeave={() => { paintingRef.current = false; hoverRef.current = null; schedule() }} style={{ cursor }} />
      {!canvasOnly && (
        <div className="gmv-layers">
          {(Object.keys(LAYERS) as Array<keyof typeof LAYERS>).map((k) => (
            <button key={k} className={`btn btn-sm${basemap === k ? ' btn-primary' : ''}`} onClick={() => { setBasemap(k); schedule() }}>
              {LAYERS[k].label}
            </button>
          ))}
          <span className="muted small">Z{zoomLabel}</span>
        </div>
      )}
      {!canvasOnly && !tk && <div className="gmv-tkwarn">未取得天地图密钥，底图不可用</div>}
      {!canvasOnly && tk && tileFail && <div className="gmv-tkwarn">底图瓦片加载失败（密钥无效、配额用尽或网络受限），六角网格不受影响</div>}
    </div>
  )
}
