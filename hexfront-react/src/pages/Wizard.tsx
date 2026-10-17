/**
 * 创建副本向导：5步流程
 * 1. 剧本与名称
 * 2. 战场环境
 * 3. 天气
 * 4. 部队编成
 * 5. 审阅并创建
 */

import { useState } from 'react'
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

interface WizardState {
  step: number
  scenarioId: string
  name: string
  seed: string
  env: {
    width: string
    height: string
    cell_km: string
    lon: string
    lat: string
  }
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
  weather: {
    initial: string
    mode: string
  }
  sides: {
    red: { oob: OOBRow[]; budget: string }
    blue: { oob: OOBRow[]; budget: string }
  }
  ai: {
    red: boolean
    blue: boolean
  }
}

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

export default function Wizard() {
  const [step, setStep] = useState(0)
  const [state, setState] = useState<WizardState>({
    step: 0,
    scenarioId: 'default',
    name: '',
    seed: '',
    env: {
      width: '',
      height: '',
      cell_km: '',
      lon: '',
      lat: ''
    },
    gis: {
      mode: 'existing',
      pkg: '',
      bbox: { w: '', s: '', e: '', n: '' },
      cell_km: '',
      dem: 'auto',
      landuse: 'auto',
      roads: true,
      water: true,
      network: true,
      features: true,
      name: ''
    },
    flags: Object.fromEntries(FLAGS.map(([k]) => [k, 'default'])),
    weather: {
      initial: '',
      mode: ''
    },
    sides: {
      red: { oob: [], budget: '' },
      blue: { oob: [], budget: '' }
    },
    ai: {
      red: false,
      blue: false
    }
  })

  const updateState = (updates: Partial<WizardState>) => {
    setState(prev => ({ ...prev, ...updates }))
  }

  const handleCreate = async () => {
    // TODO: Call API to create instance
    toast('创建副本功能待后端API支持', 'warn')
    // navigate('/director')
  }

  return (
    <div className="page" style={{ paddingBottom: 0 }}>
      {/* Steps bar */}
      <div className="steps">
        {STEP_TITLES.map((title, i) => (
          <div
            key={i}
            className={`st${i === step ? ' on' : ''}`}
            onClick={() => setStep(i)}
          >
            {i + 1}. {title}
          </div>
        ))}
      </div>

      {/* Main content area */}
      <div className="wiz" style={{ padding: '0 18px 18px', height: 'calc(100% - 78px)' }}>
        <div className="body card">
          {step === 0 && <Step1Scenario state={state} updateState={updateState} />}
          {step === 1 && <Step2Environment state={state} updateState={updateState} />}
          {step === 2 && <Step3Weather state={state} updateState={updateState} />}
          {step === 3 && <Step4OOB state={state} updateState={updateState} />}
          {step === 4 && <Step5Review state={state} onCreate={handleCreate} />}
        </div>

        {/* Side panel */}
        <div className="side">
          <SidePanel state={state} />
        </div>
      </div>

      {/* Navigation buttons */}
      <div style={{ padding: '18px', display: 'flex', gap: '8px', justifyContent: 'center' }}>
        {step > 0 && (
          <button className="btn" onClick={() => setStep(step - 1)}>
            上一步
          </button>
        )}
        {step < 4 ? (
          <button className="btn btn-1" onClick={() => setStep(step + 1)}>
            下一步
          </button>
        ) : (
          <button className="btn btn-primary" onClick={handleCreate}>
            创建副本并进入导演部
          </button>
        )}
      </div>
    </div>
  )
}

