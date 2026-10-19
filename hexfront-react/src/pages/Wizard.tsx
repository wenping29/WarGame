/**
 * 创建副本向导：5步流程。核心原则——未编辑字段绝不进入 overrides，
 * 服务端把 overrides deepMerge 到剧本默认；未出现的键即"用默认"。
 * 第 2 步支持现场生成六角格：GET /api/gis/plan 出档位阶梯与服务端推荐，
 * POST /api/gis/jobs 起后台作业并 1.5s 轮询日志，产物按 overrides.gis_package 进入本局。
 */

import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, netError, type CreateInstanceBody, type GisCapabilities, type GisJob, type GisPlan, type ValidateResult } from '../services/api'
import { loadMeta, useMeta, type UnitTypeDef } from '../room/useUnitFull'
import { toast } from '../components/Toast'

const STEP_TITLES = ['剧本与名称', '战场环境', '天气', '部队编成', '审阅并创建']

const FLAGS = [
  ['fog', '战争迷雾'],
  ['air_superiority', '制空权'],
  ['strict_supply', '严格后勤'],
  ['night', '夜间作战']
] as const

const WMODES = [
  ['', '（继承默认）'],
  ['fixed', '固定'],
  ['stochastic', '逐回合概率转移'],
  ['director', '导演部手控']
] as const

const TYPE_ATTRS = [
  ['mobility', '机动'],
  ['firepower', '火力'],
  ['protection', '防护'],
  ['recon', '侦察'],
  ['load', '负荷'],
  ['consumption', '消耗']
] as const

const num = (v: string | number | null | undefined): number | null =>
  v === '' || v == null || isNaN(+(v as number)) ? null : +(v as number)

interface OOBRow {
  type: string
  qty: string
  q: string
  r: string
  training: string
  experience: string
  cname: string
  cquality: string
}

/** 现场生成会话：与表单状态分开存，切步骤/轮询都不丢日志。 */
interface GenSession {
  caps: GisCapabilities | null
  plan: GisPlan | null
  planErr: string
  planBusy: boolean
  job: GisJob | null
  jobErr: string
}

const LAYER_LABEL_CN: Record<string, string> = {
  recommended: '推荐档位', heavy: '格数偏多', coarse: '格数偏少',
}

const JOB_STATUS_CN: Record<string, string> = {
  queued: '排队中', running: '生成中', done: '已完成', failed: '失败',
}

/** 范围框 → [西,南,东,北]；不合法返回 null（服务端同样拒，前置判断只为给中文提示） */
function bboxOf(b: { w: string; s: string; e: string; n: string }): [number, number, number, number] | null {
  const w = num(b.w), s = num(b.s), e = num(b.e), n = num(b.n)
  if (w === null || s === null || e === null || n === null) return null
  if (!(w < e && s < n)) return null
  return [w, s, e, n]
}

