/**
 * 认证服务：登录 / 注册 / 登出 / 会话信息。
 * token 持久化在 localStorage，页面刷新后经 fetchMe 恢复会话。
 */

import apiClient from './api'
import { clearToken, getToken, setToken } from './token'

export interface AuthMenu {
  code: string
  name: string
  path: string
}

export interface AuthUser {
  id: number
  username: string
  display_name?: string | null
  /** 角色 code；me 接口返回的是 role 字段 */
  role: string
  menus: AuthMenu[]
  last_login_at?: string | null
}

export interface AdminRoleInfo {
  code: string
  name: string
  description?: string | null
}

/** 角色 code → 中文名（角色管理页新建的角色走 roles 接口取名字） */
export const ROLE_NAMES: Record<string, string> = {
  super_admin: '超级管理员',
  director: '导演部',
  red: '红军',
  blue: '蓝军',
  spectator: '观战',
}

export const roleLabel = (code: string, known?: AdminRoleInfo[]): string => {
  const hit = known?.find((r) => r.code === code)
  if (hit) return hit.name
  return ROLE_NAMES[code] ?? code
}

export async function login(username: string, password: string): Promise<AuthUser> {
  const r = await apiClient.post<{ token: string; user: AuthUser }>('/auth/login', { username, password })
  setToken(r.data.token)
  return r.data.user
}

/** 注册成功后自动登录 */
export async function register(username: string, password: string, display_name?: string): Promise<AuthUser> {
  await apiClient.post('/auth/register', { username, password, display_name: display_name || undefined })
  return login(username, password)
}

export async function logout(): Promise<void> {
  try {
    await apiClient.post('/auth/logout', {})
  } catch {
    /* token 失效时登出接口报错无所谓，本地清掉即可 */
  }
  clearToken()
}

/** 会话恢复：无 token 或 token 失效都返回 null */
export async function fetchMe(): Promise<AuthUser | null> {
  if (!getToken()) return null
  try {
    const r = await apiClient.get<{ user: AuthUser }>('/auth/me')
    return r.data.user
  } catch {
    clearToken()
    return null
  }
}
