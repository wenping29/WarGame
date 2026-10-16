/**
 * Canvas-based hex grid map renderer.
 * Renders terrain, features, units, and fog of war overlay.
 */

import { useEffect, useRef, useCallback } from 'react'
import type { Cell } from '../services/api'
import type { Unit as StoreUnit } from '../store/gameStore'

interface HexMapProps {
  width: number
  height: number
  cells: Cell[]
  units: StoreUnit[]
  selectedUnit?: StoreUnit | null
  hoveredHex?: { q: number; r: number } | null
  pathPoints?: Array<{ q: number; r: number }>
  attackTarget?: StoreUnit | null
  reachableHexes?: Set<string>
  obsPoints?: Record<string, number> // hexKey -> observation points
  onHexClick?: (q: number, r: number) => void
  onUnitClick?: (unit: StoreUnit) => void
  onPathPointAdd?: (q: number, r: number) => void
}

// Terrain colors matching the design system
const TERRAIN_COLORS: Record<string, string> = {
  clear: '#7a9e6b',      // 平原 - green
  rough: '#8b7355',      // 荒地 - brown
  woods: '#4a7c3f',      // 林地 - dark green
  hills: '#a0826d',      // 丘陵 - tan
  mountain: '#6b5d52',   // 山地 - gray-brown
  city: '#808080',       // 城镇 - gray
  swamp: '#5a7a6a',      // 沼泽 - dark teal
  river: '#4a90e2',      // 河流 - blue
  water: '#3a7bd5',      // 水域 - deep blue
}

// Hex geometry constants
const HEX_SIZE = 25 // pixels (flat-to-flat / 2)
const HEX_WIDTH = Math.sqrt(3) * HEX_SIZE
const HEX_HEIGHT = 2 * HEX_SIZE

