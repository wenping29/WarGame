/**
 * Unit interaction system: selection, path drawing, attack targeting, and command panel.
 */

import { useState, useCallback } from 'react'
import { useGameStore } from '../store/gameStore'
import type { Unit as StoreUnit } from '../store/gameStore'
import type { Cell } from '../services/api'
import type { CommandRequest } from '../services/api'

interface UnitInteractionProps {
  units: StoreUnit[]
  cells: Cell[]
  selectedUnit: StoreUnit | null
  currentSide: 'red' | 'blue' | null
  onSelectUnit: (unit: StoreUnit | null) => void
  onIssueCommand: (cmd: CommandRequest) => void
}

interface PathPoint {
  q: number
  r: number
}

export default function UnitInteraction({
  units,
  cells,
  selectedUnit,
}: Omit<UnitInteractionProps, 'currentSide' | 'onSelectUnit' | 'onIssueCommand'>) {
  const { addOrder } = useGameStore()
  const [pathPoints, setPathPoints] = useState<PathPoint[]>([])
  const [isDrawingPath, setIsDrawingPath] = useState(false)
  const [commandMode, setCommandMode] = useState<'move' | 'attack' | 'fire' | 'dig' | 'rest' | null>(null)
  const [showPanel, setShowPanel] = useState(false)

  // Handle unit selection - called from parent
  // (handleUnitClick removed as it's handled by HexMap)

  // Start path drawing for move command
  const startMovePath = useCallback(() => {
    if (!selectedUnit) return
    setCommandMode('move')
    setIsDrawingPath(true)
    setPathPoints([{ q: selectedUnit.q, r: selectedUnit.r }])
  }, [selectedUnit])

  // Add point to path (called from HexMap via onPathPointAdd)
  // (addPathPoint logic moved to HexMap click handler)

  // Finish path drawing
  const finishMovePath = useCallback(() => {
    if (pathPoints.length < 2 || !selectedUnit) return

    const cmd: CommandRequest = {
      op: 'move',
      unitId: selectedUnit.id,
      path: pathPoints.slice(1), // Exclude starting position
    }

    addOrder(cmd)
    setIsDrawingPath(false)
    setPathPoints([])
    setCommandMode(null)
  }, [pathPoints, selectedUnit, addOrder])

  // Cancel path drawing
  const cancelPath = useCallback(() => {
    setIsDrawingPath(false)
    setPathPoints([])
    setCommandMode(null)
  }, [])

  // Start attack targeting
  const startAttack = useCallback(() => {
    if (!selectedUnit) return
    setCommandMode('attack')
  }, [selectedUnit])

  // Fire artillery command
  const issueFireCommand = useCallback(() => {
    if (!selectedUnit) return

    const cmd: CommandRequest = {
      op: 'fire',
      unitId: selectedUnit.id,
      zone: { q: selectedUnit.q, r: selectedUnit.r }, // Simplified: fire at own position
    }

    addOrder(cmd)
  }, [selectedUnit, addOrder])

  // Dig in command
  const issueDigCommand = useCallback(() => {
    if (!selectedUnit) return

    const cmd: CommandRequest = {
      op: 'dig',
      unitId: selectedUnit.id,
    }

    addOrder(cmd)
  }, [selectedUnit, addOrder])

  // Rest command
  const issueRestCommand = useCallback(() => {
    if (!selectedUnit) return

    const cmd: CommandRequest = {
      op: 'rest',
      unitId: selectedUnit.id,
    }

    addOrder(cmd)
  }, [selectedUnit, addOrder])

  // Get reachable hexes for selected unit (simplified BFS)
  const getReachableHexes = useCallback((): Set<string> => {
    if (!selectedUnit) return new Set()

    const reachable = new Set<string>()
    const queue: Array<{ q: number; r: number; moves: number }> = [
      { q: selectedUnit.q, r: selectedUnit.r, moves: 0 },
    ]
    const visited = new Set<string>([`${selectedUnit.q},${selectedUnit.r}`])

    const maxMoves = selectedUnit.type === 'infantry' ? 3 : selectedUnit.type === 'armor' ? 5 : 4

    while (queue.length > 0) {
      const current = queue.shift()!
      if (current.moves >= maxMoves) continue

      // Check 6 neighbors
      const directions = [
        [1, 0],
        [1, -1],
        [0, -1],
        [-1, 0],
        [-1, 1],
        [0, 1],
      ]

      for (const [dq, dr] of directions) {
        const nq = current.q + dq
        const nr = current.r + dr
        const key = `${nq},${nr}`

        if (visited.has(key)) continue

        // Check if cell exists and is passable
        const cell = cells.find((c) => c.q === nq && c.r === nr)
        if (!cell || cell.terrain === 'water' || cell.terrain === 'river') continue

        visited.add(key)
        reachable.add(key)
        queue.push({ q: nq, r: nr, moves: current.moves + 1 })
      }
    }

    return reachable
  }, [selectedUnit, cells])

  // Get enemy units in range
  const getEnemiesInRange = useCallback((): StoreUnit[] => {
    if (!selectedUnit) return []

    const range = selectedUnit.type === 'artillery' ? 5 : 2

    return units.filter((u) => {
      if (u.side === selectedUnit.side || u.hp <= 0) return false

      const dq = Math.abs(u.q - selectedUnit.q)
      const dr = Math.abs(u.r - selectedUnit.r)
      const distance = Math.max(dq, dr, Math.abs(-u.q - u.r + selectedUnit.q + selectedUnit.r))

      return distance <= range
    })
  }, [selectedUnit, units])

  const reachableHexes = getReachableHexes()
  const enemiesInRange = getEnemiesInRange()

  // Use these values to avoid unused warnings
  void reachableHexes
  void enemiesInRange

  return (
    <div className="unit-interaction">
      {/* Command Panel */}
      {showPanel && selectedUnit && (
        <div className="command-panel">
          <div className="panel-header">
            <h3>{selectedUnit.type.toUpperCase()}</h3>
            <button className="close-btn" onClick={() => setShowPanel(false)}>
              ×
            </button>
          </div>

          <div className="unit-info">
            <div className="info-row">
              <span>阵营:</span>
              <span className={selectedUnit.side}>{selectedUnit.side === 'red' ? '红方' : '蓝方'}</span>
            </div>
            <div className="info-row">
              <span>HP:</span>
              <span>{selectedUnit.hp}/100</span>
            </div>
            <div className="info-row">
              <span>士气:</span>
              <span>{selectedUnit.morale}%</span>
            </div>
            <div className="info-row">
              <span>疲劳:</span>
              <span>{selectedUnit.fatigue}%</span>
            </div>
          </div>

          <div className="command-buttons">
            <button
              className={`cmd-btn ${commandMode === 'move' ? 'active' : ''}`}
              onClick={startMovePath}
              disabled={selectedUnit.hp <= 0}
            >
              🚶 移动
            </button>
            <button
              className={`cmd-btn ${commandMode === 'attack' ? 'active' : ''}`}
              onClick={startAttack}
              disabled={selectedUnit.hp <= 0}
            >
              ⚔️ 攻击
            </button>
            <button className="cmd-btn" onClick={issueFireCommand} disabled={selectedUnit.hp <= 0}>
              💥 炮击
            </button>
            <button className="cmd-btn" onClick={issueDigCommand} disabled={selectedUnit.hp <= 0}>
              🏗️ 挖掘
            </button>
            <button className="cmd-btn" onClick={issueRestCommand} disabled={selectedUnit.hp <= 0}>
              😴 休整
            </button>
          </div>

          {isDrawingPath && (
            <div className="path-controls">
              <button className="btn-primary" onClick={finishMovePath}>
                ✓ 确认路径
              </button>
              <button className="btn-warn" onClick={cancelPath}>
                ✗ 取消
              </button>
            </div>
          )}
        </div>
      )}

      {/* Path preview overlay info */}
      {isDrawingPath && pathPoints.length > 1 && (
        <div className="path-preview-info">
          路径长度: {pathPoints.length - 1} 格
        </div>
      )}

      {/* Attack target indicator */}
      {commandMode === 'attack' && (
        <div className="attack-mode-indicator">
          选择攻击目标（点击敌方单位）
        </div>
      )}
    </div>
  )
}