interface WizardState {
  scenarioId: string
  name: string
  seed: string
  env: { width: string; height: string; cell_km: string; lon: string; lat: string }
  gis: {
    mode: 'existing' | 'generate'
    pkg: string
    /** generate 模式作业完成后的产物包名：进入 overrides.gis_package */
    genPkg: string
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
  flags: Record<string, string>
  weather: {
    initial: string
    mode: string
    /** 天气计划：回合区间 → 天气；只预排，启动/暂停/删除归导演部 */
    schedule: Array<{ from: string; to: string; weather: string }>
  }
  /** 日夜交替：留空即继承剧本默认（起 6 时 / 每回合 6 小时 / 夜幕 20–6） */
  clock: { start_hour: string; turn_hours: string; night_from: string; night_to: string }
  sides: { red: { oob: OOBRow[]; budget: string }; blue: { oob: OOBRow[]; budget: string } }
  typeRows: string[]
  typeAttrs: Record<string, Record<string, string>>
  ai: { red: boolean; blue: boolean }
}

function rowToOob(row: OOBRow): Record<string, unknown> | null {
  if (!row.type) return null
  const out: Record<string, unknown> = { type: row.type }
  if (num(row.qty) !== null) out.qty = num(row.qty)
  const q = num(row.q), r = num(row.r)
  if (q !== null && r !== null) out.at = [q, r]
  if (num(row.training) !== null) out.training = num(row.training)
  if (num(row.experience) !== null) out.experience = num(row.experience)
  if (row.cname && row.cname.trim()) {
    const commander: Record<string, unknown> = { name: row.cname.trim() }
    if (num(row.cquality) !== null) commander.quality = num(row.cquality)
    out.commander = commander
  }
  return out
}

/// 剧本 environment → 表单初值（范围框按中心 ±0.95/±0.55° 起算）
function envDefaults(scn: { environment?: Record<string, unknown> } | null) {
  const e = (scn?.environment || {}) as Record<string, unknown>
  const center = e.center_lonlat as [number, number] | undefined
  const cellKm = e.cell_km != null ? String(e.cell_km) : ''
  if (!center) return { cell_km: cellKm, lon: '', lat: '', w: '', s: '', e: '', n: '' }
  const fmt = (v: number) => String(Math.round(v * 1000) / 1000)
  return {
    cell_km: cellKm, lon: fmt(center[0]), lat: fmt(center[1]),
    w: fmt(center[0] - 0.95), e: fmt(center[0] + 0.95), s: fmt(center[1] - 0.55), n: fmt(center[1] + 0.55),
  }
}

/// 服务端 400 也返回 {errors,...} JSON，axios 会 throw；这里把响应体捞回来当正常结果用
async function safeValidate(scenario: string, overrides: Record<string, unknown>): Promise<ValidateResult> {
  try {
    return await api.validate(scenario, overrides)
  } catch (e) {
    const data = (e as { response?: { data?: unknown } })?.response?.data as ValidateResult | undefined
    if (data && typeof data === 'object' && Array.isArray(data.errors)) return data
    throw e
  }
}

export default function Wizard() {
  const navigate = useNavigate()
  const meta = useMeta()
  const [step, setStep] = useState(0)
  const [oobDirty, setOobDirty] = useState<Record<string, boolean>>({ red: false, blue: false })
  const [lastValidate, setLastValidate] = useState<ValidateResult | null>(null)
  const [state, setState] = useState<WizardState>({
    scenarioId: '',
    name: '',
    seed: '',
    env: { width: '', height: '', cell_km: '', lon: '', lat: '' },
    gis: {
      mode: 'existing', pkg: '', genPkg: '',
      bbox: { w: '', s: '', e: '', n: '' },
      cell_km: '', dem: 'auto', landuse: 'auto',
      roads: true, water: true, network: true, features: true, name: '',
    },
    flags: Object.fromEntries(FLAGS.map(([k]) => [k, 'default'])),
    weather: { initial: '', mode: '', schedule: [] },
    clock: { start_hour: '', turn_hours: '', night_from: '', night_to: '' },
    sides: { red: { oob: [], budget: '' }, blue: { oob: [], budget: '' } },
    typeRows: [],
    typeAttrs: {},
    ai: { red: false, blue: false },
  })

  // meta 到达后用第一个剧本初始化
  useEffect(() => {
    if (!meta || state.scenarioId) return
    const s = meta.scenarios[0]
    if (!s) return
    const ed = envDefaults(s)
    setState(prev => ({
      ...prev,
      scenarioId: s.id || s.file,
      name: s.name || '',
      env: { ...prev.env, cell_km: prev.env.cell_km || ed.cell_km, lon: ed.lon, lat: ed.lat },
      gis: { ...prev.gis, bbox: { w: ed.w, s: ed.s, e: ed.e, n: ed.n } },
    }))
  }, [meta, state.scenarioId])

  const u = (updates: Partial<WizardState> | ((prev: WizardState) => Partial<WizardState>)) =>
    setState(prev => ({ ...prev, ...(typeof updates === 'function' ? updates(prev) : updates) }))

  // ── 现场生成六角格：能力探测 / 格网方案 / 作业轮询 ──
  const [gen, setGen] = useState<GenSession>({ caps: null, plan: null, planErr: '', planBusy: false, job: null, jobErr: '' })
  const patchGen = (p: Partial<GenSession>) => setGen(prev => ({ ...prev, ...p }))

  useEffect(() => {
    if (state.gis.mode !== 'generate' || gen.caps) return
    api.gisCapabilities().then(c => patchGen({ caps: c })).catch(() => {})
  }, [state.gis.mode, gen.caps])

  // 范围框/对边距变化后 420ms 去抖算方案：几何一律由服务端 plan 给，前端不自算
  useEffect(() => {
    if (state.gis.mode !== 'generate') return
    const bb = bboxOf(state.gis.bbox)
    if (!bb) {
      setGen(prev => ({ ...prev, plan: null, planErr: '范围框需满足 西<东、南<北' }))
      return
    }
    const cell = num(state.gis.cell_km) || undefined
    setGen(prev => ({ ...prev, planBusy: true }))
    const t = setTimeout(() => {
      api.gisPlan(bb, cell)
        .then(res => setGen(prev => ({ ...prev, planBusy: false, plan: res, planErr: res.error || '' })))
        .catch(e => setGen(prev => ({ ...prev, planBusy: false, plan: null, planErr: netError(e) })))
    }, 420)
    return () => clearTimeout(t)
  }, [state.gis.mode, state.gis.bbox, state.gis.cell_km])

  const jobLive = gen.job != null && (gen.job.status === 'queued' || gen.job.status === 'running')
  useEffect(() => {
    const id = gen.job?.id
    if (!id || !jobLive) return
    const timer = setInterval(async () => {
      let j: GisJob
      try {
        j = await api.gisJob(id)
      } catch (e) {
        setGen(prev => ({ ...prev, job: null, jobErr: netError(e) }))
        toast(netError(e), 'err')
        return
      }
      setGen(prev => ({ ...prev, job: j, jobErr: '' }))
      if (j.status === 'done') {
        // 新包要进 /api/scenarios 的 gis 列表，也要成为本局的 gis_package
        await loadMeta(true).catch(() => {})
        setState(prev => ({ ...prev, gis: { ...prev.gis, pkg: j.name, genPkg: j.name } }))
        toast(`六角格数据包已生成：${j.name}（${j.meta?.width ?? '?'}×${j.meta?.height ?? '?'} 格）`)
      } else if (j.status === 'failed') {
        toast('生成失败：' + (j.error || '见日志'), 'err')
      }
    }, 1500)
    return () => clearInterval(timer)
  }, [gen.job?.id, jobLive])

  const startGen = async () => {
    const g = state.gis
    if (gen.job && (gen.job.status === 'queued' || gen.job.status === 'running')) return
    const bb = bboxOf(g.bbox)
    if (!bb) return toast('范围框需满足 西<东、南<北', 'err')
    const cell = num(g.cell_km) || gen.plan?.recommended.cell_km
    if (!cell) return toast(gen.planErr || '正在计算格网方案，稍候', 'warn')
    try {
      const job = await api.gisStartJob({
        bbox: bb, cell_km: cell, name: g.name.trim(),
        dem: g.dem as 'auto' | 'local' | 'synthetic',
        landuse: g.landuse as 'auto' | 'proxy' | 'off',
        roads: g.roads, water: g.water, network: g.network, features: g.features,
      })
      setGen(prev => ({ ...prev, job, jobErr: '' }))
    } catch (e) {
      const msg = netError(e)
      setGen(prev => ({ ...prev, jobErr: msg }))
      toast(msg, 'err')
    }
  }

  const types: UnitTypeDef[] = useMemo(
    () => {
      const raw = meta?.unit_types as unknown
      const list = Array.isArray(raw) ? raw : raw ? Object.values(raw as Record<string, unknown>) : []
      return list as UnitTypeDef[]
    },
    [meta]
  )
  const firstType = () => types[0]?.id || 'rifle'
  const weatherList = useMemo(() => {
    const raw = meta?.weather as unknown
    return (Array.isArray(raw) ? raw : raw ? Object.values(raw as Record<string, unknown>) : []) as Array<{ id: string; label: string }>
  }, [meta])

  const doValidate = async (st = state) => {
    const overrides = buildOverridesFor(st)
    if (overrides.__invalid) {
      setLastValidate({ errors: [overrides.__invalid], preview: {}, type_points: {} })
      return
    }
    try {
      setLastValidate(await safeValidate(st.scenarioId, overrides))
    } catch (e) {
      setLastValidate({ errors: ['校验请求失败：' + e], preview: {}, type_points: {} })
    }
  }

  // ── overrides 构建（未编辑字段绝不进入） ──
  // 与 web wizard 的 buildOverrides 同语义，但接受显式 state（避免 setState 异步下读到旧值）
  function buildOverridesFor(st: WizardState): Record<string, unknown> & { __invalid?: string } {
    const ov: Record<string, unknown> = {}
    const pkg = st.gis.mode === 'existing' ? st.gis.pkg : st.gis.genPkg
    if (st.gis.mode === 'generate' && !pkg) {
      return { __invalid: '战场数据尚未生成：请先在「战场环境」里点「生成六角格网格」并等待完成' }
    }
    if (pkg) ov.gis_package = pkg
    const env: Record<string, unknown> = {}
    if (!pkg) {
      if (num(st.env.width) !== null) env.width = num(st.env.width)
      if (num(st.env.height) !== null) env.height = num(st.env.height)
      if (num(st.env.cell_km) !== null) env.cell_km = num(st.env.cell_km)
      if (num(st.env.lon) !== null && num(st.env.lat) !== null) env.center_lonlat = [num(st.env.lon), num(st.env.lat)]
    }
    if (Object.keys(env).length) ov.environment = env
    const flags: Record<string, boolean> = {}
    for (const [k] of FLAGS) if (st.flags[k] !== 'default') flags[k] = st.flags[k] === 'on'
    if (Object.keys(flags).length) ov.flags = flags
    const wx: Record<string, unknown> = {}
    if (st.weather.initial) wx.initial = st.weather.initial
    if (st.weather.mode) wx.mode = st.weather.mode
    const phases = st.weather.schedule
      .map(p => ({ from: num(p.from), to: num(p.to), weather: p.weather }))
      .filter((p): p is { from: number; to: number; weather: string } =>
        p.from !== null && p.to !== null && p.from >= 1 && p.to >= p.from)
    if (phases.length) wx.schedule = phases
    if (Object.keys(wx).length) ov.weather = wx
    // 昼夜交替：起始时刻/夜幕起止 → clock；每回合推进 → rules.turn_hours
    const ck: Record<string, unknown> = {}
    const ckStart = num(st.clock.start_hour)
    const ckNf = num(st.clock.night_from)
    const ckNt = num(st.clock.night_to)
    if (ckStart !== null) ck.start_hour = ckStart
    if (ckNf !== null) ck.night_from = ckNf
    if (ckNt !== null) ck.night_to = ckNt
    if (Object.keys(ck).length) ov.clock = ck
    const th = num(st.clock.turn_hours)
    if (th !== null) ov.rules = { turn_hours: th }
    const sides: Record<string, unknown> = {}
    for (const side of ['red', 'blue'] as const) {
      const cfg: Record<string, unknown> = {}
      const b = num(st.sides[side].budget)
      if (b !== null) cfg.points_budget = b
      if (oobDirty[side]) {
        const oob = st.sides[side].oob.map(rowToOob).filter(Boolean)
        if (!oob.length) return { __invalid: `阵营 ${side} 的 OOB 为空（若要保留默认请不要提交该字段）` }
        cfg.oob = oob
      }
      if (Object.keys(cfg).length) sides[side] = cfg
    }
    if (Object.keys(sides).length) ov.sides = sides
    const tdefs: Array<Record<string, unknown>> = []
    for (const id of st.typeRows) {
      const p = st.typeAttrs[id] || {}
      const e: Record<string, unknown> = { id }
      for (const [k] of TYPE_ATTRS) {
        const v = num(p[k])
        if (v !== null) e[k] = v
      }
      if (Object.keys(e).length > 1) tdefs.push(e)
    }
    if (tdefs.length) ov.unit_type_defs = tdefs
    return ov
  }

  const create = async () => {
    if (!state.scenarioId) return toast('剧本未就绪', 'err')
    const overrides = buildOverridesFor(state)
    if (overrides.__invalid) return toast(overrides.__invalid, 'err')
    let v: ValidateResult
    try {
      v = await safeValidate(state.scenarioId, overrides)
    } catch (e) {
      return toast('校验请求失败：' + e, 'err')
    }
    setLastValidate(v)
    if ((v.errors || []).length) return toast('校验未通过：' + v.errors[0], 'err')
    const b: CreateInstanceBody = { scenario: state.scenarioId }
    if (state.name.trim()) b.name = state.name.trim()
    if (state.seed !== '' && !isNaN(parseInt(state.seed))) b.seed = parseInt(state.seed)
    if (state.ai.red) b.ai_red = true
    if (state.ai.blue) b.ai_blue = true
    if (Object.keys(overrides).length) b.overrides = overrides
    const res = await api.createInstance(b)
    if (res.code) {
      toast(`副本已创建 · 房间 ${res.code} · 种子 ${res.seed ?? '?'}`)
      navigate(`/room/${res.code}/director`)
    } else {
      toast(res.error || res.errors?.join(';') || '创建失败', 'err')
    }
  }

  // 覆盖字段变化后自动重校验（步骤≥1）
  useEffect(() => {
    if (step >= 1 && state.scenarioId) doValidate()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.weather.initial, state.weather.mode, state.weather.schedule, state.clock, state.gis.pkg, state.gis.genPkg, state.flags,
      state.sides, state.typeRows, state.typeAttrs, state.scenarioId, oobDirty])

  return (
    <div className="page wizard-page" style={{ paddingBottom: 0 }}>
      <div className="steps">
        {STEP_TITLES.map((title, i) => (
          <div key={i} className={`st${i === step ? ' on' : ''}`} onClick={() => setStep(i)}>
            {i + 1}. {title}
          </div>
        ))}
      </div>

      <div className="wiz" style={{ padding: '0 18px' }}>
        <div className="body card">
          {step === 0 && (
            <div>
              <h2>第 1 步 · 剧本与名称/种子</h2>
              <div className="row">
                <label>
                  <span>剧本</span>
                  <select
                    value={state.scenarioId}
                    onChange={e => {
                      const s = (meta?.scenarios || []).find(x => (x.id || x.file) === e.target.value)
                      const ed = envDefaults(s ?? null)
                      u({
                        scenarioId: e.target.value,
                        name: s?.name || state.name,
                        env: { ...state.env, lon: ed.lon, lat: ed.lat, cell_km: ed.cell_km },
                        gis: { ...state.gis, genPkg: '', bbox: { w: ed.w, s: ed.s, e: ed.e, n: ed.n } },
                      })
                    }}
                  >
                    {(() => {
                      // 按战争分期（era）分组展示，保持服务端组内顺序
                      const groups = new Map<string, Array<{ id?: string; file?: string; name?: string; era?: string }>>()
                      for (const s of meta?.scenarios || []) {
                        const era = s.era || '其他'
                        if (!groups.has(era)) groups.set(era, [])
                        groups.get(era)!.push(s)
                      }
                      return [...groups.entries()].map(([era, list]) => (
                        <optgroup key={era} label={era}>
                          {list.map(s => (
                            <option key={s.id || s.file} value={s.id || s.file}>{s.name || s.id}</option>
                          ))}
                        </optgroup>
                      ))
                    })()}
                  </select>
                </label>
                <label>
                  <span>副本名称</span>
                  <input value={state.name} placeholder="继承剧本名称" onChange={e => u({ name: e.target.value })} />
                </label>
                <label>
                  <span>随机种子</span>
                  <input value={state.seed} placeholder="留空 = 每局随机" onChange={e => u({ seed: e.target.value })} />
                </label>
              </div>
              <p className="muted small">不填写的字段不会覆盖剧本默认，服务端以 deepMerge 合并。</p>
            </div>
          )}

          {step === 1 && <Step2Environment state={state} updateState={u} gen={gen} onStart={startGen} />}
          {step === 2 && <Step3Weather state={state} updateState={u} weatherList={weatherList} />}
          {step === 3 && (
            <Step4OOB
              state={state}
              updateState={u}
              types={types}
              firstType={firstType}
              setOobDirty={setOobDirty}
            />
          )}
          {step === 4 && <Step5Review state={state} overrides={buildOverridesFor(state)} onCreate={create} onBack={() => setStep(0)} />}
        </div>

        <div className="side">
          <SidePanel state={state} updateState={u} lastValidate={lastValidate} types={types} onCreate={create} />
        </div>
      </div>

      <div className="wiz-foot">
        {step > 0 && <button className="btn" onClick={() => setStep(step - 1)}>上一步</button>}
        {step < 4 ? (
          <button className="btn btn-1" onClick={() => setStep(step + 1)}>{step === 3 ? '审阅' : '下一步'}</button>
        ) : (
          <button className="btn btn-primary" onClick={create}>创建副本并进入导演部</button>
        )}
      </div>
    </div>
  )
}

// Step 2: Environment
function Step2Environment({ state, updateState, gen, onStart }: {
  state: WizardState; updateState: (u: Partial<WizardState>) => void
  gen: GenSession; onStart: () => void
}) {
  const meta = useMeta()
  const packages = (meta?.gis || []) as Array<{ name: string; cell_km?: number; width?: number; height?: number; cells?: number }>
  const g = state.gis

  return (
    <div>
      <h2>第 2 步 · 战场环境与六角格</h2>

      <div className="gis-box">
        <h3>战场数据与六角格网络</h3>
        <p className="muted small">
          六角格由真实栅格按范围现场生成，也可复用已生成的数据包。
        </p>

        <div className="row">
          <button
            className={`btn btn-sm${g.mode === 'existing' ? ' btn-1' : ''}`}
            onClick={() => updateState({ gis: { ...g, mode: 'existing' } })}
          >
            使用已有数据包
          </button>
          <button
            className={`btn btn-sm${g.mode === 'generate' ? ' btn-1' : ''}`}
            onClick={() => updateState({ gis: { ...g, mode: 'generate' } })}
          >
            按范围现场生成
          </button>
        </div>

        {g.mode === 'existing' ? (
          <div style={{ marginTop: '10px' }}>
            <div className="row">
              <label>
                <span>数据包</span>
                <select value={g.pkg} onChange={e => updateState({ gis: { ...g, pkg: e.target.value } })}>
                  <option value="">（无：用剧本默认）</option>
                  {packages.map(p => (
                    <option key={p.name} value={p.name}>
                      {p.name} · 对边距 {p.cell_km} km · {p.width}×{p.height} 格
                    </option>
                  ))}
                </select>
              </label>
            </div>
            {g.pkg && (() => {
              const p = packages.find(x => x.name === g.pkg)
              return p ? (
                <div className="card pkg-card" style={{ marginTop: '8px' }}>
                  <div className="kv">
                    <span>格网</span><b>{p.width}×{p.height} 格</b>
                  </div>
                  <div className="kv">
                    <span>有效格</span><b>{p.cells ?? '-'}</b>
                  </div>
                </div>
              ) : null
            })()}
          </div>
        ) : (
          <GeneratePanel state={state} updateState={updateState} gen={gen} onStart={onStart} />
        )}
      </div>

      <h3 style={{ marginTop: '14px' }}>模式开关</h3>
      <div className="row">
        {FLAGS.map(([key, label]) => (
          <label key={key}>
            <span>{label}</span>
            <select value={state.flags[key]}
              onChange={e => updateState({ flags: { ...state.flags, [key]: e.target.value } })}>
              <option value="default">（默认）</option>
              <option value="on">开</option>
              <option value="off">关</option>
            </select>
          </label>
        ))}
      </div>

      <details style={{ marginTop: '12px' }}>
        <summary className="muted small">程序化地形参数（仅在没有任何数据包时生效）</summary>
        <div className="row" style={{ marginTop: '8px' }}>
          <label><span>宽（格）</span>
            <input type="number" value={state.env.width} placeholder="继承默认"
              onChange={e => updateState({ env: { ...state.env, width: e.target.value } })} /></label>
          <label><span>高（格）</span>
            <input type="number" value={state.env.height} placeholder="继承默认"
              onChange={e => updateState({ env: { ...state.env, height: e.target.value } })} /></label>
          <label><span>对边距（实地 km）</span>
            <input type="number" step="0.1" value={state.env.cell_km} placeholder="继承默认"
              onChange={e => updateState({ env: { ...state.env, cell_km: e.target.value } })} /></label>
        </div>
        <div className="row">
          <label><span>中心经度</span>
            <input type="number" step="0.01" value={state.env.lon} placeholder="如 6.95"
              onChange={e => updateState({ env: { ...state.env, lon: e.target.value } })} /></label>
          <label><span>中心纬度</span>
            <input type="number" step="0.01" value={state.env.lat} placeholder="如 51.42"
              onChange={e => updateState({ env: { ...state.env, lat: e.target.value } })} /></label>
        </div>
      </details>

      <p className="muted small">
        六角格几何（原点、半径、行列数）始终取自所选数据包，与在线天地图瓦片同一墨卡托框架；对边距按实地公里计。
      </p>
    </div>
  )
}

/** 现场生成面板：范围框 → 服务端方案（档位阶梯 + 推荐）→ 数据层 → 作业日志轮询 */
function GeneratePanel({ state, updateState, gen, onStart }: {
  state: WizardState; updateState: (u: Partial<WizardState>) => void
  gen: GenSession; onStart: () => void
}) {
  const g = state.gis
  const setGis = (p: Partial<WizardState['gis']>) => updateState({ gis: { ...g, ...p } })
  const rec = gen.plan?.recommended.cell_km
  const customKm = num(g.cell_km)
  const activeKm = customKm ?? rec
  const running = gen.job != null && (gen.job.status === 'queued' || gen.job.status === 'running')
  const caps = gen.caps
  const notes = capsNotes(caps, g)

  return (
    <div style={{ marginTop: '10px' }}>
      <div className="row">
        {(['w', 's', 'e', 'n'] as const).map((k, i) => (
          <label key={k}>
            <span>{['西经', '南纬', '东经', '北纬'][i]}</span>
            <input type="number" step="0.01" value={g.bbox[k]}
              onChange={e => setGis({ bbox: { ...g.bbox, [k]: e.target.value } })} />
          </label>
        ))}
      </div>
      <p className="muted small">四至指六角格「格心」范围（WGS84）。单边不超过 8°，超过 3 万格会被拒绝。</p>

      <div className="row">
        <label>
          <span>六角格对边距（实地 km）</span>
          <input type="number" step="0.1" value={g.cell_km} placeholder={rec ? `推荐 ${rec}` : '正在计算…'}
            onChange={e => setGis({ cell_km: e.target.value })} />
        </label>
        <button className="btn btn-sm"
          onClick={() => {
            if (!gen.plan) return toast(gen.planErr || '正在计算格网方案，稍候', 'warn')
            setGis({ cell_km: String(gen.plan.recommended.cell_km) })
          }}>
          用推荐对边距
        </button>
      </div>

      <PlanCard gen={gen} activeKm={activeKm ?? null} customKm={customKm} onPick={km => setGis({ cell_km: String(km) })} />

      <h3 style={{ marginTop: '12px' }}>数据层</h3>
      <div className="row">
        <label>
          <span>高程</span>
          <select value={g.dem} onChange={e => setGis({ dem: e.target.value })}>
            <option value="auto">自动（本地真实 DEM 优先）</option>
            <option value="local">仅本地 DEM 栅格</option>
            <option value="synthetic">合成（离线演示）</option>
          </select>
        </label>
        <label>
          <span>土地利用/覆盖</span>
          <select value={g.landuse} onChange={e => setGis({ landuse: e.target.value })}>
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
            <input type="checkbox" checked={g[key]}
              onChange={e => setGis({ [key]: e.target.checked } as Partial<WizardState['gis']>)} />
            <span>{label}</span>
          </label>
        ))}
      </div>

      {caps && (
        <p className="muted small">
          生成引擎：<b>{caps.engine}</b>
          {caps.python_version ? ` · Python ${caps.python_version}` : ''}
          {caps.rasterio ? ' · rasterio 就绪' : ' · 缺 rasterio'}
        </p>
      )}
      {notes.map(t => <p key={t} className="warn small">{t}</p>)}

      <div className="row" style={{ marginTop: '12px', alignItems: 'flex-end' }}>
        <label>
          <span>数据包名</span>
          <input value={g.name} placeholder="留空 = 按范围自动命名" onChange={e => setGis({ name: e.target.value })} />
        </label>
        <button className="btn btn-primary" disabled={!!running || caps?.available === false} onClick={onStart}>
          {running ? '生成中…' : '生成六角格网格'}
        </button>
        {gen.job?.status === 'done' && (
          <button className="btn" onClick={() => updateState({ gis: { ...g, mode: 'existing', pkg: gen.job!.name } })}>
            改用已有数据包列表
          </button>
        )}
      </div>

      <JobCard job={gen.job} err={gen.jobErr} />
    </div>
  )
}

