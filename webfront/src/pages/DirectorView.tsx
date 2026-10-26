/**
 * 导演部：全知视图 + 双方已受领指令 + 就绪态 + 导调动作（天气/AI/迷雾/全知/注入情报/跳回合）+ 强制结算/存档。
 * 数据全部来自服务端 director 视图；last_errors 是 JSON 字符串数组，渲染前解析。
 */

import { useEffect, useRef, useState } from 'react'
import HexMap from '../components/HexMap'
import { useRoom } from '../room/RoomSocket'
import { useUnitTypes, useWeatherList } from '../room/useUnitFull'
import type { Frame, WireCommand, WireDirectorView, WireFactors, WireUnit } from '../room/protocol'
import { hhmm, sideLabel } from '../room/protocol'
import { play } from '../services/sound'

const CLS_LABEL: Record<string, string> = {
  foot: '徒步', motor: '轮式', mech: '装甲', air: '航空', structure: '固定设施',
}

/// 因子链展示顺序与命名（doc §7 战力影响链，逐项解释贡献）
const FACTOR_LABEL: Array<[keyof WireFactors, string]> = [
  ['base', '火力'], ['terrain', '地形'], ['fort', '工事'], ['strength', '编制'],
  ['supply', '补给'], ['morale', '士气'], ['personnel', '兵员'], ['commander', '指挥'],
  ['weather', '天候'], ['air', '制空'], ['counter', '相克'], ['ammo', '弹药'],
]

const BATTLE_KIND: Record<string, string> = {
  combat: '突击', encounter: '遭遇', fire: '炮击', air: '空袭', strike: '打击',
}

