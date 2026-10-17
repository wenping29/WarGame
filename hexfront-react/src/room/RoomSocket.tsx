/**
 * 房间连接：原生 /ws 协议（welcome/state/events/saved/frames/error/kicked/pong）。
 * 服务端是唯一真相，本文件只做收发与状态托管，不做任何本地模拟。
 */

import {
  createContext, useCallback, useContext, useEffect, useMemo, useRef, useState,
  type ReactNode,
} from 'react'
import type {
  AnyView, Frame, SavedMsg, StateMsg, WireCommand, WireEvent,
} from './protocol'
import { toast } from '../components/Toast'
import { play } from '../services/sound'
import { api as restApi } from '../services/api'

type Status = 'connecting' | 'live' | 'kicked' | 'closed' | 'missing'

interface DirectorCmd { action: string; [k: string]: unknown }

/// 视图里与"该出声"相关的计数快照
interface Snapshot { turn: number; contacts: number; errors: number }

function snapshotOf(view: AnyView | null): Snapshot | null {
  const v = view as unknown as { turn?: number; contacts?: unknown[]; errors?: unknown[] } | null
  if (!v || typeof v.turn !== 'number') return null
  return { turn: v.turn, contacts: v.contacts?.length ?? 0, errors: v.errors?.length ?? 0 }
}

/// 只比对增量，避免每条 state 广播都响：结算=回合推进，接触=敌情变多，警告=校验错误变多
function cueState(next: Snapshot | null, prev: Snapshot | null) {
  if (!next || !prev) return
  if (next.turn > prev.turn) play('resolve')
  else if (next.errors > prev.errors) play('warn')
  if (next.turn === prev.turn && next.contacts > prev.contacts) play('contact')
}

export interface RoomApi {
  code: string
  role: string
  status: Status
  state: StateMsg | null
  view: AnyView | null
  log: WireEvent[]
  frames: Frame[]
  saved: SavedMsg | null
  error: string | null
  clearError: () => void
  send: (obj: Record<string, unknown>) => void
  submitOrders: (commands: WireCommand[], replace?: boolean) => void
  setReady: (value: boolean) => void
  clearOrders: () => void
  forceResolve: () => void
  save: (name: string) => void
  requestFrames: () => void
  directorCmd: (cmd: DirectorCmd) => void
  rejoin: (force?: boolean) => void
}

const RoomCtx = createContext<RoomApi | null>(null)

export function useRoom(): RoomApi {
  const ctx = useContext(RoomCtx)
  if (!ctx) throw new Error('useRoom 必须在 RoomProvider 内使用')
  return ctx
}

