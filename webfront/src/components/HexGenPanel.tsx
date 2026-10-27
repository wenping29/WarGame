/**
 * HexGenPanel —— 「按范围现场生成六角格网格」公共组件。
 *
 * 从 Wizard（创建副本）与 ScenManager（剧本管理）的重复实现提炼而来，封装：
 *   范围四至表单 + 在线地图框选弹窗、对边距输入 + 服务端推荐方案卡（去抖自动规划）、
 *   数据层选项（高程/土地利用/道路/水系/地物，可精简隐藏）、生成引擎能力降级提示、
 *   作业启动 + 1.5s 轮询日志卡。作业完成时服务端已把产物发布为服务器网络包（hexgrid_id），
 *   通过 onPublished 回调交给调用方绑定使用。
 *
 * 表单字段受控（form/onChange），会话状态（caps/plan/job/轮询）由组件内部持有。
 */
import { useEffect, useRef, useState } from 'react'
import { api, netError, type GisCapabilities, type GisJob, type GisJobSpec, type GisPlan } from '../services/api'
import GeoMapView, { type BBox } from './GeoMapView'
import { toast } from './Toast'

/* ── 表单状态（受控部分，调用方持有） ── */
export interface GisGenForm {
  bbox: { w: string; s: string; e: string; n: string }
  cell_km: string
  dem: string
  landuse: string
  roads: boolean
  water: boolean
  network: boolean
  features: boolean
  name: string
}

/** 全默认表单（数据层全开 + 自动数据源），ScenManager 等精简场景直接用 */
export const defaultGenForm = (): GisGenForm => ({
  bbox: { w: '', s: '', e: '', n: '' },
  cell_km: '', dem: 'auto', landuse: 'auto',
  roads: true, water: true, network: true, features: true, name: '',
})

const num = (v: string | number | null | undefined): number | null =>
  v === '' || v == null || isNaN(+(v as number)) ? null : +(v as number)

/** 范围框 → [西,南,东,北]；不合法返回 null（服务端同样拒，前置判断只为给中文提示） */
function bboxOf(b: { w: string; s: string; e: string; n: string }): [number, number, number, number] | null {
  const w = num(b.w), s = num(b.s), e = num(b.e), n = num(b.n)
  if (w === null || s === null || e === null || n === null) return null
  if (!(w < e && s < n)) return null
  return [w, s, e, n]
}

const LAYER_LABEL_CN: Record<string, string> = {
  recommended: '推荐档位', heavy: '格数偏多', coarse: '格数偏少',
}

const JOB_STATUS_CN: Record<string, string> = {
  queued: '排队中', running: '生成中', done: '已完成', failed: '失败',
}

/* ── 能力降级提示：缺什么就说什么，不冒充实数据 ── */
function capsNotes(caps: GisCapabilities | null, f: GisGenForm): string[] {
  if (!caps) return []
  if (!caps.available) return ['本机没有可用的 Python，无法现场生成：' + (caps.reason || '未知原因')]
  // 远程生成服务承担生成：本机 Python / rasterio 那些探测项不再适用，只报远程数据层实况
  if (caps.remote) {
    const out: string[] = []
    const dem = caps.remote.dem
    if (dem && dem.real_tiles_available === false)
      out.push(`生成服务未发现本地真实地形瓦片（${dem.terrain_dir || '?'}）⇒ 高程将退回合成地形。请把 GisApi 的 TerrainDir（Linux 写 TerrainDirLinux）指向含 terrain_manifest.json 与 *.ter 的目录（发布包内为 server/data/terrain）。`)
    else if (dem && dem.effective_real === false)
      out.push('生成服务当前按合成地形跑（dem 档位不是真实数据）⇒ 高程为确定性噪声代理。')
    if (dem && dem.effective_real && dem.real_tiles_has_landcover === false)
      out.push('生成服务的瓦片不含土地覆盖层 ⇒ 地形判类只用高程。')
    return out
  }
  const out: string[] = []
  if (!caps.rasterio) out.push('Python 缺 rasterio/numpy ⇒ 只能生成合成地形；真实栅格请 pip 安装后重试。')
  if (caps.rasterio && !caps.dem_present) out.push(`未发现本地 DEM（${caps.dem_tif || '?'}）⇒ 高程将退回合成。`)
  if (f.water && caps.water_vector_ok === false) out.push('真实水系不可用（' + (caps.water_reason || '缺 GeoPackage 或缺 geopandas') + '）⇒ 水文退回 DEM 派生河带。')
  if (f.network && caps.vector_ok === false) out.push('矢量地物读取不可用（' + (caps.vector_reason || '缺 pyogrio') + '）⇒ 真实路网卡关闭，地物只按地形派生。')
  if (f.network && caps.roads_gdb_present === false) out.push('未发现本地道路库（' + (caps.roads_gdb || '?') + '）⇒ 请先解压：7zr x data/gis/World_Roads.lpk "v10/roads.gdb/*" -odata/gis/roads_pkg -y')
  if (f.network && caps.airports_present === false) out.push('未发现机场库（' + (caps.airports_shp || '?') + '）⇒ 机场改为按地形派生一处野战场站。')
  return out
}

