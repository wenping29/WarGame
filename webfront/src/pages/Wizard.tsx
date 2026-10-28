/**
 * 创建副本向导：5步流程。核心原则——未编辑字段绝不进入 overrides，
 * 服务端把 overrides deepMerge 到剧本默认；未出现的键即"用默认"。
 * 第 2 步支持现场生成六角格：GET /api/gis/plan 出档位阶梯与服务端推荐，
 * POST /api/gis/jobs 起后台作业并 1.5s 轮询日志；生成完成时服务端自动把数据包存储为服务器网络包（data/hexgrids/<id>.zip），
 * 副本统一通过 overrides.hexgrid 引用服务器网络包的六角格。
 */

import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type CreateInstanceBody, type GisPlan, type HexGridMeta, type ScenarioInfo, type ValidateResult } from '../services/api'
import { loadMeta, useMeta, type UnitTypeDef } from '../room/useUnitFull'
import HexGenPanel from '../components/HexGenPanel'
import { lngToMx, latToMy } from '../components/GeoMapView'
import { SQRT3, metersToHex } from '../map/hex'
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
  /** 初始经纬度（度）：创建时换算成网格坐标写入 at，Q/R 直填优先 */
  lon: string
  lat: string
  training: string
  experience: string
  cname: string
  cquality: string
}

const emptyOobRow = (type = ''): OOBRow =>
  ({ type, qty: '1', q: '', r: '', lon: '', lat: '', training: '', experience: '', cname: '', cquality: '' })

/** 绑定网格包的几何（经纬度 → 网格坐标换算用）；ox/oy 口径与后端 HexGridPackageService.Build 一致 */
interface HexGeo {
  ox: number
  oy: number
  radius: number
  rows: number
  cols: number
  holes: Set<string>
}

/** 部队行初始位置：Q/R 直填优先；否则经纬度换算（需已绑定网格包）。越界/挖除格返回 error。 */
function resolveAt(row: OOBRow, geo: HexGeo | null): { at: [number, number] | null; error?: string } {
  const q = num(row.q), r = num(row.r)
  if (q !== null || r !== null) {
    if (q === null || r === null) return { at: null, error: 'Q/R 需成对填写' }
    return { at: [q, r] }
  }
  const lon = num(row.lon), lat = num(row.lat)
  if (lon === null && lat === null) return { at: null }
  if (lon === null || lat === null) return { at: null, error: '经纬度需成对填写' }
  if (!geo) return { at: null, error: '未绑定网格包，无法换算经纬度' }
  const [cq, cr] = metersToHex({ ox: geo.ox, oy: geo.oy, radius: geo.radius }, lngToMx(lon), latToMy(lat))
  if (cq < 0 || cq >= geo.cols || cr < 0 || cr >= geo.rows)
    return { at: null, error: `经纬度（${lon}, ${lat}）换算为 ${cq},${cr}，落在网格之外` }
  if (geo.holes.has(`${cq},${cr}`))
    return { at: null, error: `经纬度（${lon}, ${lat}）落在挖除格 ${cq},${cr}` }
  return { at: [cq, cr] }
}

