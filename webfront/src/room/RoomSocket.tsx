/**
 * 房间连接：原生 /ws 协议（welcome/state/events/saved/frames/error/kicked/pong）。
 * 服务端是唯一真相，本文件只做收发与状态托管，不做任何本地模拟。
 */

import {
  createContext, useCallback, useContext, useEffect, useMemo, useRef, useState,
  type ReactNode,
} from 'react'
import type {
  EventsMsg, ErrorMsg, Frame, KickedMsg, SavedMsg, ServerMsg, StateMsg, WelcomeMsg,
  AnyView, WireCommand, WireEvent,
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

/// 上一次握手拿到的令牌（按角色存，与 web/js/ws.js 同一个 key）
function readStoredToken(role: string): string | null {
  try { return localStorage.getItem('hf.tok.' + role) } catch { return null }
}

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
  // welcome 首包自带的视图：state 还没到时的第一帧（对齐 web 各视图读 welcome.view）
  const [welcomeView, setWelcomeView] = useState<AnyView | null>(null)
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
  const attemptsRef = useRef(0)
  const snapRef = useRef<Snapshot | null>(null)
  const [epoch, setEpoch] = useState(0)

  const handle = useCallback((m: ServerMsg) => {
    switch (m.type) {
      case 'welcome': {
        const w = m as unknown as WelcomeMsg
        tokenRef.current = w.token || null
        // 令牌要活过整页刷新：doc §12.1 的「同 token 重连接管原会话」靠它把阵营位认回自己人，
        // 只存在内存里会让每次刷新都变成一次陌生接管。
        try { if (w.token) localStorage.setItem('hf.tok.' + role, w.token) } catch { /* 无痕模式忽略 */ }
        setWelcomeView(w.view ?? null)
        setError(null)
        errorRef.current = null
        // 接管成功（或正常入位）后才消费 force，避免 StrictMode 双挂载/重试把接管标志丢掉
        forceRef.current = false
        forceSentRef.current = false
        break
      }
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
      case 'events': {
        const e = m as unknown as EventsMsg
        setLog((prev) => [...prev, ...(e.events || [])].slice(-400))
        break
      }
      case 'frames':
        setFrames(((m as unknown as { frames?: Frame[] }).frames || []).slice())
        break
      case 'saved': {
        const s = m as unknown as SavedMsg
        setSaved(s)
        toast(`已存档 回合 ${s.turn} 副本 ${s.instance || ''}`)
        break
      }
      case 'error': {
        const e = m as unknown as ErrorMsg
        errorRef.current = e.message || '服务器错误'
        setError(errorRef.current)
        play('err')
        break
      }
      case 'kicked': {
        const k = m as unknown as KickedMsg
        kickedRef.current = true
        setError(k.reason || '本角色已在其他终端打开')
        setStatus('kicked')
        play('kick')
        break
      }
      default:
        break
    }
  }, [role])

  useEffect(() => {
    let ws: WebSocket | null = null
    let disposed = false
    let opened = false
    setStatus('connecting')
    setWelcomeView(null)
    // 重连首包不做差分，否则连接前的回合/敌情变化会被当成刚发生
    snapRef.current = null

    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    let url = `${proto}://${location.host}/ws?room=${encodeURIComponent(code)}&role=${encodeURIComponent(role)}`
    if (name) url += '&name=' + encodeURIComponent(name)
    // 内存里没有就认localStorage 里上次握手拿到的令牌：刷新后凭它接回自己的阵营位
    const stored = tokenRef.current || readStoredToken(role)
    if (stored && !forceRef.current) { tokenRef.current = stored; url += '&token=' + stored }
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
    ws.onopen = () => { opened = true; attemptsRef.current = 0; setStatus('live') }
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

    // doc §12.1：重试 8 次后停止并给出原因，退避随次数递增（与 web/js/ws.js 一致）
    const scheduleRetry = () => {
      attemptsRef.current += 1
      if (attemptsRef.current > 8) {
        setStatus('closed')
        setError('重连次数过多，已停止（角色位可能被占用或服务离线）')
        play('err')
        return
      }
      const delay = Math.min(1000 * attemptsRef.current, 5000)
      retryRef.current = window.setTimeout(() => setEpoch((e) => e + 1), delay)
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
    view: state?.view ?? welcomeView,
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
  }), [code, role, status, state, welcomeView, log, frames, saved, error, send, rejoin])

  return <RoomCtx.Provider value={api}>{children}</RoomCtx.Provider>
}
