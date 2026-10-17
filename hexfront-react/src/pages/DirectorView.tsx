/**
 * 导演部：全知视图 + 双方已受领指令 + 就绪态 + 导调动作（天气/AI/迷雾/全知/注入情报/跳回合）+ 强制结算/存档。
 * 数据全部来自服务端 director 视图；last_errors 是 JSON 字符串数组，渲染前解析。
 */

import { useState } from 'react'
import HexMap, { type MapUnit } from '../components/HexMap'
import { useRoom } from '../room/RoomSocket'
import { useUnitTypes, useWeatherList } from '../room/useUnitFull'
import type { WireCommand, WireDirectorView, WireUnit } from '../room/protocol'
import { play } from '../services/sound'

const CLS_LABEL: Record<string, string> = {
  foot: '徒步', motor: '轮式', mech: '装甲', air: '航空', structure: '固定设施',
}

const hhmm = (h: number) => `${String(Math.floor(h)).padStart(2, '0')}:${String(Math.round((h % 1) * 60)).padStart(2, '0')}`

export default function DirectorView() {
  const room = useRoom()
  const view = room.view?.kind === 'director' ? (room.view as WireDirectorView) : null
  const typeMap = useUnitTypes()
  const weatherList = useWeatherList()

  const [inject, setInject] = useState<{ side: string; unit: string }>({ side: 'red', unit: '' })
  const [newWeather, setNewWeather] = useState('')
  const [jumpTurn, setJumpTurn] = useState(1)

  const units = view?.units ?? []
  const cfMax = Math.max(0.001, ...units.map(u => cfIndex(u, typeMap)))

  const mapUnits: MapUnit[] = units.map(u => {
    const full = typeMap[u.type]?.personnel ?? 0
    const hp = full > 0 ? Math.max(1, Math.min(100, Math.round(u.personnel / full * 100))) : 50
    return { id: u.id, side: u.side, type: u.type, q: u.q, r: u.r, hp, morale: u.morale, fatigue: u.fatigue }
  })

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

  return (
    <div className="terminal" style={{ gridTemplateColumns: 'minmax(280px,340px) minmax(420px,1fr) minmax(260px,320px)' }}>
      {/* Left column: Global forces */}
      <div className="col">
        <div className="panel grow">
          <h3>全局兵力</h3>
          <div className="scroll">
            <div className="mini">
              <b className="nred">红 {units.filter(u => u.side === 'red').length}</b> ·{' '}
              <b className="nblue">蓝 {units.filter(u => u.side === 'blue').length}</b> · 合计 {units.length}
            </div>
            <div className="legend mini">
              {['foot', 'motor', 'mech', 'air', 'structure'].map(c => (
                <span key={c}><i className={`cs ${c}`} />{CLS_LABEL[c]}</span>
              ))}
              <span>兵种 ｜ 战力条 · 士气点按档位着色 ｜ 悬停看明细</span>
            </div>
            {rowsFor('red').map(u => (
              <UnitRow key={u.id} u={u} cf={cfIndex(u, typeMap)} cfMax={cfMax} typeDef={typeMap[u.type]}
                selected={inject.unit === u.id}
                onClick={() => setInject(prev => ({ ...prev, unit: u.id }))} />
            ))}
            {rowsFor('blue').map(u => (
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
            width={view?.width ?? 10}
            height={view?.height ?? 10}
            cells={view?.cells ?? []}
            units={mapUnits}
            onHexClick={(q, r) => {
              const u = units.find(x => x.q === q && x.r === r)
              if (u) setInject(prev => ({ ...prev, unit: u.id }))
            }}
            onUnitClick={(u) => setInject(prev => ({ ...prev, unit: u.id }))}
          />
          <div className="maphud" id="hud">
            {view ? (
              <>
                <b>{view.name || ''}</b> · 导演部 · 回合 {view.turn}/{view.max_turns} ·{' '}
                {hhmm(view.hour)} {view.day?.label || ''} · {view.weather?.label || ''} ·{' '}
                迷雾 {view.flags?.fog ? '开' : '关'} · 要点 红 {held.red}/蓝 {held.blue}
                {view.power && <> · 电网 红×{view.power.red} 蓝×{view.power.blue}</>}
                {view.over && <> · <b>已结束 {view.winner || ''}</b></>}
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

            <div className="row">
              <label>
                <span>改天气</span>
                <select value={newWeather || view?.weather?.id || ''} onChange={e => setNewWeather(e.target.value)}>
                  {weatherList.map(w => <option key={w.id} value={w.id}>{w.label}</option>)}
                </select>
              </label>
              <button className="btn btn-sm" onClick={() => send({ action: 'weather', id: newWeather || view?.weather?.id })}>设定</button>
            </div>

            <div className="toolbar">
              <Toggle label="战争迷雾" on={view?.flags?.fog === true} onChange={on => send({ action: 'fog', on })} />
              <Toggle label="观战全知" on={view?.spectator_omniscient === true} onChange={on => send({ action: 'spectator_omniscient', on })} />
            </div>

            <div className="toolbar">
              <Toggle label="红方 AI 代管" on={view?.ai?.red === true} onChange={on => send({ action: 'ai', side: 'red', on })} />
              <Toggle label="蓝方 AI 代管" on={view?.ai?.blue === true} onChange={on => send({ action: 'ai', side: 'blue', on })} />
            </div>

            <div className="mini" style={{ margin: '4px 0' }}>
              就绪：红 {(view?.ready?.red ? '✓' : '✗')} · 蓝 {(view?.ready?.blue ? '✓' : '✗')}
            </div>

            <h3 style={{ marginTop: '8px' }}>注入情报</h3>
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

            <h3 style={{ marginTop: '8px' }}>跳回合（引擎暂未开放回跳）</h3>
            <div className="row">
              <input type="number" value={jumpTurn} min={1} style={{ width: '80px' }}
                onChange={e => setJumpTurn(parseInt(e.target.value) || 1)} />
              <button className="btn btn-sm" onClick={() => send({ action: 'jump_turn', turn: jumpTurn })}>跳转</button>
            </div>
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
