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

type Status = 'connecting' | 'live' | 'kicked' | 'closed'

interface DirectorCmd { action: string; [k: string]: unknown }

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
  code, role, name, children,
}: {
  code: string
  role: string
  name?: string
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
  const retryRef = useRef<number | undefined>(undefined)
  const [epoch, setEpoch] = useState(0)

  useEffect(() => {
    let ws: WebSocket | null = null
    let disposed = false
    setStatus('connecting')

    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    let url = `${proto}://${location.host}/ws?room=${encodeURIComponent(code)}&role=${encodeURIComponent(role)}`
    if (name) url += '&name=' + encodeURIComponent(name)
    if (tokenRef.current) url += '&token=' + tokenRef.current

    try {
      ws = new WebSocket(url)
    } catch {
      setStatus('closed')
      return
    }
    wsRef.current = ws

    ws.onopen = () => setStatus('live')
    ws.onmessage = (ev) => {
      let m: Record<string, unknown>
      try { m = JSON.parse(String(ev.data)) } catch { return }
      switch (m.type) {
        case 'welcome':
          tokenRef.current = (m.token as string) || null
          setError(null)
          break
        case 'state': {
          const s = m as unknown as StateMsg
          setState(s)
          // state 携带最近 200 条可见事件，直接作为日志基线
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
          break
        case 'error':
          setError((m.message as string) || '服务器错误')
          break
        case 'kicked':
          kickedRef.current = true
          setError((m.reason as string) || '本角色已在其他终端打开')
          setStatus('kicked')
          break
        default:
          break
      }
    }
    ws.onclose = () => {
      if (disposed) return
      wsRef.current = null
      if (kickedRef.current) { setStatus('kicked'); return }
      setStatus('closed')
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
  }, [code, role, name, epoch])

  const send = useCallback((obj: Record<string, unknown>) => {
    const ws = wsRef.current
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj))
  }, [])

  const rejoin = useCallback((force?: boolean) => {
    // 顶号：带 force=1 重开一条连接抢占角色位
    kickedRef.current = false
    tokenRef.current = null
    if (force) {
      const proto = location.protocol === 'https:' ? 'wss' : 'ws'
      const ws = new WebSocket(
        `${proto}://${location.host}/ws?room=${encodeURIComponent(code)}&role=${encodeURIComponent(role)}&force=1`)
      wsRef.current = ws
      ws.onopen = () => setStatus('live')
      ws.onclose = () => setStatus('closed')
      // 事件处理沿用同一套逻辑：直接复用下方 mount effect 的解析器不便，这里转发
      ws.onmessage = (ev) => {
        let m: Record<string, unknown>
        try { m = JSON.parse(String(ev.data)) } catch { return }
        handleRaw(m, setState, setLog, setFrames, setSaved, setError, setStatus, tokenRef, kickedRef)
      }
      return
    }
    setEpoch((e) => e + 1)
  }, [code, role])

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

/** rejoin(force) 通道与主通道共用同一消息解析 */
function handleRaw(
  m: Record<string, unknown>,
  setState: (s: StateMsg) => void,
  setLog: (f: (prev: WireEvent[]) => WireEvent[]) => void,
  setFrames: (f: Frame[]) => void,
  setSaved: (s: SavedMsg) => void,
  setError: (e: string | null) => void,
  setStatus: (s: Status) => void,
  tokenRef: { current: string | null },
  kickedRef: { current: boolean },
) {
  switch (m.type) {
    case 'welcome':
      tokenRef.current = (m.token as string) || null
      setError(null)
      break
    case 'state': {
      const s = m as unknown as StateMsg
      setState(s)
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
      break
    case 'error':
      setError((m.message as string) || '服务器错误')
      break
    case 'kicked':
      kickedRef.current = true
      setError((m.reason as string) || '本角色已在其他终端打开')
      setStatus('kicked')
      break
    default:
      break
  }
}
