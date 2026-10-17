/**
 * 导演部：全知视图 + 双方已受领指令 + 就绪态 + 导调动作（天气/AI/迷雾/全知/注入情报/跳回合）+ 强制结算/存档。
 */

import { useState } from 'react'
import HexMap from '../components/HexMap'
import { useGameStore } from '../store/gameStore'
import type { Unit as StoreUnit } from '../store/gameStore'

interface EventLog {
  turn: number
  code: string
  text: string
}

interface InjectState {
  side: 'red' | 'blue'
  unit: string
}

export default function DirectorView() {
  const { 
    turn, 
    maxTurns, 
    units, 
    selectedUnit, 
    selectUnit,
    weather,
    pendingOrders,
  } = useGameStore()
  
  const [logs] = useState<EventLog[]>([
    { turn: 1, code: 'start', text: '战斗开始，双方部队进入战场' },
    { turn: 2, code: 'move', text: 'R001 向东北方向机动 3 格' },
    { turn: 2, code: 'weather', text: '天气转为多云，能见度降低' },
    { turn: 3, code: 'contact', text: '发现敌方装甲单位 E001，距离 5 格' },
    { turn: 3, code: 'fire', text: 'R004 对区域 (7,3) 实施炮击' },
    { turn: 4, code: 'attack', text: 'R002 突击 E002，造成轻微损伤' },
    { turn: 5, code: 'supply', text: '红方补给线建立，所有单位恢复补给' },
  ])
  
  const [inject, setInject] = useState<InjectState>({ side: 'red', unit: '' })
  const [newWeather, setNewWeather] = useState('')
  const [jumpTurn, setJumpTurn] = useState(1)
  
  // Mock data
  const mockCells = Array.from({ length: 100 }, (_, i) => ({
    q: i % 10,
    r: Math.floor(i / 10),
    terrain: ['clear', 'woods', 'hills', 'mountain'][i % 4],
    elev: 50 + (i * 5),
    slope: 2 + (i % 3),
    road: i % 7 === 0,
    river: false,
    bridge: false,
    city: 0,
    railway: false,
    railStation: false,
    tunnel: false,
    powerline: false,
    powerPlant: false,
    substation: false,
    airport: 0,
  }))
  
  // Calculate combat force index for each unit
  const calculateCF = (u: StoreUnit): number => {
    const strength = u.hp / 100
    const mor = Math.max(0.4, Math.min(1.2, 0.5 + (u.morale ?? 0) / 100 * 0.7))
    return (80) * strength * mor // Simplified CF calculation
  }
  
  const redUnits = units.filter(u => u.side === 'red')
  const blueUnits = units.filter(u => u.side === 'blue')
  const cfMax = Math.max(0.001, ...units.map(calculateCF))
  
  const sortedRed = [...redUnits].sort((a, b) => calculateCF(b) - calculateCF(a))
  const sortedBlue = [...blueUnits].sort((a, b) => calculateCF(b) - calculateCF(a))

  const handleForceResolve = () => {
    console.log('强制结算')
  }
  
  const handleSave = () => {
    console.log('存档')
  }
  
  const handleSetWeather = () => {
    console.log('设置天气:', newWeather)
  }
  
  const handleToggleFog = () => {
    console.log('切换战争迷雾')
  }
  
  const handleToggleOmniscient = () => {
    console.log('切换观战全知')
  }
  
  const handleToggleAI = (side: 'red' | 'blue') => {
    console.log('切换AI代管:', side)
  }
  
  const handleInjectIntel = () => {
    if (!inject.unit) {
      alert('请选择目标单位')
      return
    }
    console.log('注入情报:', inject.side, inject.unit)
  }
  
  const handleJumpTurn = () => {
    console.log('跳转到回合:', jumpTurn)
  }
  
  const handleMapPick = (q: number, r: number) => {
    const u = units.find(x => x.q === q && x.r === r)
    if (u) {
      setInject(prev => ({ ...prev, unit: u.id }))
    }
  }

  return (
    <div className="terminal" style={{ gridTemplateColumns: 'minmax(280px,340px) minmax(420px,1fr) minmax(260px,320px)' }}>
      {/* Left column: Global forces */}
      <div className="col">
        <div className="panel grow">
          <h3>全局兵力</h3>
          <div className="scroll">
            <div className="mini">
              <b className="nred">红 {redUnits.length}</b> · <b className="nblue">蓝 {blueUnits.length}</b> · 合计 {units.length}
            </div>
            
            <div className="legend mini">
              <span><i className="cs foot" />徒步</span>
              <span><i className="cs motor" />轮式</span>
              <span><i className="cs mech" />装甲</span>
              <span><i className="cs air" />航空</span>
              <span>兵种 ｜ 战力条 · 士气点按档位着色</span>
            </div>
            
            {sortedRed.map(u => {
              const cf = calculateCF(u)
              const band = cf >= cfMax * 0.6 ? 'hi' : cf >= cfMax * 0.3 ? 'mid' : 'lo'
              const m = u.morale ?? 0
              const mband = m >= 70 ? 'hi' : m >= 45 ? 'mid' : m >= 25 ? 'low' : 'crit'
              
              return (
                <div
                  key={u.id}
                  className={`urow ${u.side === 'red' ? 'red' : 'blue'}${inject.unit === u.id ? ' sel' : ''}`}
                  onClick={() => setInject(prev => ({ ...prev, unit: u.id }))}
                >
                  <span className="uid">{u.id}</span>
                  <span className="cls other">{u.type.slice(0, 2)}</span>
                  <span className={`pwr ${band}`}>
                    <span className="tk"><i style={{ width: `${Math.round(cf / cfMax * 100)}%` }} /></span>
                    <b>{cf.toFixed(1)}</b>
                  </span>
                  <span className={`mor ${mband}`} title={`士气 ${m}`}>
                    <i />{m}
                  </span>
                </div>
              )
            })}
            
            {sortedBlue.map(u => {
              const cf = calculateCF(u)
              const band = cf >= cfMax * 0.6 ? 'hi' : cf >= cfMax * 0.3 ? 'mid' : 'lo'
              const m = u.morale ?? 0
              const mband = m >= 70 ? 'hi' : m >= 45 ? 'mid' : m >= 25 ? 'low' : 'crit'
              
              return (
                <div
                  key={u.id}
                  className={`urow ${u.side === 'blue' ? 'blue' : 'red'}${inject.unit === u.id ? ' sel' : ''}`}
                  onClick={() => setInject(prev => ({ ...prev, unit: u.id }))}
                >
                  <span className="uid">{u.id}</span>
                  <span className="cls other">{u.type.slice(0, 2)}</span>
                  <span className={`pwr ${band}`}>
                    <span className="tk"><i style={{ width: `${Math.round(cf / cfMax * 100)}%` }} /></span>
                    <b>{cf.toFixed(1)}</b>
                  </span>
                  <span className={`mor ${mband}`} title={`士气 ${m}`}>
                    <i />{m}
                  </span>
                </div>
              )
            })}
          </div>
        </div>
      </div>

      {/* Middle column: Map */}
      <div className="col">
        <div className="mapwrap">
          <HexMap
            width={10}
            height={10}
            cells={mockCells}
            units={units}
            selectedUnit={selectedUnit || undefined}
            obsPoints={{}}
            onUnitClick={selectUnit}
            onHexClick={handleMapPick}
          />
          <div className="maphud" id="hud">
            <b>测试副本</b> · 导演部 · 回合 {turn}/{maxTurns} · {weather} · 迷雾 开
          </div>
          <div className="mapzoom">
            <button title="放大">+</button>
            <button title="缩小">−</button>
            <button title="复位">⤢</button>
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
        {/* Director controls */}
        <div className="panel">
          <h3>导调控制</h3>
          <div className="scroll">
            <div className="toolbar">
              <button className="btn btn-primary" onClick={handleForceResolve}>强制结算</button>
              <button className="btn" onClick={handleSave}>存档</button>
            </div>
            
            <div className="row">
              <label>
                <span>改天气</span>
                <select value={newWeather} onChange={(e) => setNewWeather(e.target.value)}>
                  <option value="">选择天气</option>
                  <option value="clear">晴朗</option>
                  <option value="cloudy">多云</option>
                  <option value="rain">雨天</option>
                  <option value="fog">雾天</option>
                </select>
              </label>
              <button className="btn btn-sm" onClick={handleSetWeather}>设定</button>
            </div>
            
            <div className="toolbar">
              <button className="btn btn-sm" onClick={handleToggleFog}>○ 战争迷雾</button>
              <button className="btn btn-sm" onClick={handleToggleOmniscient}>○ 观战全知</button>
            </div>
            
            <div className="toolbar">
              <button className="btn btn-sm" onClick={() => handleToggleAI('red')}>○ 红方 AI 代管</button>
              <button className="btn btn-sm" onClick={() => handleToggleAI('blue')}>○ 蓝方 AI 代管</button>
            </div>
            
            <div className="mini" style={{ margin: '4px 0' }}>
              就绪：红 ✓ · 蓝 ✓
            </div>
            
            <h3 style={{ marginTop: '8px' }}>注入情报</h3>
            <div className="row">
              <select 
                value={inject.side} 
                onChange={(e) => setInject(prev => ({ ...prev, side: e.target.value as 'red' | 'blue' }))}
              >
                <option value="red">向红方</option>
                <option value="blue">向蓝方</option>
              </select>
              <input 
                value={inject.unit} 
                placeholder="目标单位 id" 
                onChange={(e) => setInject(prev => ({ ...prev, unit: e.target.value }))}
                style={{ width: '110px' }}
              />
              <button className="btn btn-sm" onClick={handleInjectIntel}>暴露</button>
            </div>
            <div className="mini">提示：点选地图单位即填入目标 id</div>
            
            <h3 style={{ marginTop: '8px' }}>跳回合</h3>
            <div className="row">
              <input 
                type="number" 
                value={jumpTurn} 
                min={1} 
                onChange={(e) => setJumpTurn(parseInt(e.target.value))}
                style={{ width: '80px' }}
              />
              <button className="btn btn-sm" onClick={handleJumpTurn}>跳转</button>
            </div>
          </div>
        </div>

        {/* Orders panel */}
        <div className="panel">
          <h3>双方已受领指令</h3>
          <div className="scroll">
            <div>
              <h3>红方指令 ({pendingOrders?.length || 0})</h3>
              {pendingOrders && pendingOrders.length ? (
                pendingOrders.map((cmd: any, i: number) => (
                  <div key={i} className="ordbox">
                    <div className="o">
                      <span className="mono">{cmd.unitId}</span>
                      <span>{cmd.op}</span>
                    </div>
                  </div>
                ))
              ) : (
                <div className="muted small">（无）</div>
              )}
            </div>
            
            <div style={{ marginTop: '12px' }}>
              <h3>蓝方指令 (0)</h3>
              <div className="muted small">（无）</div>
            </div>
          </div>
        </div>

        {/* Event log panel */}
        <div className="panel grow">
          <h3>事件日志</h3>
          <div className="scroll">
            {logs.length === 0 ? (
              <div className="muted small">（无事件）</div>
            ) : (
              logs.slice(-120).reverse().map((e, i) => (
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