export function RoomProvider({
  code, role, name, force, children,
}: {
  code: string
  role: string
  name?: string
  force?: boolean
  children: ReactNode
}) {
  const [status, setStatus] = useState<Status>('connecting')
  const [state, setState] = useState<StateMsg | null>(null)
  const [log, setLog] = useState<WireEvent[]>([])
  const [frames, setFrames] = useState<Frame[]>([])
  const [saved, setSaved] = useState<SavedMsg | null>(null)
  const [error, setError] = useState<string | null>(null)
  const wsRef = useRef<WebSocket | null>(null)
  const tokenRef = useRef<string | null>(null)
  const kickedRef = useRef(false)
  const forceRef = useRef(!!force)
  const forceSentRef = useRef(false)
  const errorRef = useRef<string | null>(null)
  const retryRef = useRef<number | undefined>(undefined)
  const snapRef = useRef<Snapshot | null>(null)
  const [epoch, setEpoch] = useState(0)

  const handle = useCallback((m: Record<string, unknown>) => {
    switch (m.type) {
      case 'welcome':
        tokenRef.current = (m.token as string) || null
        setError(null)
        errorRef.current = null
        // 接管成功（或正常入位）后才消费 force，避免 StrictMode 双挂载/重试把接管标志丢掉
        forceRef.current = false
        forceSentRef.current = false
        break
      case 'state': {
        const s = m as unknown as StateMsg
        const next = snapshotOf(s.view ?? null)
        cueState(next, snapRef.current)
        snapRef.current = next
        setState(s)
        // state 自带最近可见事件，作为日志基线；增量 events 在其后追加
        if (Array.isArray(s.events)) setLog(s.events)
        break
      }
      case 'events':
        setLog((prev) => [...prev, ...((m.events as WireEvent[]) || [])].slice(-400))
        break
      case 'frames':
        setFrames(((m.frames as Frame[]) || []).slice())
        break
      case 'saved':
        setSaved(m as unknown as SavedMsg)
        toast(`已存档 回合 ${m.turn} 副本 ${m.instance || ''}`)
        break
      case 'error':
        errorRef.current = (m.message as string) || '服务器错误'
        setError(errorRef.current)
        play('err')
        break
      case 'kicked':
        kickedRef.current = true
        setError((m.reason as string) || '本角色已在其他终端打开')
        setStatus('kicked')
        play('kick')
        break
      default:
        break
    }
  }, [])

  useEffect(() => {
    let ws: WebSocket | null = null
    let disposed = false
    let opened = false
    setStatus('connecting')
    // 重连首包不做差分，否则连接前的回合/敌情变化会被当成刚发生
    snapRef.current = null

    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    let url = `${proto}://${location.host}/ws?room=${encodeURIComponent(code)}&role=${encodeURIComponent(role)}`
    if (name) url += '&name=' + encodeURIComponent(name)
    if (tokenRef.current && !forceRef.current) url += '&token=' + tokenRef.current
    forceSentRef.current = forceRef.current
    if (forceRef.current) url += '&force=1'

    try {
      ws = new WebSocket(url)
    } catch {
      setStatus('closed')
      return
    }
    wsRef.current = ws
    ws.onmessage = (ev) => {
      try { handle(JSON.parse(String(ev.data))) } catch { /* 忽略坏帧 */ }
    }
    ws.onopen = () => { opened = true; setStatus('live') }
    ws.onclose = () => {
      if (disposed) return
      wsRef.current = null
      if (kickedRef.current) { setStatus('kicked'); return }
      const occupied = String(errorRef.current || '').includes('角色位')
      // 未带 force 却被告知"角色位占用"：直接给出接管入口，不自动重试刷屏
      if (occupied && !forceSentRef.current) { setStatus('kicked'); return }
      if (!opened) {
        // 握手都没成功过：未知房间码会被服务端直接 404 掉，无脑重连等于对着不存在的房间刷屏。
        // 探一次 REST，真没有就停在终态上把原因交给人。
        setStatus('closed')
        void restApi.room(code).then(
          () => { if (!disposed) scheduleRetry() },
          (e: unknown) => {
            if (disposed) return
            if ((e as { response?: { status?: number } })?.response?.status === 404) {
              setError('房间不存在：房间码有误，或服务端重启后内存局已清空，请用存档载入')
              play('err')
              setStatus('missing')
              return
            }
            scheduleRetry()
          },
        )
        return
      }
      setStatus('closed')
      // force 已发但服务端仍判占用（对端刚掉线、槽位未释放）：保留接管标志重试
      if (!occupied) {
        forceRef.current = false
        forceSentRef.current = false
      }
      scheduleRetry()
    }

    const scheduleRetry = () => {
      retryRef.current = window.setTimeout(() => setEpoch((e) => e + 1), 1500)
    }

    return () => {
      disposed = true
      if (retryRef.current) window.clearTimeout(retryRef.current)
      if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
        ws.onclose = null
        ws.close()
      }
    }
  }, [code, role, name, epoch, handle])

  const send = useCallback((obj: Record<string, unknown>) => {
    const ws = wsRef.current
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj))
  }, [])

  const rejoin = useCallback((force?: boolean) => {
    kickedRef.current = false
    tokenRef.current = null
    forceRef.current = !!force
    setEpoch((e) => e + 1)
  }, [])

  const api = useMemo<RoomApi>(() => ({
    code,
    role,
    status,
    state,
    view: state?.view ?? null,
    log,
    frames,
    saved,
    error,
    clearError: () => setError(null),
    send,
    submitOrders: (commands, replace = true) => send({ type: 'orders', replace, commands }),
    setReady: (value) => send({ type: 'ready', value }),
    clearOrders: () => send({ type: 'clear_orders' }),
    forceResolve: () => send({ type: 'resolve' }),
    save: (n) => send({ type: 'save', name: n }),
    requestFrames: () => send({ type: 'frames' }),
    directorCmd: (cmd) => send({ type: 'director_cmd', ...cmd }),
    rejoin,
  }), [code, role, status, state, log, frames, saved, error, send, rejoin])

  return <RoomCtx.Provider value={api}>{children}</RoomCtx.Provider>
}
