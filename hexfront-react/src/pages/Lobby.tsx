/**
 * 大厅：剧本 / 房间 / 存档 一览 + 一键开局 + 按房间码加入
 */

import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { listSaves } from '../services/api'

interface Scenario {
  id: string
  name: string
  description?: string
  era?: string
  max_turns?: number
  unit_count?: number
  scale?: string
}

interface Room {
  code: string
  name: string
  players: number
  maxPlayers: number
  occupiedRoles: string[]
}

interface SaveInfo {
  id: string
  name: string
  savedAt: string
}

const ROLES = [
  ['director', '导演部'],
  ['red', '红方'],
  ['blue', '蓝方'],
  ['spectator', '观战']
] as const

export default function Lobby() {
  const navigate = useNavigate()
  const [scenarios, setScenarios] = useState<Scenario[]>([])
  const [rooms, setRooms] = useState<Room[]>([])
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [joinCode, setJoinCode] = useState('')
  const [joinRole, setJoinRole] = useState<string>('director')
  const [nickname, setNickname] = useState('')

  useEffect(() => {
    loadMeta()
  }, [])

  const loadMeta = async () => {
    // TODO: Load from API when backend is ready
    // For now, use mock data
    setScenarios([
      {
        id: 'default',
        name: '默认剧本',
        description: '标准对抗场景，双方兵力均衡',
        era: '现代',
        max_turns: 14,
        unit_count: 20,
        scale: '营级'
      }
    ])
    
    setRooms([]) // Will be populated via WebSocket
    
    try {
      const saveList = await listSaves()
      setSaves(saveList)
    } catch (e) {
      console.error('Failed to load saves:', e)
    }
  }

  const quickStart = () => {
    // Quick start with default scenario
    navigate('/director')
  }

  const joinRoom = () => {
    if (!joinCode.trim()) {
      alert('请输入房间码')
      return
    }
    navigate(`/room/${joinCode.toUpperCase()}/${joinRole}`)
  }

  return (
    <div className="page">
      {/* Hero section */}
      <div className="hero">
        <button 
          className="btn btn-primary" 
          onClick={quickStart}
        >
          一键开局（默认剧本）
        </button>
        <button 
          className="btn" 
          onClick={() => navigate('/new')}
        >
          创建副本向导
        </button>
        <span className="muted small">
          默认剧本 {scenarios[0]?.name || ''} · 免覆盖直接进导演部
        </span>
      </div>

      {/* Join room bar */}
      <div className="join-bar">
        <label>
          <span>房间码</span>
          <input 
            placeholder="房间码（6位）" 
            style={{ width: '140px' }}
            value={joinCode}
            onChange={(e) => setJoinCode(e.target.value)}
          />
        </label>
        <label>
          <span>角色</span>
          <select value={joinRole} onChange={(e) => setJoinRole(e.target.value)}>
            {ROLES.map(([value, label]) => (
              <option key={value} value={value}>{label}</option>
            ))}
          </select>
        </label>
        <label>
          <span>昵称</span>
          <input 
            placeholder="昵称（可空）" 
            style={{ width: '130px' }}
            value={nickname}
            onChange={(e) => setNickname(e.target.value)}
          />
        </label>
        <button 
          className="btn btn-1" 
          onClick={joinRoom}
        >
          加入
        </button>
        <div className="spacer" />
        <button 
          className="btn btn-sm" 
          onClick={loadMeta}
        >
          刷新列表
        </button>
      </div>

      {/* Active rooms */}
      <section className="card" style={{ marginBottom: '16px' }}>
        <h3>活跃房间 ({rooms.length})</h3>
        {rooms.length === 0 ? (
          <div className="muted small">暂无活跃房间</div>
        ) : (
          <div className="room-list">
            <table>
              <thead>
                <tr>
                  <th>房间码</th>
                  <th>名称</th>
                  <th>玩家</th>
                  <th>操作</th>
                </tr>
              </thead>
              <tbody>
                {rooms.map((room) => (
                  <tr key={room.code}>
                    <td><b className="mono">{room.code}</b></td>
                    <td>{room.name}</td>
                    <td>{room.players}/{room.maxPlayers}</td>
                    <td>
                      <button 
                        className="btn btn-sm"
                        onClick={() => navigate(`/room/${room.code}/${joinRole}`)}
                      >
                        加入
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {/* Scenarios */}
      <section className="card" style={{ marginBottom: '16px' }}>
        <h3>可用剧本</h3>
        <div className="grid-3">
          {scenarios.map((scenario) => (
            <div key={scenario.id} className="card scn-card">
              <div>
                <b>{scenario.name}</b>
                {scenario.era && (
                  <span className="tag" style={{ marginLeft: '8px' }}>{scenario.era}</span>
                )}
              </div>
              <div className="desc">{scenario.description}</div>
              <div className="kv">
                <span>回合 {scenario.max_turns ?? '?'}</span>
                <span>单位 {scenario.unit_count ?? '?'}</span>
                <span>{scenario.scale}</span>
              </div>
              <div className="foot">
                <button 
                  className="btn btn-sm btn-primary"
                  onClick={() => navigate('/director')}
                >
                  以此开局
                </button>
                <button 
                  className="btn btn-sm"
                  onClick={() => navigate('/new')}
                >
                  向导创建
                </button>
              </div>
            </div>
          ))}
        </div>
      </section>

      {/* Saves */}
      <section className="card" style={{ marginBottom: '16px' }}>
        <h3>已存副本 ({saves.length})</h3>
        {saves.length === 0 ? (
          <div className="muted small">暂无存档</div>
        ) : (
          <div className="grid-3">
            {saves.map((save) => (
              <div key={save.id} className="card scn-card">
                <div><b>{save.name}</b></div>
                <div className="kv">
                  <span>保存于: {new Date(save.savedAt).toLocaleString('zh-CN')}</span>
                </div>
                <div className="foot">
                  <button 
                    className="btn btn-sm btn-primary"
                    onClick={() => navigate(`/director?load=${save.id}`)}
                  >
                    载入
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  )
}
