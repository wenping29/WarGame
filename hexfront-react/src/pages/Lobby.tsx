/**
 * 大厅：剧本 / 房间 / 存档 一览 + 一键开局 + 按房间码加入。
 * 对抗位已有人时必须先确认，接管=顶掉对方终端（不静默抢位）。
 */

import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type RoomInfo, type ScenarioInfo, type SaveInfo } from '../services/api'
import { loadMeta } from '../room/useUnitFull'
import { sideLabel } from '../room/protocol'
import { toast } from '../components/Toast'
import { play } from '../services/sound'

const ROLES = [
  ['director', '导演部'],
  ['red', '红方'],
  ['blue', '蓝方'],
  ['spectator', '观战']
] as const

// 天气 ID → 中文（与 C# LibraryService.WEATHER_JSON 的 Label 同源）
const WEATHER_LABEL: Record<string, string> = {
  clear: '晴',
  overcast: '多云',
  rain: '降雨',
  storm: '暴雨',
  snow: '降雪',
  fog: '浓雾',
  sandstorm: '沙尘'
}

export default function Lobby() {
  const navigate = useNavigate()
  const [scenarios, setScenarios] = useState<ScenarioInfo[]>([])
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [rooms, setRooms] = useState<RoomInfo[]>([])
  const [joinCode, setJoinCode] = useState('')
  const [joinRole, setJoinRole] = useState<string>('director')

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
      const replayed = res.replayed_turns?.length ?? 0
      toast(`已载入房间 ${res.code}${replayed ? ` · 重放 ${replayed} 回合` : ''}`)
      if (res.replay_warnings?.length) toast(`重放指纹与存档不符：${res.replay_warnings[0]}`, 'err')
      navigate(`/room/${res.code}/director`)
    } else {
      toast(res.error || '载入失败', 'err')
    }
  }

  // 删除已存副本：二次确认（不可恢复）；活跃房间占用时后端拒删（409）
  const removeSave = async (s: SaveInfo) => {
    const label = s.name ? `${s.name}（${s.instance}）` : s.instance
    if (!window.confirm(`删除副本 ${label}？将同时删除其存档目录与全部历史数据，不可恢复。`)) return
    try {
      const res = await api.deleteSave(s.instance)
      if (res.deleted) {
        toast('已删除副本 ' + s.instance)
        const meta = await loadMeta(true) // force：绕过模块缓存，否则列表仍显示已删条目
        setSaves(meta.saves || [])
      } else {
        toast(res.error || '删除失败', 'err')
      }
    } catch (e) {
      toast('删除失败：' + e, 'err')
    }
  }

  return (
    <div className="page lb-page">
      <h1 style={{ margin: '0 0 14px', fontSize: 18 }}>大厅</h1>
      <div className="lb-grid">
        {/* ============ 左列：开局 / 加入 / 存档 ============ */}
        <div className="lb-col">
          <div className="lb-label">一键开局</div>
          <button
            className="btn btn-primary lb-quick"
            title={'默认剧本 ' + (scenarios[0]?.name || '') + ' · 无覆盖直接进导演部'}
            onClick={quickStart}
          >
            默认剧本 · 无覆盖 · 直进导演部
          </button>

          <div className="lb-label" style={{ marginTop: 14 }}>按房间码加入</div>
          <div className="lb-join">
            <input
              placeholder="房间码"
              value={joinCode}
              onChange={e => setJoinCode(e.target.value)}
              onKeyDown={e => {
                if (e.key === 'Enter' && joinCode.trim()) joinRoom(joinCode.trim(), joinRole, '')
              }}
            />
            <select value={joinRole} onChange={e => setJoinRole(e.target.value)}>
              {ROLES.map(([value, label]) => (
                <option key={value} value={value}>{label}</option>
              ))}
            </select>
            <button className="btn" onClick={() => {
              if (!joinCode.trim()) return toast('请输入房间码', 'warn')
              joinRoom(joinCode.trim(), joinRole, '')
            }}>
              加入
            </button>
          </div>

          <div className="lb-label" style={{ marginTop: 14 }}>存档</div>
          <div className="lb-panel">
            {saves.length === 0 ? (
              <div className="lb-empty">暂无存档</div>
            ) : (
              <div className="lb-scroll">
                {saves.map(s => (
                  <div key={s.instance} className="lb-row">
                    <span className="lb-row-main" title={s.scenario_name || s.scenario}>
                      {s.name || s.instance}{' '}
                      <span className="lb-dim">t{s.last_turn ?? '?'}</span>
                    </span>
                    <span className="lb-row-ops">
                      <button className="btn btn-sm lb-del" onClick={() => removeSave(s)}>删除</button>
                      <button className="btn btn-sm" onClick={() => loadSave(s)}>载入</button>
                    </span>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

        {/* ============ 右列：房间 / 剧本 ============ */}
        <div className="lb-col">
          <div className="lb-label lb-label-row">
            <span>活跃房间</span>
            <button className="btn btn-sm lb-mini" onClick={refreshRooms}>刷新</button>
          </div>
          <div className="lb-panel">
            {rooms.length === 0 ? (
              <div className="lb-empty">暂无活跃房间</div>
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
                          <span className="tag red">终局 · {sideLabel(room.winner)}</span>
                        )}
                        <span className="spacer" />
                        <span className="mono muted small">{room.turn ?? '?'}/{room.max_turns ?? '?'}</span>
                      </div>
                      <div className="name">{room.name}</div>
                      <div className="sub">
                        {room.scenario_name || room.scenario} · {room.width}×{room.height} · {WEATHER_LABEL[room.weather ?? ''] ?? (room.weather || '—')}
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
          </div>

          <div className="lb-label lb-label-row" style={{ marginTop: 14 }}>
            <span>可用剧本</span>
            <button className="btn btn-sm lb-mini" onClick={() => navigate('/new')}>向导创建</button>
          </div>
          <div className="lb-panel">
            {scenarios.length === 0 ? (
              <div className="lb-empty">暂无可用剧本</div>
            ) : (
              <div>
                {scenarios.map(scenario => (
                  <div
                    key={scenario.id || scenario.file}
                    className="lb-row lb-scn"
                    title="点击以此剧本开局"
                    onClick={() => startScenario(scenario)}
                  >
                    <span className="lb-row-main">
                      {scenario.name || scenario.id}
                      {scenario.description && (
                        <span className="lb-dim lb-desc">{scenario.description}</span>
                      )}
                    </span>
                    <span className="lb-meta">{scnMeta(scenario)}</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

/** 剧本行右侧摘要：二战·西线 24×16格·红11/蓝11·16回合 */
function scnMeta(s: ScenarioInfo): string {
  const env = (s.environment || {}) as Record<string, unknown>
  const w = env.width, h = env.height
  const sideCount = (side: string) => {
    const oob = (s.sides as Record<string, unknown> | undefined)?.[side] as { oob?: Array<{ qty?: number }> } | undefined
    return (oob?.oob || []).reduce((a, u) => a + (u.qty ?? 1), 0)
  }
  const parts: string[] = []
  const head = [s.era, s.scale].filter(Boolean).join('·')
  if (head) parts.push(head)
  if (typeof w === 'number' && typeof h === 'number') parts.push(`${w}×${h}格`)
  const r = sideCount('red'), b = sideCount('blue')
  if (r || b) parts.push(`红${r || 0}/蓝${b || 0}`)
  if (s.max_turns) parts.push(`${s.max_turns}回合`)
  return parts.join('·')
}
