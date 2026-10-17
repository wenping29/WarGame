/**
 * 大厅：剧本 / 房间 / 存档 一览 + 一键开局 + 按房间码加入。
 * 对抗位已有人时必须先确认，接管=顶掉对方终端（不静默抢位）。
 */

import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type RoomInfo, type ScenarioInfo, type SaveInfo } from '../services/api'
import { loadMeta } from '../room/useUnitFull'
import { toast } from '../components/Toast'
import { play } from '../services/sound'

const ROLES = [
  ['director', '导演部'],
  ['red', '红方'],
  ['blue', '蓝方'],
  ['spectator', '观战']
] as const

export default function Lobby() {
  const navigate = useNavigate()
  const [scenarios, setScenarios] = useState<ScenarioInfo[]>([])
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [rooms, setRooms] = useState<RoomInfo[]>([])
  const [joinCode, setJoinCode] = useState('')
  const [joinRole, setJoinRole] = useState<string>('director')
  const [nickname, setNickname] = useState('')

  useEffect(() => {
    loadMeta()
      .then(meta => {
        setScenarios(meta.scenarios || [])
        setSaves(meta.saves || [])
      })
      .catch(e => toast('剧本加载失败：' + e, 'err'))
    refreshRooms()
  }, [])

  const refreshRooms = async () => {
    try {
      const res = await api.rooms()
      setRooms(res.rooms || [])
    } catch {
      setRooms([])
    }
  }

  const startScenario = async (s: ScenarioInfo) => {
    play('tap')
    toast('正在创建副本…')
    const res = await api.createInstance({ scenario: s.id || s.file, name: s.name })
    if (res.code) {
      toast('房间已创建 ' + res.code)
      navigate(`/room/${res.code}/director`)
    } else {
      toast(res.error || res.errors?.join(';') || '创建失败', 'err')
    }
  }

  const quickStart = () => {
    const s = scenarios[0]
    if (!s) return toast('没有可用剧本', 'err')
    toast('一键开局中…')
    api.createInstance({ scenario: s.id || s.file }).then(res => {
      if (res.code) {
        toast('开局成功 · 房间 ' + res.code)
        navigate(`/room/${res.code}/director`)
      } else {
        toast(res.error || res.errors?.join(';') || '创建失败', 'err')
      }
    })
  }

  // 加入房间：对抗位已有人时必须先确认，接管写入 sessionStorage 供 RoomPage 使用
  const joinRoom = (code: string, role: string, name: string) => {
    const r = rooms.find(x => (x.code || '').toUpperCase() === code.toUpperCase())
    const taken = (role === 'red' || role === 'blue') && r?.side_taken?.[role] === true
    let force = false
    if (taken) {
      const label = role === 'red' ? '红方' : '蓝方'
      if (!window.confirm(`${label}已有指挥官在线。加入将接管该角色并把对方终端踢下线，确认接管？`)) return
      force = true
    }
    try {
      sessionStorage.setItem('hf.join.' + code, JSON.stringify({ role, name, force }))
    } catch { /* 忽略 */ }
    play('tap')
    navigate(`/room/${code.toUpperCase()}/${role}`)
  }

  const loadSave = async (s: SaveInfo) => {
    const res = await api.loadSave(s.instance)
    if (res.code) {
      toast('已载入房间 ' + res.code)
      navigate(`/room/${res.code}/director`)
    } else {
      toast(res.error || '载入失败', 'err')
    }
  }

  return (
    <div className="page">
      <div className="hero">
        <button className="btn btn-primary" onClick={quickStart}>
          一键开局（默认剧本）
        </button>
        <button className="btn" onClick={() => navigate('/new')}>
          创建副本向导
        </button>
        <span className="muted small">
          默认剧本 {scenarios[0]?.name || ''} · 免覆盖直接进导演部
        </span>
      </div>

      <div className="join-bar">
        <label>
          <span>房间码</span>
          <input
            placeholder="房间码（6位）"
            style={{ width: '140px' }}
            value={joinCode}
            onChange={e => setJoinCode(e.target.value)}
          />
        </label>
        <label>
          <span>角色</span>
          <select value={joinRole} onChange={e => setJoinRole(e.target.value)}>
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
            onChange={e => setNickname(e.target.value)}
          />
        </label>
        <button className="btn btn-1" onClick={() => {
          if (!joinCode.trim()) return toast('请输入房间码', 'warn')
          joinRoom(joinCode.trim(), joinRole, nickname)
        }}>
          加入
        </button>
        <div className="spacer" />
        <button className="btn btn-sm" onClick={refreshRooms}>刷新列表</button>
      </div>

      <section className="card" style={{ marginBottom: '16px' }}>
        <h3>活跃房间 ({rooms.length})</h3>
        {rooms.length === 0 ? (
          <div className="muted small">暂无活跃房间</div>
        ) : (
          <div className="grid-3">
            {rooms.map(room => {
              const online = ROLES.filter(([value]) => (room.roles?.[value] ?? 0) > 0)
              const aiOn = ROLES.filter(([value]) =>
                (value === 'red' || value === 'blue') && room.ai?.[value] === true)
              return (
                <div key={room.code} className="card room-card">
                  <div className="hd">
                    <b className="code">{room.code}</b>
                    <span className={'tag' + (room.fog ? '' : ' blue')}>{room.fog ? '迷雾' : '明牌'}</span>
                    {room.over && (
                      <span className="tag red">终局{room.winner ? ' · ' + room.winner : ''}</span>
                    )}
                    <span className="spacer" />
                    <span className="mono muted small">{room.turn ?? '?'}/{room.max_turns ?? '?'}</span>
                  </div>
                  <div className="name">{room.name}</div>
                  <div className="sub">
                    {room.scenario_name || room.scenario} · {room.width}×{room.height} · {room.weather || '—'}
                  </div>
                  <div className="kv">
                    <span>{online.length ? '在线 ' + online.map(([, l]) => l).join(' · ') : '无人在线'}</span>
                    <span>
                      {aiOn.map(([value]) => (
                        <span key={value} className="tag warn" style={{ marginLeft: 4 }}>
                          {value === 'red' ? '红' : '蓝'}AI
                        </span>
                      ))}
                    </span>
                  </div>
                  <div className="foot">
                    {ROLES.map(([value, label]) => {
                      const taken = (value === 'red' || value === 'blue') && room.side_taken?.[value] === true
                      return (
                        <button key={value}
                          className={'btn btn-sm' + (taken ? ' role-taken' : '')}
                          title={taken ? '加入将顶掉当前指挥官' : undefined}
                          onClick={() => joinRoom(room.code, value, '')}>
                          {taken ? label + '·接管' : label}
                        </button>
                      )
                    })}
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </section>

      <section className="card" style={{ marginBottom: '16px' }}>
        <h3>可用剧本</h3>
        <div className="grid-3">
          {scenarios.map(scenario => (
            <div key={scenario.id || scenario.file} className="card scn-card">
              <div>
                <b>{scenario.name || scenario.id}</b>
                <span className="tag" style={{ marginLeft: '8px' }}>{scenario.era || ''}</span>
              </div>
              <div className="desc">{scenario.description}</div>
              <div className="kv">
                <span>回合 {scenario.max_turns ?? '?'}</span>
                <span>单位 {scenario.unit_count ?? '?'}</span>
                <span>{scenario.scale}</span>
              </div>
              <div className="foot">
                <button className="btn btn-sm btn-primary" onClick={() => startScenario(scenario)}>
                  以此开局
                </button>
                <button className="btn btn-sm" onClick={() => navigate('/new')}>
                  向导创建
                </button>
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="card" style={{ marginBottom: '16px' }}>
        <h3>已存副本 ({saves.length})</h3>
        {saves.length === 0 ? (
          <div className="muted small">暂无存档</div>
        ) : (
          <div className="grid-3">
            {saves.map(s => (
              <div key={s.instance} className="card save-card">
                <div className="hd">
                  <b className="code">{s.instance}</b>
                  <span className="spacer" />
                  <span className="tag">回合 {s.last_turn ?? '?'}</span>
                </div>
                <div className="name">{s.name}</div>
                <div className="sub">
                  {s.scenario_name || s.scenario}
                  {' · '}{new Date(s.created_at).toLocaleString('zh-CN')}
                  {s.unit_count ? ` · 单位 ${s.unit_count}` : ''}
                  {s.weather ? ` · ${s.weather}` : ''}
                </div>
                <div className="foot">
                  <button className="btn btn-sm btn-primary" onClick={() => loadSave(s)}>
                    载入并导调
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
