import { ReactNode } from 'react'
import { Link, useLocation } from 'react-router-dom'

interface LayoutProps {
  children: ReactNode
}

export default function Layout({ children }: LayoutProps) {
  const location = useLocation()

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          兵棋对抗 <span className="sub">HEXFRONT-REACT</span>
        </div>
        <nav className="nav">
          <Link to="/" className={location.pathname === '/' ? 'active' : ''}>
            首页
          </Link>
          <Link to="/lobby" className={location.pathname === '/lobby' ? 'active' : ''}>
            大厅
          </Link>
        </nav>
        <div className="conn">本地模式</div>
      </header>
      <main id="app">{children}</main>
      <div id="toast"></div>
    </div>
  )
}
