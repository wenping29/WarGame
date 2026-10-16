/**
 * Order submission system: batch command queue, validation feedback, and turn advancement.
 */

import { useState, useCallback } from 'react'
import type { CommandRequest } from '../services/api'

interface ValidationError {
  unitId: string
  reason: string
  message: string
}

interface OrderSubmissionProps {
  pendingOrders: CommandRequest[]
  currentSide: 'red' | 'blue' | null
  onRemoveOrder: (index: number) => void
  onSubmitOrders: () => Promise<void>
  onAdvanceTurn: () => Promise<void>
  isSubmitting: boolean
  validationErrors: ValidationError[]
  clearValidationErrors: () => void
}

export default function OrderSubmission({
  pendingOrders,
  currentSide,
  onRemoveOrder,
  onSubmitOrders,
  onAdvanceTurn,
  isSubmitting,
  validationErrors,
  clearValidationErrors,
}: OrderSubmissionProps) {
  const [showHistory, setShowHistory] = useState(false)
  const [commandHistory, setCommandHistory] = useState<CommandRequest[]>([])

  // Add to history after successful submission
  const addToHistory = useCallback((orders: CommandRequest[]) => {
    setCommandHistory((prev) => [...prev, ...orders].slice(-50)) // Keep last 50 commands
  }, [])

  // Handle submit with history tracking
  const handleSubmit = async () => {
    if (pendingOrders.length === 0) return
    
    clearValidationErrors()
    await onSubmitOrders()
    
    // If no errors, add to history and clear pending
    if (validationErrors.length === 0) {
      addToHistory(pendingOrders)
    }
  }

  // Get operation label
  const getOpLabel = (op: string): string => {
    const labels: Record<string, string> = {
      move: '🚶 移动',
      attack: '⚔️ 攻击',
      fire: '💥 炮击',
      dig: '🏗️ 挖掘',
      rest: '😴 休整',
      air_mission: '✈️ 空袭',
      halt: '🛑 停止',
    }
    return labels[op] || op
  }

  // Format path for display
  const formatPath = (path?: Array<{ q: number; r: number }>): string => {
    if (!path || path.length === 0) return ''
    return `${path[0].q},${path[0].r} → ${path[path.length - 1].q},${path[path.length - 1].r} (${path.length}格)`
  }

  // Count orders by type
  const ordersByType = pendingOrders.reduce((acc, cmd) => {
    acc[cmd.op] = (acc[cmd.op] || 0) + 1
    return acc
  }, {} as Record<string, number>)

  return (
    <div className="order-submission">
      {/* Header */}
      <div className="submission-header">
        <h3>指令队列</h3>
        <button 
          className="history-toggle" 
          onClick={() => setShowHistory(!showHistory)}
        >
          {showHistory ? '隐藏历史' : `历史记录 (${commandHistory.length})`}
        </button>
      </div>

      {/* Current side indicator */}
      {currentSide && (
        <div className={`side-indicator ${currentSide}`}>
          当前阵营: {currentSide === 'red' ? '红方' : '蓝方'}
        </div>
      )}

      {/* Validation errors */}
      {validationErrors.length > 0 && (
        <div className="validation-errors">
          <div className="error-header">
            ⚠️ 验证失败 ({validationErrors.length}条)
          </div>
          <div className="error-list">
            {validationErrors.map((err, idx) => (
              <div key={idx} className="error-item">
                <strong>{err.unitId}</strong>: {err.message}
                <span className="error-reason">({err.reason})</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Pending orders list */}
      <div className="pending-orders">
        {pendingOrders.length === 0 ? (
          <div className="empty-queue">
            暂无待提交指令，点击地图上的单位下达命令
          </div>
        ) : (
          <>
            <div className="queue-summary">
              <span>共 {pendingOrders.length} 条指令</span>
              <span className="type-breakdown">
                {Object.entries(ordersByType).map(([op, count]) => (
                  <span key={op} className="type-tag">{getOpLabel(op)} ×{count}</span>
                ))}
              </span>
            </div>
            
            <div className="order-list">
              {pendingOrders.map((cmd, index) => (
                <div key={index} className="order-item">
                  <div className="order-info">
                    <span className="order-op">{getOpLabel(cmd.op)}</span>
                    <span className="order-unit">{cmd.unitId}</span>
                    {cmd.path && (
                      <span className="order-detail">{formatPath(cmd.path)}</span>
                    )}
                    {cmd.target && (
                      <span className="order-detail">目标: {cmd.target}</span>
                    )}
                    {cmd.zone && (
                      <span className="order-detail">区域: {cmd.zone.q},{cmd.zone.r}</span>
                    )}
                  </div>
                  <button
                    className="remove-btn"
                    onClick={() => onRemoveOrder(index)}
                    title="移除指令"
                  >
                    ✕
                  </button>
                </div>
              ))}
            </div>
          </>
        )}
      </div>

      {/* Action buttons */}
      <div className="submission-actions">
        <button
          className="btn-submit"
          onClick={handleSubmit}
          disabled={pendingOrders.length === 0 || isSubmitting}
        >
          {isSubmitting ? '提交中...' : `提交指令 (${pendingOrders.length})`}
        </button>
        
        <button
          className="btn-advance"
          onClick={onAdvanceTurn}
          disabled={pendingOrders.length > 0 || isSubmitting}
        >
          推进回合
        </button>
      </div>

      {/* Command history panel */}
      {showHistory && (
        <div className="history-panel">
          <div className="history-header">
            <h4>指令历史</h4>
            <button 
              className="clear-history"
              onClick={() => setCommandHistory([])}
              disabled={commandHistory.length === 0}
            >
              清空
            </button>
          </div>
          
          {commandHistory.length === 0 ? (
            <div className="empty-history">暂无历史记录</div>
          ) : (
            <div className="history-list">
              {commandHistory.slice().reverse().map((cmd, idx) => (
                <div key={idx} className="history-item">
                  <span className="history-op">{getOpLabel(cmd.op)}</span>
                  <span className="history-unit">{cmd.unitId}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