function PlanCard({ plan, planErr, planBusy, activeKm, customKm, onPick }: {
  plan: GisPlan | null; planErr: string; planBusy: boolean
  activeKm: number | null; customKm: number | null; onPick: (km: number) => void
}) {
  if (planErr && !plan) return <p className="warn small">{planErr}</p>
  const p = plan
  if (!p) return <p className="muted small">{planBusy ? '正在计算格网方案…' : '填好范围框后自动给出推荐对边距与行列数。'}</p>
  const rec = p.recommended.cell_km
  const cur = p.ladder.find(c => c.cell_km === activeKm) || (customKm != null ? p.current : null)
  return (
    <div className="card plan-card">
      <div className="row">
        <div className="kv"><span>实地范围</span><b>{Math.round(p.ground_km[0])}×{Math.round(p.ground_km[1])} km</b></div>
        <div className="kv"><span>墨卡托放大</span><b>×{Math.round(p.mercator_scale * 1000) / 1000}</b></div>
        <div className="kv"><span>推荐对边距</span><b>{rec} km</b></div>
        <div className="kv">
          <span>当前格网</span>
          <b>{cur ? `${cur.cell_km} km · ${cur.width}×${cur.height} = ${cur.cells} 格${customKm == null ? '（用推荐）' : ''}` : '未定'}</b>
        </div>
      </div>
      <div className="chips">
        {p.ladder.map(c => (
          <button key={c.cell_km}
            className={`btn btn-sm chip${c.cell_km === customKm ? ' btn-1' : ''}${c.cell_km === rec ? ' rec' : ''}`}
            title={LAYER_LABEL_CN[c.label] || ''}
            onClick={() => onPick(c.cell_km)}>
            {c.cell_km} km · {c.width}×{c.height} · {c.cells} 格
          </button>
        ))}
      </div>
      <ul className="muted small why">{p.recommended.reasons.map((r, i) => <li key={i}>{r}</li>)}</ul>
    </div>
  )
}

function JobCard({ job, err, onDownload }: { job: GisJob | null; err: string; onDownload: () => void }) {
  if (!job) return <p className="muted small">尚未生成。真实栅格生成约需十几秒到几分钟。</p>
  const tail = (job.log || []).slice(-9)
  const m = job.meta
  const counts = m?.terrain_counts
  const live = job.status === 'queued' || job.status === 'running'
  const pct = Math.max(0, Math.min(100, Math.round(job.progress ?? 0)))
  return (
    <div className="card job-card">
      <div className="row">
        <div className="kv"><span>作业</span><b>{job.id}</b></div>
        <div className="kv"><span>状态</span><b>{JOB_STATUS_CN[job.status] || job.status}</b></div>
        <div className="kv"><span>产物</span><b>data/gis/{job.name}</b></div>
        {job.remote_id && <div className="kv"><span>远程作业</span><b>{job.remote_id}</b></div>}
      </div>
      {live && (
        <div className="job-progress">
          <div className="job-progress-bar"><i style={{ width: pct + '%' }} /></div>
          <span className="muted small">{pct}%{job.stage ? ' · ' + job.stage : ''}</span>
        </div>
      )}
      <pre className="mono job-log">{tail.join('\n') || '…'}</pre>
      {(job.error || err) && <p className="warn small">{job.error || err}</p>}
      {job.status === 'done' && m && (
        <p className="small">
          自校验 {(m.fails || 0) === 0 ? `全部通过（${m.warns || 0} 项 WARN）` : `${m.fails} 项未通过`}
          {' · '}{m.source || '?'} · {m.width}×{m.height} 格
          {counts ? ' · 地形构成 ' + JSON.stringify(counts) : ''}
        </p>
      )}
      {job.status === 'done' && (
        <div className="row" style={{ marginTop: '6px' }}>
          <button className="btn btn-sm" onClick={onDownload}
            title="下载该作业生成的数据包（HexPackage JSON，含逐格高程/地形/地物）">
            ⬇️ 下载数据包
          </button>
        </div>
      )}
    </div>
  )
}

