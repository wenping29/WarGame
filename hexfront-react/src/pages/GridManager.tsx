/**
 * 六角格管理系统：创建 / 删除 / 修改六角格网络。
 * 几何与主地图一致：pointy-top + odd-r 偏移坐标，对边距（flat-to-flat）= √3 · 外接圆半径。
 * 数据持久化在浏览器 localStorage，支持导出 / 导入 JSON。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from '../components/Toast'

type TerrainKey = 'plain' | 'forest' | 'city' | 'water' | 'mountain'
type Tool = 'select' | 'paint' | 'dig' | 'fill'

const TERRAINS: Array<[TerrainKey, string, string]> = [
  ['plain', '平原', '#5d7a4a'],
  ['forest', '森林', '#38573c'],
  ['city', '城镇', '#8a8072'],
  ['water', '水域', '#33587a'],
  ['mountain', '山地', '#6e675c'],
]
const TERRAIN_COLOR = Object.fromEntries(TERRAINS.map(([k, , c]) => [k, c])) as Record<TerrainKey, string>
const TERRAIN_NAME = Object.fromEntries(TERRAINS.map(([k, n]) => [k, n])) as Record<TerrainKey, string>

interface GridDoc {
  id: string
  name: string
  rows: number
  cols: number
  flatToFlat: number // 对边距
  holes: string[] // 已删除格键 "col,row"
  terrains: Record<string, TerrainKey>
  createdAt: number
}

const LS_KEY = 'hexfront.gridmgr.v1'
const SQRT3 = Math.sqrt(3)
const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
const key = (col: number, row: number) => `${col},${row}`
const parseKey = (k: string): [number, number] => {
  const [a, b] = k.split(',')
  return [Number(a), Number(b)]
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
  return [makeGrid('默认棋盘', 10, 14, 2500)]
}

function saveStore(list: GridDoc[]) {
  try {
    localStorage.setItem(LS_KEY, JSON.stringify(list))
  } catch {
    toast('本地保存失败（存储空间不足？）', 'err')
  }
}

function makeGrid(name: string, rows: number, cols: number, flatToFlat: number): GridDoc {
  return {
    id: crypto.randomUUID(),
    name,
    rows,
    cols,
    flatToFlat,
    holes: [],
    terrains: {},
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

/** odd-r 偏移坐标：格心（格坐标系，未含缩放与原点偏移） */
function cellCenter(col: number, row: number, size: number): [number, number] {
  return [SQRT3 * size * (col + 0.5 * (row & 1)), 1.5 * size * row]
}

function hexPath(ctx: CanvasRenderingContext2D, cx: number, cy: number, size: number) {
  ctx.beginPath()
  for (let i = 0; i < 6; i++) {
    const a = (Math.PI / 180) * (60 * i - 30)
    const x = cx + size * Math.cos(a)
    const y = cy + size * Math.sin(a)
    if (i === 0) ctx.moveTo(x, y)
    else ctx.lineTo(x, y)
  }
  ctx.closePath()
}

const TOOLS: Array<[Tool, string]> = [
  ['select', '浏览'],
  ['paint', '涂刷'],
  ['dig', '挖格'],
  ['fill', '补格'],
]

