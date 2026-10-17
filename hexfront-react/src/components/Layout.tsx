import { Link, useLocation } from 'react-router-dom'
import type { ReactNode } from 'react'
import { useWebSocket } from '../hooks/useWebSocket'

interface LayoutProps {
  children: ReactNode
}

export default function Layout({ children }: LayoutProps) {
  const location = useLocation()
  const { status } = useWebSocket({ autoConnect: false })

  const getStatusText = () => {
    switch (status) {
      case 'live': return '已连接'
      case 'connecting': return '连接中...'
      case 'dead': return '连接断开'
      default: return '本地模式'
    }
  }

  const getStatusClass = () => {
    switch (status) {
      case 'live': return 'conn live'
      case 'dead': return 'conn dead'
      default: return 'conn'
    }
  }

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
        </nav>
        <div className={getStatusClass()}>{getStatusText()}</div>
      </header>
      <main id="app">{children}</main>
      <div id="toast"></div>
    </div>
  )
}
