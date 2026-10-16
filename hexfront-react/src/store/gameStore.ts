import { create } from 'zustand'
import type { CommandRequest } from '../services/api'

// 游戏状态类型定义
export interface Unit {
  id: string
  side: 'red' | 'blue' | 'neutral'
  type: string
  q: number
  r: number
  hp: number
  morale: number
  fatigue: number
}

export interface ValidationError {
  unitId: string
  reason: string
  message: string
}

export interface GameState {
  // 游戏基本信息
  turn: number
  maxTurns: number
  hour: number
  weather: string
  over: boolean
  winner: string | null

  // 单位和地图数据
  units: Unit[]
  cells: any[]

  // 当前视角
  viewMode: 'director' | 'side' | 'spectator'
  currentSide: 'red' | 'blue' | null

  // 选中的单位
  selectedUnit: Unit | null

  // 指令队列
  pendingOrders: CommandRequest[]
  validationErrors: ValidationError[]
  isSubmitting: boolean

  // Actions
  setTurn: (turn: number) => void
  setUnits: (units: Unit[]) => void
  setViewMode: (mode: 'director' | 'side' | 'spectator') => void
  setCurrentSide: (side: 'red' | 'blue' | null) => void
  selectUnit: (unit: Unit | null) => void
  nextTurn: () => void
  reset: () => void
  
  // Order management
  addOrder: (order: CommandRequest) => void
  removeOrder: (index: number) => void
  clearOrders: () => void
  setValidationErrors: (errors: ValidationError[]) => void
  clearValidationErrors: () => void
  setSubmitting: (submitting: boolean) => void
}

export const useGameStore = create<GameState>((set) => ({
  // 初始状态
  turn: 1,
  maxTurns: 14,
  hour: 6,
  weather: 'clear',
  over: false,
  winner: null,
  units: [],
  cells: [],
  viewMode: 'director',
  currentSide: null,
  selectedUnit: null,
  
  // Order management state
  pendingOrders: [],
  validationErrors: [],
  isSubmitting: false,

  // Actions
  setTurn: (turn) => set({ turn }),
  setUnits: (units) => set({ units }),
  setViewMode: (mode) => set({ viewMode: mode }),
  setCurrentSide: (side) => set({ currentSide: side }),
  selectUnit: (unit) => set({ selectedUnit: unit }),
  nextTurn: () => set((state) => ({ turn: state.turn + 1 })),
  reset: () => set({
    turn: 1,
    units: [],
    over: false,
    winner: null,
    selectedUnit: null,
    pendingOrders: [],
    validationErrors: [],
  }),
  
  // Order management actions
  addOrder: (order) => set((state) => ({
    pendingOrders: [...state.pendingOrders, order],
  })),
  removeOrder: (index) => set((state) => ({
    pendingOrders: state.pendingOrders.filter((_, i) => i !== index),
  })),
  clearOrders: () => set({ pendingOrders: [] }),
  setValidationErrors: (errors) => set({ validationErrors: errors }),
  clearValidationErrors: () => set({ validationErrors: [] }),
  setSubmitting: (submitting) => set({ isSubmitting: submitting }),
}))
