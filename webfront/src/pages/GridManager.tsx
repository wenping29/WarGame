/**
 * 六角格管理页：只读浏览。网格的生成入口统一在剧本管理页「战场环境」与创建副本向导，
 * 本页只做查看/浏览/展示与管理（下载/删除/导入导出），不提供生成与编辑。
 * 左栏合并列表（本地副本 + 服务器包，卡片标注下载状态，展示前需先下载）+ 导入导出 + 统计；
 * 右侧纯 canvas 展示六角格网（经纬网格线为底，不加载在线底图，避免卡顿）。
 * 几何与主地图一致：pointy-top + odd-r，对边距 = √3 · 半径，几何量以墨卡托米计。
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { toast } from '../components/Toast'
import GeoMapView, { type BBox, type HexOverlay, lngToMx, latToMy, mxToLng, myToLat } from '../components/GeoMapView'
import { api, netError, type HexGridMeta } from '../services/api'
import type { GisData } from '../gis/hexgen'

const SQRT3 = Math.sqrt(3)
const LS_KEY = 'hexfront.gridmgr.v1'
const parseKey = (k: string): [number, number] => {
  const [a, b] = k.split(',')
  return [Number(a), Number(b)]
}
const KM_PER_DEG = (Math.PI * 6378137) / 180 / 1000

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
  /** GIS 生成的全套格数据（查看时叠加显示） */
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

/** 裁掉越界的洞与涂装 */
function pruneDoc(d: GridDoc): GridDoc {
  const inRange = (k: string) => {
    const [c, r] = parseKey(k)
    return c >= 0 && c < d.cols && r >= 0 && r < d.rows
  }
  return { ...d, holes: d.holes.filter(inRange), terrains: Object.fromEntries(Object.entries(d.terrains).filter(([k]) => inRange(k))) }
}

