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
  max_turns?: number
  seed?: number
  created_at: string
  updated_at?: string
  turns_recorded?: number
  snapshot_turn?: number
  description?: string
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
  seed?: number
  fingerprint?: string
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
  /** 数据包选择走 overrides.gis_package，与其他覆盖同一入口 */
  overrides?: Record<string, unknown>
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

// ==================== GIS 现场生成契约（GET plan / jobs，字段一律 snake_case） ====================

export interface GisCapabilities {
  available: boolean
  engine: 'python-hexgen' | 'synthetic' | string
  python?: string | null
  python_version?: string | null
  rasterio?: boolean
  dem_present?: boolean
  dem_tif?: string | null
  water_vector_ok?: boolean
  water_reason?: string | null
  vector_ok?: boolean
  vector_reason?: string | null
  roads_gdb_present?: boolean
  roads_gdb?: string | null
  airports_present?: boolean
  airports_shp?: string | null
  reason?: string | null
}

export type PlanRungLabel = 'recommended' | 'heavy' | 'coarse' | 'ok' | string

export interface GisPlanRung {
  cell_km: number
  width: number
  height: number
  cells: number
  label: PlanRungLabel
}

export interface GisPlan {
  ok?: boolean
  error?: string
  bbox: [number, number, number, number]
  center_lonlat: [number, number]
  ground_km: [number, number]
  mercator_scale: number
  target_cells: number
  min_side: number
  dem_floor_km: number
  max_cells: number
  recommended: { cell_km: number; reasons: string[] }
  /** 只在请求带了 cell_km 时出现 */
  current?: GisPlanRung & { mercator_scale?: number }
  ladder: GisPlanRung[]
}

export type GisJobStatus = 'queued' | 'running' | 'done' | 'failed' | string

export interface GisJob {
  id: string
  status: GisJobStatus
  name: string
  spec?: Record<string, unknown>
  log: string[]
  error?: string | null
  meta?: {
    ok?: boolean
    name?: string
    source?: string
    cell_km?: number
    width?: number
    height?: number
    fails?: number
    warns?: number
    layers?: Record<string, string>
    terrain_counts?: Record<string, number>
    errors?: string[]
  }
  created_at?: string
}

export interface GisJobSpec {
  bbox: [number, number, number, number]
  cell_km: number
  name: string
  dem: 'auto' | 'local' | 'synthetic'
  landuse: 'auto' | 'proxy' | 'off'
  roads: boolean
  water: boolean
  network: boolean
  features: boolean
  seed?: number
  offline?: boolean
}

/** GIS 端点的失败一律 400/404 + {"error":"…"}；axios 会 throw，这里把服务端原文捞回来展示。 */
export const netError = (e: unknown): string => {
  const data = (e as { response?: { data?: { error?: string } } })?.response?.data
  return data?.error || String(e)
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
    post<{ code?: string; instance?: string; seed?: number; room?: RoomInfo; error?: string; errors?: string[] }>('/instances', body),
  loadSave: (instance: string) =>
    post<{
      code?: string
      resumed_turn?: number
      replayed_turns?: number[]
      fingerprint?: string
      replay_warnings?: string[]
      room?: RoomInfo
      error?: string
    }>('/saves/load', { instance }),
  gisCapabilities: () => get<GisCapabilities>('/gis/capabilities'),
  gisPlan: (bbox: [number, number, number, number], cellKm?: number) =>
    get<GisPlan>('/gis/plan?w=' + bbox[0] + '&s=' + bbox[1] + '&e=' + bbox[2] + '&n=' + bbox[3] +
      (cellKm ? '&cell_km=' + cellKm : '')),
  gisJobs: () => get<{ jobs: GisJob[] }>('/gis/jobs'),
  gisJob: (id: string) => get<GisJob>('/gis/jobs/' + id),
  gisStartJob: (spec: GisJobSpec) => post<GisJob>('/gis/jobs', spec),
  gisValidate: (packageName: string) =>
    post<{ file: string; total: number; fails: number; warns: number; passes: number; results: Array<{ level: string; check: string; detail: string }> }>('/gis/validate', { package_name: packageName }),
}

export default apiClient
