/**
 * Toast notification system
 */

import { useState, useCallback } from 'react'
import { play } from '../services/sound'

interface ToastMessage {
  id: number
  text: string
  type?: 'info' | 'warn' | 'err' | 'ok'
}

let nextId = 0
const toasts: ToastMessage[] = []
const listeners: Array<() => void> = []

function notify(text: string, type: ToastMessage['type'] = 'info') {
  play(type === 'err' ? 'err' : type === 'warn' ? 'warn' : 'ok')
  const id = nextId++
  toasts.push({ id, text, type })
  listeners.forEach(fn => fn())
  
  // Auto-remove after 5 seconds
  setTimeout(() => {
    const idx = toasts.findIndex(t => t.id === id)
    if (idx >= 0) {
      toasts.splice(idx, 1)
      listeners.forEach(fn => fn())
    }
  }, 5000)
}

export function toast(text: string, type?: ToastMessage['type']) {
  notify(text, type)
}

export function useToast() {
  const [messages, setMessages] = useState<ToastMessage[]>([])

  const update = useCallback(() => {
    setMessages([...toasts])
  }, [])

  // Subscribe to changes
  useState(() => {
    listeners.push(update)
    return () => {
      const idx = listeners.indexOf(update)
      if (idx >= 0) listeners.splice(idx, 1)
    }
  })

  return messages
}

export default function ToastContainer() {
  const messages = useToast()

  if (messages.length === 0) return null

  return (
    <div id="toast">
      {messages.map(msg => (
        <div key={msg.id} className={`toast-item ${msg.type || ''}`}>
          {msg.text}
        </div>
      ))}
    </div>
  )
}