/// 能力降级提示：缺什么就说什么，不冒充实数据
function capsNotes(caps: GisCapabilities | null, g: WizardState['gis']): string[] {
  if (!caps) return []
  if (!caps.available) return ['本机没有可用的 Python，无法现场生成：' + (caps.reason || '未知原因')]
  const out: string[] = []
  if (!caps.rasterio) out.push('Python 缺 rasterio/numpy ⇒ 只能生成合成地形；真实栅格请 pip 安装后重试。')
  if (caps.rasterio && !caps.dem_present) out.push(`未发现本地 DEM（${caps.dem_tif || '?'}）⇒ 高程将退回合成。`)
  if (g.water && caps.water_vector_ok === false) out.push('真实水系不可用（' + (caps.water_reason || '缺 GeoPackage 或缺 geopandas') + '）⇒ 水文退回 DEM 派生河带。')
  if (g.network && caps.vector_ok === false) out.push('矢量地物读取不可用（' + (caps.vector_reason || '缺 pyogrio') + '）⇒ 真实路网卡关闭，地物只按地形派生。')
  if (g.network && caps.roads_gdb_present === false) out.push('未发现本地道路库（' + (caps.roads_gdb || '?') + '）⇒ 请先解压：7zr x data/gis/World_Roads.lpk "v10/roads.gdb/*" -odata/gis/roads_pkg -y')
  if (g.network && caps.airports_present === false) out.push('未发现机场库（' + (caps.airports_shp || '?') + '）⇒ 机场改为按地形派生一处野战场站。')
  return out
}

