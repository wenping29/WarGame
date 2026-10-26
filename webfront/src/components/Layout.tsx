import { Link, useLocation } from 'react-router-dom'
import { useState, type ComponentType, type ReactNode } from 'react'
import { BookOpen, FolderOpen, Hexagon, Home, Plus, ScrollText, Settings, Users } from 'lucide-react'
import { isMuted, play, setMuted } from '../services/sound'
import { useAuth } from './Auth'
import { roleLabel } from '../services/auth'

interface LayoutProps {
  children: ReactNode
}

type NavItem = { code: string; to: string; label: string; icon: ComponentType<{ size?: number }> }

/** 导航项与菜单表的 code 对应；未登录时展示除系统管理外的全部入口；always 项不受授权限制 */
const NAV: Array<NavItem & { always?: boolean }> = [
  { code: 'lobby', to: '/lobby', label: '大厅', icon: Users },
  { code: 'new_game', to: '/new', label: '创建副本', icon: Plus },
  { code: 'saves', to: '/saves', label: '副本管理', icon: FolderOpen },
  { code: 'grid', to: '/grid', label: '六角格管理', icon: Hexagon },
  { code: 'scenarios', to: '/scenarios', label: '剧本管理', icon: ScrollText },
  { code: 'system', to: '/admin', label: '系统管理', icon: Settings },
  { code: 'guide', to: '/guide', label: '使用说明', icon: BookOpen, always: true },
]

const HOME: NavItem = { code: 'home', to: '/', label: '首页', icon: Home }

export default function Layout({ children }: LayoutProps) {
  const location = useLocation()
  const [muted, setLocalMuted] = useState(isMuted())
  const { user, ready, logout } = useAuth()

  const visible = NAV.filter((n) => {
    if (n.code === 'system') return !!user && user.role === 'super_admin'
    if (n.always || !user) return true
    return user.menus.some((m) => m.code === n.code)
  })

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          兵棋对抗 <span className="sub">HEXFRONT-REACT</span>
        </div>
        <nav className="nav">
          {[HOME, ...visible].map((n) => {
            const Icon = n.icon
            return (
              <Link
                key={n.code}
                to={n.to}
                className={location.pathname === n.to ? 'on' : ''}
                title={n.label}
              >
                <Icon size={14} aria-hidden />
                <span className="nav-label">{n.label}</span>
              </Link>
            )
          })}
        </nav>
        <div className="topbar-right">
          {ready && user && (
            <span className="user-chip" title={`${user.username} · ${roleLabel(user.role)}`}>
              {user.display_name || user.username}
              <em>{roleLabel(user.role)}</em>
            </span>
          )}
          {ready && user && (
            <button className="btn btn-sm" onClick={() => void logout()}>
              退出
            </button>
          )}
          {ready && !user && (
            <Link className="btn btn-sm" to="/login" state={{ from: location.pathname }}>
              登录
            </Link>
          )}
          <button
            className="btn btn-sm"
            title="交互提示音开关"
            onClick={() => {
              const next = !muted
              setMuted(next)
              setLocalMuted(next)
              if (!next) play('ok')
            }}
          >
            提示音 {muted ? '关' : '开'}
          </button>
        </div>
      </header>
      <main id="app">{children}</main>
    </div>
  )
}