interface WizardState {
  scenarioId: string
  name: string
  seed: string
  gis: {
    /** hexgrid = 绑定服务器网络包；generate = 现场生成（完成即存储为服务器网络包）。
     *  副本统一只用服务器网络包六角格：overrides 恒为 overrides.hexgrid，不再发 gis_package。 */
    mode: 'generate' | 'hexgrid'
    /** hexgrid 模式下选中的包 id；generate 模式下是作业完成后自动存储的服务器网络包 id（overrides.hexgrid） */
    hexPkg: string
    /** 已生成包的实际格距 km（作业完成时从 meta.cell_km 记录；用于检测"改了方案但没重新生成"） */
    pkgCellKm: string
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
  /** 日夜交替：留空即继承剧本默认（起 6 时 / 每回合 6 小时 / 夜幕 19–7） */
  clock: { start_hour: string; turn_hours: string; night_from: string; night_to: string }
  sides: { red: { oob: OOBRow[]; budget: string }; blue: { oob: OOBRow[]; budget: string } }
  typeRows: string[]
  typeAttrs: Record<string, Record<string, string>>
  ai: { red: boolean; blue: boolean }
}

function rowToOob(row: OOBRow, geo: HexGeo | null): { entry: Record<string, unknown> | null; error?: string } {
  if (!row.type) return { entry: null }
  const res = resolveAt(row, geo)
  if (res.error) return { entry: null, error: res.error }
  const out: Record<string, unknown> = { type: row.type }
  if (num(row.qty) !== null) out.qty = num(row.qty)
  if (res.at) out.at = res.at
  if (num(row.training) !== null) out.training = num(row.training)
  if (num(row.experience) !== null) out.experience = num(row.experience)
  if (row.cname && row.cname.trim()) {
    const commander: Record<string, unknown> = { name: row.cname.trim() }
    if (num(row.cquality) !== null) commander.quality = num(row.cquality)
    out.commander = commander
  }
  return { entry: out }
}

/// 剧本卡片点击：剧本内数据整体回填到向导各步表单
/// （剧本、副本名称、战场环境、部队编制、天气、日夜交替、模式开关）
function scenarioToState(s: ScenarioInfo, prev: WizardState): WizardState {
  const str = (v: unknown) => (v == null ? '' : String(v))
  const fmt = (v: unknown) => (typeof v === 'number' ? String(Math.round(v * 1000) / 1000) : '')
  const env = (s.environment || {}) as Record<string, unknown>
  const center = env.center_lonlat as [number, number] | undefined
  const flagsIn = (s.flags || {}) as Record<string, unknown>
  const wx = (s.weather || {}) as Record<string, unknown>
  const ck = (s.clock || {}) as Record<string, unknown>
  const rules = (s.rules || {}) as Record<string, unknown>
  const schedule = Array.isArray(wx.schedule)
    ? (wx.schedule as Array<Record<string, unknown>>).map(p => ({
        from: str(p.from), to: str(p.to), weather: str(p.weather),
      }))
    : []
  const sideCfg = (side: 'red' | 'blue') => {
    const raw = ((s.sides || {}) as Record<string, Record<string, unknown>>)[side] || {}
    const oob = Array.isArray(raw.oob) ? (raw.oob as Array<Record<string, unknown>>) : []
    const rows: OOBRow[] = oob.map(e => {
      const at = Array.isArray(e.at) ? (e.at as unknown[]) : []
      const cmd = (e.commander || {}) as Record<string, unknown>
      return {
        type: str(e.type), qty: str(e.qty ?? 1),
        q: str(at[0]), r: str(at[1]), lon: '', lat: '',
        training: str(e.training), experience: str(e.experience),
        cname: str(cmd.name), cquality: str(cmd.quality),
      }
    })
    return { oob: rows, budget: str(raw.points_budget) }
  }
  const red = sideCfg('red'), blue = sideCfg('blue')
  return {
    ...prev,
    scenarioId: s.id || s.file,
    name: s.name || '',
    gis: {
      ...prev.gis,
      // 默认使用服务器网络包；hexPkg 优先取剧本内绑定的网格包 id（步骤 2 载入列表后按 id 匹配校验）
      mode: 'hexgrid',
      hexPkg: s.hexgrid || '',
      pkgCellKm: '',
      bbox: center
        ? { w: fmt(center[0] - 0.95), e: fmt(center[0] + 0.95), s: fmt(center[1] - 0.55), n: fmt(center[1] + 0.55) }
        : prev.gis.bbox,
    },
    flags: Object.fromEntries(FLAGS.map(([k]) => [
      k, typeof flagsIn[k] === 'boolean' ? (flagsIn[k] ? 'on' : 'off') : 'default',
    ])),
    weather: { initial: str(wx.initial), mode: str(wx.mode), schedule },
    clock: {
      start_hour: str(ck.start_hour),
      turn_hours: str(rules.turn_hours),
      night_from: str(ck.night_from), night_to: str(ck.night_to),
    },
    sides: { red, blue },
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
    gis: {
      mode: 'hexgrid', hexPkg: '', pkgCellKm: '',
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

  // meta 到达后用第一个剧本整体回填
  useEffect(() => {
    if (!meta || state.scenarioId) return
    const s = meta.scenarios[0]
    if (!s) return
    setState(prev => scenarioToState(s, prev))
    setOobDirty({
      red: ((s.sides as Record<string, Record<string, unknown>> | null)?.red?.oob as unknown[] | undefined)?.length ? true : false,
      blue: ((s.sides as Record<string, Record<string, unknown>> | null)?.blue?.oob as unknown[] | undefined)?.length ? true : false,
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meta, state.scenarioId])

  // 点击剧本卡片：整体回填 + 标记 OOB 已装载（所见即所得）
  const applyScenario = (s: ScenarioInfo) => {
    setState(prev => scenarioToState(s, prev))
    setOobDirty({
      red: ((s.sides as Record<string, Record<string, unknown>> | null)?.red?.oob as unknown[] | undefined)?.length ? true : false,
      blue: ((s.sides as Record<string, Record<string, unknown>> | null)?.blue?.oob as unknown[] | undefined)?.length ? true : false,
    })
    toast(`已载入剧本「${s.name || s.id}」：环境/编制/天气已回填`)
  }

  const u = (updates: Partial<WizardState> | ((prev: WizardState) => Partial<WizardState>)) =>
    setState(prev => ({ ...prev, ...(typeof updates === 'function' ? updates(prev) : updates) }))

  // ── 现场生成六角格：能力探测 / 格网方案 / 作业轮询 ──
  // 现场生成的会话逻辑（caps/方案/作业/轮询）由公共组件 HexGenPanel 内部持有；
  // 这里只保留推荐方案引用，供创建前的一致性校验使用
  const [genPlan, setGenPlan] = useState<GisPlan | null>(null)

  // ── 绑定网格包几何：部队编成经纬度 → 网格坐标换算（与后端 Build 同口径） ──
  const [hexGeo, setHexGeo] = useState<HexGeo | null>(null)
  useEffect(() => {
    let dead = false
    setHexGeo(null)
    if (!state.gis.hexPkg) return
    api.hexgridGet(state.gis.hexPkg)
      .then(({ grid }) => {
        if (dead) return
        const radius = grid.flat_to_flat / SQRT3
        setHexGeo({
          ox: lngToMx(grid.west) + (SQRT3 / 2) * radius,
          oy: latToMy(grid.south) + radius,
          radius,
          rows: grid.rows,
          cols: grid.cols,
          holes: new Set(grid.holes || []),
        })
      })
      .catch(() => { if (!dead) setHexGeo(null) })
    return () => { dead = true }
  }, [state.gis.hexPkg])

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
    // 六角格棋盘恒来自服务器网络包：hexgrid 模式选包；generate 模式作业完成时服务端已自动存储（hexPkg = 发布 id）
    if (!st.gis.hexPkg) {
      return {
        __invalid: st.gis.mode === 'generate'
          ? '战场数据尚未生成：请先在「战场环境」里点「生成六角格网格」并等待完成'
          : '请选择服务器六角格网络包',
      }
    }
    ov.hexgrid = st.gis.hexPkg
    // 一致性校验：已生成包的格距必须与当前选择的方案一致，否则副本仍按旧格距生成
    if (st.gis.mode === 'generate') {
      const wantKm = num(st.gis.cell_km) ?? (genPlan?.recommended.cell_km ?? null)
      const gotKm = num(st.gis.pkgCellKm)
      if (wantKm !== null && gotKm !== null && Math.abs(wantKm - gotKm) > 1e-6) {
        return { __invalid: `已生成的数据包是 ${gotKm} km 格距，与当前选择的 ${wantKm} km 不一致：请回到「战场环境」重新点「生成六角格网格」` }
      }
    }
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
    // 向导改过的 OOB 里 at 已是实际棋盘绝对格号（经纬度当场换算过），必须声明口径，否则服务端会按剧本小棋盘再映射一次
    let oobRewritten = false
    for (const side of ['red', 'blue'] as const) {
      const cfg: Record<string, unknown> = {}
      const b = num(st.sides[side].budget)
      if (b !== null) cfg.points_budget = b
      if (oobDirty[side]) {
        const sideName = side === 'red' ? '红方' : '蓝方'
        const oob: Record<string, unknown>[] = []
        for (let i = 0; i < st.sides[side].oob.length; i++) {
          const { entry, error } = rowToOob(st.sides[side].oob[i], hexGeo)
          if (error) return { __invalid: `${sideName}部队第 ${i + 1} 行：${error}` }
          if (entry) oob.push(entry)
        }
        if (!oob.length) return { __invalid: `阵营 ${side} 的 OOB 为空（若要保留默认请不要提交该字段）` }
        cfg.oob = oob
        oobRewritten = true
      }
      if (Object.keys(cfg).length) sides[side] = cfg
    }
    if (Object.keys(sides).length) ov.sides = sides
    if (oobRewritten) ov.oob_space = 'board'
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
  }, [state.weather.initial, state.weather.mode, state.weather.schedule, state.clock, state.gis.hexPkg, state.flags,
      state.sides, state.typeRows, state.typeAttrs, state.scenarioId, oobDirty, hexGeo])

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

              {/* 上栏：名称与选中剧本 */}
              <div className="scn-top">
                <div className="row">
                  <label>
                    <span>副本名称</span>
                    <input value={state.name} placeholder="继承剧本名称" onChange={e => u({ name: e.target.value })} />
                  </label>
                  <label>
                    <span>随机种子</span>
                    <input value={state.seed} placeholder="留空 = 每局随机" onChange={e => u({ seed: e.target.value })} />
                  </label>
                </div>
                {(() => {
                  const cur = (meta?.scenarios || []).find(x => (x.id || x.file) === state.scenarioId)
                  if (!cur) return <p className="muted small">尚未选择剧本：请在下方卡片中点选。</p>
                  return (
                    <div className="card scn-current">
                      <div className="scn-current-head">
                        <b>{cur.name || cur.id}</b>
                        {cur.era && <span className="tag">{cur.era}</span>}
                        {cur.scale && <span className="tag">{cur.scale}</span>}
                      </div>
                      {cur.description && <p className="muted small scn-desc">{cur.description}</p>}
                      <div className="row scn-current-meta">
                        <span className="mini">回合 {cur.max_turns ?? '?'}</span>
                        <span className="mini">单位 {cur.unit_count ?? '?'}</span>
                        {cur.gis_package && <span className="mini">数据包 {cur.gis_package}</span>}
                      </div>
                    </div>
                  )
                })()}
                <p className="muted small">
                  点击下方卡片即载入该剧本：副本名称、战场环境、部队编制、天气与日夜交替一并按剧本数据回填；之后在各步可继续调整。
                </p>
              </div>

              {/* 下栏：已知剧本卡片墙（按战争分期分组） */}
              <h3 style={{ marginTop: 10 }}>可选剧本</h3>
              {[...(() => {
                const groups = new Map<string, ScenarioInfo[]>()
                for (const s of meta?.scenarios || []) {
                  const era = s.era || '其他'
                  if (!groups.has(era)) groups.set(era, [])
                  groups.get(era)!.push(s)
                }
                return [...groups.entries()]
              })()].map(([era, list]) => (
                <div key={era} style={{ marginBottom: '10px' }}>
                  <div className="scn-era">{era}</div>
                  <div className="scn-cards">
                    {list.map(s => {
                      const sel = (s.id || s.file) === state.scenarioId
                      return (
                        <button key={s.id || s.file} type="button"
                          className={`scn-card${sel ? ' on' : ''}`}
                          title={s.description || s.name}
                          onClick={() => applyScenario(s)}>
                          <b className="scn-card-name">{s.name || s.id}</b>
                          <span className="scn-card-meta">
                            {s.scale ? s.scale + ' · ' : ''}回合 {s.max_turns ?? '?'} · 单位 {s.unit_count ?? '?'}
                          </span>
                          {s.description && <span className="scn-card-desc">{s.description}</span>}
                        </button>
                      )
                    })}
                  </div>
                </div>
              ))}
            </div>
          )}

          {step === 1 && <Step2Environment state={state} updateState={u} onPlanChange={setGenPlan} />}
          {step === 2 && <Step3Weather state={state} updateState={u} weatherList={weatherList} />}
          {step === 3 && (
            <Step4OOB
              state={state}
              updateState={u}
              types={types}
              firstType={firstType}
              setOobDirty={setOobDirty}
              geo={hexGeo}
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
function Step2Environment({ state, updateState, onPlanChange }: {
  state: WizardState; updateState: (u: Partial<WizardState> | ((prev: WizardState) => Partial<WizardState>)) => void
  onPlanChange: (p: GisPlan | null) => void
}) {
  const g = state.gis
  const setGis = (p: Partial<WizardState['gis']>) => updateState({ gis: { ...g, ...p } })
  const [hgList, setHgList] = useState<HexGridMeta[]>([])
  const loadHgList = () => api.hexgrids().then(r => setHgList(r.grids || [])).catch(() => {})
  useEffect(() => {
    // 默认选中服务器网络包：优先保持剧本内绑定的网格包 id（在列表中按 id 匹配）；
    // 未绑定或绑定的包已不存在时，回退选中第一个可用包
    api.hexgrids().then(r => {
      const grids = r.grids || []
      setHgList(grids)
      if (!grids.length) return
      updateState(prev => {
        const cur = prev.gis.hexPkg
        if (cur && grids.some(g => g.id === cur)) return {}
        return { gis: { ...prev.gis, mode: 'hexgrid', hexPkg: grids[0].id } }
      })
    }).catch(() => {})
  }, [])

  return (
    <div>
      <h2>第 2 步 · 战场环境与六角格</h2>

      <div className="gis-box">
        <h3>战场数据与六角格网络</h3>
        <p className="muted small">
          副本统一使用服务器网络包的六角格：可直接绑定已有包，或按范围现场生成（完成时自动存储为服务器网络包）。
        </p>

        <div className="row">
          <button
            className={`btn btn-sm${g.mode === 'hexgrid' ? ' btn-1' : ''}`}
            onClick={() => updateState({ gis: { ...g, mode: 'hexgrid' } })}
          >
            服务器网络包
          </button>
          <button
            className={`btn btn-sm${g.mode === 'generate' ? ' btn-1' : ''}`}
            onClick={() => updateState({ gis: { ...g, mode: 'generate' } })}
          >
            按范围现场生成
          </button>
        </div>

        {g.mode === 'hexgrid' ? (
          <div style={{ marginTop: '10px' }}>
            <div className="row">
              <label>
                <span>服务器网络包</span>
                <select value={g.hexPkg} onChange={e => updateState({ gis: { ...g, hexPkg: e.target.value } })}>
                  <option value="">（选择服务器网络包…）</option>
                  {hgList.map(h => (
                    <option key={h.id} value={h.id}>
                      {h.name}（{h.rows}×{h.cols} · 对边距 {h.flat_to_flat}m · {h.id}{h.used_by.length ? ' · 已被引用' : ''}）
                    </option>
                  ))}
                </select>
              </label>
            </div>
            {g.hexPkg && (() => {
              const h = hgList.find(x => x.id === g.hexPkg)
              return (
                <div className="card pkg-card" style={{ marginTop: '8px' }}>
                  <div className="kv">
                    <span>格网</span><b>{h ? `${h.rows}×${h.cols} 格` : g.hexPkg}</b>
                  </div>
                  {h && (
                    <>
                      <div className="kv"><span>对边距</span><b>{h.flat_to_flat} m</b></div>
                      <div className="kv"><span>挖格 / 涂装</span><b>{h.holes_count} / {h.terrains_count}</b></div>
                    </>
                  )}
                </div>
              )
            })()}
          </div>
        ) : (
          <HexGenPanel
            form={g}
            onChange={p => setGis(p)}
            onDone={j => { if (j.status === 'done') loadHgList() }}
            onPublished={(id, j) => {
              // 服务端已发布为服务器网络包；刷新 meta 供其他剧本引用，并记录实际格距供一致性校验
              void loadMeta(true).catch(() => {})
              setGis({ hexPkg: id, pkgCellKm: String(j.meta?.cell_km ?? '') })
              toast(`六角格已生成并存储到服务器：${id}（${j.meta?.width ?? '?'}×${j.meta?.height ?? '?'} 格）`)
            }}
            onPlanChange={onPlanChange}
            publishedKm={g.pkgCellKm}
            publishedHint={id => `已存储为服务器网络包：${id}——创建副本时自动引用，无需再选。`}
          />
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

      <p className="muted small">
        六角格几何（原点、对边距、行列数）与地形、高程、道路水系等地物全部取自服务器网络包；
        现场生成完成后自动存储为服务器网络包，创建的副本只引用服务器网络包的 id。
      </p>
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
    : '默认夜幕 19–7 时：拂晓 7–10 / 黄昏 16–19 / 夜间 19–7'

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
          <button className="btn btn-sm" onClick={() => setSched(prev => prev.filter((_, j) => j !== i))}>删除</button>
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
          <input type="number" min={0} max={23} placeholder="19" value={clock.night_from}
            onChange={e => setClock({ night_from: e.target.value })} />
        </label>
        <label>
          <span>夜幕结束（时）</span>
          <input type="number" min={0} max={23} placeholder="7" value={clock.night_to}
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
function Step4OOB({ state, updateState, types, firstType, setOobDirty, geo }: {
  state: WizardState
  updateState: (u: Partial<WizardState>) => void
  types: UnitTypeDef[]
  firstType: () => string
  setOobDirty: Dispatch<SetStateAction<Record<string, boolean>>>
  geo: HexGeo | null
}) {
  const patchSide = (side: 'red' | 'blue', rows: OOBRow[]) =>
    updateState({ sides: { ...state.sides, [side]: { ...state.sides[side], oob: rows } } })

  return (
    <div>
      <h2>第 4 步 · 红蓝部队编成（OOB 与兵种属性）</h2>
      <p className="muted small">
        留空即继承剧本默认兵力。一旦增删行即整体替换该阵营 OOB（服务端按列表替换语义合并）。
        初始位置可直填 Q/R，或填经纬度（度，WGS84）——创建时自动换算成网格坐标存入副本；两种都填时 Q/R 优先。
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
              patchSide(side, [...state.sides[side].oob, emptyOobRow(firstType())])
            }}>
              增一行
            </button>
          </div>

          {state.sides[side].oob.length > 0 ? (
            <div style={{ overflow: 'auto' }}>
              <table className="oob-table">
                <thead>
                  <tr>{['兵种', '数量', 'Q', 'R', '经度', '纬度', '初始格', '训练', '经验', '指挥官', '素质', ''].map(h => <th key={h}>{h}</th>)}</tr>
                </thead>
                <tbody>
                  {state.sides[side].oob.map((row, i) => {
                    const setRow = (patch: Partial<OOBRow>) => {
                      const rows = [...state.sides[side].oob]
                      rows[i] = { ...rows[i], ...patch }
                      patchSide(side, rows)
                    }
                    const at = resolveAt(row, geo)
                    return (
                    <tr key={i}>
                      <td>
                        <select value={row.type}
                          onChange={e => setRow({ type: e.target.value })}>
                          {types.map(t => <option key={t.id} value={t.id}>{t.label}·{t.points ?? '?'}点</option>)}
                        </select>
                      </td>
                      {(['qty', 'q', 'r'] as const).map(f => (
                        <td key={f}>
                          <input className="num" type="number" placeholder={f}
                            value={row[f]}
                            onChange={e => setRow({ [f]: e.target.value } as Partial<OOBRow>)} />
                        </td>
                      ))}
                      <td>
                        <input className="num" type="number" step="0.0001" placeholder="经度" title="初始经度（度），创建时换算为网格坐标"
                          value={row.lon}
                          onChange={e => setRow({ lon: e.target.value })} />
                      </td>
                      <td>
                        <input className="num" type="number" step="0.0001" placeholder="纬度" title="初始纬度（度），创建时换算为网格坐标"
                          value={row.lat}
                          onChange={e => setRow({ lat: e.target.value })} />
                      </td>
                      <td>
                        {at.error ? (
                          <span style={{ color: '#c85a54', whiteSpace: 'nowrap' }} title={at.error}>⚠ 无效</span>
                        ) : at.at ? (
                          <code className="mono small">{at.at[0]},{at.at[1]}</code>
                        ) : (
                          <span className="muted small">-</span>
                        )}
                      </td>
                      {(['training', 'experience'] as const).map(f => (
                        <td key={f}>
                          <input className="num" type="number" placeholder="-"
                            value={row[f]}
                            onChange={e => setRow({ [f]: e.target.value } as Partial<OOBRow>)} />
                        </td>
                      ))}
                      <td>
                        <input type="text" placeholder="姓名" value={row.cname}
                          onChange={e => setRow({ cname: e.target.value })} />
                      </td>
                      <td>
                        <input className="num" type="number" step="0.05" placeholder="0-1" value={row.cquality}
                          onChange={e => setRow({ cquality: e.target.value })} />
                      </td>
                      <td>
                        <span className="x" style={{ cursor: 'pointer', color: '#c85a54' }}
                          onClick={() => patchSide(side, state.sides[side].oob.filter((_, j) => j !== i))}>
                          删
                        </span>
                      </td>
                    </tr>
                    )
                  })}
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
