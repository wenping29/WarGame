/**
 * Bearer token 存取。独立小模块避免 api.ts ↔ auth.ts 循环依赖。
 */

const TOKEN_KEY = 'hf_token'

export const getToken = (): string | null => localStorage.getItem(TOKEN_KEY)
export const setToken = (t: string): void => localStorage.setItem(TOKEN_KEY, t)
export const clearToken = (): void => localStorage.removeItem(TOKEN_KEY)
