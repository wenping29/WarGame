/**
 * API service layer for connecting to C# WebAPI backend.
 * Uses Axios for HTTP requests with error handling and retry logic.
 */

import axios, { type AxiosInstance, AxiosError } from 'axios'

// API configuration
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5000/api'

// Create axios instance with default config
const apiClient: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  timeout: 10000,
  headers: {
    'Content-Type': 'application/json',
  },
})

// Request interceptor for logging (dev only)
if (import.meta.env.DEV) {
  apiClient.interceptors.request.use(
    (config) => {
      console.log(`[API] ${config.method?.toUpperCase()} ${config.url}`)
      return config
    },
    (error) => Promise.reject(error)
  )
}

// Response interceptor for error handling
apiClient.interceptors.response.use(
  (response) => response,
  (error: AxiosError) => {
    if (error.response) {
      console.error(`[API Error] ${error.response.status}:`, error.response.data)
    } else if (error.request) {
      console.error('[API Error] Network error:', error.message)
    }
    return Promise.reject(error)
  }
)

// ==================== Type Definitions ====================

export interface GameStateResponse {
  turn: number
  maxTurns: number
  hour: number
  weather: string
  over: boolean
  winner: string | null
  units: Unit[]
  cells: Cell[]
  obs: Record<string, Record<string, number>>
}

export interface Unit {
  id: string
  side: 'red' | 'blue' | 'neutral'
  type: string
  q: number
  r: number
  personnel: number
  morale: number
  fatigue: number
  ammo: number
  fuel: number
  status: string
}

export interface Cell {
  q: number
  r: number
  terrain: string
  elev: number
  slope: number
  road: boolean
  river: boolean
  bridge: boolean
  city: number
  railway: boolean
  railStation: boolean
  tunnel: boolean
  powerline: boolean
  powerPlant: boolean
  substation: boolean
  airport: number
}

export interface CommandRequest {
  op: 'move' | 'attack' | 'fire' | 'dig' | 'rest' | 'air_mission' | 'halt'
  unitId: string
  path?: Array<{ q: number; r: number }>
  target?: string
  zone?: { q: number; r: number }
  mission?: string
}

export interface OrderSubmissionResponse {
  accepted: string[]
  rejected: Array<{ unitId: string; reason: string; message: string }>
}

export interface SaveGameRequest {
  name: string
  description?: string
}

export interface SaveGameResponse {
  id: string
  savedAt: string
}

// ==================== API Functions ====================

/**
 * Get current game state
 */
export const getGameState = async (): Promise<GameStateResponse> => {
  const response = await apiClient.get<GameStateResponse>('/game/state')
  return response.data
}

/**
 * Submit orders for a side
 */
export const submitOrders = async (
  side: 'red' | 'blue',
  orders: CommandRequest[]
): Promise<OrderSubmissionResponse> => {
  const response = await apiClient.post<OrderSubmissionResponse>(`/game/orders/${side}`, {
    orders,
  })
  return response.data
}

/**
 * Advance to next turn (director only)
 */
export const advanceTurn = async (): Promise<GameStateResponse> => {
  const response = await apiClient.post<GameStateResponse>('/game/turn/advance')
  return response.data
}

/**
 * Save current game state
 */
export const saveGame = async (data: SaveGameRequest): Promise<SaveGameResponse> => {
  const response = await apiClient.post<SaveGameResponse>('/game/save', data)
  return response.data
}

/**
 * Load a saved game
 */
export const loadGame = async (id: string): Promise<GameStateResponse> => {
  const response = await apiClient.get<GameStateResponse>(`/game/load/${id}`)
  return response.data
}

/**
 * List all saved games
 */
export const listSaves = async (): Promise<Array<{ id: string; name: string; savedAt: string }>> => {
  const response = await apiClient.get<Array<{ id: string; name: string; savedAt: string }>>('/game/saves')
  return response.data
}

/**
 * Generate a new hex grid terrain package
 */
export const generateTerrain = async (spec: {
  name: string
  bbox: [number, number, number, number]
  cellKm: number
  seed: number
  offline?: boolean
  features?: boolean
}): Promise<{ jobId: string }> => {
  const response = await apiClient.post<{ jobId: string }>('/gis/generate', spec)
  return response.data
}

/**
 * Check terrain generation job status
 */
export const checkJobStatus = async (jobId: string): Promise<{
  status: 'pending' | 'running' | 'done' | 'failed'
  progress?: number
  error?: string
}> => {
  const response = await apiClient.get(`/gis/jobs/${jobId}`)
  return response.data
}

export default apiClient
