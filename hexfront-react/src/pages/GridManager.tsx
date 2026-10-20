/**
 * 六角格管理系统：左侧网格列表（删除/修改/点击在地图上显示），
 * 中间纯 canvas 视图显示六角格网（经纬网格线为底，不加载在线底图，避免卡顿），
 * 右侧创建/编辑表单（行列、对边距、区域坐标、名称、
 * 框选地图范围、创建按钮）。框选与主地图工具一致，完成后矩形常驻显示在地图上。几何与主地图一致：pointy-top + odd-r，对边距 = √3 · 半径，
 * 几何量以墨卡托米计。数据持久化 localStorage，支持导入导出 JSON。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from '../components/Toast'
import GeoMapView, { type BBox, type HexOverlay, type MapMode, TERRAINS, lngToMx, latToMy, mxToLng, myToLat } from '../components/GeoMapView'
import { api, netError, type HexGridMeta } from '../services/api'
import { gisFromPackage, type GisData, type PackageJson } from '../gis/hexgen'

const SQRT3 = Math.sqrt(3)
const LS_KEY = 'hexfront.gridmgr.v1'
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
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
  /** 来源服务器网格包 id（从服务器下载到本地后标记，用于合并列表判重） */
  srvId?: string
  /** GIS 生成数据包名（/api/gis/jobs 产物，存于 data/gis/） */
  gisPkg?: string
  /** GIS 生成的全套格数据（保存修改后自动计算） */
  gis?: GisData
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