// Step 1: Scenario & Name
function Step1Scenario({ state, updateState }: { state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  // Mock scenarios
  const scenarios = [
    { id: 'default', name: '默认剧本' }
  ]

  return (
    <div>
      <h2>第 1 步 · 剧本与名称/种子</h2>
      <div className="row">
        <label>
          <span>剧本</span>
          <select
            value={state.scenarioId}
            onChange={(e) => updateState({ scenarioId: e.target.value })}
          >
            {scenarios.map(s => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
        </label>
        <label>
          <span>副本名称</span>
          <input
            value={state.name}
            placeholder="继承剧本名称"
            onChange={(e) => updateState({ name: e.target.value })}
          />
        </label>
        <label>
          <span>随机种子</span>
          <input
            value={state.seed}
            placeholder="留空 = 每局随机"
            onChange={(e) => updateState({ seed: e.target.value })}
          />
        </label>
      </div>
      <p className="muted small">
        不填写的字段不会覆盖剧本默认，服务端以 deepMerge 合并。
      </p>
    </div>
  )
}

// Step 2: Environment
function Step2Environment({ state, updateState }: { state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  return (
    <div>
      <h2>第 2 步 · 战场环境与六角格</h2>
      
      {/* GIS mode selection */}
      <div className="gis-box">
        <h3>战场数据与六角格网络</h3>
        <p className="muted small">
          六角格由真实栅格按范围现场生成，也可复用已生成的数据包。
        </p>
        
        <div className="row">
          <button
            className={`btn btn-sm${state.gis.mode === 'existing' ? ' btn-1' : ''}`}
            onClick={() => updateState({ gis: { ...state.gis, mode: 'existing' } })}
          >
            使用已有数据包
          </button>
          <button
            className={`btn btn-sm${state.gis.mode === 'generate' ? ' btn-1' : ''}`}
            onClick={() => updateState({ gis: { ...state.gis, mode: 'generate' } })}
          >
            按范围现场生成
          </button>
        </div>

        {state.gis.mode === 'existing' ? (
          <ExistingPackagePanel state={state} updateState={updateState} />
        ) : (
          <GeneratePackagePanel state={state} updateState={updateState} />
        )}
      </div>

      {/* Flags */}
      <h3 style={{ marginTop: '14px' }}>模式开关</h3>
      <div className="row">
        {FLAGS.map(([key, label]) => (
          <label key={key}>
            <span>{label}</span>
            <select
              value={state.flags[key]}
              onChange={(e) => updateState({
                flags: { ...state.flags, [key]: e.target.value }
              })}
            >
              <option value="default">（默认）</option>
              <option value="on">开</option>
              <option value="off">关</option>
            </select>
          </label>
        ))}
      </div>

      {/* Procedural terrain params */}
      <details style={{ marginTop: '12px' }}>
        <summary className="muted small">程序化地形参数（仅在没有任何数据包时生效）</summary>
        <div className="row" style={{ marginTop: '8px' }}>
          <label>
            <span>宽（格）</span>
            <input
              type="number"
              value={state.env.width}
              onChange={(e) => updateState({ env: { ...state.env, width: e.target.value } })}
            />
          </label>
          <label>
            <span>高（格）</span>
            <input
              type="number"
              value={state.env.height}
              onChange={(e) => updateState({ env: { ...state.env, height: e.target.value } })}
            />
          </label>
          <label>
            <span>对边距（实地 km）</span>
            <input
              type="number"
              step="0.1"
              value={state.env.cell_km}
              onChange={(e) => updateState({ env: { ...state.env, cell_km: e.target.value } })}
            />
          </label>
        </div>
        <div className="row">
          <label>
            <span>中心经度</span>
            <input
              type="number"
              step="0.01"
              value={state.env.lon}
              placeholder="如 6.95"
              onChange={(e) => updateState({ env: { ...state.env, lon: e.target.value } })}
            />
          </label>
          <label>
            <span>中心纬度</span>
            <input
              type="number"
              step="0.01"
              value={state.env.lat}
              placeholder="如 51.42"
              onChange={(e) => updateState({ env: { ...state.env, lat: e.target.value } })}
            />
          </label>
        </div>
      </details>

      <p className="muted small">
        六角格几何（原点、半径、行列数）始终取自所选数据包，与在线天地图瓦片同一墨卡托框架；对边距按实地公里计。
      </p>
    </div>
  )
}

function ExistingPackagePanel({ state, updateState }: { state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  // Mock packages
  const packages = [
    { name: 'demo_grid', cell_km: 2.5, width: 20, height: 15, cells: 300 }
  ]

  return (
    <div style={{ marginTop: '10px' }}>
      <div className="row">
        <label>
          <span>数据包</span>
          <select
            value={state.gis.pkg}
            onChange={(e) => updateState({ gis: { ...state.gis, pkg: e.target.value } })}
          >
            <option value="">（无：用程序化地形）</option>
            {packages.map(p => (
              <option key={p.name} value={p.name}>
                {p.name} · 对边距 {p.cell_km} km · {p.width}×{p.height} 格
              </option>
            ))}
          </select>
        </label>
      </div>
      {state.gis.pkg && (
        <div className="card pkg-card" style={{ marginTop: '8px' }}>
          <div className="row">
            <div className="kv">
              <span>来源</span>
              <b>本地生成</b>
            </div>
            <div className="kv">
              <span>格网</span>
              <b>{packages.find(p => p.name === state.gis.pkg)?.width}×{packages.find(p => p.name === state.gis.pkg)?.height} 格</b>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function GeneratePackagePanel({ state, updateState }: { state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  const g = state.gis
  
  return (
    <div style={{ marginTop: '10px' }}>
      <div className="row">
        <label>
          <span>西经</span>
          <input
            type="number"
            step="0.01"
            value={g.bbox.w}
            onChange={(e) => updateState({ gis: { ...g, bbox: { ...g.bbox, w: e.target.value } } })}
          />
        </label>
        <label>
          <span>南纬</span>
          <input
            type="number"
            step="0.01"
            value={g.bbox.s}
            onChange={(e) => updateState({ gis: { ...g, bbox: { ...g.bbox, s: e.target.value } } })}
          />
        </label>
        <label>
          <span>东经</span>
          <input
            type="number"
            step="0.01"
            value={g.bbox.e}
            onChange={(e) => updateState({ gis: { ...g, bbox: { ...g.bbox, e: e.target.value } } })}
          />
        </label>
        <label>
          <span>北纬</span>
          <input
            type="number"
            step="0.01"
            value={g.bbox.n}
            onChange={(e) => updateState({ gis: { ...g, bbox: { ...g.bbox, n: e.target.value } } })}
          />
        </label>
      </div>
      <p className="muted small">
        四至指六角格「格心」范围（WGS84）。单边不超过 8°，超过 3 万格会被拒绝。
      </p>
      <div className="row">
        <label>
          <span>六角格对边距（实地 km）</span>
          <input
            type="number"
            step="0.1"
            min="0.5"
            value={g.cell_km}
            placeholder="留空 = 用推荐"
            onChange={(e) => updateState({ gis: { ...g, cell_km: e.target.value } })}
          />
        </label>
      </div>
      
      <h3 style={{ marginTop: '12px' }}>数据层</h3>
      <div className="row">
        <label>
          <span>高程</span>
          <select
            value={g.dem}
            onChange={(e) => updateState({ gis: { ...g, dem: e.target.value } })}
          >
            <option value="auto">自动（本地真实 DEM 优先）</option>
            <option value="local">仅本地 DEM 栅格</option>
            <option value="synthetic">合成（离线演示）</option>
          </select>
        </label>
        <label>
          <span>土地利用/覆盖</span>
          <select
            value={g.landuse}
            onChange={(e) => updateState({ gis: { ...g, landuse: e.target.value } })}
          >
            <option value="auto">真实 WorldCover 10 m</option>
            <option value="proxy">噪声代理（无网络）</option>
            <option value="off">不参与判类</option>
          </select>
        </label>
        <label className="sw">
          <input
            type="checkbox"
            checked={g.roads}
            onChange={(e) => updateState({ gis: { ...g, roads: e.target.checked } })}
          />
          <span>道路走廊</span>
        </label>
        <label className="sw">
          <input
            type="checkbox"
            checked={g.water}
            onChange={(e) => updateState({ gis: { ...g, water: e.target.checked } })}
          />
          <span>水系</span>
        </label>
        <label className="sw">
          <input
            type="checkbox"
            checked={g.network}
            onChange={(e) => updateState({ gis: { ...g, network: e.target.checked } })}
          />
          <span>真实矢量地物</span>
        </label>
        <label className="sw">
          <input
            type="checkbox"
            checked={g.features}
            onChange={(e) => updateState({ gis: { ...g, features: e.target.checked } })}
          />
          <span>地物玩法层</span>
        </label>
      </div>
      
      <div className="row" style={{ marginTop: '12px' }}>
        <label>
          <span>数据包名</span>
          <input
            type="text"
            value={g.name}
            placeholder="留空自动加时间戳"
            onChange={(e) => updateState({ gis: { ...g, name: e.target.value } })}
          />
        </label>
        <button className="btn btn-primary" disabled>
          生成六角格网格
        </button>
      </div>
      <p className="muted small">尚未生成。真实栅格生成约需十几秒到几分钟。</p>
    </div>
  )
}

// Step 3: Weather
function Step3Weather({ state, updateState }: { state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  // Mock weather list
  const weatherList = [
    { id: 'clear', label: '晴朗' },
    { id: 'cloudy', label: '多云' },
    { id: 'rain', label: '小雨' },
    { id: 'storm', label: '暴雨' },
    { id: 'fog', label: '大雾' },
    { id: 'snow', label: '大雪' }
  ]

  return (
    <div>
      <h2>第 3 步 · 天气与演变</h2>
      <div className="row">
        <label>
          <span>初始天气</span>
          <select
            value={state.weather.initial}
            onChange={(e) => updateState({ weather: { ...state.weather, initial: e.target.value } })}
          >
            <option value="">（继承默认）</option>
            {weatherList.map(w => (
              <option key={w.id} value={w.id}>{w.label}</option>
            ))}
          </select>
        </label>
        <label>
          <span>演变模式</span>
          <select
            value={state.weather.mode}
            onChange={(e) => updateState({ weather: { ...state.weather, mode: e.target.value } })}
          >
            {WMODES.map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
        </label>
      </div>
      <p className="muted small">
        天气可在导演部终端逐回合手改；此处仅设定初始与默认演变。
      </p>
    </div>
  )
}

// Step 4: OOB
function Step4OOB({ state, updateState }: { state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  return (
    <div>
      <h2>第 4 步 · 红蓝部队编成（OOB 与兵种属性）</h2>
      <p className="muted small">
        留空即继承剧本默认兵力。一旦增删行即整体替换该阵营 OOB（服务端按列表替换语义合并）。
      </p>
      
      <OOBSection side="red" state={state} updateState={updateState} />
      <OOBSection side="blue" state={state} updateState={updateState} />
      
      <div className="typedev">
        <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline', marginBottom: '6px' }}>
          <b>兵种属性覆盖（机动 / 战力 / 后勤）</b>
        </div>
        <p className="muted small">
          留空的字段继承出厂定义；改动即按属性偏离度自动加价/减价。
          兵种属性是本局双方共用的装备性能基线，阵营差异请用上面的 OOB 人员水平表达。
        </p>
        <div className="muted small">（未覆盖任何兵种：全部使用剧本/标准库定义）</div>
      </div>
    </div>
  )
}

function OOBSection({ side, state, updateState }: { side: 'red' | 'blue'; state: WizardState; updateState: (u: Partial<WizardState>) => void }) {
  const rows = state.sides[side].oob
  
  const addRow = () => {
    const newRow: OOBRow = {
      type: 'infantry',
      qty: '1',
      q: '',
      r: '',
      training: '',
      experience: '',
      cname: '',
      cquality: ''
    }
    updateState({
      sides: {
        ...state.sides,
        [side]: { ...state.sides[side], oob: [...rows, newRow] }
      }
    })
  }

  const removeRow = (index: number) => {
    const newRows = rows.filter((_, i) => i !== index)
    updateState({
      sides: {
        ...state.sides,
        [side]: { ...state.sides[side], oob: newRows }
      }
    })
  }

  const updateRow = (index: number, field: keyof OOBRow, value: string) => {
    const newRows = [...rows]
    newRows[index] = { ...newRows[index], [field]: value }
    updateState({
      sides: {
        ...state.sides,
        [side]: { ...state.sides[side], oob: newRows }
      }
    })
  }

  // Mock unit types
  const unitTypes = [
    { id: 'infantry', label: '步兵营', points: 1.0 },
    { id: 'armor', label: '装甲营', points: 3.0 },
    { id: 'mechanized', label: '机械化步兵', points: 2.5 },
    { id: 'artillery', label: '炮兵营', points: 2.0 }
  ]

  return (
    <div style={{ marginBottom: '14px' }}>
      <div className="row" style={{ justifyContent: 'space-between', marginBottom: '6px' }}>
        <b className={`tag ${side}`}>{side === 'red' ? '红方' : '蓝方'}</b>
        <label style={{ flexDirection: 'row', alignItems: 'center', gap: '6px' }}>
          <span className="mini">点数预算</span>
          <input
            type="number"
            value={state.sides[side].budget}
            style={{ width: '90px' }}
            placeholder="继承默认"
            onChange={(e) => updateState({
              sides: {
                ...state.sides,
                [side]: { ...state.sides[side], budget: e.target.value }
              }
            })}
          />
        </label>
        <button className="btn btn-sm" onClick={addRow}>
          增一行
        </button>
      </div>
      
      {rows.length > 0 ? (
        <div style={{ overflow: 'auto' }}>
          <table className="oob-table">
            <thead>
              <tr>
                <th>兵种</th>
                <th>数量</th>
                <th>Q</th>
                <th>R</th>
                <th>训练</th>
                <th>经验</th>
                <th>指挥官</th>
                <th>素质</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={i}>
                  <td>
                    <select
                      value={row.type}
                      onChange={(e) => updateRow(i, 'type', e.target.value)}
                    >
                      {unitTypes.map(t => (
                        <option key={t.id} value={t.id}>
                          {t.label}·{t.points}点
                        </option>
                      ))}
                    </select>
                  </td>
                  <td>
                    <input
                      className="num"
                      type="number"
                      value={row.qty}
                      onChange={(e) => updateRow(i, 'qty', e.target.value)}
                    />
                  </td>
                  <td>
                    <input
                      className="num"
                      type="number"
                      value={row.q}
                      placeholder="q"
                      onChange={(e) => updateRow(i, 'q', e.target.value)}
                    />
                  </td>
                  <td>
                    <input
                      className="num"
                      type="number"
                      value={row.r}
                      placeholder="r"
                      onChange={(e) => updateRow(i, 'r', e.target.value)}
                    />
                  </td>
                  <td>
                    <input
                      className="num"
                      type="number"
                      value={row.training}
                      placeholder="-"
                      onChange={(e) => updateRow(i, 'training', e.target.value)}
                    />
                  </td>
                  <td>
                    <input
                      className="num"
                      type="number"
                      value={row.experience}
                      placeholder="-"
                      onChange={(e) => updateRow(i, 'experience', e.target.value)}
                    />
                  </td>
                  <td>
                    <input
                      type="text"
                      value={row.cname}
                      placeholder="姓名"
                      onChange={(e) => updateRow(i, 'cname', e.target.value)}
                    />
                  </td>
                  <td>
                    <input
                      className="num"
                      type="number"
                      step="0.05"
                      value={row.cquality}
                      placeholder="0-1"
                      onChange={(e) => updateRow(i, 'cquality', e.target.value)}
                    />
                  </td>
                  <td>
                    <span
                      className="x"
                      style={{ cursor: 'pointer', color: '#c85a54' }}
                      onClick={() => removeRow(i)}
                    >
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
  )
}

// Step 5: Review
function Step5Review({ state, onCreate }: { state: WizardState; onCreate: () => void }) {
  const buildPreview = () => {
    const preview: any = {
      scenario: state.scenarioId
    }
    if (state.name.trim()) preview.name = state.name.trim()
    if (state.seed) preview.seed = parseInt(state.seed)
    
    const overrides: any = {}
    if (state.gis.pkg) overrides.gis_package = state.gis.pkg
    
    if (Object.keys(state.flags).some(k => state.flags[k] !== 'default')) {
      overrides.flags = {}
      for (const [k, v] of Object.entries(state.flags)) {
        if (v !== 'default') overrides.flags[k] = v === 'on'
      }
    }
    
    if (state.weather.initial || state.weather.mode) {
      overrides.weather = {}
      if (state.weather.initial) overrides.weather.initial = state.weather.initial
      if (state.weather.mode) overrides.weather.mode = state.weather.mode
    }
    
    if (Object.keys(overrides).length) preview.overrides = overrides
    
    return preview
  }

  const preview = buildPreview()

  return (
    <div>
      <h2>第 5 步 · 审阅并创建</h2>
      <p className="muted small">
        以下为本局将提交的请求体（未出现即代表使用剧本默认）：
      </p>
      <pre className="card mono" style={{ whiteSpace: 'pre-wrap', maxHeight: '300px', overflow: 'auto' }}>
        {JSON.stringify(preview, null, 2)}
      </pre>
      <div className="row" style={{ marginTop: '12px' }}>
        <button className="btn btn-primary" onClick={onCreate}>
          创建副本并进入导演部
        </button>
        <button className="btn" onClick={() => window.location.hash = '#/lobby'}>
          返回修改
        </button>
      </div>
    </div>
  )
}

// Side Panel
function SidePanel({ state }: { state: WizardState }) {
  return (
    <div>
      {/* Points budget preview */}
      <div className="preview-box">
        <h3>点数预算</h3>
        <div className="budget">
          <span className="tag red">红方</span>
          <span>- / ∞</span>
          <span className="mini">0 单位</span>
        </div>
        <div className="budget">
          <span className="tag blue">蓝方</span>
          <span>- / ∞</span>
          <span className="mini">0 单位</span>
        </div>
      </div>

      {/* AI control */}
      <div className="preview-box">
        <h3>AI 代管（开局即托管，便于单端自测）</h3>
        <div className="row">
          <label className="sw">
            <input
              type="checkbox"
              checked={state.ai.red}
            />
            <span>红方</span>
          </label>
          <label className="sw">
            <input
              type="checkbox"
              checked={state.ai.blue}
            />
            <span>蓝方</span>
          </label>
        </div>
      </div>

      {/* Validation errors */}
      <div className="preview-box">
        <span className="muted small">当前覆盖无校验错误</span>
      </div>

      <button className="btn btn-primary" style={{ width: '100%' }} onClick={() => {}}>
        创建副本并进入导演部
      </button>
    </div>
  )
}