function PlanCard({ gen, activeKm, customKm, onPick }: {
  gen: GenSession; activeKm: number | null; customKm: number | null; onPick: (km: number) => void
}) {
  if (gen.planErr && !gen.plan) return <p className="warn small">{gen.planErr}</p>
  const p = gen.plan
  if (!p) return <p className="muted small">{gen.planBusy ? '正在计算格网方案…' : '填好范围框后自动给出推荐对边距与行列数。'}</p>
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

function JobCard({ job, err }: { job: GisJob | null; err: string }) {
  if (!job) return <p className="muted small">尚未生成。真实栅格生成约需十几秒到几分钟。</p>
  const tail = (job.log || []).slice(-9)
  const m = job.meta
  const counts = m?.terrain_counts
  return (
    <div className="card job-card">
      <div className="row">
        <div className="kv"><span>作业</span><b>{job.id}</b></div>
        <div className="kv"><span>状态</span><b>{JOB_STATUS_CN[job.status] || job.status}</b></div>
        <div className="kv"><span>产物</span><b>data/gis/{job.name}</b></div>
      </div>
      <pre className="mono job-log">{tail.join('\n') || '…'}</pre>
      {(job.error || err) && <p className="warn small">{job.error || err}</p>}
      {job.status === 'done' && m && (
        <p className="small">
          自校验 {(m.fails || 0) === 0 ? `全部通过（${m.warns || 0} 项 WARN）` : `${m.fails} 项未通过`}
          {' · '}{m.source || '?'} · {m.width}×{m.height} 格
          {counts ? ' · 地形构成 ' + JSON.stringify(counts) : ''}
        </p>
      )}
    </div>
  )
}

// Step 3: Weather
function Step3Weather({ state, updateState, weatherList }: {
  state: WizardState
  updateState: (u: Partial<WizardState> | ((prev: WizardState) => Partial<WizardState>)) => void
  weatherList: Array<{ id: string; label: string }>
}) {
  const sched = state.weather.schedule
  // 函数式更新：连点"添加时段"不会因旧闭包互相覆盖
  const setSched = (fn: (prev: WizardState['weather']['schedule']) => WizardState['weather']['schedule']) =>
    updateState(prev => ({ weather: { ...prev.weather, schedule: fn(prev.weather.schedule) } }))
  const patch = (i: number, p: Partial<{ from: string; to: string; weather: string }>) =>
    setSched(rows => rows.map((x, j) => j === i ? { ...x, ...p } : x))
  const addRow = () => setSched(rows => {
    const lastTo = rows.length ? Number(rows[rows.length - 1].to) || 0 : 0
    return [...rows, { from: String(lastTo + 1), to: String(lastTo + 3), weather: weatherList[0]?.id || 'clear' }]
  })
  const clock = state.clock
  const setClock = (p: Partial<WizardState['clock']>) =>
    updateState(prev => ({ clock: { ...prev.clock, ...p } }))
  const nf = Number(clock.night_from), nt = Number(clock.night_to)
  const cycleHint = Number.isInteger(nf) && Number.isInteger(nt) && nf >= 0 && nf <= 23 && nt >= 0 && nt <= 23 && nf !== nt
    ? `夜幕 ${clock.night_from}–${clock.night_to} 时，其前后各约 3 小时为拂晓/黄昏，其余为昼间`
    : '默认夜幕 20–6 时：拂晓 6–9 / 黄昏 17–20 / 夜间 20–6'

  return (
    <div>
      <h2>第 3 步 · 天气与演变</h2>
      <div className="row">
        <label>
          <span>初始天气</span>
          <select value={state.weather.initial}
            onChange={e => updateState({ weather: { ...state.weather, initial: e.target.value } })}>
            <option value="">（继承默认）</option>
            {weatherList.map(w => <option key={w.id} value={w.id}>{w.label}</option>)}
          </select>
        </label>
        <label>
          <span>演变模式</span>
          <select value={state.weather.mode}
            onChange={e => updateState({ weather: { ...state.weather, mode: e.target.value } })}>
            {WMODES.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </select>
        </label>
      </div>
      <p className="muted small">天气可在导演部终端逐回合手改；此处仅设定初始与默认演变。</p>

      <h3 style={{ marginTop: 12 }}>天气计划（可选）</h3>
      <p className="muted small">
        按回合区间预排多段天气，执行中每逢结算自动切换。
        副本开局只装载不执行 —— 启动 / 暂停 / 改排 / 删除都是导演部的专属操作，红蓝双方无法干预。
      </p>
      {sched.map((p, i) => (
        <div className="row" key={i}>
          <label>
            <span>起始回合</span>
            <input type="number" min={1} value={p.from} onChange={e => patch(i, { from: e.target.value })} />
          </label>
          <label>
            <span>结束回合</span>
            <input type="number" min={1} value={p.to} onChange={e => patch(i, { to: e.target.value })} />
          </label>
          <label>
            <span>天气</span>
            <select value={p.weather} onChange={e => patch(i, { weather: e.target.value })}>
              {weatherList.map(w => <option key={w.id} value={w.id}>{w.label}</option>)}
            </select>
          </label>
          <button className="btn btn-sm" onClick={() => setSched(sched.filter((_, j) => j !== i))}>删除</button>
        </div>
      ))}
      <div className="toolbar" style={{ marginTop: 6 }}>
        <button className="btn btn-sm" onClick={addRow}>＋ 添加时段</button>
      </div>
      <p className="muted small">留空的时段不会提交；区间需满足 1 ≤ 起始 ≤ 结束，且互不重叠，校验不通过会在上方报错。</p>

      <h3 style={{ marginTop: 12 }}>日夜交替</h3>
      <div className="row">
        <label>
          <span>起始时刻</span>
          <input type="number" min={0} max={23} placeholder="6" value={clock.start_hour}
            onChange={e => setClock({ start_hour: e.target.value })} />
        </label>
        <label>
          <span>每回合推进（小时）</span>
          <input type="number" min={1} max={24} placeholder="6" value={clock.turn_hours}
            onChange={e => setClock({ turn_hours: e.target.value })} />
        </label>
      </div>
      <div className="row">
        <label>
          <span>夜幕开始（时）</span>
          <input type="number" min={0} max={23} placeholder="20" value={clock.night_from}
            onChange={e => setClock({ night_from: e.target.value })} />
        </label>
        <label>
          <span>夜幕结束（时）</span>
          <input type="number" min={0} max={23} placeholder="6" value={clock.night_to}
            onChange={e => setClock({ night_to: e.target.value })} />
        </label>
      </div>
      <p className="muted small">
        时间随回合推进自动循环，拂晓/黄昏/夜间对侦察与战斗有修正。
        {cycleHint}；夜幕结束可小于开始（跨零点）。
      </p>
    </div>
  )
}

// Step 4: OOB + 兵种属性覆盖
function Step4OOB({ state, updateState, types, firstType, setOobDirty }: {
  state: WizardState
  updateState: (u: Partial<WizardState>) => void
  types: UnitTypeDef[]
  firstType: () => string
  setOobDirty: Dispatch<SetStateAction<Record<string, boolean>>>
}) {
  const patchSide = (side: 'red' | 'blue', rows: OOBRow[]) =>
    updateState({ sides: { ...state.sides, [side]: { ...state.sides[side], oob: rows } } })

  return (
    <div>
      <h2>第 4 步 · 红蓝部队编成（OOB 与兵种属性）</h2>
      <p className="muted small">
        留空即继承剧本默认兵力。一旦增删行即整体替换该阵营 OOB（服务端按列表替换语义合并）。
      </p>

      {(['red', 'blue'] as const).map(side => (
        <div key={side} style={{ marginBottom: '14px' }}>
          <div className="row" style={{ justifyContent: 'space-between', marginBottom: '6px' }}>
            <b className={`tag ${side}`}>{side === 'red' ? '红方' : '蓝方'}</b>
            <label style={{ flexDirection: 'row', alignItems: 'center', gap: '6px' }}>
              <span className="mini">点数预算</span>
              <input type="number" style={{ width: '90px' }} placeholder="继承默认"
                value={state.sides[side].budget}
                onChange={e => updateState({ sides: { ...state.sides, [side]: { ...state.sides[side], budget: e.target.value } } })} />
            </label>
            <button className="btn btn-sm" onClick={() => {
              setOobDirty(prev => ({ ...prev, [side]: true }))
              patchSide(side, [...state.sides[side].oob,
                { type: firstType(), qty: '1', q: '', r: '', training: '', experience: '', cname: '', cquality: '' }])
            }}>
              增一行
            </button>
          </div>

          {state.sides[side].oob.length > 0 ? (
            <div style={{ overflow: 'auto' }}>
              <table className="oob-table">
                <thead>
                  <tr>{['兵种', '数量', 'Q', 'R', '训练', '经验', '指挥官', '素质', ''].map(h => <th key={h}>{h}</th>)}</tr>
                </thead>
                <tbody>
                  {state.sides[side].oob.map((row, i) => (
                    <tr key={i}>
                      <td>
                        <select value={row.type}
                          onChange={e => {
                            const rows = [...state.sides[side].oob]
                            rows[i] = { ...rows[i], type: e.target.value }
                            patchSide(side, rows)
                          }}>
                          {types.map(t => <option key={t.id} value={t.id}>{t.label}·{t.points ?? '?'}点</option>)}
                        </select>
                      </td>
                      {(['qty', 'q', 'r', 'training', 'experience'] as const).map(f => (
                        <td key={f}>
                          <input className="num" type="number" placeholder={f === 'training' || f === 'experience' ? '-' : f}
                            value={row[f]}
                            onChange={e => {
                              const rows = [...state.sides[side].oob]
                              rows[i] = { ...rows[i], [f]: e.target.value }
                              patchSide(side, rows)
                            }} />
                        </td>
                      ))}
                      <td>
                        <input type="text" placeholder="姓名" value={row.cname}
                          onChange={e => {
                            const rows = [...state.sides[side].oob]
                            rows[i] = { ...rows[i], cname: e.target.value }
                            patchSide(side, rows)
                          }} />
                      </td>
                      <td>
                        <input className="num" type="number" step="0.05" placeholder="0-1" value={row.cquality}
                          onChange={e => {
                            const rows = [...state.sides[side].oob]
                            rows[i] = { ...rows[i], cquality: e.target.value }
                            patchSide(side, rows)
                          }} />
                      </td>
                      <td>
                        <span className="x" style={{ cursor: 'pointer', color: '#c85a54' }}
                          onClick={() => patchSide(side, state.sides[side].oob.filter((_, j) => j !== i))}>
                          删
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="muted small">（留空：继承剧本默认 OOB）</div>
          )}
        </div>
      ))}

      <TypeDefsEditor state={state} updateState={updateState} types={types} />
    </div>
  )
}

function TypeDefsEditor({ state, updateState, types }: {
  state: WizardState
  updateState: (u: Partial<WizardState>) => void
  types: UnitTypeDef[]
}) {
  const [pick, setPick] = useState(types[0]?.id || '')

  const addRow = () => {
    if (!pick) return toast('请选择兵种', 'err')
    if (state.typeRows.includes(pick)) return toast('该兵种已在列表里', 'err')
    updateState({ typeRows: [...state.typeRows, pick] })
  }

  return (
    <div className="typedev">
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline', marginBottom: '6px' }}>
        <b>兵种属性覆盖（机动 / 战力 / 后勤）</b>
        <div className="row" style={{ gap: '6px' }}>
          <select value={pick} onChange={e => setPick(e.target.value)}>
            {types.map(t => <option key={t.id} value={t.id}>{t.label}·{t.points ?? '?'}点</option>)}
          </select>
          <button className="btn btn-sm" onClick={addRow}>加入覆盖</button>
        </div>
      </div>
      <p className="muted small">
        留空的字段继承出厂定义；改动即按属性偏离度自动加价/减价（见右侧「兵种成交价」）。
        兵种属性是本局双方共用的装备性能基线，阵营差异请用上面的 OOB 人员水平表达。
      </p>
      {state.typeRows.length ? (
        <div style={{ overflow: 'auto' }}>
          <table className="oob-table">
            <thead>
              <tr>{['兵种', ...TYPE_ATTRS.map(([, l]) => l), ''].map(h => <th key={h}>{h}</th>)}</tr>
            </thead>
            <tbody>
              {state.typeRows.map((id, i) => {
                const base = types.find(t => t.id === id) || ({} as UnitTypeDef)
                const baseRec = base as unknown as Record<string, unknown>
                const cur = state.typeAttrs[id] || {}
                return (
                  <tr key={id}>
                    <td><b>{base.label || id}</b></td>
                    {TYPE_ATTRS.map(([k, label]) => (
                      <td key={k}>
                        <input className="num" type="number" step="0.1"
                          value={cur[k] ?? ''}
                          placeholder={String(baseRec[k] ?? '')}
                          title={`${label}：留空=出厂默认 ${baseRec[k] ?? '-'}`}
                          onChange={e => updateState({
                            typeAttrs: { ...state.typeAttrs, [id]: { ...cur, [k]: e.target.value } },
                          })} />
                      </td>
                    ))}
                    <td>
                      <button className="btn btn-sm"
                        onClick={() => updateState({ typeRows: state.typeRows.filter((_, j) => j !== i) })}>
                        移除
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="muted small">（未覆盖任何兵种：全部使用剧本/标准库定义）</div>
      )}
    </div>
  )
}

// Step 5: Review
function Step5Review({ state, overrides, onCreate, onBack }: {
  state: WizardState
  overrides: Record<string, unknown> & { __invalid?: string }
  onCreate: () => void
  onBack: () => void
}) {
  if (overrides.__invalid) {
    return (
      <div>
        <h2>第 5 步 · 审阅并创建</h2>
        <ul className="err-list"><li>{overrides.__invalid}</li></ul>
        <div className="row" style={{ marginTop: '12px' }}>
          <button className="btn" onClick={onBack}>返回修改</button>
        </div>
      </div>
    )
  }
  const body: Record<string, unknown> = { scenario: state.scenarioId }
  if (state.name.trim()) body.name = state.name.trim()
  if (Object.keys(overrides).length) body.overrides = overrides

  return (
    <div>
      <h2>第 5 步 · 审阅并创建</h2>
      <p className="muted small">以下为本局将提交的请求体（未出现即代表使用剧本默认）：</p>
      <pre className="card mono" style={{ whiteSpace: 'pre-wrap', maxHeight: '300px', overflow: 'auto' }}>
        {JSON.stringify(body, null, 2)}
      </pre>
      <div className="row" style={{ marginTop: '12px' }}>
        <button className="btn btn-primary" onClick={onCreate}>创建副本并进入导演部</button>
        <button className="btn" onClick={onBack}>返回修改</button>
      </div>
    </div>
  )
}

// Side Panel：预算/成交价/AI/校验错误（来自 /api/validate 实时回显）
function SidePanel({ state, updateState, lastValidate, types, onCreate }: {
  state: WizardState
  updateState: (u: Partial<WizardState>) => void
  lastValidate: ValidateResult | null
  types: UnitTypeDef[]
  onCreate: () => void
}) {
  const errs = lastValidate?.errors || []
  const preview = (lastValidate?.preview?.sides || {}) as Record<string, { points?: number; budget?: number; units?: number }>
  const tp = lastValidate?.type_points || {}

  const budgetRow = (s: 'red' | 'blue') => {
    const p = preview[s] || {}
    const over = p.budget != null && p.points != null && p.points > p.budget + 0.001
    return (
      <div className={`budget${over ? ' over' : ''}`}>
        <span className={`tag ${s}`}>{s === 'red' ? '红方' : '蓝方'}</span>
        <span>{p.points != null ? Math.round(p.points) : '-'} / {p.budget != null ? Math.round(p.budget) : '∞'}</span>
        <span className="mini">{(p.units || 0)} 单位</span>
      </div>
    )
  }

  const priceRows = Object.keys(tp).map(id => {
    const base: UnitTypeDef = types.find(t => t.id === id) || { id }
    const up = tp[id] > (base.points || 0)
    return (
      <div key={id} className={`price${up ? ' up' : ''}`}>
        <span>{base.label || id}</span>
        <span className="mono">{base.points != null ? base.points : '-'} → {tp[id]} 点</span>
      </div>
    )
  })

  return (
    <div>
      <div className="preview-box">
        <h3>点数预算</h3>
        {budgetRow('red')}
        {budgetRow('blue')}
      </div>

      {priceRows.length > 0 && (
        <div className="preview-box">
          <h3>兵种成交价（服务端自动计价）</h3>
          {priceRows}
        </div>
      )}

      <div className="preview-box">
        <h3>AI 代管（开局即托管，便于单端自测）</h3>
        <div className="row">
          <label className="sw">
            <input type="checkbox" checked={state.ai.red}
              onChange={e => updateState({ ai: { ...state.ai, red: e.target.checked } })} />
            <span>红方</span>
          </label>
          <label className="sw">
            <input type="checkbox" checked={state.ai.blue}
              onChange={e => updateState({ ai: { ...state.ai, blue: e.target.checked } })} />
            <span>蓝方</span>
          </label>
        </div>
      </div>

      {errs.length ? (
        <div className="preview-box">
          <h3>校验错误</h3>
          <ul className="err-list">{errs.map((e, i) => <li key={i}>{e}</li>)}</ul>
        </div>
      ) : (
        <div className="preview-box">
          <span className="muted small">{lastValidate ? '当前覆盖无校验错误' : '尚未校验'}</span>
        </div>
      )}

      <button className="btn btn-primary" style={{ width: '100%' }} onClick={onCreate}>
        创建副本并进入导演部
      </button>
    </div>
  )
}