export default function GridManager() {
  const [grids, setGrids] = useState<GridDoc[]>(loadStore)
  const [viewId, setViewId] = useState<string | null>(null)
  const [fit, setFit] = useState<{ bbox: BBox; nonce: number } | null>(null)
  const [confirmDel, setConfirmDel] = useState<string | null>(null)
  /* ---------- 服务器六角格网络包（剧本管理页生成的 zip 包） ---------- */
  const [srvList, setSrvList] = useState<HexGridMeta[]>([])
  const [srvConfirmDel, setSrvConfirmDel] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const refreshSrv = useCallback(() => {
    api.hexgrids().then(r => setSrvList(r.grids || [])).catch(() => {})
  }, [])
  useEffect(refreshSrv, [refreshSrv])

  // 持久化防抖
  useEffect(() => {
    const t = setTimeout(() => saveStore(grids), 250)
    return () => clearTimeout(t)
  }, [grids])

  const fitGrid = (b: BBox) => setFit({ bbox: b, nonce: Date.now() })

  const viewDoc = (g: GridDoc) => {
    setViewId(g.id)
    fitGrid(g.bbox ?? deriveBBox(107, 33, g.rows, g.cols, g.flatToFlat))
  }

  // 下载：把服务器网格包取回并转成本地网格文档（保留挖格与涂装），标记 srvId 供合并列表判重
  const loadSrvToLocal = async (m: HexGridMeta) => {
    // 已有同源副本：直接查看，不再重复下载
    const exist = grids.find((g) => g.srvId === m.id)
    if (exist) {
      viewDoc(exist)
      toast(`「${m.name}」已在本地（${exist.id}），直接查看`)
      return
    }
    try {
      const r = await api.hexgridGet(m.id)
      const [w, s] = r.bbox
      const d = pruneDoc({
        ...makeGrid(r.grid.name || m.id, r.grid.rows, r.grid.cols, r.grid.flat_to_flat,
          { west: w, south: s, east: r.bbox[2], north: r.bbox[3] }),
        holes: r.grid.holes || [],
        terrains: r.grid.terrains || {},
        srvId: m.id,
      })
      setGrids(list => [...list, d])
      viewDoc(d)
      toast(`已下载「${d.name}」到本地并展示`, 'ok')
    } catch (err) {
      toast('下载失败：' + netError(err), 'err')
    }
  }

  const deleteSrv = async (m: HexGridMeta) => {
    try {
      await api.hexgridDelete(m.id)
      toast('已删除网格包 ' + m.id)
      refreshSrv()
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err)
      toast(/409|引用/.test(msg) ? `删除被拒：${m.id} 正被剧本引用，先在剧本管理页解除绑定` : '删除失败：' + msg, 'err')
    } finally {
      setSrvConfirmDel(null)
    }
  }

  const deleteGrid = (id: string) => {
    setGrids((list) => {
      const next = list.filter((g) => g.id !== id)
      const final = next.length ? next : [makeGrid('默认棋盘', 10, 14, 2500)]
      if (id === viewId) setViewId(null)
      return final
    })
    setConfirmDel(null)
    toast('已删除网格')
  }

  /* ---------- 导入导出 ---------- */
  const exportDoc = () => {
    if (!viewed) return toast('先选择一个网格', 'err')
    const blob = new Blob([JSON.stringify(viewed, null, 2)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${viewed.name}.hexgrid.json`
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
        viewDoc(d)
        toast(`已导入「${d.name}」`)
      } catch {
        toast('导入失败：不是有效的网格 JSON', 'err')
      }
    })
  }

  const viewed = useMemo(() => grids.find((g) => g.id === viewId) ?? null, [grids, viewId])

  /* 合并卡片列表：服务器包一张卡（未下载/已下载）；
     已下载的本地副本不再单独出卡（由服务器卡代表），源包已删的孤儿副本回退为本地卡 */
  type Card =
    | { kind: 'srv'; m: HexGridMeta; dl: GridDoc | undefined }
    | { kind: 'local'; g: GridDoc }
  const cards: Card[] = useMemo(() => {
    const srvIds = new Set(srvList.map((m) => m.id))
    const out: Card[] = srvList.map((m) => ({ kind: 'srv', m, dl: grids.find((g) => g.srvId === m.id) }))
    for (const g of grids) {
      if (!g.srvId || !srvIds.has(g.srvId)) out.push({ kind: 'local', g })
    }
    return out
  }, [grids, srvList])
  const localOnlyCount = cards.filter((c) => c.kind === 'local').length

  const overlay: HexOverlay | null = useMemo(() => {
    if (!viewed) return null
    const b = viewed.bbox ?? deriveBBox(107, 33, viewed.rows, viewed.cols, viewed.flatToFlat)
    return {
      west: b.west, south: b.south, rows: viewed.rows, cols: viewed.cols,
      size: viewed.flatToFlat / SQRT3, holes: viewed.holes, terrains: viewed.terrains, preview: false,
    }
  }, [viewed])

  return (
    <div className="page gridmgr">
      {/* 左栏：合并网格列表 + 导入导出 + 统计 */}
      <div className="gm-col">
        <div className="panel gm-list-panel">
          <div className="panel-header">
            <h3>网格列表（{srvList.length} 服务器包 · {localOnlyCount} 本地）</h3>
            <button className="btn btn-sm" title="刷新服务器列表" onClick={refreshSrv}>↻</button>
          </div>
          <div className="gm-list">
            {cards.map((c) => {
              if (c.kind === 'local') {
                const g = c.g
                return (
                  <div key={g.id} className={`gm-item${g.id === viewId ? ' on' : ''}`} title="点击在地图上查看" onClick={() => viewDoc(g)}>
                    <div className="gm-item-main">
                      <b>{g.name}{g.srvId && <span className="tag" style={{ marginLeft: 6 }} title="来源服务器包已删除，本地副本保留">本地副本</span>}</b>
                      <span className="muted small">{g.rows} 行 × {g.cols} 列 · 对边距 {g.flatToFlat}</span>
                    </div>
                    <div className="gm-item-ops">
                      {confirmDel === g.id ? (
                        <button className="btn btn-sm btn-red" onClick={(e) => { e.stopPropagation(); deleteGrid(g.id) }}>确认</button>
                      ) : (
                        <button className="btn btn-sm" title="删除本地网格" onClick={(e) => { e.stopPropagation(); setConfirmDel(g.id); setTimeout(() => setConfirmDel((v) => (v === g.id ? null : v)), 3000) }}>删</button>
                      )}
                    </div>
                  </div>
                )
              }
              const { m, dl } = c
              return (
                <div key={'srv-' + m.id} className={`gm-item${dl && dl.id === viewId ? ' on' : ''}`} style={{ cursor: 'pointer' }}
                  title={dl ? '点击在地图上查看' : '未下载：请先下载后查看'}
                  onClick={() => (dl ? viewDoc(dl) : toast('请先下载后查看', 'warn'))}>
                  <div className="gm-item-main">
                    <b>{m.name} <span className={'tag' + (dl ? ' on' : '')} style={{ marginLeft: 4 }}>{dl ? '已下载' : '未下载'}</span></b>
                    <span className="muted small">
                      服务器包 · {m.rows} 行 × {m.cols} 列 · 对边距 {m.flat_to_flat} · {m.id}
                      {m.used_by.length ? ` · 被 ${m.used_by.length} 个剧本引用` : ''}
                    </span>
                  </div>
                  <div className="gm-item-ops">
                    {!dl && (
                      <button className="btn btn-sm" title="下载到本地（下载后才能查看）"
                        onClick={(e) => { e.stopPropagation(); void loadSrvToLocal(m) }}>下载</button>
                    )}
                    <button className="btn btn-sm" title={dl ? '在地图上查看' : '请先下载后查看'} disabled={!dl}
                      onClick={(e) => { e.stopPropagation(); if (dl) viewDoc(dl) }}>看</button>
                    {srvConfirmDel === m.id ? (
                      <button className="btn btn-sm btn-red" title="确认删除服务器包" onClick={(e) => { e.stopPropagation(); void deleteSrv(m) }}>确认</button>
                    ) : (
                      <button className="btn btn-sm" title="删除服务器包（不影响已下载的本地副本）"
                        onClick={(e) => { e.stopPropagation(); setSrvConfirmDel(m.id); setTimeout(() => setSrvConfirmDel((v) => (v === m.id ? null : v)), 3000) }}>删</button>
                    )}
                  </div>
                </div>
              )
            })}
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
        {viewed && (
          <details className="gm-sec" open>
            <summary>统计</summary>
            <div className="kv"><span>网格规格</span><span>{viewed.rows} 行 × {viewed.cols} 列</span></div>
            <div className="kv"><span>有效格数</span><span>{viewed.rows * viewed.cols - viewed.holes.length}</span></div>
            <div className="kv"><span>已挖格数</span><span>{viewed.holes.length}</span></div>
            <div className="kv"><span>涂装格数</span><span>{Object.keys(viewed.terrains).length}</span></div>
            {viewed.bbox && (
              <div className="kv">
                <span>覆盖范围</span>
                <span>{((viewed.bbox.east - viewed.bbox.west) * KM_PER_DEG).toFixed(1)} × {((viewed.bbox.north - viewed.bbox.south) * KM_PER_DEG).toFixed(1)} km</span>
              </div>
            )}
            {viewed.gis && (
              <div className="kv"><span>GIS 数据</span><span>道路 {Object.keys(viewed.gis.roads).length} 格 · 河流 {viewed.gis.rivers.length} 格</span></div>
            )}
          </details>
        )}
      </div>

      {/* 右侧：纯 canvas 展示 */}
      <div className="gm-canvas-col">
        <div className="gm-toolbar">
          <span className="muted small gm-modehint">
            {viewed ? `正在查看：${viewed.name} · 拖动地图 · 滚轮缩放` : '从左侧选择网格进行查看 · 拖动地图 · 滚轮缩放'}
          </span>
        </div>
        <div className="gm-canvas-wrap">
          <GeoMapView mode="pan" overlay={overlay} fit={fit} gis={viewed?.gis ?? null} canvasOnly />
        </div>
      </div>
    </div>
  )
}
