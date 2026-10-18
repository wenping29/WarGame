/**
 * 对抗方战术终端：数据全部来自服务端 side 视图（own/contacts/draft/errors/ready），
 * 草案经 /ws orders(replace) 落到服务端，就绪走屏障自动结算。
 */

import { useEffect, useRef, useState } from 'react'
import HexMap from '../components/HexMap'
import { toast } from '../components/Toast'
import { hexLine } from '../map/hex'
import { useRoom } from '../room/RoomSocket'
import type { WireCommand, WireSideView } from '../room/protocol'
import { hhmm, statusTag } from '../room/protocol'
import { useUnitFull, useUnitTypes } from '../room/useUnitFull'
import { play } from '../services/sound'

const MODES = [
  ['move', '机动'],
  ['attack', '突击'],
  ['fire', '炮击'],
  ['air', '空任务'],
  ['dig', '构筑工事'],
  ['rest', '休整'],
  ['halt', '停止']
] as const

type Mode = typeof MODES[number][0]

export default function SideView() {
  const room = useRoom()
  const view = room.view?.kind === 'side' ? (room.view as WireSideView) : null
  const fullOf = useUnitFull()

  const [mode, setMode] = useState<Mode>('move')
  const [mission, setMission] = useState('patrol')
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const own = view?.own ?? []
  const contacts = view?.contacts ?? []
  const errors = view?.errors ?? []
  const side = view?.side ?? room.role
  const selected = own.find(u => u.id === selectedId) ?? null

  // 草案以本地镜像为准（点了就上屏，不等一次往返），回合推进时才用服务端回抄一次：
  // 结算把草案清空并推进回合，回合不变说明还是同一回合的编辑，服务端那份不会比本地新。
  const [draft, setDraft] = useState<WireCommand[]>([])
  const turnRef = useRef<number | null>(null)
  useEffect(() => {
    if (!view || turnRef.current === view.turn) return
    turnRef.current = view.turn
    setDraft((view.draft ?? []).map(d => ({ ...d })))
  }, [view])

  // 服务端指令裁决口径（CommandValidator：炮击 dist≤range+1、空任务 dist≤air_range、
  // 突击 航空兵用 air_range / 地面用 range 或近距 1），画大了会让玩家点出必然被拒的指令
  const typeMap = useUnitTypes()
  const preview = (() => {
    if (!selected) return { center: null as [number, number] | null, radius: 0 }
    const center: [number, number] = [selected.q, selected.r]
    const t = typeMap[selected.type] || {}
    const range = t.range || 0
    if (mode === 'fire') return { center, radius: range > 0 ? range + 1 : 0 }
    if (mode === 'air') return { center, radius: t.air_range || 0 }
    if (mode === 'attack') {
      const air = t.is_air === true || selected.cls === 'air'
      return { center, radius: air ? (t.air_range || 0) : (range > 0 ? range : 1) }
    }
    return { center: null as [number, number] | null, radius: 0 }
  })()

  const draftPath = (() => {
    if (!selected) return null
    const mv = draft.find(d => d.unit === selected.id && d.op === 'move')
    return mv?.path ?? null
  })()

  const hpPct = (id: string, personnel: number) => {
    const full = fullOf(id)
    return full > 0 ? Math.max(1, Math.min(100, Math.round(personnel / full * 100))) : 50
  }

  // 草案改动本地立即生效，同时整份覆盖到服务端（replace 语义），就绪态由服务端一并撤销
  const pushDraft = (next: WireCommand[]) => {
    play('tap')
    setDraft(next)
    room.submitOrders(next, true)
  }

  const addDraft = (cmd: WireCommand) =>
    pushDraft([...draft.filter(d => d.unit !== cmd.unit), cmd])

  const removeDraft = (unit: string) =>
    pushDraft(draft.filter(d => d.unit !== unit))

  const describeCommand = (cmd: WireCommand): string => {
    switch (cmd.op) {
      case 'move': {
        const p = cmd.path ?? []
        const last = p[p.length - 1]
        return last ? `机动→${last[0]},${last[1]}（${Math.max(0, p.length - 1)}格）` : '机动'
      }
      case 'attack':
        return `突击 ${cmd.target ?? '?'}`
      case 'fire':
        return cmd.zone ? `炮击 ${cmd.zone[0]},${cmd.zone[1]}` : '炮击'
      case 'air_mission':
        return `空任务 ${cmd.mission ?? 'none'}${cmd.zone ? ` @${cmd.zone[0]},${cmd.zone[1]}` : ''}`
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

  // 地图只回格坐标，敌我由坐标判定：点自己单位也要走这条路，
  // 否则构筑工事/休整/停止这三种就地指令永远下不出去。
  const handleMapPick = ({ q, r }: { q: number; r: number }) => {
    if (!view) return
    const clickedOwn = own.find(u => u.q === q && u.r === r)
    if (clickedOwn) {
      setSelectedId(clickedOwn.id)
      if (mode === 'rest' || mode === 'dig' || mode === 'halt') {
        addDraft({ op: mode, unit: clickedOwn.id })
      }
      return
    }
    if (!selected) {
      room.clearError()
      toast('先选中一个己方单位', 'warn')
      return
    }
    if (mode === 'move') {
      // 路径按六角直线逐格给出：服务端按相邻格校验机动，跳格直连会被判非法
      const path = hexLine(selected.q, selected.r, q, r)
      if (path.length < 2) { toast('目标即当前格', 'warn'); return }
      addDraft({ op: 'move', unit: selected.id, path })
    } else if (mode === 'attack') {
      const c = contacts.find(x => x.q === q && x.r === r && x.level >= 1)
      if (!c) { toast('该格无可攻击的已暴露敌情', 'warn'); return }
      addDraft({ op: 'attack', unit: selected.id, target: c.id })
    } else if (mode === 'fire') {
      addDraft({ op: 'fire', unit: selected.id, zone: [q, r] })
    } else if (mode === 'air') {
      if (mission === 'none') addDraft({ op: 'air_mission', unit: selected.id, mission: 'none' })
      else addDraft({ op: 'air_mission', unit: selected.id, mission, zone: [q, r] })
    } else {
      toast('该模式请点击己方单位就地执行', 'warn')
    }
  }

  // 视图里的 node_holders 已按本方裁剪（FogService.nodeHolders(onlySide)），数出来就是己方掌握数；
  // 目标取自 victory 里 kind=nodes 的那条（doc §9 胜利条件），没有节点条件就不显示分母
  const heldNodes = Object.keys(view?.node_holders ?? {}).length
  const nodeGoal = (view?.victory ?? []).find(v => v.kind === 'nodes' && v.side === side)

  return (
    <div className="terminal">
      {/* Left column: Own units */}
      <div className="col">
        <div className="panel grow">
          <h3>己方部队 <span className="muted small">({own.length})</span></h3>
          <div className="scroll">
            {!view ? (
              <div className="muted small">（等待视图…）</div>
            ) : own.length === 0 ? (
              <div className="muted small">（无敌）</div>
            ) : (
              own.map(u => (
                <UnitCard key={u.id} unit={u} hpPct={hpPct(u.type, u.personnel)}
                  err={errors.find(e => e.unit === u.id)?.message}
                  isSelected={selected?.id === u.id} onClick={() => setSelectedId(u.id)} />
              ))
            )}
          </div>
        </div>
      </div>

      {/* Middle column: Map */}
      <div className="col">
        <div className="mapwrap">
          <HexMap
            view={view}
            selection={selected ? { q: selected.q, r: selected.r } : null}
            draftPath={draftPath}
            rangeCenter={preview.center}
            rangeRadius={preview.radius}
            onPick={handleMapPick}
          />
          <div className="maphud" id="hud">
            <b>{view?.name ?? room.code}</b> · {side === 'red' ? '红方' : '蓝方'} 终端 ·
            {' '}回合 {view?.turn ?? '-'}/{view?.max_turns ?? '-'} ·
            {' '}{view ? hhmm(view.hour) : '-:--'} {view?.day?.label ?? ''} ·
            {' '}{view?.weather?.label ?? '…'} · 迷雾 {view?.fog ? '开' : '关'}
            {heldNodes > 0 && <> · 掌握要点 {heldNodes}{nodeGoal?.amount ? `/${nodeGoal.amount}` : ''}</>}
            {room.status !== 'live' && <span className="tag"> {room.status === 'kicked' ? '已顶号' : '连接中…'}</span>}
          </div>
        </div>

        {/* Mode bar */}
        <div className="toolbar" id="modebar">
          <div className="seg">
            {MODES.map(([value, label]) => (
              <button
                key={value}
                className={mode === value ? 'on' : ''}
                onClick={() => setMode(value)}
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

          {draft.length > 0 && (
            <button className="btn btn-sm" onClick={() => pushDraft([])}>
              清空草案
            </button>
          )}
        </div>
      </div>

      {/* Right column: Draft & Contacts & Events */}
      <div className="col">
        {room.error && (
          <div className="card warn-card">
            <span className="warn">{room.error}</span>
            <button className="btn btn-sm" style={{ marginLeft: 8 }} onClick={room.clearError}>×</button>
          </div>
        )}

        {errors.length > 0 && (
          <div className="card">
            <h4>被拒指令</h4>
            {errors.map((e, i) => (
              <div key={i} className="small warn">
                <code className="mono">{e.reason}</code> {e.message}
                <span className="muted">({e.unit})</span>
              </div>
            ))}
          </div>
        )}

        <div className="panel">
          <h3>指令草案 <span className="muted small">({draft.length})</span></h3>
          <div className="scroll">
            {draft.length === 0 ? (
              <div className="muted small">（无草案：选中单位后在地图点击目标）</div>
            ) : (
              draft.map(cmd => (
                <div key={cmd.unit} className="draft-item">
                  <span className="mono">{cmd.unit}</span>
                  <span>{describeCommand(cmd)}</span>
                  <span
                    className="x"
                    style={{ cursor: 'pointer', color: '#c85a54' }}
                    onClick={() => removeDraft(cmd.unit)}
                  >
                    ×
                  </span>
                </div>
              ))
            )}
          </div>
        </div>

        <div className="panel">
          <h3>敌情接触 <span className="muted small">({contacts.length})</span></h3>
          <div className="scroll">
            {contacts.length === 0 ? (
              <div className="muted small">（无敌情接触）</div>
            ) : (
              contacts.map(c => (
                <div key={c.id} className={`unit contact${c.level === 0 || c.stale ? ' unknown' : ''}`}>
                  <div className="hd">
                    <span className="id">{c.id}</span>
                    <span className="tag red">E{c.level}</span>
                    {c.stale && <span className="tag">{c.known_turns_ago}回合前</span>}
                  </div>
                  <div className="meta">
                    <span>{c.q},{c.r}</span>
                    {c.strength_band && <span>兵力{c.strength_band}</span>}
                    {c.type_label && <span>{c.type_label}</span>}
                    {c.status && <span>{statusTag(c.status)?.text ?? c.status}</span>}
                    {c.fort ? <span>工事{c.fort}</span> : null}
                    {c.personnel != null && <span>{c.personnel}人</span>}
                    {c.commander && <span>指挥 {c.commander.name}</span>}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>

        <div className="panel grow">
          <h3>事件日志</h3>
          <div className="scroll">
            {room.log.length === 0 ? (
              <div className="muted small">（无事件）</div>
            ) : (
              room.log.slice(-80).reverse().map((e, i) => (
                <div key={i} className={`ev ${e.code}`}>
                  <span className="t">T{e.turn}</span>
                  {e.text}
                </div>
              ))
            )}
          </div>
        </div>

        <ReadyBar
          ready={view?.ready ?? false}
          aiManaged={view?.ai_managed ?? false}
          onReady={() => { play('send'); room.setReady(true) }}
          onCancel={() => { play('tap'); room.setReady(false) }}
        />
      </div>
    </div>
  )
}

function UnitCard({ unit, hpPct, err, isSelected, onClick }: {
  unit: WireSideView['own'][number]
  hpPct: number
  err?: string
  isSelected: boolean
  onClick: () => void
}) {
  const st = statusTag(unit.status)
  return (
    <div className={`unit${isSelected ? ' sel' : ''}`} onClick={onClick}>
      <div className="hd">
        <span className="id">{unit.id}</span>
        <span>
          {st && <span className={`tag ${st.cls}`}>{st.text}</span>}
          <span className="tag">{unit.type_label}</span>
        </span>
      </div>
      <div className="bar"><i style={{ width: `${Math.max(0, Math.min(100, unit.morale))}%`, background: '#7fb069' }} /></div>
      <div className="bar"><i style={{ width: `${hpPct}%`, background: '#4e82b3' }} /></div>
      <div className="bar"><i style={{ width: `${Math.min(100, unit.ammo * 100)}%`, background: '#d8a24a' }} /></div>
      <div className="meta">
        <span>{unit.personnel}人</span>
        <span>补给 {unit.supply}</span>
        <span>{unit.q},{unit.r}</span>
        <span>疲劳 {unit.fatigue}</span>
        {unit.hq && <span className="tag">指挥所</span>}
        {unit.fort > 0 && <span className="tag">工事{unit.fort}</span>}
      </div>
      {err && <div className="small warn">⚠ {err}</div>}
    </div>
  )
}

function ReadyBar({ ready, aiManaged, onReady, onCancel }: {
  ready: boolean
  aiManaged: boolean
  onReady: () => void
  onCancel: () => void
}) {
  return (
    <div className="toolbar" style={{ padding: 0 }}>
      <button className="btn btn-primary" disabled={aiManaged} onClick={onReady}>
        {ready ? '● 已就绪' : '就 绪'}
      </button>
      <button className="btn" disabled={aiManaged} onClick={onCancel}>解除就绪</button>
      {aiManaged && <span className="muted small">AI 代管中</span>}
      {!aiManaged && (ready
        ? <span className="ready-yes">本方就绪后自动结算</span>
        : <span className="muted small">○ 未就绪</span>)}
    </div>
  )
}
