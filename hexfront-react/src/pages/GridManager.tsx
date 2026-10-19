/**
 * 六角格管理系统：左侧网格列表（删除/修改/点击在地图上显示），
 * 中间在线地图显示六角格网，右侧创建/编辑表单（行列、对边距、区域坐标、名称、
 * 框选地图范围弹窗、创建按钮）。几何与主地图一致：pointy-top + odd-r，对边距 = √3 · 半径，
 * 几何量以墨卡托米计（与天地图底图同坐标系）。数据持久化 localStorage，支持导入导出 JSON。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from '../components/Toast'
import GeoMapView, { type BBox, type HexOverlay, type MapMode, TERRAINS, lngToMx, latToMy, mxToLng, myToLat } from '../components/GeoMapView'

const SQRT3 = Math.sqrt(3)
const LS_KEY = 'hexfront.gridmgr.v1'
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
const key = (col: number, row: number) => `${col},${row}`
const parseKey = (k: string): [number, number] => {
  const [a, b] = k.split(',')
  return [Number(a), Number(b)]
}
const KM_PER_DEG = (Math.PI * 6378137) / 180 / 1000
const TERRAIN_NAME = Object.fromEntries(TERRAINS.map(([k, n]) => [k, n])) as Record<string, string>

interface GridDoc {
  id: string
  name: string
  rows: number
  cols: number
  flatToFlat: number // 对边距
  holes: string[] // 已删除格键 "col,row"
  terrains: Record<string, string>
  /** 溯源：网格覆盖的经纬度范围 */
  bbox?: BBox
  createdAt: number
}

/** 行列/锚点/对边距 → 派生区域范围（经纬度） */
function deriveBBox(west: number, south: number, rows: number, cols: number, flatToFlat: number): BBox {
  const size = flatToFlat / SQRT3
  return {
    west,
    south,
    east: mxToLng(lngToMx(west) + SQRT3 * size * (cols + 0.5)),
    north: myToLat(latToMy(south) + size * (1.5 * rows + 0.5)),
  }
}
/** 由东/北边界反推行列 */
function deriveCount(west: number, south: number, edge: number, flatToFlat: number, isEast: boolean): number {
  const size = flatToFlat / SQRT3
  if (isEast) return clamp(Math.round((lngToMx(edge) - lngToMx(west)) / (SQRT3 * size) - 0.5), 1, 200)
  return clamp(Math.round((latToMy(edge) - latToMy(south)) / (1.5 * size) - 1 / 3), 1, 200)
}

function loadStore(): GridDoc[] {
  try {
    const raw = localStorage.getItem(LS_KEY)
    if (raw) {
      const v = JSON.parse(raw)
      if (Array.isArray(v) && v.length) return v
    }
  } catch {
    /* 忽略坏数据 */
  }
  return [makeGrid('默认棋盘', 10, 14, 2500, { west: 107, south: 33, east: 107.7, north: 33.6 })]
}

function saveStore(list: GridDoc[]) {
  try {
    localStorage.setItem(LS_KEY, JSON.stringify(list))
  } catch {
    toast('本地保存失败（存储空间不足？）', 'err')
  }
}

function makeGrid(name: string, rows: number, cols: number, flatToFlat: number, bbox?: BBox): GridDoc {
  return {
    id: crypto.randomUUID(),
    name,
    rows,
    cols,
    flatToFlat,
    holes: [],
    terrains: {},
    ...(bbox ? { bbox } : {}),
    createdAt: Date.now(),
  }
}

/** 行列调整后裁掉越界的洞与涂装 */
function pruneDoc(d: GridDoc): GridDoc {
  const inRange = (k: string) => {
    const [c, r] = parseKey(k)
    return c >= 0 && c < d.cols && r >= 0 && r < d.rows
  }
  return { ...d, holes: d.holes.filter(inRange), terrains: Object.fromEntries(Object.entries(d.terrains).filter(([k]) => inRange(k))) }
}

interface Draft {
  name: string
  rows: string
  cols: string
  flatToFlat: string
  west: string
  south: string
  east: string
  north: string
}

const DEFAULT_DRAFT: Draft = { name: '', rows: '10', cols: '14', flatToFlat: '2500', west: '107', south: '33', east: '', north: '' }
const DEFAULT_BBOX: BBox = { west: 107, south: 33, east: 107.7, north: 33.6 }

