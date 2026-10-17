/**
 * WebSocket connection management hook
 */

import { useState, useEffect, useCallback, useRef } from 'react'

interface WSMessage {
  type: string
  data: any
}

interface UseWebSocketOptions {
  url?: string
  autoConnect?: boolean
  onMessage?: (msg: WSMessage) => void
  onError?: (err: Event) => void
  onOpen?: () => void
  onClose?: () => void
}

export function useWebSocket(options: UseWebSocketOptions = {}) {
  const {
    url = import.meta.env.VITE_WS_URL || 'ws://localhost:5000/ws',
    autoConnect = false,
    onMessage,
    onError,
    onOpen,
    onClose,
  } = options

  const [status, setStatus] = useState<'disconnected' | 'connecting' | 'live' | 'dead'>('disconnected')
  const wsRef = useRef<WebSocket | null>(null)
  const reconnectTimerRef = useRef<number | null>(null)

  const connect = useCallback(() => {
    if (wsRef.current?.readyState === WebSocket.OPEN) return
    
    setStatus('connecting')
    
    try {
      const ws = new WebSocket(url)
      wsRef.current = ws

      ws.onopen = () => {
        setStatus('live')
        onOpen?.()
        if (reconnectTimerRef.current) {
          clearTimeout(reconnectTimerRef.current)
          reconnectTimerRef.current = null
        }
      }

      ws.onmessage = (event) => {
        try {
          const msg = JSON.parse(event.data) as WSMessage
          onMessage?.(msg)
        } catch (e) {
          console.error('Failed to parse WS message:', e)
        }
      }

      ws.onerror = (err) => {
        setStatus('dead')
        onError?.(err)
      }

      ws.onclose = () => {
        setStatus('disconnected')
        onClose?.()
        
        // Auto-reconnect after 3 seconds
        if (!reconnectTimerRef.current) {
          reconnectTimerRef.current = window.setTimeout(() => {
            connect()
          }, 3000)
        }
      }
    } catch (e) {
      console.error('WebSocket connection failed:', e)
      setStatus('dead')
    }
  }, [url, onMessage, onError, onOpen, onClose])

  const disconnect = useCallback(() => {
    if (reconnectTimerRef.current) {
      clearTimeout(reconnectTimerRef.current)
      reconnectTimerRef.current = null
    }
    
    if (wsRef.current) {
      wsRef.current.close()
      wsRef.current = null
    }
    
    setStatus('disconnected')
  }, [])

  const send = useCallback((data: any) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify(data))
    } else {
      console.warn('WebSocket not connected, cannot send')
    }
  }, [])

  useEffect(() => {
    if (autoConnect) {
      connect()
    }
    
    return () => {
      disconnect()
    }
  }, [autoConnect, connect, disconnect])

  return {
    status,
    connect,
    disconnect,
    send,
    isConnected: status === 'live',
  }
}

export default useWebSocket
