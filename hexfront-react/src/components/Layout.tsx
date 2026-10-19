import { Link, useLocation } from 'react-router-dom'
import { useState, type ReactNode } from 'react'
import { isMuted, play, setMuted } from '../services/sound'

interface LayoutProps {
  children: ReactNode
}

export default function Layout({ children }: LayoutProps) {
  const location = useLocation()
  const [muted, setLocalMuted] = useState(isMuted())

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
          <Link to="/lobby" className={location.pathname === '/lobby' ? 'on' : ''}>
            大厅
          </Link>
          <Link to="/grid" className={location.pathname === '/grid' ? 'on' : ''}>
            六角格管理
          </Link>
        </nav>
        <button
          className="btn btn-sm"
          style={{ marginLeft: 'auto' }}
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
      </header>
      <main id="app">{children}</main>
    </div>
  )
}
