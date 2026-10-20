import { Link, useLocation } from 'react-router-dom'
import { useState, type ReactNode } from 'react'
import { isMuted, play, setMuted } from '../services/sound'
import { useAuth } from './Auth'
import { roleLabel } from '../services/auth'

interface LayoutProps {
  children: ReactNode
}

/** 导航项与菜单表的 code 对应；未登录时展示除系统管理外的全部入口 */
const NAV = [
  { code: 'lobby', to: '/lobby', label: '大厅' },
  { code: 'new_game', to: '/new', label: '创建副本' },
  { code: 'saves', to: '/saves', label: '副本管理' },
  { code: 'grid', to: '/grid', label: '六角格管理' },
  { code: 'scenarios', to: '/scenarios', label: '剧本管理' },
  { code: 'system', to: '/admin', label: '系统管理' },
]

export default function Layout({ children }: LayoutProps) {
  const location = useLocation()
  const [muted, setLocalMuted] = useState(isMuted())
  const { user, ready, logout } = useAuth()

  const visible = NAV.filter((n) => {
    if (n.code === 'system') return !!user && user.role === 'super_admin'
    if (!user) return true
    return user.menus.some((m) => m.code === n.code)
  })

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          兵棋对抗 <span className="sub">HEXFRONT-REACT</span>
        </div>
        <nav className="nav">
          <Link to="/" className={location.pathname === '/' ? 'on' : ''}>
            首页
          </Link>
          {visible.map((n) => (
            <Link key={n.code} to={n.to} className={location.pathname === n.to ? 'on' : ''}>
              {n.label}
            </Link>
          ))}
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
