/**
 * 创建副本向导：5步流程。核心原则——未编辑字段绝不进入 overrides，
 * 服务端把 overrides deepMerge 到剧本默认；未出现的键即"用默认"。
 * 现场生成数据包（GIS 作业）尚未接入 React 端：generate 模式下需先在大厅/旧前端生成，
 * 或改用已有数据包。
 */

import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type CreateInstanceBody, type ValidateResult } from '../services/api'
import { useMeta, type UnitTypeDef } from '../room/useUnitFull'
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

interface WizardState {
  scenarioId: string
  name: string
  seed: string
  env: { width: string; height: string; cell_km: string; lon: string; lat: string }
  gis: {
    mode: 'existing' | 'generate'
    pkg: string
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
  weather: { initial: string; mode: string }
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
      mode: 'existing', pkg: '',
      bbox: { w: '', s: '', e: '', n: '' },
      cell_km: '', dem: 'auto', landuse: 'auto',
      roads: true, water: true, network: true, features: true, name: '',
    },
    flags: Object.fromEntries(FLAGS.map(([k]) => [k, 'default'])),
    weather: { initial: '', mode: '' },
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
      gis: { ...prev.gis, bbox: { w: ed.w, s: ed.s, e: ed.n, n: ed.n } },
    }))
  }, [meta, state.scenarioId])

  const u = (updates: Partial<WizardState>) => setState(prev => ({ ...prev, ...updates }))

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
    const pkg = st.gis.mode === 'existing' ? st.gis.pkg : ''
    if (st.gis.mode === 'generate') {
      return { __invalid: '战场数据尚未生成：React 端现场生成未接入，请改用已有数据包或旧版向导' }
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
    if (Object.keys(wx).length) ov.weather = wx
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
      toast('副本已创建 · 房间 ' + res.code)
      navigate(`/room/${res.code}/director`)
    } else {
      toast(res.error || res.errors?.join(';') || '创建失败', 'err')
    }
  }

  // 覆盖字段变化后自动重校验（步骤≥1）
  useEffect(() => {
    if (step >= 1 && state.scenarioId) doValidate()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.weather.initial, state.weather.mode, state.gis.pkg, state.flags,
      state.sides, state.typeRows, state.typeAttrs, state.scenarioId, oobDirty])

  return (
    <div className="page" style={{ paddingBottom: 0 }}>
      <div className="steps">
        {STEP_TITLES.map((title, i) => (
          <div key={i} className={`st${i === step ? ' on' : ''}`} onClick={() => setStep(i)}>
            {i + 1}. {title}
          </div>
        ))}
      </div>

      <div className="wiz" style={{ padding: '0 18px 18px', height: 'calc(100% - 78px)' }}>
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
                        gis: { ...state.gis, bbox: { w: ed.w, s: ed.s, e: ed.n, n: ed.n } },
                      })
                    }}
                  >
                    {(meta?.scenarios || []).map(s => (
                      <option key={s.id || s.file} value={s.id || s.file}>{s.name || s.id}</option>
                    ))}
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

          {step === 1 && <Step2Environment state={state} updateState={u} />}
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

      <div style={{ padding: '18px', display: 'flex', gap: '8px', justifyContent: 'center' }}>
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
function Step2Environment({ state, updateState }: {
  state: WizardState; updateState: (u: Partial<WizardState>) => void
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
          <div style={{ marginTop: '10px' }}>
            <div className="row">
              {(['w', 's', 'e', 'n'] as const).map((k, i) => (
                <label key={k}>
                  <span>{['西经', '南纬', '东经', '北纬'][i]}</span>
                  <input type="number" step="0.01" value={g.bbox[k]}
                    onChange={e => updateState({ gis: { ...g, bbox: { ...g.bbox, [k]: e.target.value } } })} />
                </label>
              ))}
            </div>
            <p className="muted small">
              现场生成六角格尚未接入 React 端（后续步骤补齐），请切回「使用已有数据包」。
            </p>
          </div>
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

// Step 3: Weather
function Step3Weather({ state, updateState, weatherList }: {
  state: WizardState
  updateState: (u: Partial<WizardState>) => void
  weatherList: Array<{ id: string; label: string }>
}) {
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