export default function HexMap({
  width,
  height,
  cells,
  units,
  selectedUnit,
  hoveredHex,
  pathPoints = [],
  attackTarget,
  reachableHexes = new Set(),
  obsPoints = {},
  onHexClick,
  onUnitClick,
  onPathPointAdd,
}: HexMapProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null)

  // Convert hex coordinates to pixel coordinates
  const hexToPixel = useCallback((q: number, r: number) => {
    const x = HEX_WIDTH * (q + r / 2)
    const y = HEX_HEIGHT * 0.75 * r
    return { x, y }
  }, [])

  // Draw a single hexagon
  const drawHex = useCallback(
    (ctx: CanvasRenderingContext2D, q: number, r: number, color: string, alpha = 1) => {
      const { x, y } = hexToPixel(q, r)
      
      ctx.save()
      ctx.globalAlpha = alpha
      ctx.fillStyle = color
      
      ctx.beginPath()
      for (let i = 0; i < 6; i++) {
        const angle = (Math.PI / 3) * i - Math.PI / 6
        const px = x + HEX_SIZE * Math.cos(angle)
        const py = y + HEX_SIZE * Math.sin(angle)
        if (i === 0) ctx.moveTo(px, py)
        else ctx.lineTo(px, py)
      }
      ctx.closePath()
      ctx.fill()
      
      // Draw border
      ctx.strokeStyle = 'rgba(0,0,0,0.3)'
      ctx.lineWidth = 1
      ctx.stroke()
      
      ctx.restore()
    },
    [hexToPixel]
  )

  // Draw terrain layer
  const drawTerrain = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      cells.forEach((cell) => {
        const color = TERRAIN_COLORS[cell.terrain] || TERRAIN_COLORS.clear
        drawHex(ctx, cell.q, cell.r, color)
      })
    },
    [cells, drawHex]
  )

  // Draw features (roads, rivers, bridges, etc.)
  const drawFeatures = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      cells.forEach((cell) => {
        const { x, y } = hexToPixel(cell.q, cell.r)
        
        // Draw roads
        if (cell.road) {
          ctx.strokeStyle = '#d4a574'
          ctx.lineWidth = 2
          ctx.beginPath()
          ctx.moveTo(x - HEX_SIZE * 0.7, y)
          ctx.lineTo(x + HEX_SIZE * 0.7, y)
          ctx.stroke()
        }
        
        // Draw railways
        if (cell.railway) {
          ctx.strokeStyle = '#8b4513'
          ctx.lineWidth = 2
          ctx.setLineDash([4, 2])
          ctx.beginPath()
          ctx.moveTo(x - HEX_SIZE * 0.7, y)
          ctx.lineTo(x + HEX_SIZE * 0.7, y)
          ctx.stroke()
          ctx.setLineDash([])
        }
        
        // Draw rivers
        if (cell.river) {
          ctx.strokeStyle = '#2a6cb8'
          ctx.lineWidth = 3
          ctx.beginPath()
          ctx.moveTo(x, y - HEX_SIZE * 0.6)
          ctx.lineTo(x, y + HEX_SIZE * 0.6)
          ctx.stroke()
        }
        
        // Draw bridge
        if (cell.bridge) {
          ctx.fillStyle = '#8b7355'
          ctx.fillRect(x - 4, y - 2, 8, 4)
        }
        
        // Draw city
        if (cell.city > 0) {
          ctx.fillStyle = '#ffffff'
          ctx.font = 'bold 12px sans-serif'
          ctx.textAlign = 'center'
          ctx.textBaseline = 'middle'
          ctx.fillText('🏙', x, y)
        }
        
        // Draw airport
        if (cell.airport > 0) {
          ctx.fillStyle = '#ffffff'
          ctx.font = '10px sans-serif'
          ctx.textAlign = 'center'
          ctx.textBaseline = 'middle'
          ctx.fillText('✈', x, y + 8)
        }
      })
    },
    [cells, hexToPixel]
  )

  // Draw units
  const drawUnits = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      units.forEach((unit) => {
        const { x, y } = hexToPixel(unit.q, unit.r)
        const isSelected = selectedUnit?.id === unit.id
        
        // Unit circle background
        ctx.fillStyle = unit.side === 'red' ? '#c15b4e' : '#4e82b3'
        if (isSelected) {
          ctx.fillStyle = '#ffd700' // Gold for selected
        }
        
        ctx.beginPath()
        ctx.arc(x, y, 10, 0, Math.PI * 2)
        ctx.fill()
        
        // Border
        ctx.strokeStyle = isSelected ? '#fff' : 'rgba(0,0,0,0.5)'
        ctx.lineWidth = isSelected ? 3 : 1.5
        ctx.stroke()
        
        // Unit icon/text
        ctx.fillStyle = '#fff'
        ctx.font = 'bold 10px sans-serif'
        ctx.textAlign = 'center'
        ctx.textBaseline = 'middle'
        const icon = getUnitIcon(unit.type)
        ctx.fillText(icon, x, y)
        
        // HP bar (using hp field from store Unit)
        const hpPercent = unit.hp / 100 // Assuming 100 is max HP
        ctx.fillStyle = hpPercent > 0.7 ? '#4caf50' : hpPercent > 0.4 ? '#ff9800' : '#f44336'
        ctx.fillRect(x - 10, y + 12, 20 * hpPercent, 3)
      })
    },
    [units, selectedUnit, hexToPixel]
  )

  // Draw fog of war overlay
  const drawFog = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      cells.forEach((cell) => {
        const key = `${cell.q},${cell.r}`
        const points = obsPoints[key] || 0
        
        // Unobserved hexes get dark overlay
        if (points === 0) {
          const { x, y } = hexToPixel(cell.q, cell.r)
          ctx.fillStyle = 'rgba(0,0,0,0.7)'
          ctx.beginPath()
          for (let i = 0; i < 6; i++) {
            const angle = (Math.PI / 3) * i - Math.PI / 6
            const px = x + HEX_SIZE * Math.cos(angle)
            const py = y + HEX_SIZE * Math.sin(angle)
            if (i === 0) ctx.moveTo(px, py)
            else ctx.lineTo(px, py)
          }
          ctx.closePath()
          ctx.fill()
        }
        // Partially observed (E1-E2) get lighter overlay
        else if (points < 3) {
          const { x, y } = hexToPixel(cell.q, cell.r)
          const alpha = 0.3 + (points / 3) * 0.3
          ctx.fillStyle = `rgba(0,0,0,${alpha})`
          ctx.beginPath()
          for (let i = 0; i < 6; i++) {
            const angle = (Math.PI / 3) * i - Math.PI / 6
            const px = x + HEX_SIZE * Math.cos(angle)
            const py = y + HEX_SIZE * Math.sin(angle)
            if (i === 0) ctx.moveTo(px, py)
            else ctx.lineTo(px, py)
          }
          ctx.closePath()
          ctx.fill()
        }
      })
    },
    [cells, obsPoints, hexToPixel]
  )

  // Draw hover highlight
  const drawHover = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      if (!hoveredHex) return
      
      const { x, y } = hexToPixel(hoveredHex.q, hoveredHex.r)
      ctx.strokeStyle = '#fff'
      ctx.lineWidth = 3
      ctx.beginPath()
      for (let i = 0; i < 6; i++) {
        const angle = (Math.PI / 3) * i - Math.PI / 6
        const px = x + HEX_SIZE * Math.cos(angle)
        const py = y + HEX_SIZE * Math.sin(angle)
        if (i === 0) ctx.moveTo(px, py)
        else ctx.lineTo(px, py)
      }
      ctx.closePath()
      ctx.stroke()
    },
    [hoveredHex, hexToPixel]
  )

  // Draw movement path
  const drawPath = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      if (pathPoints.length < 2) return

      // Draw path line
      ctx.strokeStyle = '#ffd700'
      ctx.lineWidth = 3
      ctx.setLineDash([5, 3])
      ctx.beginPath()

      pathPoints.forEach((point, i) => {
        const { x, y } = hexToPixel(point.q, point.r)
        if (i === 0) ctx.moveTo(x, y)
        else ctx.lineTo(x, y)
      })

      ctx.stroke()
      ctx.setLineDash([])

      // Draw path points
      pathPoints.forEach((point, i) => {
        const { x, y } = hexToPixel(point.q, point.r)
        
        // Start point
        if (i === 0) {
          ctx.fillStyle = '#4caf50'
          ctx.beginPath()
          ctx.arc(x, y, 6, 0, Math.PI * 2)
          ctx.fill()
        }
        // End point
        else if (i === pathPoints.length - 1) {
          ctx.fillStyle = '#f44336'
          ctx.beginPath()
          ctx.arc(x, y, 6, 0, Math.PI * 2)
          ctx.fill()
        }
        // Intermediate points
        else {
          ctx.fillStyle = '#ffd700'
          ctx.beginPath()
          ctx.arc(x, y, 4, 0, Math.PI * 2)
          ctx.fill()
        }
      })
    },
    [pathPoints, hexToPixel]
  )

  // Draw reachable hexes
  const drawReachable = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      if (reachableHexes.size === 0) return

      reachableHexes.forEach((key) => {
        const [q, r] = key.split(',').map(Number)
        const { x, y } = hexToPixel(q, r)
        
        ctx.fillStyle = 'rgba(76, 175, 80, 0.2)'
        ctx.beginPath()
        for (let i = 0; i < 6; i++) {
          const angle = (Math.PI / 3) * i - Math.PI / 6
          const px = x + HEX_SIZE * Math.cos(angle)
          const py = y + HEX_SIZE * Math.sin(angle)
          if (i === 0) ctx.moveTo(px, py)
          else ctx.lineTo(px, py)
        }
        ctx.closePath()
        ctx.fill()
      })
    },
    [reachableHexes, hexToPixel]
  )

  // Draw attack target indicator
  const drawAttackTarget = useCallback(
    (ctx: CanvasRenderingContext2D) => {
      if (!attackTarget) return

      const { x, y } = hexToPixel(attackTarget.q, attackTarget.r)
      
      // Red circle around target
      ctx.strokeStyle = '#f44336'
      ctx.lineWidth = 3
      ctx.beginPath()
      ctx.arc(x, y, 15, 0, Math.PI * 2)
      ctx.stroke()

      // Crosshair
      ctx.strokeStyle = '#f44336'
      ctx.lineWidth = 2
      ctx.beginPath()
      ctx.moveTo(x - 20, y)
      ctx.lineTo(x + 20, y)
      ctx.moveTo(x, y - 20)
      ctx.lineTo(x, y + 20)
      ctx.stroke()
    },
    [attackTarget, hexToPixel]
  )

  // Main render loop
  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    
    // Set canvas size
    const mapWidth = width * HEX_WIDTH + HEX_WIDTH / 2
    const mapHeight = height * HEX_HEIGHT * 0.75 + HEX_HEIGHT / 2
    canvas.width = mapWidth
    canvas.height = mapHeight
    
    // Clear canvas
    ctx.clearRect(0, 0, canvas.width, canvas.height)
    
    // Draw layers in order
    drawTerrain(ctx)
    drawFeatures(ctx)
    drawReachable(ctx)
    drawPath(ctx)
    drawUnits(ctx)
    drawAttackTarget(ctx)
    drawFog(ctx)
    drawHover(ctx)
  }, [width, height, cells, units, selectedUnit, hoveredHex, pathPoints, attackTarget, reachableHexes, obsPoints, drawTerrain, drawFeatures, drawReachable, drawPath, drawUnits, drawAttackTarget, drawFog, drawHover])

  // Handle click events
  const handleClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current
    if (!canvas) return
    
    const rect = canvas.getBoundingClientRect()
    const mouseX = e.clientX - rect.left
    const mouseY = e.clientY - rect.top
    
    // Find clicked hex
    for (const cell of cells) {
      const { x, y } = hexToPixel(cell.q, cell.r)
      const dist = Math.sqrt((mouseX - x) ** 2 + (mouseY - y) ** 2)
      
      if (dist < HEX_SIZE) {
        // If drawing path, add point
        if (onPathPointAdd && pathPoints.length > 0) {
          onPathPointAdd(cell.q, cell.r)
          return
        }
        
        // Check if clicking on a unit
        const unit = units.find(u => u.q === cell.q && u.r === cell.r)
        if (unit && onUnitClick) {
          onUnitClick(unit)
        } else if (onHexClick) {
          onHexClick(cell.q, cell.r)
        }
        break
      }
    }
  }

  return (
    <canvas
      ref={canvasRef}
      onClick={handleClick}
      style={{
        border: '1px solid #33404a',
        borderRadius: '8px',
        cursor: 'pointer',
        display: 'block',
        margin: '0 auto',
      }}
    />
  )
}

// Helper: Get unit icon based on type
function getUnitIcon(type: string): string {
  const icons: Record<string, string> = {
    infantry: 'I',
    armor: 'A',
    mechanized: 'M',
    artillery: '🎯',
    hq: '★',
    depot: '📦',
    radar: '📡',
  }
  return icons[type] || '?'
}
