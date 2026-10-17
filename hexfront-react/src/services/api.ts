/**
 * REST 层：与 C# HexFront.Api GameController 同契约（Dart bin/server.dart 对齐）。
 * dev 走 vite proxy（/api → localhost:5018），生产同源部署，绝不硬编码端口。
 */

import axios, { type AxiosInstance, AxiosError } from 'axios'
import type { WireWeather } from '../room/protocol'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || '/api'

const apiClient: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  timeout: 30000,
  headers: {
    'Content-Type': 'application/json',
  },
})

if (import.meta.env.DEV) {
  apiClient.interceptors.request.use(
    (config) => {
      console.log(`[API] ${config.method?.toUpperCase()} ${config.url}`)
      return config
    },
    (error) => Promise.reject(error)
  )
}

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

const get = async <T>(path: string): Promise<T> =>
  (await apiClient.get<T>(path)).data
const post = async <T>(path: string, body: unknown): Promise<T> =>
  (await apiClient.post<T>(path, body)).data

// ==================== 契约类型 ====================

export interface ScenarioInfo {
  file: string
  id: string
  name: string
  era?: string
  scale?: string
  description?: string
  gis_package?: string | null
  environment?: Record<string, unknown>
  max_turns?: number
  sides?: Record<string, unknown>
  unit_count?: number
}

export interface SaveInfo {
  instance: string
  name: string
  scenario: string
  scenario_name?: string
  last_turn: number
  created_at: string
  weather?: string
  unit_count?: number
  file_size?: number
}

export interface RoomInfo {
  code: string
  instance: string
  name: string
  scenario: string
  scenario_name?: string
  turn: number
  max_turns: number
  weather: string
  over: boolean
  winner: string | null
  roles: Record<string, number>
  side_taken: Record<string, boolean>
  ai: Record<string, boolean>
  created_at: string
  fog: boolean
  width: number
  height: number
}

export interface ValidateResult {
  errors: string[]
  preview: Record<string, unknown>
  type_points: Record<string, number>
}

export interface CreateInstanceBody {
  scenario: string
  name?: string
  seed?: number
  ai_red?: boolean
  ai_blue?: boolean
  overrides?: Record<string, unknown>
  gis?: string
  force?: boolean
}

export interface ScenariosMeta {
  scenarios: ScenarioInfo[]
  gis: Array<Record<string, unknown>>
  saves: SaveInfo[]
  weather: WireWeather[]
  unit_types: Array<Record<string, unknown>>
  terrains: Record<string, Record<string, unknown>>
  rules_default: Record<string, unknown>
  tiles: Record<string, unknown>
}

// ==================== API（对齐 web/js/api.js） ====================

export const api = {
  scenarios: () => get<ScenariosMeta>('/scenarios'),
  rooms: () => get<{ rooms: RoomInfo[]; count?: number }>('/rooms'),
  room: (code: string) => get<RoomInfo>('/room/' + code),
  frames: (code: string) => get<{ frames: unknown[] }>('/frames/' + code),
  validate: (scenario: string, overrides: Record<string, unknown>) =>
    post<ValidateResult>('/validate', { scenario, overrides }),
  createInstance: (body: CreateInstanceBody) =>
    post<{ code?: string; instance?: string; room?: RoomInfo; error?: string; errors?: string[] }>('/instances', body),
  loadSave: (instance: string) =>
    post<{ code?: string; resumed_turn?: number; room?: RoomInfo; error?: string }>('/saves/load', { instance }),
  gisCapabilities: (refresh?: boolean) =>
    get<Record<string, unknown>>('/gis/capabilities' + (refresh ? '?refresh=1' : '')),
  gisPlan: (bbox: [number, number, number, number], cellKm?: number) =>
    get<Record<string, unknown>>('/gis/plan?w=' + bbox[0] + '&s=' + bbox[1] + '&e=' + bbox[2] + '&n=' + bbox[3] +
      (cellKm ? '&cell_km=' + cellKm : '')),
  gisJobs: () => get<Array<Record<string, unknown>>>('/gis/jobs'),
  gisJob: (id: string) => get<Record<string, unknown>>('/gis/jobs/' + id),
  gisStartJob: (spec: Record<string, unknown>) => post<Record<string, unknown>>('/gis/jobs', spec),
}

export default apiClient
