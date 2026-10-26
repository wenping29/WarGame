/**
 * REST 层：与 C# HexFront.Api GameController 同契约（Dart bin/server.dart 对齐）。
 * dev 走 vite proxy（/api → localhost:5018），生产同源部署，绝不硬编码端口。
 */

import axios, { type AxiosInstance, AxiosError } from 'axios'
import type { WireWeather } from '../room/protocol'
import { clearToken, getToken } from './token'

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || '/api'

const apiClient: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  timeout: 30000,
  headers: {
    'Content-Type': 'application/json',
  },
})

// 登录后所有请求自动带上 Bearer token
apiClient.interceptors.request.use((config) => {
  const token = getToken()
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
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
      // 会话失效：清本地 token，让下次 fetchMe 回到未登录态
      if (error.response.status === 401) clearToken()
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
const put = async <T>(path: string, body: unknown): Promise<T> =>
  (await apiClient.put<T>(path, body)).data
const del = async <T>(path: string): Promise<T> =>
  (await apiClient.delete<T>(path)).data

// ==================== 契约类型 ====================

export interface ScenarioInfo {
  file: string
  id: string
  name: string
  era?: string
  scale?: string
  description?: string
  gis_package?: string | null
  /** 服务器六角格网络包 id（data/hexgrids/<id>.zip），优先级高于 gis_package 与 environment */
  hexgrid?: string | null
  environment?: Record<string, unknown>
  max_turns?: number
  /** 完整 sides（含 oob 明细），供向导卡片一键回填部队编制 */
  sides?: Record<string, unknown> | null
  flags?: Record<string, unknown> | null
  weather?: Record<string, unknown> | null
  clock?: Record<string, unknown> | null
  rules?: Record<string, unknown> | null
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

/** 副本已入库内容文件清单项（GET /api/admin/save_docs/{instance}，仅超管） */
export interface SaveDocMeta {
  kind: string
  turn: number | null
  bytes: number
  updated_at?: string
}

/** 副本单位位置编辑（GET/PUT /api/admin/save_units/{instance}，仅超管） */
export interface SaveUnitRow {
  id: string
  side: string
  unit_type: string
  is_hq: boolean
  personnel: number
  alive: boolean
  q: number
  r: number
}

export interface SaveUnitsInfo {
  instance: string
  /** 载入基准：init = 开局全量，snapshot = 最新回合快照 */
  base_kind: 'init' | 'snapshot' | string
  base_turn: number
  width: number
  height: number
  /** 网格几何：hex(0,0) 中心墨卡托米 + 外接圆半径（单位标记地图用）；缺省 = 无地图 */
  geo?: { ox: number; oy: number; radius: number } | null
  /** 挖除格键（"q,r"） */
  holes?: string[]
  units: SaveUnitRow[]
}

/** 服务器六角格网络包清单项（GET /api/hexgrids） */
export interface HexGridMeta {
  id: string
  name: string
  rows: number
  cols: number
  flat_to_flat: number
  west: number
  south: number
  bbox: [number, number, number, number]
  default_terrain: string
  holes_count: number
  terrains_count: number
  created_at: string
  updated_at: string
  source: string
  used_by: string[]
}

/** 服务器六角格网络包全文（GET /api/hexgrids/{id} 的 grid 字段） */
export interface HexGridDoc {
  id: string
  name: string
  rows: number
  cols: number
  flat_to_flat: number
  west: number
  south: number
  default_terrain: string
  holes: string[]
  terrains: Record<string, string>
  created_at: string
  updated_at: string
  source: string
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
  /** 会话计数；doc §12.1 每个角色位同时只有一条会话（阵营位并发被拒、导演/观战顶号），所以取值只可能是 0/1 */
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
  /** 战场六角格走 overrides.hexgrid（服务器网络包 id），与其他覆盖同一入口 */
  overrides?: Record<string, unknown>
  force?: boolean
}

export interface ScenariosMeta {
  scenarios: ScenarioInfo[]
  gis: Array<Record<string, unknown>>
  saves: SaveInfo[]
  weather: WireWeather[]
  unit_types: Array<Record<string, unknown>>
  /** 与 Dart/C# 一致是数组（每项带 id），不是按 id 索引的表 */
  terrains: Array<Record<string, unknown>>
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
  /** 生成完成后自动存储到的服务器网络包 id（overrides.hexgrid 用它）；发布失败为 null */
  hexgrid_id?: string | null
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

// ==================== 系统管理（AdminController，仅超级管理员） ====================

export interface AdminUser {
  id: number
  username: string
  display_name?: string | null
  role_code: string
  status: string
  created_at: string
  last_login_at?: string | null
}

export interface AdminRole {
  id: number
  code: string
  name: string
  description?: string | null
  user_count?: number
}

export interface AdminMenu {
  id: number
  code: string
  name: string
  path?: string | null
  sort: number
  visible: number
}

export interface RoleMenuRow {
  role_code: string
  menu_code: string
}

export interface AdminLoginLog {
  id: number
  ts: string
  username?: string | null
  user_id?: number | null
  action: string
  ok: number
  ip?: string | null
  user_agent?: string | null
  detail?: string | null
}

export interface AdminQueryLog {
  id: number
  ts: string
  method: string
  path: string
  status?: number | null
  duration_ms?: number | null
  username?: string | null
  ip?: string | null
}

export interface AdminErrorLog {
  id: number
  ts: string
  source: string
  path?: string | null
  message: string
  stack?: string | null
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
  deleteSave: (instance: string) =>
    del<{ instance: string; deleted: boolean; db_rows?: number; error?: string }>(
      '/saves/' + encodeURIComponent(instance)),
  // ==================== 副本管理（内容查看走超管端点） ====================
  adminSaveDocs: (instance: string) =>
    get<{ instance: string; files: SaveDocMeta[] }>('/admin/save_docs/' + encodeURIComponent(instance)),
  adminSaveDoc: (instance: string, kind: string, turn?: number | null) =>
    get<string>('/admin/save_doc/' + encodeURIComponent(instance) +
      '?kind=' + encodeURIComponent(kind) + (turn != null ? '&turn=' + turn : '')),
  adminSaveUnits: (instance: string) =>
    get<SaveUnitsInfo>('/admin/save_units/' + encodeURIComponent(instance)),
  adminSaveUnitsUpdate: (instance: string, positions: Array<{ unit: string; q: number; r: number }>) =>
    put<{
      instance: string; base_kind: string; base_turn: number
      updated: number; unknown: string[]; invalid: string[]
    }>('/admin/save_units/' + encodeURIComponent(instance), { positions }),
  // ==================== 服务器六角格网络包（剧本只存 hexgrid id） ====================
  hexgrids: () => get<{ grids: HexGridMeta[]; count: number }>('/hexgrids'),
  hexgridGet: (id: string) => get<{ grid: HexGridDoc; bbox: [number, number, number, number] }>(
    '/hexgrids/' + encodeURIComponent(id)),
  hexgridDelete: (id: string) =>
    del<{ id: string; deleted: boolean; error?: string; used_by?: string[] }>(
      '/hexgrids/' + encodeURIComponent(id)),
  gisCapabilities: () => get<GisCapabilities>('/gis/capabilities'),
  gisPlan: (bbox: [number, number, number, number], cellKm?: number) =>
    get<GisPlan>('/gis/plan?w=' + bbox[0] + '&s=' + bbox[1] + '&e=' + bbox[2] + '&n=' + bbox[3] +
      (cellKm ? '&cell_km=' + cellKm : '')),
  gisJobs: () => get<{ jobs: GisJob[] }>('/gis/jobs'),
  gisJob: (id: string) => get<GisJob>('/gis/jobs/' + id),
  gisStartJob: (spec: GisJobSpec) => post<GisJob>('/gis/jobs', spec),
  gisPackage: (name: string) => get<unknown>('/gis/packages/' + name),
  gisValidate: (packageName: string) =>
    post<{ file: string; total: number; fails: number; warns: number; passes: number; results: Array<{ level: string; check: string; detail: string }> }>('/gis/validate', { package_name: packageName }),
  // ==================== 剧本管理（ScenAdminController） ====================
  scenDetail: (id: string) => get<{ file: string; doc: Record<string, unknown> }>('/scenadmin/' + id),
  scenSave: (id: string, doc: unknown, validateOnly = false) =>
    put<{ ok: boolean; errors: string[]; file?: string; backup?: string | null; saved_at?: string }>(
      // 必须传 true：后端 bool 查询绑定不认 "1"，传 1 会被当成 false 而静默落盘保存
      '/scenadmin/' + id + (validateOnly ? '?validateOnly=true' : ''), doc),
  feedbackList: () => get<{ feedback: FeedbackEntry[] }>('/scenadmin/feedback'),
  feedbackAdd: (body: { scenario: string; kind: string; detail: string; contact?: string }) =>
    post<{ ok: boolean; entry: FeedbackEntry }>('/scenadmin/feedback', body),
  feedbackRemove: (fid: string) => del<{ ok: boolean }>('/scenadmin/feedback/' + fid),
  // ==================== 系统管理（仅超级管理员） ====================
  adminUsers: () => get<{ users: AdminUser[] }>('/admin/users'),
  adminCreateUser: (b: { username: string; password: string; display_name?: string; role: string }) =>
    post<{ ok: boolean }>('/admin/users', b),
  adminUpdateUser: (id: number, b: { display_name?: string; role?: string; status?: string; password?: string }) =>
    put<{ ok: boolean }>('/admin/users/' + id, b),
  adminDeleteUser: (id: number) => del<{ ok: boolean }>('/admin/users/' + id),
  adminRoles: () => get<{ roles: AdminRole[] }>('/admin/roles'),
  adminCreateRole: (b: { code: string; name: string; description?: string }) => post<{ ok: boolean }>('/admin/roles', b),
  adminUpdateRole: (code: string, b: { name: string; description?: string }) =>
    put<{ ok: boolean }>('/admin/roles/' + code, b),
  adminDeleteRole: (code: string) => del<{ ok: boolean }>('/admin/roles/' + code),
  adminSetRoleMenus: (code: string, menus: string[]) =>
    put<{ ok: boolean }>('/admin/roles/' + code + '/menus', { menus }),
  adminMenus: () => get<{ menus: AdminMenu[]; role_menus: RoleMenuRow[] }>('/admin/menus'),
  adminCreateMenu: (b: { code: string; name: string; path?: string; sort?: number; visible?: boolean }) =>
    post<{ ok: boolean }>('/admin/menus', b),
  adminUpdateMenu: (id: number, b: { name: string; path?: string; sort?: number; visible?: boolean }) =>
    put<{ ok: boolean }>('/admin/menus/' + id, b),
  adminDeleteMenu: (id: number) => del<{ ok: boolean }>('/admin/menus/' + id),
  adminLoginLogs: (limit = 100, username?: string) =>
    get<{ logs: AdminLoginLog[] }>('/admin/logs/login?limit=' + limit + (username ? '&username=' + encodeURIComponent(username) : '')),
  adminQueryLogs: (limit = 200, path?: string) =>
    get<{ logs: AdminQueryLog[] }>('/admin/logs/query?limit=' + limit + (path ? '&path=' + encodeURIComponent(path) : '')),
  adminErrorLogs: (limit = 100) => get<{ logs: AdminErrorLog[] }>('/admin/logs/error?limit=' + limit),
}

/** 剧本内容错误反馈条目（data/scenario_feedback.json） */
export interface FeedbackEntry {
  id: string
  scenario: string
  kind: string
  detail: string
  contact?: string
  status: string
  created_at: string
}

export default apiClient
