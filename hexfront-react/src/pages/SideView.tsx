/**
 * 对抗方战术终端：只显示己方全量 + 敌方暴露分级；点击下达指令，维护本地草案。
 */

import { useState } from 'react'
import { useParams } from 'react-router-dom'
import HexMap from '../components/HexMap'
import { useGameStore } from '../store/gameStore'
import type { CommandRequest } from '../services/api'

const MODES = [
  ['move', '机动'],
  ['attack', '突击'],
  ['fire', '炮击'],
  ['air', '空任务'],
  ['dig', '构筑工事'],
  ['rest', '休整'],
  ['halt', '停止']
] as const

interface Unit {
  id: string
  side: 'red' | 'blue' | 'neutral'
  type: string
  q: number
  r: number
  hp: number
  morale: number
  fatigue: number
  ammo?: number
  supply?: number
  status?: string
  hq?: boolean
  fort?: number
}

export default function SideView() {
  const { side } = useParams<{ side: string }>()
  const { units, selectedUnit, selectUnit, turn, maxTurns, weather } = useGameStore()
  
  const [mode, setMode] = useState<'move' | 'attack' | 'fire' | 'air' | 'dig' | 'rest' | 'halt'>('move')
  const [mission, setMission] = useState('patrol')
  const [draft, setDraft] = useState<CommandRequest[]>([])
  const [logs] = useState<Array<{ turn: number; code: string; text: string }>>([
    { turn: 1, code: 'start', text: '战斗开始，双方部队进入战场' },
    { turn: 2, code: 'move', text: 'R001 向东北方向机动 3 格' },
    { turn: 2, code: 'weather', text: '天气转为多云，能见度降低' },
    { turn: 3, code: 'contact', text: '发现敌方装甲单位 E001，距离 5 格' },
    { turn: 3, code: 'fire', text: 'R004 对区域 (7,3) 实施炮击' },
    { turn: 4, code: 'attack', text: 'R002 突击 E002，造成轻微损伤' },
  ])
  const [ready, setReady] = useState(false)

  // Filter own units (add mock data if store is empty)
  const allOwnUnits = units.filter(u => u.side === side)
  const ownUnits = allOwnUnits.length > 0 ? allOwnUnits : [
    { id: 'R001', side: side as 'red' | 'blue', type: 'infantry', q: 3, r: 2, hp: 85, morale: 90, fatigue: 15, ammo: 0.8, supply: 100, status: '', hq: false, fort: 0 },
    { id: 'R002', side: side as 'red' | 'blue', type: 'armor', q: 4, r: 3, hp: 72, morale: 75, fatigue: 25, ammo: 0.6, supply: 80, status: '', hq: false, fort: 2 },
    { id: 'R003', side: side as 'red' | 'blue', type: 'mechanized', q: 2, r: 4, hp: 95, morale: 88, fatigue: 10, ammo: 0.9, supply: 120, hq: true, fort: 1 },
    { id: 'R004', side: side as 'red' | 'blue', type: 'artillery', q: 1, r: 1, hp: 60, morale: 70, fatigue: 30, ammo: 0.5, supply: 90, status: 'supply_low' },
  ]
  
  // Mock contacts (enemy units with exposure levels)
  const contacts: Array<{
    id: string
    q: number
    r: number
    level: number
    strength_band?: string
    type_label?: string
    fort?: number
    status?: string
    personnel?: number
    stale?: boolean
    known_turns_ago?: number
  }> = [
    { id: 'E001', q: 7, r: 3, level: 2, strength_band: '中等', type_label: '步兵', fort: 1, personnel: 45 },
    { id: 'E002', q: 8, r: 5, level: 1, strength_band: '弱', type_label: '装甲', status: '受损' },
    { id: 'E003', q: 6, r: 6, level: 3, strength_band: '强', type_label: '机械化', stale: true, known_turns_ago: 2 },
  ]

  const handleUnitClick = (unit: Unit) => {
    if (unit.side === side) {
      selectUnit(unit)
    }
  }

  const addDraft = (cmd: CommandRequest) => {
    // Each unit can only have one order
    setDraft(prev => {
      const filtered = prev.filter(d => d.unitId !== cmd.unitId)
      return [...filtered, cmd]
    })
  }

  const removeDraft = (unitId: string) => {
    setDraft(prev => prev.filter(d => d.unitId !== unitId))
  }

  const clearDraft = () => {
    setDraft([])
  }

  const describeCommand = (cmd: CommandRequest): string => {
    switch (cmd.op) {
      case 'move':
        if (cmd.path && cmd.path.length > 0) {
          const last = cmd.path[cmd.path.length - 1]
          return `机动→${last.q},${last.r}（${cmd.path.length - 1}格）`
        }
        return '机动'
      case 'attack':
        return `突击 ${cmd.target}`
      case 'fire':
        if (cmd.zone) {
          return `炮击 ${cmd.zone.q},${cmd.zone.r}`
        }
        return '炮击'
      case 'air_mission':
        return `空任务 ${cmd.mission}${cmd.zone ? ` @${cmd.zone.q},${cmd.zone.r}` : ''}`
      case 'dig':
        return '构筑工事'
      case 'rest':
        return '休整'
      case 'halt':
        return '停止'
      default:
        return cmd.op
    }
  }

  const handleMapPick = (q: number, r: number) => {
    // Check if clicked on own unit
    const clickedOwn = ownUnits.find(u => u.q === q && u.r === r)
    if (clickedOwn) {
      selectUnit(clickedOwn)
      return
    }

    if (!selectedUnit) {
      alert('先选中一个己方单位')
      return
    }

    const u = ownUnits.find(x => x.id === selectedUnit.id)
    if (!u) return

    if (mode === 'move') {
      // Simple path: just start and end
      const path = [{ q: u.q, r: u.r }, { q, r }]
      addDraft({ op: 'move', unitId: u.id, path })
    } else if (mode === 'attack') {
      const c = contacts.find(x => x.q === q && x.r === r && x.level >= 1)
      if (!c) {
        alert('该格无可攻击的已暴露敌情')
        return
      }
      addDraft({ op: 'attack', unitId: u.id, target: c.id })
    } else if (mode === 'fire') {
      addDraft({ op: 'fire', unitId: u.id, zone: { q, r } })
    } else if (mode === 'air') {
      if (mission === 'none') {
        addDraft({ op: 'air_mission', unitId: u.id, mission: 'none' })
      } else {
        addDraft({ op: 'air_mission', unitId: u.id, mission, zone: { q, r } })
      }
    } else {
      alert('该模式请点击己方单位就地执行')
    }
  }

  // Mock cells for testing
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

  return (
    <div className="terminal">
      {/* Left column: Own units */}
      <div className="col">
        <div className="panel grow">
          <h3>己方部队</h3>
          <div className="scroll">
            {ownUnits.length === 0 ? (
              <div className="muted small">（等待视图…）</div>
            ) : (
              ownUnits.map(u => <UnitCard key={u.id} unit={u} isSelected={selectedUnit?.id === u.id} onClick={() => handleUnitClick(u)} />)
            )}
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
            units={[...ownUnits, ...contacts.map(c => ({ ...c, side: side === 'red' ? 'blue' : 'red' as 'red' | 'blue', hp: 100, morale: 80, fatigue: 0, type: 'unknown' }))] as any}
            selectedUnit={selectedUnit || undefined}
            obsPoints={{}}
            onUnitClick={(u) => handleUnitClick(u as Unit)}
            onHexClick={handleMapPick}
          />
          <div className="maphud" id="hud">
            <b>测试副本</b> · {side === 'red' ? '红方' : '蓝方'} · 回合 {turn}/{maxTurns} · {weather}
          </div>
          <div className="mapzoom">
            <button title="放大">+</button>
            <button title="缩小">−</button>
            <button title="复位">⤢</button>
          </div>
        </div>
        
        {/* Mode bar */}
        <div className="toolbar" id="modebar">
          <div className="seg">
            {MODES.map(([value, label]) => (
              <button
                key={value}
                className={mode === value ? 'on' : ''}
                onClick={() => setMode(value as any)}
              >
                {label}
              </button>
            ))}
          </div>
          
          {mode === 'air' && (
            <select
              style={{ marginLeft: '8px' }}
              value={mission}
              onChange={(e) => setMission(e.target.value)}
            >
              <option value="patrol">战斗巡逻</option>
              <option value="strike">对地突击</option>
              <option value="recon">侦察</option>
              <option value="none">返回地面</option>
            </select>
          )}
          
          <div className="spacer" />
          
          {selectedUnit && (
            <button className="btn btn-sm" onClick={clearDraft}>
              清空草案
            </button>
          )}
          
          <ReadyButtons ready={ready} setReady={setReady} />
        </div>
      </div>

      {/* Right column: Draft & Contacts & Events */}
      <div className="col">
        <div className="panel">
          <h3>指令草案</h3>
          <div className="scroll">
            {draft.length === 0 ? (
              <div className="muted small">（无草案：选中单位后在地图点击目标）</div>
            ) : (
              draft.map((cmd, i) => (
                <div key={i} className="draft-item">
                  <span className="mono">{cmd.unitId}</span>
                  <span>{describeCommand(cmd)}</span>
                  <span
                    className="x"
                    style={{ cursor: 'pointer', color: '#c85a54' }}
                    onClick={() => removeDraft(cmd.unitId)}
                  >
                    ×
                  </span>
                </div>
              ))
            )}
          </div>
        </div>

        <div className="panel">
          <h3>敌情接触</h3>
          <div className="scroll">
            {contacts.length === 0 ? (
              <div className="muted small">（无敌情接触）</div>
            ) : (
              contacts.map(c => (
                <div key={c.id} className={`unit contact${c.level === 0 || c.stale ? ' unknown' : ''}`}>
                  <div className="hd">
                    <span className="id">{c.id}</span>
                    <span className="tag red">E{c.level}</span>
                  </div>
                  <div className="meta">
                    <span>{c.q},{c.r}</span>
                    {c.strength_band && <span>兵力{c.strength_band}</span>}
                    {c.type_label && <span>{c.type_label}</span>}
                    {c.personnel != null && <span>{c.personnel}人</span>}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>

        <div className="panel grow">
          <h3>事件日志</h3>
          <div className="scroll">
            {logs.length === 0 ? (
              <div className="muted small">（无事件）</div>
            ) : (
              logs.slice(-80).reverse().map((e, i) => (
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

// Unit card component
function UnitCard({ unit, isSelected, onClick }: { unit: Unit; isSelected: boolean; onClick: () => void }) {
  const pct = (val: number, max: number) => max > 0 ? (val / max) * 100 : 0

  return (
    <div className={`unit${isSelected ? ' sel' : ''}`} onClick={onClick}>
      <div className="hd">
        <span className="id">{unit.id}</span>
        <span>
          {unit.status && <span className="tag">{unit.status}</span>}
          <span className="tag">{unit.type}</span>
        </span>
      </div>
      
      {/* Morale bar */}
      <div className="bar">
        <i style={{ width: `${Math.max(0, Math.min(100, unit.morale))}%`, background: '#7fb069' }} />
      </div>
      
      {/* HP bar */}
      <div className="bar">
        <i style={{ width: `${pct(unit.hp, 100)}%`, background: '#4e82b3' }} />
      </div>
      
      {/* Ammo bar */}
      <div className="bar">
        <i style={{ width: `${(unit.ammo ?? 0) * 100}%`, background: '#d8a24a' }} />
      </div>
      
      <div className="meta">
        <span>补给 {unit.supply ?? '-'}</span>
        <span>{unit.q},{unit.r}</span>
        <span>疲劳 {unit.fatigue}</span>
        {unit.hq && <span className="tag">指挥所</span>}
        {unit.fort && <span className="tag">工事{unit.fort}</span>}
      </div>
    </div>
  )
}

// Ready buttons component
function ReadyButtons({ ready, setReady }: { ready: boolean; setReady: (v: boolean) => void }) {
  return (
    <div className="toolbar" style={{ padding: 0 }}>
      <button
        className="btn btn-primary"
        onClick={() => setReady(true)}
      >
        就 绪
      </button>
      <button
        className="btn"
        onClick={() => setReady(false)}
      >
        解除就绪
      </button>
      {ready ? (
        <span className="ready-yes">● 已就绪</span>
      ) : (
        <span className="muted small">○ 未就绪</span>
      )}
    </div>
  )
}