export default function GridManager() {
  const [grids, setGrids] = useState<GridDoc[]>(loadStore)
  const [editingId, setEditingId] = useState<string | null>(() => loadStore()[0]?.id ?? null)
  const [draft, setDraft] = useState<Draft>(DEFAULT_DRAFT)
  const [mode, setMode] = useState<MapMode>('pan')
  const [brush, setBrush] = useState('plain')
  const [confirmDel, setConfirmDel] = useState<string | null>(null)
  const [fit, setFit] = useState<{ bbox: BBox; nonce: number } | null>(null)
  /** 框选弹窗：打开时飞到当前草稿范围，框选完成后回填表单并关闭 */
  const [pickOpen, setPickOpen] = useState(false)
  const [pickFit, setPickFit] = useState<{ bbox: BBox; nonce: number } | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const nameRef = useRef<HTMLInputElement>(null)

  const active = useMemo(() => grids.find((g) => g.id === editingId) ?? null, [grids, editingId])

  useEffect(() => {
    // 防抖持久化：涂刷/拖动时避免每格全量序列化写 localStorage
    const t = setTimeout(() => saveStore(grids), 250)
    return () => clearTimeout(t)
  }, [grids])

  const patch = useCallback(
    (p: Partial<GridDoc>) => {
      if (!active) return
      setGrids((list) => list.map((g) => (g.id === active.id ? pruneDoc({ ...g, ...p }) : g)))
    },
    [active],
  )

  /* ---------- 草稿几何 ---------- */
  const geom = useMemo(() => {
    const rows = Math.round(Number(draft.rows))
    const cols = Math.round(Number(draft.cols))
    const ftf = Number(draft.flatToFlat)
    const west = Number(draft.west)
    const south = Number(draft.south)
    if (![rows, cols, ftf, west, south].every(Number.isFinite) || ftf < 10) return null
    return { rows: clamp(rows, 1, 200), cols: clamp(cols, 1, 200), ftf: clamp(ftf, 10, 1_000_000), west, south }
  }, [draft])

  const overlay: HexOverlay | null = useMemo(() => {
    if (!geom) return null
    return {
      west: geom.west,
      south: geom.south,
      rows: geom.rows,
      cols: geom.cols,
      size: geom.ftf / SQRT3,
      holes: active?.holes,
      terrains: active?.terrains,
      preview: !active || active.rows !== geom.rows || active.cols !== geom.cols || active.flatToFlat !== geom.ftf,
    }
  }, [geom, active])

  const fitGrid = (b: BBox) => setFit({ bbox: b, nonce: Date.now() })

  /* ---------- 列表操作 ---------- */
  const openGrid = (g: GridDoc) => {
    setEditingId(g.id)
    const b = g.bbox ?? deriveBBox(107, 33, g.rows, g.cols, g.flatToFlat)
    setDraft({
      name: g.name,
      rows: String(g.rows),
      cols: String(g.cols),
      flatToFlat: String(g.flatToFlat),
      west: b.west.toFixed(4),
      south: b.south.toFixed(4),
      east: b.east.toFixed(4),
      north: b.north.toFixed(4),
    })
    fitGrid(b)
  }

  const deleteGrid = (id: string) => {
    setGrids((list) => {
      const next = list.filter((g) => g.id !== id)
      const final = next.length ? next : [makeGrid('默认棋盘', 10, 14, 2500)]
      if (id === editingId) {
        setEditingId(null)
        setDraft(DEFAULT_DRAFT)
      }
      return final
    })
    setConfirmDel(null)
    toast('已删除网格')
  }

  /* ---------- 草稿字段更新：锚点/行列/对边距 → 重新推导东/北边界 ---------- */
  const updGeom = (p: Partial<{ rows: string; cols: string; flatToFlat: string; west: string; south: string }>) =>
    setDraft((prev) => {
      const rows = Math.round(Number(p.rows ?? prev.rows))
      const cols = Math.round(Number(p.cols ?? prev.cols))
      const ftf = Number(p.flatToFlat ?? prev.flatToFlat)
      const west = Number(p.west ?? prev.west)
      const south = Number(p.south ?? prev.south)
      if (![rows, cols, ftf, west, south].every(Number.isFinite) || ftf < 10) return { ...prev, ...p }
      const R = clamp(rows, 1, 200)
      const C = clamp(cols, 1, 200)
      const F = clamp(ftf, 10, 1_000_000)
      const bb = deriveBBox(west, south, R, C, F)
      return {
        ...prev,
        rows: String(R),
        cols: String(C),
        flatToFlat: String(F),
        west: west.toFixed(4),
        south: south.toFixed(4),
        east: bb.east.toFixed(4),
        north: bb.north.toFixed(4),
      }
    })

  /** 东/北边界输入：反推行列 */
  const updEdge = (isEast: boolean, v: string) => {
    const num = Number(v)
    setDraft((prev) => {
      if (!Number.isFinite(num) || !geom) return { ...prev, [isEast ? 'east' : 'north']: v }
      if (isEast) {
        const C = deriveCount(geom.west, geom.south, num, geom.ftf, true)
        const bb = deriveBBox(geom.west, geom.south, geom.rows, C, geom.ftf)
        return { ...prev, cols: String(C), east: bb.east.toFixed(4) }
      }
      const R = deriveCount(geom.west, geom.south, num, geom.ftf, false)
      const bb = deriveBBox(geom.west, geom.south, R, geom.cols, geom.ftf)
      return { ...prev, rows: String(R), north: bb.north.toFixed(4) }
    })
  }

  /* ---------- 地图交互回调 ---------- */
  const openPick = () => {
    const b = geom ? deriveBBox(geom.west, geom.south, geom.rows, geom.cols, geom.ftf) : DEFAULT_BBOX
    setPickFit({ bbox: b, nonce: Date.now() })
    setPickOpen(true)
  }

  const closePick = useCallback(() => setPickOpen(false), [])

  // 弹窗内 ESC 关闭
  useEffect(() => {
    if (!pickOpen) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') closePick()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [pickOpen, closePick])

  const onPick = (lng: number, lat: number) => {
    updGeom({ west: lng, south: lat })
    setMode('pan')
    toast(`已拾取锚点 ${lng.toFixed(4)}°E, ${lat.toFixed(4)}°N`)
  }

  const onFrame = (b: BBox) => {
    if (!geom) return toast('请先填写有效的对边距', 'err')
    // 范围以用户框选的矩形为准（不整格外扩）；对边距保持不变；行列号由范围 + 对边距重新计算（向上取整覆盖整个范围）
    const size = geom.ftf / SQRT3
    const C = clamp(Math.ceil((lngToMx(b.east) - lngToMx(b.west)) / (SQRT3 * size)), 1, 200)
    const R = clamp(Math.ceil((latToMy(b.north) - latToMy(b.south)) / (1.5 * size)), 1, 200)
    setDraft((prev) => ({
      ...prev,
      west: b.west.toFixed(4),
      south: b.south.toFixed(4),
      east: b.east.toFixed(4),
      north: b.north.toFixed(4),
      rows: String(R),
      cols: String(C),
    }))
    toast(`已框选范围：${R} 行 × ${C} 列`)
  }

  /** 弹窗内框选：回填表单并关闭弹窗 */
  const onFrameInModal = (b: BBox) => {
    onFrame(b)
    setPickOpen(false)
  }

  const onCellClick = (k: string | null) => {
    if (!k) return
    if (!active) return toast('先创建/选中一个网格，才能涂刷编辑', 'err')
    const holeSet = new Set(active.holes)
    if (mode === 'paint') {
      if (holeSet.has(k)) return toast('该格已挖除，请先补格', 'err')
      patch({ terrains: { ...active.terrains, [k]: brush } })
    } else if (mode === 'dig') {
      if (holeSet.has(k)) return
      const terrains = { ...active.terrains }
      delete terrains[k]
      patch({ holes: [...active.holes, k], terrains })
    } else if (mode === 'fill') {
      if (!holeSet.has(k)) return
      patch({ holes: active.holes.filter((h) => h !== k) })
    }
  }

  /* ---------- 创建 / 保存 ---------- */
  const saveFromDraft = () => {
    if (!geom) return toast('参数无效：请检查行列、对边距与坐标', 'err')
    const bb = deriveBBox(geom.west, geom.south, geom.rows, geom.cols, geom.ftf)
    const name = draft.name.trim() || (editingId ? active?.name : '') || `棋盘 ${grids.length + 1}`
    if (editingId && active) {
      setGrids((list) => list.map((g) => (g.id === editingId ? pruneDoc({ ...g, name, rows: geom.rows, cols: geom.cols, flatToFlat: geom.ftf, bbox: bb }) : g)))
      toast(`已保存「${name}」（${geom.rows}×${geom.cols}）`)
    } else {
      const d = makeGrid(name, geom.rows, geom.cols, geom.ftf, bb)
      setGrids((list) => [...list, d])
      setEditingId(d.id)
      toast(`已创建「${name}」（${geom.rows}×${geom.cols}）`)
    }
    fitGrid(bb)
  }

  /* ---------- 导入导出 ---------- */
  const exportDoc = () => {
    if (!active) return toast('先选择一个网格', 'err')
    const blob = new Blob([JSON.stringify(active, null, 2)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${active.name}.hexgrid.json`
    a.click()
    URL.revokeObjectURL(a.href)
    toast('已导出 JSON')
  }

  const importDoc = (file: File) => {
    file.text().then((text) => {
      try {
        const j = JSON.parse(text)
        if (typeof j.rows !== 'number' || typeof j.cols !== 'number' || typeof j.flatToFlat !== 'number') throw new Error('字段缺失')
        const d = pruneDoc({
          ...makeGrid(String(j.name || '导入网格'), j.rows, j.cols, j.flatToFlat, j.bbox),
          holes: Array.isArray(j.holes) ? j.holes.map(String) : [],
          terrains: j.terrains && typeof j.terrains === 'object' ? j.terrains : {},
        })
        setGrids((list) => [...list, d])
        openGrid(d)
        toast(`已导入「${d.name}」`)
      } catch {
        toast('导入失败：不是有效的网格 JSON', 'err')
      }
    })
  }

  const TOOLS: Array<[MapMode, string]> = [
    ['pan', '浏览'],
    ['paint', '涂刷'],
    ['dig', '挖格'],
    ['fill', '补格'],
    ['frame', '框选范围'],
  ]
  const side = geom ? geom.ftf / SQRT3 : 0
  const area = geom ? ((3 * SQRT3) / 2) * side * side : 0
  const bb = geom ? deriveBBox(geom.west, geom.south, geom.rows, geom.cols, geom.ftf) : null
  const wKm = bb ? (bb.east - bb.west) * KM_PER_DEG * Math.cos(((bb.north + bb.south) / 2) * (Math.PI / 180)) : 0
  const hKm = bb ? (bb.north - bb.south) * KM_PER_DEG : 0

  return (
    <div className="page gridmgr">
      {/* 左栏：网格列表 + 导入导出 + 统计 */}
      <div className="gm-col">
        <div className="panel gm-list-panel">
          <div className="panel-header">
            <h3>网格列表（{grids.length}）</h3>
          </div>
          <div className="gm-list">
            {grids.map((g) => (
              <div key={g.id} className={`gm-item${g.id === editingId ? ' on' : ''}`} onClick={() => openGrid(g)}>
                <div className="gm-item-main">
                  <b>{g.name}</b>
                  <span className="muted small">{g.rows} 行 × {g.cols} 列 · 对边距 {g.flatToFlat}</span>
                </div>
                <div className="gm-item-ops">
                  <button className="btn btn-sm" title="修改" onClick={(e) => { e.stopPropagation(); openGrid(g); nameRef.current?.focus() }}>改</button>
                  {confirmDel === g.id ? (
                    <button className="btn btn-sm btn-red" onClick={(e) => { e.stopPropagation(); deleteGrid(g.id) }}>确认</button>
                  ) : (
                    <button className="btn btn-sm" title="删除" onClick={(e) => { e.stopPropagation(); setConfirmDel(g.id); setTimeout(() => setConfirmDel((c) => (c === g.id ? null : c)), 3000) }}>删</button>
                  )}
                </div>
              </div>
            ))}
          </div>
        </div>
        <div className="panel">
          <div className="panel-header">
            <h3>导入 / 导出</h3>
          </div>
          <div className="gm-row2">
            <button className="btn" onClick={exportDoc}>导出当前</button>
            <button className="btn" onClick={() => fileRef.current?.click()}>导入 JSON</button>
          </div>
          <input ref={fileRef} type="file" accept=".json" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) importDoc(f); e.target.value = '' }} />
        </div>
        {active && (
          <div className="panel">
            <div className="panel-header">
              <h3>统计</h3>
            </div>
            <div className="kv"><span>网格规格</span><span>{active.rows} 行 × {active.cols} 列</span></div>
            <div className="kv"><span>有效格数</span><span>{active.rows * active.cols - active.holes.length}</span></div>
            <div className="kv"><span>已挖格数</span><span>{active.holes.length}</span></div>
            <div className="kv"><span>涂装格数</span><span>{Object.keys(active.terrains).length}</span></div>
          </div>
        )}
      </div>

      {/* 中间：在线地图 */}
      <div className="gm-canvas-col">
        <div className="gm-toolbar">
          {TOOLS.map(([t, label]) => (
            <button key={t} className={`btn btn-sm${mode === t ? ' btn-primary' : ''}`} onClick={() => setMode(t)}>
              {label}
            </button>
          ))}
          {mode === 'paint' && (
            <span className="gm-palette">
              {TERRAINS.map(([k, n, c]) => (
                <button key={k} className={`gm-swatch${brush === k ? ' on' : ''}`} style={{ background: c }} title={n} onClick={() => setBrush(k)} />
              ))}
              <span className="muted small">{TERRAIN_NAME[brush]}</span>
            </span>
          )}
          <span className="muted small gm-modehint">
            {mode === 'frame' && '在地图上拖拽矩形框选范围'}
            {mode === 'pan' && '拖动地图 · 滚轮缩放'}
            {(mode === 'paint' || mode === 'dig' || mode === 'fill') && '点击/拖动格子进行编辑'}
          </span>
        </div>
        <div className="gm-canvas-wrap">
          <GeoMapView mode={mode} overlay={overlay} onFrame={onFrame} onPick={onPick} onCellClick={onCellClick} fit={fit} />
        </div>
      </div>

      {/* 右栏：创建 / 编辑表单 */}
      <div className="gm-col">
        <div className="panel">
          <div className="panel-header">
            <h3>{editingId ? '修改网格' : '新建网格'}</h3>
          </div>
          <label>六角网格名称
            <input ref={nameRef} value={draft.name} placeholder="留空自动编号" onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          </label>
          <div className="gm-row2">
            <label>行数
              <input type="number" min={1} max={200} value={draft.rows} onChange={(e) => updGeom({ rows: e.target.value })} />
            </label>
            <label>列数
              <input type="number" min={1} max={200} value={draft.cols} onChange={(e) => updGeom({ cols: e.target.value })} />
            </label>
          </div>
          <label>对边距（墨卡托米）
            <input type="number" min={10} value={draft.flatToFlat} onChange={(e) => updGeom({ flatToFlat: e.target.value })} />
          </label>
          <div className="gm-coordhead">
            <span>区域坐标（度）</span>
            <button className="btn btn-sm" title="打开地图弹窗，框选范围自动生成网格" onClick={openPick}>
              🗺️ 框选地图范围
            </button>
          </div>
          <div className="gm-row2">
            <label>西（锚点）
              <input type="number" step="0.0001" value={draft.west} onChange={(e) => updGeom({ west: e.target.value })} />
            </label>
            <label>南（锚点）
              <input type="number" step="0.0001" value={draft.south} onChange={(e) => updGeom({ south: e.target.value })} />
            </label>
          </div>
          <div className="gm-row2">
            <label>东（推算列数）
              <input type="number" step="0.0001" value={draft.east} onChange={(e) => updEdge(true, e.target.value)} />
            </label>
            <label>北（推算行数）
              <input type="number" step="0.0001" value={draft.north} onChange={(e) => updEdge(false, e.target.value)} />
            </label>
          </div>
          <div className="muted small gm-geom">
            {geom ? (
              <>
                外接圆半径 {side.toFixed(1)} m · 单格面积 {area.toFixed(0)} m²
                <br />覆盖约 {wKm.toFixed(1)} × {hKm.toFixed(1)} km · 共 {geom.rows * geom.cols} 格
              </>
            ) : (
              '填写有效的行列与对边距后，地图上实时预览网格范围'
            )}
          </div>
          <button className="btn btn-primary" onClick={saveFromDraft}>{editingId ? '保存修改' : '创建网格'}</button>
          {editingId && (
            <button className="btn" onClick={() => { setEditingId(null); setDraft(DEFAULT_DRAFT); toast('已切换为新建模式') }}>新建（清空表单）</button>
          )}
        </div>
      </div>

      {/* 框选地图范围弹窗 */}
      {pickOpen && (
        <div className="gm-modal-mask" onMouseDown={(e) => { if (e.target === e.currentTarget) closePick() }}>
          <div className="gm-modal">
            <div className="gm-modal-head">
              <h3>框选地图范围</h3>
              <span className="gm-modal-hint">在地图上拖拽矩形框，松开后自动按对边距推算行列并填入区域坐标（Esc 关闭）</span>
              <button className="btn btn-sm" onClick={closePick}>✕</button>
            </div>
            <div className="gm-modal-body">
              <GeoMapView mode="frame" overlay={overlay} onFrame={onFrameInModal} fit={pickFit} />
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
