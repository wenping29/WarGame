/**
 * 在线地图框选范围 → 生成六角格网格。
 * 复用主地图同源的天地图 WMTS 瓦片（EPSG:3857 球面墨卡托，密钥经 /api/scenarios 下发），
 * 拖拽框选经纬度范围后按对边距换算行列数，交给 GridManager 创建网格。
 * 坐标约定与主地图一致：六角格几何量（对边距/半径）都以墨卡托米计。
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { loadMeta } from '../room/useUnitFull'
import { toast } from './Toast'

const E = 20037508.342789244 // EPSG:3857 半周长（米）
const SQRT3 = Math.sqrt(3)
const TD_HOSTS = ['t0', 't1', 't2', 't3', 't4', 't5', 't6', 't7']
const LAYERS: Record<string, { label: string; layers: string[] }> = {
  vec: { label: '矢量', layers: ['vec', 'cva'] },
  img: { label: '影像', layers: ['img', 'cia'] },
  ter: { label: '地形', layers: ['ter', 'cta'] },
}
const SCALE_MIN = (256 * Math.pow(2, 2)) / (2 * E) // 约第 2 级
const SCALE_MAX = (256 * Math.pow(2, 17)) / (2 * E) // 约第 17 级
const MAX_CELLS = 200 // 与 GridManager 行列上限一致

export interface BBox {
  west: number
  south: number
  east: number
  north: number
}
export interface PickedGrid {
  rows: number
  cols: number
  flatToFlat: number
  bbox: BBox
  name: string
}

/* ---------- 墨卡托 <-> 经纬度 ---------- */
const mxToLng = (mx: number) => (mx * 180) / E
const myToLat = (my: number) => (2 * Math.atan(Math.exp((my * Math.PI) / E)) - Math.PI / 2) * (180 / Math.PI)
const lngToMx = (lng: number) => (lng * E) / 180
const latToMy = (lat: number) => (E / Math.PI) * Math.log(Math.tan(Math.PI / 4 + ((lat * Math.PI) / 180) / 2))

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))

interface View {
  cx: number
  cy: number
  scale: number // 屏幕像素 / 墨卡托米
}

interface Tile {
  img: HTMLImageElement | null
  ok: boolean
  started: number
}

/** 由选区与对边距换算行列数（odd-r，对齐选区西南角）；bbox 是经纬度，先转墨卡托米再算 */
export function estimateGrid(bbox: BBox, flatToFlat: number): { rows: number; cols: number } {
  const size = flatToFlat / SQRT3
  const wM = lngToMx(bbox.east) - lngToMx(bbox.west)
  const hM = latToMy(bbox.north) - latToMy(bbox.south)
  const cols = Math.ceil(wM / (SQRT3 * size))
  const rows = Math.ceil(hM / (1.5 * size))
  return { rows: clamp(rows, 1, MAX_CELLS), cols: clamp(cols, 1, MAX_CELLS) }
}