export interface HexGenPanelProps {
  /** 受控表单（范围/格距/数据层/包名） */
  form: GisGenForm
  onChange: (patch: Partial<GisGenForm>) => void
  /** 作业完成（无论是否发布为网络包）回调 */
  onDone?: (job: GisJob) => void
  /** 作业完成且已发布为服务器网络包：返回地图 id（=hexgrid），供绑定剧本/副本 */
  onPublished?: (hexgridId: string, job: GisJob) => void
  /** 推荐方案变化回调（供外部做一致性校验等） */
  onPlanChange?: (plan: GisPlan | null) => void
  /** 精简模式：隐藏数据层选项（高程/土地利用/开关），固定全自动 */
  compact?: boolean
  /** 隐藏数据包名输入（配合 defaultName 自动命名） */
  hideName?: boolean
  /** form.name 为空时的作业名缺省值 */
  defaultName?: string
  /** 已发布包的实际格距 km（用于提示"改了格距需重新生成"） */
  publishedKm?: string
  /** 已发布后的内联提示文案 */
  publishedHint?: (hexgridId: string) => string
}

export default function HexGenPanel({
  form, onChange, onDone, onPublished, onPlanChange,
  compact = false, hideName = false, defaultName, publishedKm, publishedHint,
}: HexGenPanelProps) {
  const set = (p: Partial<GisGenForm>) => onChange(p)

  /* 会话状态：caps/方案/作业，与表单分开存，轮询不丢日志 */
  const [caps, setCaps] = useState<GisCapabilities | null>(null)
  const [plan, setPlan] = useState<GisPlan | null>(null)
  const [planErr, setPlanErr] = useState('')
  const [planBusy, setPlanBusy] = useState(false)
  const [job, setJob] = useState<GisJob | null>(null)
  const [jobErr, setJobErr] = useState('')
  const [publishedId, setPublishedId] = useState('')

  /* 回调经 ref 转发，轮询 interval 里永远拿到最新闭包 */
  const cb = useRef({ onDone, onPublished })
  cb.current = { onDone, onPublished }

  useEffect(() => {
    if (caps) return
    api.gisCapabilities().then(c => setCaps(c)).catch(() => {})
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  /* 范围框/对边距变化后 420ms 去抖算方案：几何一律由服务端 plan 给，前端不自算 */
  useEffect(() => {
    const bb = bboxOf(form.bbox)
    if (!bb) {
      setPlan(null)
      setPlanErr('范围框需满足 西<东、南<北')
      onPlanChange?.(null)
      return
    }
    setPlanBusy(true)
    const t = setTimeout(() => {
      api.gisPlan(bb, num(form.cell_km) || undefined)
        .then(res => {
          setPlanBusy(false)
          setPlan(res)
          setPlanErr(res.error || '')
          onPlanChange?.(res)
        })
        .catch(e => {
          setPlanBusy(false)
          setPlan(null)
          setPlanErr(netError(e))
          onPlanChange?.(null)
        })
    }, 420)
    return () => clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [form.bbox, form.cell_km])

  const running = job != null && (job.status === 'queued' || job.status === 'running')

  /* 作业轮询：1.5s 间隔；完成时服务端已发布为服务器网络包（hexgrid_id） */
  const jobLive = job != null && running
  useEffect(() => {
    const id = job?.id
    if (!id || !jobLive) return
    const timer = setInterval(async () => {
      let j: GisJob
      try {
        j = await api.gisJob(id)
      } catch (e) {
        setJob(null)
        setJobErr(netError(e))
        toast(netError(e), 'err')
        return
      }
      setJob(j)
      setJobErr('')
      if (j.status === 'done') {
        cb.current.onDone?.(j)
        if (j.hexgrid_id) {
          setPublishedId(j.hexgrid_id)
          cb.current.onPublished?.(j.hexgrid_id, j)
        } else {
          setJobErr('存储服务器网络包失败，见作业日志')
          toast('生成完成，但存储服务器网络包失败：见作业日志', 'err')
        }
      } else if (j.status === 'failed') {
        toast('生成失败：' + (j.error || '见日志'), 'err')
      }
    }, 1500)
    return () => clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [job?.id, jobLive])

  const start = async () => {
    if (running) return
    const bb = bboxOf(form.bbox)
    if (!bb) return toast('范围框需满足 西<东、南<北', 'err')
    const cell = num(form.cell_km) || plan?.recommended.cell_km
    if (!cell) return toast(planErr || '正在计算格网方案，稍候', 'warn')
    setJobErr('')
    try {
      const j = await api.gisStartJob({
        bbox: bb, cell_km: cell,
        name: form.name.trim() || defaultName?.trim() || '',
        dem: form.dem as GisJobSpec['dem'], landuse: form.landuse as GisJobSpec['landuse'],
        roads: form.roads, water: form.water, network: form.network, features: form.features,
      })
      setPublishedId('')
      setJob(j)
    } catch (e) {
      const msg = netError(e)
      setJobErr(msg)
      toast('生成启动失败：' + msg, 'err')
    }
  }

  const rec = plan?.recommended.cell_km
  const customKm = num(form.cell_km)
  const activeKm = customKm ?? rec
  const notes = capsNotes(caps, form)
  // 已生成包格距 vs 当前选择：不一致时警告（继续用旧包会按旧格距生成）
  const pkgKm = num(publishedKm)
  const kmMismatch = !running && !!publishedKm && pkgKm !== null && activeKm != null && Math.abs(pkgKm - activeKm) > 1e-6

  /* ── 地图选取弹窗：在在线地图上框选范围回填四至 ── */
  const [pickOpen, setPickOpen] = useState(false)
  const [pickFit, setPickFit] = useState<{ bbox: BBox; nonce: number } | null>(null)

  const openPick = () => {
    const bb = bboxOf(form.bbox)
    // 已填了合法四至就飞过去，否则给一个默认视野（关中地区）
    setPickFit(bb ? { bbox: { west: bb[0], south: bb[1], east: bb[2], north: bb[3] }, nonce: Date.now() } : { bbox: { west: 107, south: 33, east: 108.5, north: 34.2 }, nonce: Date.now() })
    setPickOpen(true)
  }

  // 弹窗内 ESC 关闭
  useEffect(() => {
    if (!pickOpen) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setPickOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [pickOpen])

  const onFramePick = (b: BBox) => {
    set({
      bbox: { w: b.west.toFixed(4), s: b.south.toFixed(4), e: b.east.toFixed(4), n: b.north.toFixed(4) },
    })
    setPickOpen(false)
    toast(`已选取范围：西 ${b.west.toFixed(3)} / 南 ${b.south.toFixed(3)} / 东 ${b.east.toFixed(3)} / 北 ${b.north.toFixed(3)}`)
  }

  return (
    <div style={{ marginTop: '10px' }}>
      <div className="row">
        {(['w', 's', 'e', 'n'] as const).map((k, i) => (
          <label key={k}>
            <span>{['西经', '南纬', '东经', '北纬'][i]}</span>
            <input type="number" step="0.01" value={form.bbox[k]}
              onChange={e => set({ bbox: { ...form.bbox, [k]: e.target.value } })} />
          </label>
        ))}
        <button className="btn btn-sm" style={{ alignSelf: 'flex-end' }}
          title="打开在线地图，框选范围自动填入四至"
          onClick={openPick}>🗺️ 地图选取</button>
      </div>
      <p className="muted small">四至指六角格「格心」范围（WGS84）。单边不超过 8°，超过 3 万格会被拒绝。</p>

      <div className="row">
        <label>
          <span>六角格对边距（实地 km）</span>
          <input type="number" step="0.1" value={form.cell_km} placeholder={rec ? `推荐 ${rec}` : '正在计算…'}
            onChange={e => set({ cell_km: e.target.value })} />
        </label>
        <button className="btn btn-sm"
          onClick={() => {
            if (!plan) return toast(planErr || '正在计算格网方案，稍候', 'warn')
            set({ cell_km: String(plan.recommended.cell_km) })
          }}>
          用推荐对边距
        </button>
      </div>

      {kmMismatch && (
        <p className="warn small">
          ⚠ 已生成的数据包是 <b>{pkgKm} km</b> 格距，与当前选择的 <b>{activeKm} km</b> 不一致——
          请修改后重新点下方「生成六角格网格」，否则引用的还是 {pkgKm} km 的旧六角格。
        </p>
      )}

      <PlanCard plan={plan} planErr={planErr} planBusy={planBusy}
        activeKm={activeKm ?? null} customKm={customKm} onPick={km => set({ cell_km: String(km) })} />

      {!compact && (
        <>
          <h3 style={{ marginTop: '12px' }}>数据层</h3>
          <div className="row">
            <label>
              <span>高程</span>
              <select value={form.dem} onChange={e => set({ dem: e.target.value })}>
                <option value="auto">自动（本地真实 DEM 优先）</option>
                <option value="local">仅本地 DEM 栅格</option>
                <option value="synthetic">合成（离线演示）</option>
              </select>
            </label>
            <label>
              <span>土地利用/覆盖</span>
              <select value={form.landuse} onChange={e => set({ landuse: e.target.value })}>
                <option value="auto">真实 WorldCover 10 m</option>
                <option value="proxy">噪声代理（无网络）</option>
                <option value="off">不参与判类</option>
              </select>
            </label>
          </div>
          <div className="row">
            {([
              ['roads', '道路走廊（地形派生：补齐真实路网卡住的渡河点）'],
              ['water', '水系（真实矢量：Natural Earth 10m 河流/湖泊，叠加于 DEM 派生河带之上）'],
              ['network', '真实矢量地物（World_Roads 干道 + Natural Earth 机场/桥隧标注）'],
              ['features', '地物玩法层（铁路/火车站/公路桥/隧道/电网/电厂/变电站/机场/加油站）'],
            ] as const).map(([key, label]) => (
              <label key={key} className="sw">
                <input type="checkbox" checked={form[key]}
                  onChange={e => set({ [key]: e.target.checked } as Partial<GisGenForm>)} />
                <span>{label}</span>
              </label>
            ))}
          </div>
        </>
      )}

      {caps && (
        <p className="muted small">
          生成引擎：<b>{caps.engine}</b>
          {caps.remote
            ? `${caps.remote.base_url ? ' · ' + caps.remote.base_url : ''}` +
              `${caps.remote.dem?.real_tiles_available ? ' · 本地真实地形瓦片' : ' · 无真实瓦片（合成地形）'}` +
              `${caps.remote.dem?.real_tiles_has_landcover ? ' + 土地覆盖' : ''}`
            : `${caps.python_version ? ` · Python ${caps.python_version}` : ''}` +
              `${caps.rasterio ? ' · rasterio 就绪' : ' · 缺 rasterio'}`}
        </p>
      )}
      {notes.map(t => <p key={t} className="warn small">{t}</p>)}

      <div className="row" style={{ marginTop: '12px', alignItems: 'flex-end' }}>
        {!hideName && (
          <label>
            <span>数据包名</span>
            <input value={form.name} placeholder="留空 = 按范围自动命名" onChange={e => set({ name: e.target.value })} />
          </label>
        )}
        <button className="btn btn-primary" disabled={!!running || caps?.available === false} onClick={start}>
          {running ? '生成中…' : '生成六角格网格'}
        </button>
      </div>

      {job?.status === 'done' && publishedId && publishedHint && (
        <p className="muted small">{publishedHint(publishedId)}</p>
      )}

      <JobCard job={job} err={jobErr} onDownload={() => {
        if (!job) return
        api.gisJobDownload(job.id, job.name || (job.id + '.json'))
          .catch(e => toast('下载失败：' + netError(e), 'err'))
      }} />

      {/* 地图选取弹窗：框选范围回填四至 */}
      {pickOpen && (
        <div className="wz-modal-mask" onMouseDown={e => { if (e.target === e.currentTarget) setPickOpen(false) }}>
          <div className="wz-modal">
            <div className="wz-modal-head">
              <h3>地图选取范围</h3>
              <span className="wz-modal-hint">在地图上拖拽矩形框，松开后自动填入四至（Esc 关闭）</span>
              <button className="btn btn-sm" onClick={() => setPickOpen(false)}>✕</button>
            </div>
            <div className="wz-modal-body">
              <GeoMapView mode="frame" overlay={null} onFrame={onFramePick} fit={pickFit} />
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
