/**
 * 观战视角：双方暴露并集 + summary；支持回放控制（frames）
 */

import { useState } from 'react'
import HexMap from '../components/HexMap'
import { useGameStore } from '../store/gameStore'

interface Contact {
  id: string
  side: 'red' | 'blue'
  q: number
  r: number
  level: number
  type_label?: string
  strength_band?: string
  stale?: boolean
  known_turns_ago?: number
}

interface EventLog {
  turn: number
  code: string
  text: string
}

interface ReplayFrame {
  turn: number
  director: any
}

export default function SpectatorView() {
  const { units, turn, maxTurns, weather } = useGameStore()
  
  const [logs] = useState<EventLog[]>([
    { turn: 1, code: 'start', text: '战斗开始，双方部队进入战场' },
    { turn: 2, code: 'move', text: 'R001 向东北方向机动 3 格' },
    { turn: 2, code: 'weather', text: '天气转为多云，能见度降低' },
    { turn: 3, code: 'contact', text: '发现敌方装甲单位 E001，距离 5 格' },
    { turn: 3, code: 'fire', text: 'R004 对区域 (7,3) 实施炮击' },
    { turn: 4, code: 'attack', text: 'R002 突击 E002，造成轻微损伤' },
    { turn: 5, code: 'supply', text: '红方补给线建立，所有单位恢复补给' },
  ])
  
  const [replayIdx, setReplayIdx] = useState<number>(-1) // -1 = live mode
  const [frames, setFrames] = useState<ReplayFrame[]>([])
  const isDirector = false

  // Mock contacts from both sides
  const contacts: Contact[] = [
    { id: 'E001', side: 'blue', q: 7, r: 3, level: 2, type_label: '步兵', strength_band: '中等' },
    { id: 'E002', side: 'blue', q: 8, r: 5, level: 1, type_label: '装甲', strength_band: '弱' },
    { id: 'E003', side: 'red', q: 6, r: 6, level: 3, type_label: '机械化', strength_band: '强', stale: true, known_turns_ago: 2 },
    { id: 'E004', side: 'blue', q: 5, r: 4, level: 2, type_label: '炮兵' },
  ]

  // Calculate summary statistics
  const redUnits = units.filter(u => u.side === 'red')
  const blueUnits = units.filter(u => u.side === 'blue')
  const redPersonnel = redUnits.reduce((sum, u) => sum + (u.hp || 0), 0)
  const bluePersonnel = blueUnits.reduce((sum, u) => sum + (u.hp || 0), 0)

  // Handle replay frame selection
  const handleReplayChange = (index: number) => {
    setReplayIdx(index)
    if (index >= 0 && frames[index]) {
      // In real implementation, would load the director view from frame
      console.log('Loading replay frame:', index)
    }
  }

  const returnToLive = () => {
    setReplayIdx(-1)
  }

  // Mock cells for map display
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

  const kindLabel = isDirector ? '全知（导演部已授予）' : '观战情报层'
  const replayLabel = replayIdx >= 0 ? ` · 回放 T${frames[replayIdx]?.turn || turn}` : ''

  return (
    <div className="terminal" style={{ gridTemplateColumns: '1fr minmax(280px,340px)' }}>
      {/* Left column: Map */}
      <div className="col">
        <div className="mapwrap">
          <HexMap
            width={10}
            height={10}
            cells={mockCells}
            units={units}
            obsPoints={{}}
          />
          <div className="maphud" id="hud">
            <b>测试副本</b> · {kindLabel}{replayLabel} · 回合 {turn}/{maxTurns} · {weather}
          </div>
          <div className="mapzoom">
            <button title="放大">+</button>
            <button title="缩小">−</button>
            <button title="复位">⤢</button>
          </div>
        </div>
        
        {/* Scrub control for replay */}
        <div className="scrub" id="scrub">
          {frames.length === 0 ? (
            <>
              <button className="btn btn-sm" onClick={() => {
                // Mock loading frames
                setFrames([
                  { turn: 1, director: {} },
                  { turn: 2, director: {} },
                  { turn: 3, director: {} },
                  { turn: 4, director: {} },
                  { turn: 5, director: {} },
                ])
                setReplayIdx(4)
              }}>
                载入回放帧
              </button>
              <span className="muted small">（结算产生帧后可回放）</span>
            </>
          ) : (
            <>
              <button 
                className="btn btn-sm" 
                onClick={returnToLive}
              >
                {replayIdx >= 0 ? '返回实时' : '刷新'}
              </button>
              <input
                type="range"
                min={0}
                max={frames.length - 1}
                value={Math.max(0, replayIdx)}
                onChange={(e) => handleReplayChange(parseInt(e.target.value))}
              />
              <span className="mini">
                {replayIdx >= 0 ? `T${frames[replayIdx].turn}` : `共 ${frames.length} 帧`}
              </span>
            </>
          )}
        </div>
      </div>

      {/* Right column: Summary & Contacts & Events */}
      <div className="col">
        {/* Summary panel */}
        <div className="panel">
          <h3>态势</h3>
          <div className="scroll">
            {!isDirector ? (
              <div className="grid2">
                <div className="budget">
                  <span className="muted">红方单位</span>
                  <b>{redUnits.length}</b>
                </div>
                <div className="budget">
                  <span className="muted">蓝方单位</span>
                  <b>{blueUnits.length}</b>
                </div>
                <div className="budget">
                  <span className="muted">红方兵力</span>
                  <b>{redPersonnel}</b>
                </div>
                <div className="budget">
                  <span className="muted">蓝方兵力</span>
                  <b>{bluePersonnel}</b>
                </div>
              </div>
            ) : (
              <div className="grid2">
                <div className="budget">
                  <span className="muted">红方单位</span>
                  <b>{redUnits.filter(u => u.hp > 0).length}</b>
                </div>
                <div className="budget">
                  <span className="muted">蓝方单位</span>
                  <b>{blueUnits.filter(u => u.hp > 0).length}</b>
                </div>
                <div className="budget">
                  <span className="muted">红方兵力</span>
                  <b>{redPersonnel}</b>
                </div>
                <div className="budget">
                  <span className="muted">蓝方兵力</span>
                  <b>{bluePersonnel}</b>
                </div>
                <div className="budget">
                  <span className="muted">接触目标</span>
                  <b>{contacts.length}</b>
                </div>
                <div className="budget">
                  <span className="muted">胜负</span>
                  <b>进行中</b>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Public contacts panel */}
        <div className="panel">
          <h3>公开接触</h3>
          <div className="scroll">
            {contacts.length === 0 ? (
              <div className="muted small">
                {isDirector ? '（全知：见地图）' : '（暂无公开接触）'}
              </div>
            ) : (
              contacts.map(c => (
                <div key={c.id} className={`unit contact${c.stale ? ' unknown' : ''}`}>
                  <div className="hd">
                    <span className="id">{c.id}</span>
                    <span className={`tag ${c.side === 'red' ? 'red' : 'blue'}`}>
                      {c.side === 'red' ? '红' : '蓝'} E{c.level}
                    </span>
                  </div>
                  <div className="meta">
                    <span>{c.q},{c.r}</span>
                    {c.type_label && <span>{c.type_label}</span>}
                    {c.strength_band && <span>兵力{c.strength_band}</span>}
                    {c.stale && <span className="tag">{c.known_turns_ago}回合前</span>}
                  </div>
                </div>
              ))
            )}
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
