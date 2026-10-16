import { create } from 'zustand'

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

  // Actions
  setTurn: (turn: number) => void
  setUnits: (units: Unit[]) => void
  setViewMode: (mode: 'director' | 'side' | 'spectator') => void
  setCurrentSide: (side: 'red' | 'blue' | null) => void
  selectUnit: (unit: Unit | null) => void
  nextTurn: () => void
  reset: () => void
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
  }),
}))