export default function GridManager() {
  const [grids, setGrids] = useState<GridDoc[]>(loadStore)
  const [activeId, setActiveId] = useState<string>(() => loadStore()[0]?.id ?? '')
  const [tool, setTool] = useState<Tool>('select')
  const [brush, setBrush] = useState<TerrainKey>('plain')
  const [selected, setSelected] = useState<string | null>(null)
  const [hover, setHover] = useState<string | null>(null)
  const [confirmDel, setConfirmDel] = useState<string | null>(null)
  const [form, setForm] = useState({ name: '', rows: 10, cols: 14, flatToFlat: 2500 })
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const wrapRef = useRef<HTMLDivElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const active = useMemo(() => grids.find((g) => g.id === activeId) ?? grids[0], [grids, activeId])

  useEffect(() => {
    saveStore(grids)
  }, [grids])

  const patch = useCallback(
    (p: Partial<GridDoc>) => {
      if (!active) return
      setGrids((list) => list.map((g) => (g.id === active.id ? pruneDoc({ ...g, ...p }) : g)))
    },
    [active],
  )

  /* ---------- 画布渲染 ---------- */
  const draw = useCallback(() => {
    const canvas = canvasRef.current
    const wrap = wrapRef.current
    if (!canvas || !wrap || !active) return
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
    ctx.clearRect(0, 0, W, H)

    const size = active.flatToFlat / SQRT3
    const gridW = SQRT3 * size * (active.cols + 0.5)
    const gridH = size * (1.5 * active.rows + 0.5)
    const scale = Math.min((W - 32) / gridW, (H - 32) / gridH)
    const ox = (W - gridW * scale) / 2
    const oy = (H - gridH * scale) / 2

    const holeSet = new Set(active.holes)
    for (let row = 0; row < active.rows; row++) {
      for (let col = 0; col < active.cols; col++) {
        const k = key(col, row)
        if (holeSet.has(k)) continue
        const [gx, gy] = cellCenter(col, row, size)
        const cx = gx * scale + ox
        const cy = gy * scale + oy
        hexPath(ctx, cx, cy, size * scale)
        ctx.fillStyle = TERRAIN_COLOR[active.terrains[k] ?? 'plain']
        ctx.fill()
        ctx.strokeStyle = 'rgba(12,16,20,.55)'
        ctx.lineWidth = 1
        ctx.stroke()
        // 城镇/水域加一个简单纹理标记
        const t = active.terrains[k]
        if (t === 'city') {
          ctx.fillStyle = 'rgba(20,20,20,.5)'
          ctx.fillRect(cx - 2, cy - 2, 4, 4)
        } else if (t === 'water') {
          ctx.strokeStyle = 'rgba(190,215,235,.5)'
          ctx.beginPath()
          ctx.moveTo(cx - 6, cy + 2)
          ctx.quadraticCurveTo(cx, cy - 3, cx + 6, cy + 2)
          ctx.stroke()
        }
      }
    }
    // 挖掉的格画虚线轮廓（补格工具下可见）
    if (tool === 'fill' || tool === 'select') {
      ctx.setLineDash([4, 3])
      ctx.strokeStyle = 'rgba(150,160,170,.4)'
      for (const k of active.holes) {
        const [col, row] = parseKey(k)
        const [gx, gy] = cellCenter(col, row, size)
        hexPath(ctx, gx * scale + ox, gy * scale + oy, size * scale)
        ctx.stroke()
      }
      ctx.setLineDash([])
    }
    // 悬停 / 选中高亮
    for (const [k, color] of [
      [hover, 'rgba(240,220,140,.9)'],
      [selected, 'rgba(250,200,90,1)'],
    ] as const) {
      if (!k) continue
      const [col, row] = parseKey(k)
      if (col >= active.cols || row >= active.rows) continue
      const [gx, gy] = cellCenter(col, row, size)
      hexPath(ctx, gx * scale + ox, gy * scale + oy, size * scale)
      ctx.strokeStyle = color
      ctx.lineWidth = 2.5
      ctx.stroke()
    }
  }, [active, tool, hover, selected])

  useEffect(() => {
    draw()
    const wrap = wrapRef.current
    if (!wrap) return
    const ro = new ResizeObserver(draw)
    ro.observe(wrap)
    return () => ro.disconnect()
  }, [draw])

  /* ---------- 鼠标：坐标 → 格 ---------- */
  const hitCell = useCallback(
    (e: React.MouseEvent<HTMLCanvasElement>): string | null => {
      const canvas = canvasRef.current
      const wrap = wrapRef.current
      if (!canvas || !wrap || !active) return null
      const rect = canvas.getBoundingClientRect()
      const px = e.clientX - rect.left
      const py = e.clientY - rect.top
      const size = active.flatToFlat / SQRT3
      const gridW = SQRT3 * size * (active.cols + 0.5)
      const gridH = size * (1.5 * active.rows + 0.5)
      const scale = Math.min((wrap.clientWidth - 32) / gridW, (wrap.clientHeight - 32) / gridH)
      const ox = (wrap.clientWidth - gridW * scale) / 2
      const oy = (wrap.clientHeight - gridH * scale) / 2
      const wx = (px - ox) / scale
      const wy = (py - oy) / scale
      let best: string | null = null
      let bestD = Infinity
      const lim = size * size // 六角格最近格心的最大距离 = 外接圆半径（角区由相邻格共享）
      for (let row = 0; row < active.rows; row++) {
        for (let col = 0; col < active.cols; col++) {
          const [gx, gy] = cellCenter(col, row, size)
          const d = (wx - gx) ** 2 + (wy - gy) ** 2
          if (d < bestD && d <= lim) {
            bestD = d
            best = key(col, row)
          }
        }
      }
      return best
    },
    [active],
  )

  const onMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const k = hitCell(e)
    if (k !== hover) setHover(k)
  }

  const onClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (!active) return
    const k = hitCell(e)
    if (!k) return
    const holeSet = new Set(active.holes)
    if (tool === 'select') {
      setSelected(k === selected ? null : k)
      return
    }
    if (tool === 'paint') {
      if (holeSet.has(k)) return toast('该格已挖除，请先补格', 'err')
      setSelected(k)
      patch({ terrains: { ...active.terrains, [k]: brush } })
      return
    }
    if (tool === 'dig') {
      if (holeSet.has(k)) return
      const terrains = { ...active.terrains }
      delete terrains[k]
      setSelected(null)
      patch({ holes: [...active.holes, k], terrains })
      return
    }
    if (tool === 'fill') {
      if (!holeSet.has(k)) return
      patch({ holes: active.holes.filter((h) => h !== k) })
    }
  }

  /* ---------- 网格增删改 ---------- */
  const createGrid = () => {
    const name = form.name.trim() || `棋盘 ${grids.length + 1}`
    const d = makeGrid(name, clamp(Math.round(form.rows), 1, 200), clamp(Math.round(form.cols), 1, 200), clamp(form.flatToFlat, 10, 1_000_000))
    setGrids((list) => [...list, d])
    setActiveId(d.id)
    setSelected(null)
    setForm({ name: '', rows: 10, cols: 14, flatToFlat: 2500 })
    toast(`已创建「${name}」`)
  }

  const deleteGrid = (id: string) => {
    setGrids((list) => {
      const next = list.filter((g) => g.id !== id)
      const final = next.length ? next : [makeGrid('默认棋盘', 10, 14, 2500)]
      if (id === activeId) setActiveId(final[0].id)
      return final
    })
    setConfirmDel(null)
    setSelected(null)
  }

  const exportDoc = () => {
    if (!active) return
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
        if (typeof j.rows !== 'number' || typeof j.cols !== 'number' || typeof j.flatToFlat !== 'number') {
          throw new Error('字段缺失')
        }
        const d = pruneDoc({
          ...makeGrid(String(j.name || '导入网格'), j.rows, j.cols, j.flatToFlat),
          holes: Array.isArray(j.holes) ? j.holes.map(String) : [],
          terrains: j.terrains && typeof j.terrains === 'object' ? j.terrains : {},
        })
        setGrids((list) => [...list, d])
        setActiveId(d.id)
        toast(`已导入「${d.name}」`)
      } catch {
        toast('导入失败：不是有效的网格 JSON', 'err')
      }
    })
  }

  if (!active) return null

  const selCol = selected ? parseKey(selected)[0] : -1
  const selRow = selected ? parseKey(selected)[1] : -1
  const selIn = selected && selCol < active.cols && selRow < active.rows
  const selIsHole = selected != null && active.holes.includes(selected)
  const side = active.flatToFlat / SQRT3
  const area = (3 * SQRT3 / 2) * side * side // 单格面积（平方单位）

  return (
    <div className="page gridmgr">
      {/* 左栏：网格列表 */}
      <div className="gm-col">
        <div className="panel">
          <div className="panel-header">
            <h3>网格列表（{grids.length}）</h3>
          </div>
          <div className="gm-list">
            {grids.map((g) => (
              <div key={g.id} className={`gm-item${g.id === active.id ? ' on' : ''}`} onClick={() => { setActiveId(g.id); setSelected(null) }}>
                <div className="gm-item-main">
                  <b>{g.name}</b>
                  <span className="muted small">{g.rows} 行 × {g.cols} 列 · 对边距 {g.flatToFlat}</span>
                </div>
                {confirmDel === g.id ? (
                  <button className="btn btn-sm btn-red" onClick={(e) => { e.stopPropagation(); deleteGrid(g.id) }}>确认删除</button>
                ) : (
                  <button className="btn btn-sm" onClick={(e) => { e.stopPropagation(); setConfirmDel(g.id); setTimeout(() => setConfirmDel((c) => (c === g.id ? null : c)), 3000) }}>删</button>
                )}
              </div>
            ))}
          </div>
        </div>
        <div className="panel">
          <div className="panel-header">
            <h3>新建网格</h3>
          </div>
          <label>名称
            <input value={form.name} placeholder="留空自动编号" onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </label>
          <div className="gm-row2">
            <label>行数
              <input type="number" min={1} max={200} value={form.rows} onChange={(e) => setForm({ ...form, rows: Number(e.target.value) })} />
            </label>
            <label>列数
              <input type="number" min={1} max={200} value={form.cols} onChange={(e) => setForm({ ...form, cols: Number(e.target.value) })} />
            </label>
          </div>
          <label>对边距（两平行边间距）
            <input type="number" min={10} value={form.flatToFlat} onChange={(e) => setForm({ ...form, flatToFlat: Number(e.target.value) })} />
          </label>
          <button className="btn btn-primary" onClick={createGrid}>创建网格</button>
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
      </div>

      {/* 中间：画布 */}
      <div className="gm-canvas-col">
        <div className="gm-toolbar">
          {TOOLS.map(([t, label]) => (
            <button key={t} className={`btn btn-sm${tool === t ? ' btn-primary' : ''}`} onClick={() => { setTool(t); if (t !== 'select') setSelected(null) }}>
              {label}
            </button>
          ))}
          {tool === 'paint' && (
            <span className="gm-palette">
              {TERRAINS.map(([k, n, c]) => (
                <button key={k} className={`gm-swatch${brush === k ? ' on' : ''}`} style={{ background: c }} title={n} onClick={() => setBrush(k)} />
              ))}
              <span className="muted small">{TERRAIN_NAME[brush]}</span>
            </span>
          )}
        </div>
        <div ref={wrapRef} className="gm-canvas-wrap">
          <canvas ref={canvasRef} onClick={onClick} onMouseMove={onMove} onMouseLeave={() => setHover(null)} />
          <div className="gm-status">
            {active.name} · {active.rows}×{active.cols} · 对边距 {active.flatToFlat} · 有效格 {active.rows * active.cols - active.holes.length}
            {hover && ` · 悬停 (${hover})`}
          </div>
        </div>
      </div>

      {/* 右栏：参数与选中格 */}
      <div className="gm-col">
        <div className="panel">
          <div className="panel-header">
            <h3>网格参数</h3>
          </div>
          <label>名称
            <input value={active.name} onChange={(e) => patch({ name: e.target.value })} />
          </label>
          <div className="gm-row2">
            <label>行数
              <input type="number" min={1} max={200} value={active.rows} onChange={(e) => patch({ rows: clamp(Math.round(Number(e.target.value)), 1, 200) })} />
            </label>
            <label>列数
              <input type="number" min={1} max={200} value={active.cols} onChange={(e) => patch({ cols: clamp(Math.round(Number(e.target.value)), 1, 200) })} />
            </label>
          </div>
          <label>对边距（flat-to-flat）
            <input type="number" min={10} value={active.flatToFlat} onChange={(e) => patch({ flatToFlat: clamp(Number(e.target.value), 10, 1_000_000) })} />
          </label>
          <div className="muted small gm-geom">
            外接圆半径（边长）{side.toFixed(2)} · 顶对顶 {((2 / SQRT3) * active.flatToFlat).toFixed(2)} · 单格面积 {area.toFixed(1)}
          </div>
        </div>
        <div className="panel">
          <div className="panel-header">
            <h3>选中格</h3>
          </div>
          {!selected || !selIn ? (
            <div className="muted small">浏览模式下点击格子选中。</div>
          ) : selIsHole ? (
            <>
              <div className="kv"><span>坐标</span><span>({selected})</span></div>
              <div className="kv"><span>状态</span><span>已挖除</span></div>
              <button className="btn" onClick={() => patch({ holes: active.holes.filter((h) => h !== selected) })}>补回该格</button>
            </>
          ) : (
            <>
              <div className="kv"><span>坐标</span><span>({selected})</span></div>
              <div className="kv"><span>地形</span><span>{TERRAIN_NAME[active.terrains[selected] ?? 'plain']}</span></div>
              <div className="gm-row2">
                {TERRAINS.map(([k, n, c]) => (
                  <button key={k} className={`btn btn-sm${(active.terrains[selected] ?? 'plain') === k ? ' btn-primary' : ''}`} onClick={() => patch({ terrains: { ...active.terrains, [selected]: k } })}>
                    <span className="gm-dot" style={{ background: c }} />{n}
                  </button>
                ))}
              </div>
              <button className="btn btn-red" onClick={() => { const t = { ...active.terrains }; delete t[selected]; patch({ holes: [...active.holes, selected], terrains: t }); setSelected(null) }}>
                删除该格（挖洞）
              </button>
            </>
          )}
        </div>
        <div className="panel">
          <div className="panel-header">
            <h3>统计</h3>
          </div>
          <div className="kv"><span>网格规格</span><span>{active.rows} 行 × {active.cols} 列</span></div>
          <div className="kv"><span>有效格数</span><span>{active.rows * active.cols - active.holes.length}</span></div>
          <div className="kv"><span>已挖格数</span><span>{active.holes.length}</span></div>
          <div className="kv"><span>涂装格数</span><span>{Object.keys(active.terrains).length}</span></div>
        </div>
      </div>
    </div>
  )
}