export default function BBoxPicker({ onClose, onCreate }: { onClose: () => void; onCreate: (p: PickedGrid) => void }) {
  const wrapRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const viewRef = useRef<View>({ cx: lngToMx(107), cy: latToMy(34), scale: (256 * Math.pow(2, 5)) / (2 * E) })
  const tilesRef = useRef<Map<string, Tile>>(new Map())
  const rafRef = useRef(0)
  const dragRef = useRef<{ x: number; y: number; cx: number; cy: number } | null>(null)
  const bandRef = useRef<{ ax: number; ay: number; bx: number; by: number } | null>(null) // 屏幕坐标橡皮筋

  const [tk, setTk] = useState<string | null>(null)
  const [basemap, setBasemap] = useState<keyof typeof LAYERS>('vec')
  const [selectMode, setSelectMode] = useState(true)
  const [ftf, setFtf] = useState(2500)
  const [name, setName] = useState('')
  const [bbox, setBbox] = useState<BBox | null>(null)
  const [zoomLabel, setZoomLabel] = useState(5)

  const schedule = useCallback(() => {
    if (rafRef.current) return
    rafRef.current = requestAnimationFrame(() => {
      rafRef.current = 0
      drawRef.current?.()
    })
  }, [])
  const drawRef = useRef<(() => void) | null>(null)

  const bmRef = useRef(basemap)
  const tkRef = useRef(tk)
  bmRef.current = basemap
  tkRef.current = tk

  useEffect(() => {
    loadMeta().then((m) => setTk((m.tiles as { tk?: string } | undefined)?.tk ?? null)).catch(() => {})
  }, [])

  // 密钥异步到达：清掉带空 tk 失败的瓦片，换钥匙重载
  useEffect(() => {
    if (!tk) return
    tilesRef.current.clear()
    schedule()
  }, [tk, schedule])

  // 还有未就绪的瓦片时周期性重绘（触发失败瓦片的到点重试）
  useEffect(() => {
    const iv = setInterval(() => {
      if ([...tilesRef.current.values()].some((t) => !t.ok)) schedule()
    }, 3000)
    return () => clearInterval(iv)
  }, [schedule])

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
    // 失败瓦片 8 秒后允许重试（密钥晚到 / 网络抖动）
    if (t && !t.ok && Date.now() - t.started > 8000) {
      tilesRef.current.delete(key)
      t = undefined
    }
    if (!t) {
      t = { img: null, ok: false, started: Date.now() }
      const img = new Image()
      img.onload = () => { t!.img = img; t!.ok = true; schedule() }
      img.onerror = () => {}
      img.src = tileUrl(layer, zi, tx, ty)
      tilesRef.current.set(key, t!)
    }
    return t
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
    const toSx = (mx: number) => (mx - v.cx) * v.scale + W / 2
    const toSy = (my: number) => H / 2 - (my - v.cy) * v.scale

    // 瓦片层
    const zfac = (v.scale * 2 * E) / 256
    const zi = clamp(Math.round(Math.log2(zfac)), 1, 18)
    if (Math.round(Math.log2(zfac)) !== zoomLabel) setZoomLabel(Math.round(Math.log2(zfac)))
    const worldPx = 2 * E * v.scale
    const tilePx = worldPx / Math.pow(2, zi)
    if (tilePx >= 64 && tilePx <= 1024) {
      const n = Math.pow(2, zi)
      const mxL = v.cx - W / 2 / v.scale
      const mxR = v.cx + W / 2 / v.scale
      const myT = v.cy + H / 2 / v.scale
      const myB = v.cy - H / 2 / v.scale
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
            ctx.drawImage(t.img, toSx(m0x), toSy(m0y), tilePx + 1, tilePx + 1)
          }
        }
      }
      ctx.fillStyle = 'rgba(255,255,255,.75)'
      ctx.font = '10px sans-serif'
      ctx.textAlign = 'right'
      ctx.fillText('© 天地图 · 国家基础地理信息中心', W - 6, H - 6)
      ctx.textAlign = 'left'
    }

    // 选区矩形（含拖拽中的橡皮筋）
    const sel = bbox ?? (bandRef.current ? bandToBBox(bandRef.current) : null)
    if (sel) {
      const x0 = toSx(lngToMx(sel.west))
      const y0 = toSy(latToMy(sel.north))
      const x1 = toSx(lngToMx(sel.east))
      const y1 = toSy(latToMy(sel.south))
      const rx = Math.min(x0, x1)
      const ry = Math.min(y0, y1)
      const rw = Math.abs(x1 - x0)
      const rh = Math.abs(y1 - y0)

      // 六角格预览
      const { rows, cols } = estimateGrid(sel, ftf)
      const size = ftf / SQRT3
      ctx.save()
      ctx.beginPath()
      ctx.rect(rx, ry, rw, rh)
      ctx.clip()
      ctx.strokeStyle = 'rgba(120,220,255,.55)'
      ctx.lineWidth = 1
      for (let row = 0; row < rows; row++) {
        for (let col = 0; col < cols; col++) {
          const mx = lngToMx(sel.west) + SQRT3 * size * (col + 0.5 * (row & 1))
          const my = latToMy(sel.south) + 1.5 * size * row
          ctx.beginPath()
          for (let i = 0; i < 6; i++) {
            const a = (Math.PI / 180) * (60 * i - 30)
            const px = toSx(mx + size * Math.cos(a))
            const py = toSy(my + size * Math.sin(a))
            if (i === 0) ctx.moveTo(px, py)
            else ctx.lineTo(px, py)
          }
          ctx.closePath()
          ctx.stroke()
        }
      }
      ctx.restore()

      ctx.strokeStyle = 'rgba(120,220,255,.95)'
      ctx.lineWidth = 2
      ctx.strokeRect(rx, ry, rw, rh)
      ctx.fillStyle = 'rgba(10,16,22,.8)'
      const label = `${rows}×${cols} 格 · 宽 ${(kmWidth(sel)).toFixed(1)} km × 高 ${(kmHeight(sel)).toFixed(1)} km`
      ctx.font = '12px sans-serif'
      const tw = ctx.measureText(label).width + 14
      ctx.fillRect(rx, ry - 22, tw, 19)
      ctx.fillStyle = '#bfe9ff'
      ctx.fillText(label, rx + 7, ry - 8)
    }
  }, [bbox, ftf, zoomLabel])
  drawRef.current = draw // rAF 重绘直调 draw，不依赖重渲染

  const bandToBBox = (b: { ax: number; ay: number; bx: number; by: number }): BBox => {
    const v = viewRef.current
    const W = wrapRef.current?.clientWidth ?? 0
    const H = wrapRef.current?.clientHeight ?? 0
    const toM = (px: number, py: number): [number, number] => [v.cx + (px - W / 2) / v.scale, v.cy - (py - H / 2) / v.scale]
    const [mx0, my0] = toM(b.ax, b.ay)
    const [mx1, my1] = toM(b.bx, b.by)
    return {
      west: mxToLng(Math.min(mx0, mx1)),
      east: mxToLng(Math.max(mx0, mx1)),
      south: myToLat(Math.min(my0, my1)),
      north: myToLat(Math.max(my0, my1)),
    }
  }
  const KM_PER_DEG = (Math.PI * 6378137) / 180 / 1000 // 每经纬度的地面千米
  const kmWidth = (b: BBox) => (b.east - b.west) * KM_PER_DEG * Math.cos(((b.north + b.south) / 2) * (Math.PI / 180))
  const kmHeight = (b: BBox) => (b.north - b.south) * KM_PER_DEG

  useEffect(() => {
    draw()
    const wrap = wrapRef.current
    if (!wrap) return
    const ro = new ResizeObserver(draw)
    ro.observe(wrap)
    return () => ro.disconnect()
  }, [draw])

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
    // 光标下的地理点保持不动
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

  const onDown = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const wrap = wrapRef.current
    if (!wrap) return
    const rect = wrap.getBoundingClientRect()
    const px = e.clientX - rect.left
    const py = e.clientY - rect.top
    if (selectMode) {
      bandRef.current = { ax: px, ay: py, bx: px, by: py }
    } else {
      const v = viewRef.current
      dragRef.current = { x: px, y: py, cx: v.cx, cy: v.cy }
    }
    const move = (ev: MouseEvent) => {
      const mx = ev.clientX - rect.left
      const my = ev.clientY - rect.top
      if (bandRef.current) {
        bandRef.current = { ...bandRef.current, bx: mx, by: my }
      } else if (dragRef.current) {
        const v = viewRef.current
        v.cx = dragRef.current.cx - (mx - dragRef.current.x) / v.scale
        v.cy = dragRef.current.cy + (my - dragRef.current.y) / v.scale
      }
      schedule()
    }
    const up = () => {
      window.removeEventListener('mousemove', move)
      window.removeEventListener('mouseup', up)
      if (bandRef.current) {
        const b = bandRef.current
        bandRef.current = null
        // 太小视为误触
        if (Math.abs(b.bx - b.ax) > 10 && Math.abs(b.by - b.ay) > 10) setBbox(bandToBBox(b))
      }
      dragRef.current = null
      schedule()
    }
    window.addEventListener('mousemove', move)
    window.addEventListener('mouseup', up)
  }

  const { rows, cols } = bbox ? estimateGrid(bbox, ftf) : { rows: 0, cols: 0 }
  const overLimit = !!bbox && (Math.ceil((lngToMx(bbox.east) - lngToMx(bbox.west)) / ftf) > MAX_CELLS || Math.ceil((latToMy(bbox.north) - latToMy(bbox.south)) / (1.5 * (ftf / SQRT3))) > MAX_CELLS)

  const create = () => {
    if (!bbox) return
    if (overLimit) return toast('范围过大：行列数超过 200，请缩小框选或增大对边距', 'err')
    onCreate({ rows, cols, flatToFlat: ftf, bbox, name: name.trim() })
  }

  return (
    <div className="bbox-mask" onClick={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="bbox-panel">
        <div className="bbox-toolbar">
          <b>在线地图框选</b>
          <span className="bbox-sep" />
          {(Object.keys(LAYERS) as Array<keyof typeof LAYERS>).map((k) => (
            <button key={k} className={`btn btn-sm${basemap === k ? ' btn-primary' : ''}`} onClick={() => { setBasemap(k); tilesRef.current.clear(); schedule() }}>
              {LAYERS[k].label}
            </button>
          ))}
          <span className="bbox-sep" />
          <button className={`btn btn-sm${selectMode ? ' btn-primary' : ''}`} onClick={() => setSelectMode(!selectMode)}>
            {selectMode ? '▭ 框选模式' : '✥ 平移模式'}
          </button>
          <button className="btn btn-sm" onClick={() => setBbox(null)} disabled={!bbox}>清除选区</button>
          <span className="muted small">Z{zoomLabel}</span>
          <span className="flex1" />
          <button className="btn btn-sm" onClick={onClose}>关闭</button>
        </div>
        <div ref={wrapRef} className="bbox-map">
          <canvas ref={canvasRef} onMouseDown={onDown} style={{ cursor: selectMode ? 'crosshair' : 'grab' }} />
          <div className="bbox-hint">
            {selectMode ? '在地图上按住鼠标拖出矩形，框选六角格覆盖范围' : '拖动地图；滚轮缩放；切回框选模式选择范围'}
            {!tk && <span className="bbox-tkwarn">（未取得天地图密钥，底图不可用；可先在主地图加载一次）</span>}
          </div>
        </div>
        <div className="bbox-footer">
          <label>网格名称
            <input value={name} placeholder="留空自动编号" onChange={(e) => setName(e.target.value)} />
          </label>
          <label>对边距（墨卡托米）
            <input type="number" min={10} value={ftf} onChange={(e) => setFtf(clamp(Number(e.target.value) || 10, 10, 1_000_000))} />
          </label>
          <div className="bbox-stat">
            {bbox ? (
              <>
                <span>选区 {bbox.west.toFixed(3)}°E ~ {bbox.east.toFixed(3)}°E · {bbox.south.toFixed(3)}°N ~ {bbox.north.toFixed(3)}°N</span>
                <b className={overLimit ? 'bbox-warn' : ''}>{rows} 行 × {cols} 列 = {rows * cols} 格</b>
                <span className="muted small">实地约 {kmWidth(bbox).toFixed(1)} × {kmHeight(bbox).toFixed(1)} km（按中心纬度换算）</span>
              </>
            ) : (
              <span className="muted">尚未框选：拖动鼠标拉出范围后在此确认行列数</span>
            )}
          </div>
          <button className="btn btn-primary" onClick={create} disabled={!bbox || overLimit}>生成网格</button>
        </div>
      </div>
    </div>
  )
}