export default function GridManager() {
  const [grids, setGrids] = useState<GridDoc[]>(loadStore)
  const [editingId, setEditingId] = useState<string | null>(() => loadStore()[0]?.id ?? null)
  const [draft, setDraft] = useState<Draft>(DEFAULT_DRAFT)
  const [mode, setMode] = useState<MapMode>('pan')
  const [brush, setBrush] = useState('plain')
  const [confirmDel, setConfirmDel] = useState<string | null>(null)
  const [fit, setFit] = useState<{ bbox: BBox; nonce: number } | null>(null)
  /** 常驻显示在地图上的框选范围矩形（与表单四至对应，手动改字段时清除） */
  const [rangeBox, setRangeBox] = useState<BBox | null>(null)
  /** 框选弹窗：打开状态 + 弹窗内已框选的范围 + 打开时的初始视角 */
  const [frameOpen, setFrameOpen] = useState(false)
  const [picked, setPicked] = useState<BBox | null>(null)
  const [frameFit, setFrameFit] = useState<{ bbox: BBox; nonce: number } | null>(null)
  /** GIS 分析进行中的阶段提示（null = 空闲） */
  const [gisBusy, setGisBusy] = useState<string | null>(null)
  /* ---------- 服务器六角格网络包（剧本管理页生成的 zip 包） ---------- */
  const [srvList, setSrvList] = useState<HexGridMeta[]>([])
  const [srvView, setSrvView] = useState<HexOverlay | null>(null)
  const [srvConfirmDel, setSrvConfirmDel] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const nameRef = useRef<HTMLInputElement>(null)

  const refreshSrv = useCallback(() => {
    api.hexgrids().then(r => setSrvList(r.grids || [])).catch(() => {})
  }, [])
  useEffect(refreshSrv, [refreshSrv])

  // 查看：拉全文 → 以覆盖层画在地图上并定位；任何本地编辑动作会退出查看态
  const viewSrv = async (m: HexGridMeta) => {
    try {
      const r = await api.hexgridGet(m.id)
      setSrvView({
        west: r.grid.west, south: r.grid.south, rows: r.grid.rows, cols: r.grid.cols,
        size: r.grid.flat_to_flat / SQRT3, holes: r.grid.holes, terrains: r.grid.terrains, preview: false,
      })
      const [w, s, e, n] = r.bbox
      fitGrid({ west: w, south: s, east: e, north: n })
    } catch (err) {
      toast('读取网格包失败：' + netError(err), 'err')
    }
  }

  // 载入到本地编辑器：转成本地网格文档（保留挖格与涂装），可继续涂刷/修改
  const loadSrvToLocal = async (m: HexGridMeta) => {
    try {
      const r = await api.hexgridGet(m.id)
      const [w, s] = r.bbox
      const d = pruneDoc({
        ...makeGrid(r.grid.name || m.id, r.grid.rows, r.grid.cols, r.grid.flat_to_flat,
          { west: w, south: s, east: r.bbox[2], north: r.bbox[3] }),
        holes: r.grid.holes || [],
        terrains: r.grid.terrains || {},
      })
      setGrids(list => [...list, d])
      setSrvView(null)
      openGrid(d)
      toast(`已载入「${d.name}」到本地编辑器`, 'ok')
    } catch (err) {
      toast('载入失败：' + netError(err), 'err')
    }
  }

  const deleteSrv = async (m: HexGridMeta) => {
    try {
      await api.hexgridDelete(m.id)
      toast('已删除网格包 ' + m.id)
      if (srvView) setSrvView(null)
      refreshSrv()
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err)
      toast(/409|引用/.test(msg) ? `删除被拒：${m.id} 正被剧本引用，先在剧本管理页解除绑定` : '删除失败：' + msg, 'err')
    } finally {
      setSrvConfirmDel(null)
    }
  }

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
    setRangeBox(b)
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
  const updGeom = (p: Partial<{ rows: string; cols: string; flatToFlat: string; west: string; south: string }>) => {
    setRangeBox(null) // 手动修改字段后，原框选矩形不再对应表单
    setSrvView(null) // 退出服务器包查看态
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
  }

  /** 东/北边界输入：反推行列 */
  const updEdge = (isEast: boolean, v: string) => {
    const num = Number(v)
    setRangeBox(null) // 手动修改边界后，原框选矩形不再对应表单
    setSrvView(null) // 退出服务器包查看态
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
  const onPick = (lng: number, lat: number) => {
    updGeom({ west: String(lng), south: String(lat) })
    setMode('pan')
    toast(`已拾取锚点 ${lng.toFixed(4)}°E, ${lat.toFixed(4)}°N`)
  }

  const onFrame = (b: BBox) => {
    if (!geom) return toast('请先填写有效的对边距', 'err')
    setSrvView(null) // 退出服务器包查看态
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
    setRangeBox(b) // 框选后矩形常驻显示在地图上
    toast(`已框选范围：${R} 行 × ${C} 列`)
  }

  /* ---------- 框选弹窗 ---------- */
  const openFrameModal = () => {
    // 初始视角：当前表单对应范围（无效时给默认区域）
    const b = geom
      ? deriveBBox(geom.west, geom.south, geom.rows, geom.cols, geom.ftf)
      : { west: 106.5, south: 32.5, east: 107.5, north: 33.5 }
    setFrameFit({ bbox: b, nonce: Date.now() })
    setPicked(null)
    setFrameOpen(true)
  }

  /** 弹窗内拖出的矩形 → 记录，确认时走与主地图一致的 onFrame 逻辑 */
  const applyPicked = () => {
    if (!picked) return
    onFrame(picked)
    setFrameOpen(false)
  }

  const onCellClick = (k: string | null) => {
    if (!k) return
    if (srvView) setSrvView(null) // 退出服务器包查看态，落到本地网格编辑
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
      const id = editingId
      setGrids((list) => list.map((g) => (g.id === id ? pruneDoc({ ...g, name, rows: geom.rows, cols: geom.cols, flatToFlat: geom.ftf, bbox: bb }) : g)))
      toast(`已保存「${name}」（${geom.rows}×${geom.cols}）`)
      void runGis(id, name, geom.rows, geom.cols, geom.ftf, bb, active.holes, active.terrains)
    } else {
      const d = makeGrid(name, geom.rows, geom.cols, geom.ftf, bb)
      setGrids((list) => [...list, d])
      setEditingId(d.id)
      toast(`已创建「${name}」（${geom.rows}×${geom.cols}）`)
      void runGis(d.id, name, geom.rows, geom.cols, geom.ftf, bb, [], {})
    }
    setRangeBox(bb)
    fitGrid(bb)
  }

  /* ---------- GIS 分析：保存修改后调用服务端工具链生成全套格数据（道路/铁路/地形等） ---------- */
  const runGis = useCallback(
    async (id: string, gridName: string, rows: number, cols: number, ftf: number, bb: BBox, holes: string[], manual: Record<string, string>) => {
      if (gisBusy) return
      try {
        setGisBusy('提交生成作业…')
        const job = await api.gisStartJob({
          bbox: [bb.west, bb.south, bb.east, bb.north],
          cell_km: ftf / 1000,
          name: 'gridmgr-' + (gridName.trim() || '棋盘'),
          dem: 'auto',
          landuse: 'auto',
          roads: true,
          water: true,
          network: true,
          features: true,
        })
        let cur = job
        while (cur.status === 'queued' || cur.status === 'running') {
          await new Promise((r) => setTimeout(r, 1500))
          cur = await api.gisJob(job.id)
          setGisBusy(cur.log.length ? cur.log[cur.log.length - 1] : '生成中…')
        }
        if (cur.status !== 'done' || !cur.name) throw new Error(cur.error || '生成失败（见作业日志）')
        setGisBusy('加载数据包并映射六角格…')
        const pkg = (await api.gisPackage(cur.name)) as PackageJson
        const gis = gisFromPackage(pkg, { west: bb.west, south: bb.south, rows, cols, size: ftf / SQRT3 }, holes, manual)
        setGrids((list) => list.map((g) => (g.id === id ? { ...g, gis, gisPkg: cur.name } : g)))
        toast(`GIS 分析完成：道路 ${Object.keys(gis.roads).length} 格 · 铁路 ${gis.rails.length} 格 · 河流 ${gis.rivers.length} 格 · 自动地形 ${Object.keys(gis.terrains).length} 格`)
      } catch (e) {
        toast(`GIS 分析失败：${e instanceof Error ? e.message : netError(e)}`, 'err')
      } finally {
        setGisBusy(null)
      }
    },
    [gisBusy],
  )

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
        <details className="gm-sec">
          <summary>导入 / 导出</summary>
          <div className="gm-row2">
            <button className="btn" onClick={exportDoc}>导出当前</button>
            <button className="btn" onClick={() => fileRef.current?.click()}>导入 JSON</button>
          </div>
          <input ref={fileRef} type="file" accept=".json" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) importDoc(f); e.target.value = '' }} />
        </details>
        <details className="gm-sec">
          <summary>服务器网格包（{srvList.length}）</summary>
          <div className="gm-sec-head">
            <span className="muted small">剧本管理页「战场环境」生成的六角格网络（zip 存服务器，剧本只引用 id）</span>
            <button className="btn btn-sm" title="刷新" onClick={refreshSrv}>↻</button>
          </div>
          <div className="gm-list">
            {srvList.length === 0 && <div className="muted small">暂无服务器网格包</div>}
            {srvList.map((m) => (
              <div key={m.id} className="gm-item" onClick={() => void viewSrv(m)} style={{ cursor: 'pointer' }}>
                <div className="gm-item-main">
                  <b>{m.name}</b>
                  <span className="muted small">
                    {m.rows} 行 × {m.cols} 列 · 对边距 {m.flat_to_flat} · {m.id}
                    {m.used_by.length ? ` · 被 ${m.used_by.length} 个剧本引用` : ''}
                  </span>
                </div>
                <div className="gm-item-ops">
                  <button className="btn btn-sm" title="查看" onClick={(e) => { e.stopPropagation(); void viewSrv(m) }}>看</button>
                  <button className="btn btn-sm" title="载入到本地编辑" onClick={(e) => { e.stopPropagation(); void loadSrvToLocal(m) }}>载入</button>
                  {srvConfirmDel === m.id ? (
                    <button className="btn btn-sm btn-red" title="确认删除" onClick={(e) => { e.stopPropagation(); void deleteSrv(m) }}>确认</button>
                  ) : (
                    <button className="btn btn-sm" title="删除" onClick={(e) => { e.stopPropagation(); setSrvConfirmDel(m.id); setTimeout(() => setSrvConfirmDel((c) => (c === m.id ? null : c)), 3000) }}>删</button>
                  )}
                </div>
              </div>
            ))}
          </div>
        </details>
        {active && (
          <details className="gm-sec">
            <summary>统计</summary>
            <div className="kv"><span>网格规格</span><span>{active.rows} 行 × {active.cols} 列</span></div>
            <div className="kv"><span>有效格数</span><span>{active.rows * active.cols - active.holes.length}</span></div>
            <div className="kv"><span>已挖格数</span><span>{active.holes.length}</span></div>
            <div className="kv"><span>涂装格数</span><span>{Object.keys(active.terrains).length}</span></div>
          </details>
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
            {mode === 'pan' && '拖动地图 · 滚轮缩放 · 框选范围请用右侧「框选地图范围」'}
            {(mode === 'paint' || mode === 'dig' || mode === 'fill') && '点击/拖动格子进行编辑'}
          </span>
        </div>
        <div className="gm-canvas-wrap">
          <GeoMapView mode={mode} overlay={srvView ?? overlay} onFrame={onFrame} onPick={onPick} onCellClick={onCellClick} fit={fit} range={rangeBox} gis={active?.gis ?? null} canvasOnly />
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
            <button className="btn btn-sm" title="打开弹框，在弹框地图上拖拽框选范围" onClick={openFrameModal}>
              🔲 框选地图范围
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
          <button className="btn btn-primary" onClick={saveFromDraft} disabled={!!gisBusy}>{editingId ? '保存修改' : '创建网格'}</button>
          {gisBusy && (
            <div className="muted small gm-geom">⏳ GIS 分析中：{gisBusy}</div>
          )}
          {!gisBusy && active?.gis && (
            <details className="gm-sec">
              <summary>GIS 数据</summary>
              <div className="muted small gm-geom">
                道路 {Object.keys(active.gis.roads).length} 格 · 铁路 {active.gis.rails.length} 格 · 河流 {active.gis.rivers.length} 格 · 自动地形 {Object.keys(active.gis.terrains).length} 格
                {active.gis.pkg?.terrainCounts && (
                  <><br />地形分布：{Object.entries(active.gis.pkg.terrainCounts).map(([k, v]) => `${k} ${v}`).join(' · ')}</>
                )}
                <br />数据包 {active.gisPkg ?? active.gis.pkg?.name}（{new Date(active.gis.at).toLocaleString()}）
              </div>
              {geom && (
                <button className="btn" onClick={() => void runGis(active.id, draft.name || active.name, geom.rows, geom.cols, geom.ftf, bb!, active.holes, active.terrains)}>重新分析 GIS 数据</button>
              )}
            </details>
          )}
          {editingId && (
            <button className="btn" onClick={() => { setEditingId(null); setDraft(DEFAULT_DRAFT); setRangeBox(null); toast('已切换为新建模式') }}>新建（清空表单）</button>
          )}
        </div>
      </div>

      {/* 框选地图范围弹窗：弹框内拖拽矩形框选，确认后写入表单 */}
      {frameOpen && (
        <div className="scm-modal-mask" onClick={() => setFrameOpen(false)}>
          <div className="gm-frame-modal" onClick={(e) => e.stopPropagation()}>
            <div className="gm-frame-head">
              <b>框选地图范围</b>
              <span className="muted small">在地图上按住拖拽矩形，松开后矩形保持显示，可反复框选</span>
              <button className="btn btn-sm" title="关闭" onClick={() => setFrameOpen(false)}>×</button>
            </div>
            <div className="gm-frame-map">
              <GeoMapView mode="frame" overlay={srvView ?? overlay} onFrame={setPicked} fit={frameFit} range={picked} canvasOnly />
            </div>
            <div className="gm-frame-foot">
              <span className="muted small">
                {picked
                  ? (geom
                    ? `已选 ${picked.west.toFixed(4)}°E, ${picked.south.toFixed(4)}°N → ${picked.east.toFixed(4)}°E, ${picked.north.toFixed(4)}°N · 约 ${clamp(Math.ceil((lngToMx(picked.east) - lngToMx(picked.west)) / (SQRT3 * (geom.ftf / SQRT3))), 1, 200)} 列 × ${clamp(Math.ceil((latToMy(picked.north) - latToMy(picked.south)) / (1.5 * (geom.ftf / SQRT3))), 1, 200)} 行`
                    : '已选范围（先填写有效对边距才能换算行列）')
                  : '尚未框选：在上方地图拖拽矩形'}
              </span>
              <button className="btn btn-sm" onClick={() => setFrameOpen(false)}>取消</button>
              <button className="btn btn-sm primary" disabled={!picked || !geom} onClick={applyPicked}>确认使用该范围</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