export default function DirectorView() {
  const room = useRoom()
  const view = room.view?.kind === 'director' ? (room.view as WireDirectorView) : null
  const typeMap = useUnitTypes()
  const weatherList = useWeatherList()

  const [inject, setInject] = useState<{ side: string; unit: string }>({ side: 'red', unit: '' })
  const [newWeather, setNewWeather] = useState('')
  const [jumpTurn, setJumpTurn] = useState(1)
  const [sideFilter, setSideFilter] = useState<'all' | 'red' | 'blue'>('all')

  const units = view?.units ?? []
  const cfMax = Math.max(0.001, ...units.map(u => cfIndex(u, typeMap)))

  const turn = view?.turn ?? 0
  const askedTurn = useRef(-1)
  // 因子链只随回放帧下发（服务端结算仅推 events/state），回合推进后补取一次帧
  useEffect(() => {
    if (askedTurn.current === turn) return
    askedTurn.current = turn
    room.requestFrames()
  }, [turn, room])

  const send = (o: Record<string, unknown>) => {
    play('tap')
    room.directorCmd({ action: o.action as string, ...o })
  }

  const held = { red: 0, blue: 0 }
  Object.values(view?.node_holders ?? {}).forEach(s => {
    if (s === 'red' || s === 'blue') held[s]++
  })

  const rowsFor = (side: string) =>
    units.filter(u => u.side === side).sort((a, b) => cfIndex(b, typeMap) - cfIndex(a, typeMap))

  const nRed = units.filter(u => u.side === 'red').length
  const nBlue = units.filter(u => u.side === 'blue').length
  const shownRed = sideFilter !== 'blue' ? rowsFor('red') : []
  const shownBlue = sideFilter !== 'red' ? rowsFor('blue') : []

  return (
    <div className="terminal" style={{ gridTemplateColumns: 'minmax(280px,340px) minmax(420px,1fr) minmax(300px,370px)' }}>
      {/* Left column: Global forces */}
      <div className="col">
        <div className="panel grow">
          <h3>全局兵力</h3>
          <div className="scroll">
            <div className="mini">
              <b className="nred">红 {nRed}</b> ·{' '}
              <b className="nblue">蓝 {nBlue}</b> · 合计 {units.length}
            </div>
            <div className="urow-filter">
              {([['all', `全部 ${units.length}`], ['red', `红方 ${nRed}`], ['blue', `蓝方 ${nBlue}`]] as const).map(([key, label]) => (
                <button key={key}
                  className={`btn btn-sm${sideFilter === key ? ' btn-primary' : ''}`}
                  onClick={() => setSideFilter(key)}>{label}</button>
              ))}
            </div>
            <div className="legend mini">
              {['foot', 'motor', 'mech', 'air', 'structure'].map(c => (
                <span key={c}><i className={`cs ${c}`} />{CLS_LABEL[c]}</span>
              ))}
              <span>悬停看明细 · 点击选中</span>
            </div>
            {shownRed.length > 0 && (
              <div className="side-hd nred">红方 {shownRed.length}</div>
            )}
            {shownRed.map(u => (
              <UnitRow key={u.id} u={u} cf={cfIndex(u, typeMap)} cfMax={cfMax} typeDef={typeMap[u.type]}
                selected={inject.unit === u.id}
                onClick={() => setInject(prev => ({ ...prev, unit: u.id }))} />
            ))}
            {shownBlue.length > 0 && (
              <div className="side-hd nblue">蓝方 {shownBlue.length}</div>
            )}
            {shownBlue.map(u => (
              <UnitRow key={u.id} u={u} cf={cfIndex(u, typeMap)} cfMax={cfMax} typeDef={typeMap[u.type]}
                selected={inject.unit === u.id}
                onClick={() => setInject(prev => ({ ...prev, unit: u.id }))} />
            ))}
          </div>
        </div>
      </div>

      {/* Middle column: Map */}
      <div className="col">
        <div className="mapwrap">
          <HexMap
            view={view}
            onPick={({ q, r }) => {
              const u = units.find(x => x.q === q && x.r === r)
              if (u) setInject(prev => ({ ...prev, unit: u.id }))
            }}
          />
          <div className="maphud" id="hud">
            {view ? (
              <>
                <b>{view.name || ''}</b> · 导演部 · 回合 {view.turn}/{view.max_turns} ·{' '}
                {hhmm(view.hour)} {view.day?.label || ''} · {view.weather?.label || ''} ·{' '}
                迷雾 {view.flags?.fog ? '开' : '关'} · 要点 红 {held.red}/蓝 {held.blue}
                {view.power && <> · 电网 红×{view.power.red} 蓝×{view.power.blue}</>}
                {view.over && <> · <b>已结束 {sideLabel(view.winner)}</b></>}
              </>
            ) : '连接中…'}
          </div>
        </div>
        <div className="legend" id="legend">
          <span><i style={{ background: '#c15b4e' }} /> 红方</span>
          <span><i style={{ background: '#4e82b3' }} /> 蓝方</span>
          <span><i style={{ background: '#f0d9a5' }} /> 指挥所</span>
        </div>
      </div>

      {/* Right column: Controls & Orders & Events */}
      <div className="col">
        {room.error && (
          <div className="card warn-card">
            <span className="warn">{room.error}</span>
            <button className="btn btn-sm" style={{ marginLeft: 8 }} onClick={room.clearError}>×</button>
          </div>
        )}

        <div className="panel">
          <h3>导调控制</h3>
          <div className="scroll">
            <div className="toolbar">
              <button className="btn btn-primary" onClick={() => { play('send'); room.forceResolve() }}>强制结算</button>
              <button className="btn" onClick={() => { play('tap'); room.save('') }}>存档</button>
            </div>

            <div className="mini" style={{ margin: '4px 0' }}>
              就绪：红 {(view?.ready?.red ? '✓' : '✗')} · 蓝 {(view?.ready?.blue ? '✓' : '✗')}
            </div>

            <details className="sec" open>
              <summary>天气与天候计划</summary>
              <div className="row">
                <label>
                  <span>改天气</span>
                  <select value={newWeather || view?.weather?.id || ''} onChange={e => setNewWeather(e.target.value)}>
                    {weatherList.map(w => <option key={w.id} value={w.id}>{w.label}</option>)}
                  </select>
                </label>
                <button className="btn btn-sm" onClick={() => send({ action: 'weather', id: newWeather || view?.weather?.id })}>设定</button>
              </div>

              <WeatherScheduleCard sched={view?.weather_schedule} weatherList={weatherList} send={send} />
            </details>

            <details className="sec">
              <summary>战场开关（迷雾 / 全知 / AI）</summary>
              <div className="toolbar">
                <Toggle label="战争迷雾" on={view?.flags?.fog === true} onChange={on => send({ action: 'fog', on })} />
                <Toggle label="观战全知" on={view?.spectator_omniscient === true} onChange={on => send({ action: 'spectator_omniscient', on })} />
              </div>

              <div className="toolbar">
                <Toggle label="红方 AI 代管" on={view?.ai?.red === true} onChange={on => send({ action: 'ai', side: 'red', on })} />
                <Toggle label="蓝方 AI 代管" on={view?.ai?.blue === true} onChange={on => send({ action: 'ai', side: 'blue', on })} />
              </div>
            </details>

            <details className="sec">
              <summary>注入情报</summary>
              <div className="row">
                <select value={inject.side} onChange={e => setInject(prev => ({ ...prev, side: e.target.value }))}>
                  <option value="red">向红方</option>
                  <option value="blue">向蓝方</option>
                </select>
                <input value={inject.unit} placeholder="目标单位 id" style={{ width: '110px' }}
                  onChange={e => setInject(prev => ({ ...prev, unit: e.target.value }))} />
                <button className="btn btn-sm" onClick={() => send({ action: 'inject_intel', side: inject.side, unit: inject.unit })}>暴露</button>
              </div>
              <div className="mini">提示：点选地图单位即填入目标 id</div>
            </details>

            <details className="sec">
              <summary>跳回合（引擎暂未开放回跳）</summary>
              <div className="row">
                <input type="number" value={jumpTurn} min={1} style={{ width: '80px' }}
                  onChange={e => setJumpTurn(parseInt(e.target.value) || 1)} />
                <button className="btn btn-sm" onClick={() => send({ action: 'jump_turn', turn: jumpTurn })}>跳转</button>
              </div>
            </details>
          </div>
        </div>

        <div className="panel">
          <h3>双方已受领指令</h3>
          <div className="scroll">
            {(['red', 'blue'] as const).map(side => {
              const list = view?.orders?.[side] ?? []
              return (
                <div key={side} style={{ marginTop: side === 'blue' ? 12 : 0 }}>
                  <h3>{side === 'red' ? '红方' : '蓝方'} 指令 ({list.length})</h3>
                  {list.length ? list.map(c => (
                    <div key={c.unit} className="ordbox">
                      <div className="o">
                        <span className="mono">{c.unit}</span>
                        <span>{describeCommand(c)}</span>
                      </div>
                    </div>
                  )) : <div className="muted small">（无）</div>}
                </div>
              )
            })}
            {renderRejected(view)}
          </div>
        </div>

        <BattleReport frames={room.frames} />

        <div className="panel grow">
          <h3>事件日志</h3>
          <div className="scroll">
            {room.log.length === 0 ? (
              <div className="muted small">（无事件）</div>
            ) : (
              room.log.slice(-120).reverse().map((e, i) => (
                <div key={i} className={`ev ${e.code}`}>
                  <span className="t">T{e.turn}</span>
                  {e.text}
                </div>
              ))
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

function renderRejected(view: WireDirectorView | null) {
  const le = view?.last_errors
  if (!le) return null
  const rows: Array<[string, string]> = []
  for (const side of ['red', 'blue'] as const) {
    for (const raw of le[side] ?? []) {
      let msg = raw
      try { msg = (JSON.parse(raw) as { message?: string }).message ?? raw } catch { /* 原样显示 */ }
      rows.push([side, msg])
    }
  }
  if (!rows.length) return null
  return (
    <div>
      <h3>上轮被拒</h3>
      {rows.map(([s, m], i) => <div key={i} className="errrow">[{s}] {m}</div>)}
    </div>
  )
}

function describeCommand(c: WireCommand): string {
  switch (c.op) {
    case 'move': return '机动 ' + (c.path?.length ? `${c.path[c.path.length - 1][0]},${c.path[c.path.length - 1][1]}` : '?')
    case 'attack': return '突击 ' + (c.target ?? '?')
    case 'fire': return '炮击 ' + (c.zone ? `${c.zone[0]},${c.zone[1]}` : '?')
    case 'air_mission': return '空任务 ' + (c.mission ?? '?')
    case 'dig': return '构筑工事'
    case 'rest': return '休整'
    case 'halt': return '停止'
    default: return c.op
  }
}

/// 战力指数：复刻引擎 CF 链中与战场上下文无关的因子（不含地形/工事/天候/空权/相克）
function cfIndex(u: WireUnit, typeMap: Record<string, { personnel?: number }>): number {
  const t = typeMap[u.type] || {}
  const strength = t.personnel ? u.personnel / t.personnel : 1
  const pers = 0.75 + 0.0025 * (u.training ?? 0) + 0.0015 * (u.experience ?? 0)
  const mor = Math.max(0.4, Math.min(1.2, 0.5 + (u.morale ?? 0) / 100 * 0.7))
  const sup = Math.max(0.6, Math.min(1, 0.6 + 0.4 * (u.supply ?? 1)))
  const ammo = u.ammo <= 0 ? 0.3 : Math.max(0.3, Math.min(1, 0.5 + 0.5 * u.ammo))
  return (u.firepower ?? 0) * strength * pers * mor * sup * ammo
}

function UnitRow({ u, cf, cfMax, typeDef, selected, onClick }: {
  u: WireUnit
  cf: number
  cfMax: number
  typeDef?: { personnel?: number }
  selected: boolean
  onClick: () => void
}) {
  const band = cf >= cfMax * 0.6 ? 'hi' : cf >= cfMax * 0.3 ? 'mid' : 'lo'
  const m = u.morale ?? 0
  const mband = m >= 70 ? 'hi' : m >= 45 ? 'mid' : m >= 25 ? 'low' : 'crit'
  const detail = [
    `${u.type_label || u.type} · ${u.personnel}/${typeDef?.personnel ?? '-'}人`,
    `士气 ${m} · 战力 ${cf.toFixed(1)}（编制火力 ${u.firepower ?? 0}）`,
    `弹 ${u.ammo} 油 ${u.fuel} 粮 ${u.food} · 补给 ${u.supply}`,
    `(${u.q},${u.r}) · 训练 ${u.training} 经验 ${u.experience}${u.commander ? ' · ' + u.commander : ''}`,
  ].join('\n')

  return (
    <div
      className={`urow ${u.side === 'red' ? 'red' : u.side === 'blue' ? 'blue' : 'neutral'}${u.status === 'destroyed' ? ' dead' : ''}${selected ? ' sel' : ''}`}
      title={detail}
      onClick={onClick}
    >
      <span className="uid">{u.id}</span>
      <span className={`cls ${u.cls || 'other'}`}>{(u.type_label || u.type).slice(0, 2)}</span>
      <span className={`pwr ${band}`}>
        <span className="tk"><i style={{ width: `${Math.round(cf / cfMax * 100)}%` }} /></span>
        <b>{cf.toFixed(1)}</b>
      </span>
      <span className={`mor ${mband}`} title={`士气 ${m}`}><i />{m}</span>
      {u.status === 'destroyed' && <span className="al bad" title="状态：已歼灭">亡</span>}
      {u.status === 'routed' && <span className="al bad" title="状态：溃退">溃</span>}
      {(u.supply ?? 1) < 0.5 && <span className="al bad" title={`补给率 ${u.supply}`}>断</span>}
      {(u.supply ?? 1) >= 0.5 && (u.ammo ?? 1) < 0.35 && <span className="al warn" title={`弹药 ${u.ammo}`}>弹</span>}
      {(u.fuel ?? 1) < 0.35 && <span className="al warn" title={`油料 ${u.fuel}`}>油</span>}
      {u.fort ? <span className="al" title={`工事等级 ${u.fort}`}>固{u.fort}</span> : null}
      {u.hq && <span className="al hq" title="指挥所">指</span>}
    </div>
  )
}

function Toggle({ label, on, onChange }: { label: string; on: boolean; onChange: (v: boolean) => void }) {
  return (
    <button className={`btn btn-sm${on ? ' btn-primary' : ''}`} onClick={() => onChange(!on)}>
      {(on ? '● ' : '○ ') + label}
    </button>
  )
}

/// 天气计划：预排"回合区间 → 天气"，执行中每逢结算按区间强制切换。
/// 设置/启动/暂停/删除是导演部专属操作（服务端在 director_cmd 里挡 role）。
const SCHED_STATE: Record<string, string> = { off: '未启动', running: '执行中', paused: '已暂停' }

function WeatherScheduleCard({ sched, weatherList, send }: {
  sched?: WireDirectorView['weather_schedule']
  weatherList: Array<{ id: string; label: string }>
  send: (o: Record<string, unknown>) => void
}) {
  const [rows, setRows] = useState<Array<{ from: string; to: string; weather: string }>>([])
  const has = !!sched && sched.phases.length > 0
  const wxLabel = (id: string) => weatherList.find(w => w.id === id)?.label || id

  const submit = () => {
    const phases = rows
      .map(r => ({ from: parseInt(r.from, 10), to: parseInt(r.to, 10), weather: r.weather }))
      .filter(p => Number.isFinite(p.from) && Number.isFinite(p.to) && p.from >= 1 && p.to >= p.from)
    if (phases.length) send({ action: 'weather_schedule_set', phases })
  }

  return (
    <>
      <div className="mini" style={{ marginTop: 6 }}>
        天气计划：{has ? <b>{SCHED_STATE[sched!.state] || sched!.state}</b> : '未设置'}
        {has && sched!.state === 'paused' && <span>（天气冻结）</span>}
      </div>
      {has && (
        <div className="mini">
          {sched!.phases.map((p, i) => (
            <span key={i} style={{ marginRight: 8 }}>T{p.from_turn}–{p.to_turn} {wxLabel(p.weather)}</span>
          ))}
        </div>
      )}
      <div className="toolbar">
        <button className="btn btn-sm" disabled={!has || sched!.state === 'running'}
          onClick={() => send({ action: 'weather_schedule_start' })}>启动</button>
        <button className="btn btn-sm" disabled={sched?.state !== 'running'}
          onClick={() => send({ action: 'weather_schedule_pause' })}>暂停</button>
        <button className="btn btn-sm" disabled={!has}
          onClick={() => send({ action: 'weather_schedule_delete' })}>删除</button>
      </div>
      <details>
        <summary className="mini">设定 / 编辑计划（整组替换）</summary>
        {rows.map((r, i) => (
          <div className="row" key={i}>
            <label>
              <span>起始回合</span>
              <input type="number" min={1} value={r.from}
                onChange={e => setRows(rows.map((x, j) => j === i ? { ...x, from: e.target.value } : x))} />
            </label>
            <label>
              <span>结束回合</span>
              <input type="number" min={1} value={r.to}
                onChange={e => setRows(rows.map((x, j) => j === i ? { ...x, to: e.target.value } : x))} />
            </label>
            <label>
              <span>天气</span>
              <select value={r.weather}
                onChange={e => setRows(rows.map((x, j) => j === i ? { ...x, weather: e.target.value } : x))}>
                {weatherList.map(w => <option key={w.id} value={w.id}>{w.label}</option>)}
              </select>
            </label>
            <button className="btn btn-sm" onClick={() => setRows(rows.filter((_, j) => j !== i))}>×</button>
          </div>
        ))}
        <div className="toolbar">
          <button className="btn btn-sm"
            onClick={() => setRows([...rows, { from: '1', to: '3', weather: weatherList[0]?.id || 'clear' }])}>＋ 时段</button>
          <button className="btn btn-sm btn-primary" onClick={submit}>提交计划</button>
        </div>
      </details>
    </>
  )
}

/// 战报：取最近一帧的交战，逐因子解释 CF 从哪来（doc M6「战报可解释各系数贡献」）
function BattleReport({ frames }: { frames: Frame[] }) {
  const frame = [...frames].reverse().find(f => (f.battles?.length ?? 0) > 0)
  const battles = frame?.battles ?? []

  return (
    <div className="panel">
      <h3>战报 · 战力链{frame ? ` · 第 ${frame.turn} 回合` : ''}</h3>
      <div className="scroll" style={{ maxHeight: 220 }}>
        {battles.length === 0 ? (
          <div className="muted small">（暂无交战记录，结算后显示各因子贡献）</div>
        ) : battles.map((b, i) => (
          <div key={i} className="ordbox">
            <div className="o">
              <span className="mono">{b.atk} → {b.def}</span>
              <span>{BATTLE_KIND[b.kind] || b.kind}{b.routed ? ' · 守方溃退' : ''}</span>
            </div>
            <div className="mini">CF {b.atk_cf.toFixed(1)} : {b.def_cf.toFixed(1)} · 损 {b.atk_loss} / {b.def_loss}</div>
            <FactorRow label="攻" f={b.factors_atk} />
            <FactorRow label="守" f={b.factors_def} />
          </div>
        ))}
      </div>
    </div>
  )
}

function FactorRow({ label, f }: { label: string; f: WireFactors }) {
  return (
    <div className="frow">
      <span className="flab">{label}</span>
      {FACTOR_LABEL.map(([key, text]) => {
        const v = f[key] ?? 1
        return (
          <span key={key} className={`fcell${v > 1.001 ? ' up' : v < 0.999 ? ' dn' : ''}`} title={`${text} ×${v.toFixed(3)}`}>
            {text} {v.toFixed(2)}
          </span>
        )
      })}
      <b className="fsum">={f.cf.toFixed(1)}</b>
    </div>
  )
}
