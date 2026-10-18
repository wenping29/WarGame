/**
 * 观战：默认情报层（双方暴露并集 + summary）；导演部开启全知后同一 socket
 * 收到 kind:'director' 视图——UI 按 view.kind 自适应。含回放控制（frames）。
 */

import { useState } from 'react'
import HexMap from '../components/HexMap'
import { useRoom } from '../room/RoomSocket'
import type { AnyView, Frame } from '../room/protocol'
import { hhmm, sideLabel } from '../room/protocol'

export default function SpectatorView() {
  const room = useRoom()
  const live = room.view

  const [replayIdx, setReplayIdx] = useState(-1) // -1 = 实时
  const replayView: AnyView | null = replayIdx >= 0 && room.frames[replayIdx]
    ? room.frames[replayIdx].director
    : null
  const view = replayView ?? live

  const frames = room.frames

  const setReplay = (i: number) => {
    setReplayIdx(i)
    if (i < 0) room.send({ type: 'state' })
  }

  return (
    <div className="terminal" style={{ gridTemplateColumns: '1fr minmax(280px,340px)' }}>
      <div className="col">
        <div className="mapwrap">
          <HexMap view={view} />
          <div className="maphud">
            {view ? (
              <>
                <b>{view.name || ''}</b> · {kindLabel(view, replayIdx >= 0 ? frames[replayIdx]?.turn ?? null : null)} ·
                {' '}回合 {view.turn}/{view.max_turns} · {hhmm(view.hour)} {view.day?.label || ''} ·{' '}
                {view.weather?.label || ''}
              </>
            ) : (
              <>连接中…{room.status === 'kicked' && (
                <> · 本角色已在其他终端打开{' '}
                  <button className="btn btn-sm" onClick={() => { setReplayIdx(-1); room.rejoin(true) }}>强行接管</button>
                </>
              )}</>
            )}
          </div>
        </div>

        <div className="scrub">
          {!frames.length ? (
            <>
              <button className="btn btn-sm" onClick={room.requestFrames}>载入回放帧</button>
              <span className="muted small">（结算产生帧后可回放）</span>
            </>
          ) : (
            <>
              <button className="btn btn-sm" onClick={() => { if (replayIdx >= 0) setReplay(-1); else room.send({ type: 'state' }) }}>
                {replayIdx >= 0 ? '返回实时' : '刷新'}
              </button>
              <input
                type="range" min={0} max={frames.length - 1}
                value={Math.max(0, replayIdx)}
                onChange={e => setReplay(parseInt(e.target.value))}
                style={{ flex: 1 }}
              />
              <span className="mini">{replayIdx >= 0 ? `T${frames[replayIdx].turn}` : `共 ${frames.length} 帧`}</span>
            </>
          )}
        </div>
      </div>

      <div className="col">
        {room.error && (
          <div className="card warn-card">
            <span className="warn">{room.error}</span>
            <button className="btn btn-sm" style={{ marginLeft: 8 }} onClick={room.clearError}>×</button>
          </div>
        )}

        <div className="panel">
          <h3>态势</h3>
          <div className="scroll">
            {!view ? '…' : view.kind === 'spectator' && view.summary ? (
              <div className="grid2">
                <Kv k="红方单位" v={view.summary.red_units} />
                <Kv k="蓝方单位" v={view.summary.blue_units} />
                <Kv k="红方兵力" v={view.summary.red_personnel} />
                <Kv k="蓝方兵力" v={view.summary.blue_personnel} />
                {/* 「接触目标」只有带 contacts 的视图才有的说：导演部/全知视图下发的是 units 全量真值，
                    没有 contacts 字段（web 在这一档恒显示 0，是个空壳行，这里不复刻） */}
                <Kv k="接触目标" v={(view.contacts ?? []).length} />
              </div>
            ) : view.kind === 'director' ? (
              <div className="grid2">
                <Kv k="红方单位" v={(view.units || []).filter(u => u.side === 'red' && u.status !== 'destroyed').length} />
                <Kv k="蓝方单位" v={(view.units || []).filter(u => u.side === 'blue' && u.status !== 'destroyed').length} />
                <Kv k="红方兵力" v={(view.units || []).filter(u => u.side === 'red').reduce((a, u) => a + (u.personnel || 0), 0)} />
                <Kv k="蓝方兵力" v={(view.units || []).filter(u => u.side === 'blue').reduce((a, u) => a + (u.personnel || 0), 0)} />
                <Kv k="胜负" v={view.over ? sideLabel(view.winner) : '进行中'} />
              </div>
            ) : (
              <div className="muted small">（该视图无汇总）</div>
            )}
          </div>
        </div>

        <div className="panel">
          <h3>公开接触</h3>
          <div className="scroll">
            {(() => {
              const contacts = view && view.kind !== 'director' ? view.contacts : []
              return contacts.length === 0 ? (
                <div className="muted small">{view?.kind === 'director' ? '（全知：见地图）' : '（暂无公开接触）'}</div>
              ) : (
                contacts.map(c => (
                  <div key={c.id + String(c.side)} className={`unit contact${c.stale ? ' unknown' : ''}`}>
                    <div className="hd">
                      <span className="id">{c.id}</span>
                      <span className={`tag ${c.side === 'red' ? 'red' : 'blue'}`}>{c.side === 'red' ? '红' : '蓝'} E{c.level}</span>
                    </div>
                    <div className="meta">
                      <span>{c.q},{c.r}</span>
                      {c.type_label && <span>{c.type_label}</span>}
                      {c.strength_band && <span>兵力{c.strength_band}</span>}
                      {c.stale && <span className="tag">{c.known_turns_ago}回合前</span>}
                    </div>
                  </div>
                ))
              )
            })()}
          </div>
        </div>

        <div className="panel grow">
          <h3>事件日志</h3>
          <div className="scroll">
            {(() => {
              const upto = replayIdx >= 0 && room.frames[replayIdx]
                ? room.log.filter(e => e.turn <= (room.frames[replayIdx] as Frame).turn)
                : room.log
              return upto.length === 0
                ? <div className="muted small">（无事件）</div>
                : upto.slice(-120).reverse().map((e, i) => (
                    <div key={i} className={`ev ${e.code}`}><span className="t">T{e.turn}</span>{e.text}</div>
                  ))
            })()}
          </div>
        </div>
      </div>
    </div>
  )
}

function kindLabel(v: AnyView, replayTurn: number | null) {
  // 回放帧带的是 frame.director（全知切片），但「导演部已授予」说的是实时授权状态：
  // 回放时沿用它会让人以为观战被开了全知，所以单独措辞。
  // 帧号取 frame.turn（被结算的那一回合），与滑杆、导演部战报同一口径；视图内的 turn 已是结算后。
  const kind = replayTurn === null
    ? v.kind === 'director' ? '全知（导演部已授予）' : v.kind === 'side' ? '对抗方视图' : '观战情报层'
    : `回放帧（导演部全知视角） T${replayTurn}`
  return kind
}

function Kv({ k, v }: { k: string; v: unknown }) {
  return <div className="budget"><span className="muted">{k}</span><b>{String(v ?? 0)}</b></div>
}
