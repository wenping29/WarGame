import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { fetchMe, login as apiLogin, logout as apiLogout, register as apiRegister, type AuthUser } from '../services/auth'

interface AuthContextValue {
  user: AuthUser | null
  /** 首次会话恢复是否完成（完成前不渲染导航，避免闪烁） */
  ready: boolean
  login: (username: string, password: string) => Promise<AuthUser>
  register: (username: string, password: string, displayName?: string) => Promise<AuthUser>
  logout: () => Promise<void>
  refresh: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue>({
  user: null,
  ready: false,
  login: async () => {
    throw new Error('AuthProvider 未挂载')
  },
  register: async () => {
    throw new Error('AuthProvider 未挂载')
  },
  logout: async () => {},
  refresh: async () => {},
})

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null)
  const [ready, setReady] = useState(false)

  const refresh = useCallback(async () => {
    const u = await fetchMe()
    setUser(u)
    setReady(true)
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const login = useCallback(async (username: string, password: string) => {
    const u = await apiLogin(username, password)
    setUser(u)
    return u
  }, [])

  const register = useCallback(async (username: string, password: string, displayName?: string) => {
    const u = await apiRegister(username, password, displayName)
    setUser(u)
    return u
  }, [])

  const logout = useCallback(async () => {
    await apiLogout()
    setUser(null)
  }, [])

  return (
    <AuthContext.Provider value={{ user, ready, login, register, logout, refresh }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthContextValue {
  return useContext(AuthContext)
}
