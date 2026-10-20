import { BrowserRouter, Routes, Route, Link } from 'react-router-dom'
import Home from './pages/Home'
import Lobby from './pages/Lobby'
import ScenManager from './pages/ScenManager'
import Wizard from './pages/Wizard'
import GridManager from './pages/GridManager'
import SaveManager from './pages/SaveManager'
import RoomPage from './pages/RoomPage'
import Admin from './pages/Admin'
import Login from './pages/Login'
import Layout from './components/Layout'
import { AuthProvider } from './components/Auth'
import ToastContainer from './components/Toast'

// 404 兜底：未知路径此前整页空白（只有顶栏），给出明确去向
function NotFound() {
  return (
    <div className="page" style={{ textAlign: 'center', paddingTop: 80 }}>
      <h2 style={{ marginBottom: 8 }}>页面不存在</h2>
      <p className="muted" style={{ marginTop: 0 }}>
        地址可能有误，或该房间已结束/被清理。
      </p>
      <div style={{ display: 'flex', gap: 10, justifyContent: 'center' }}>
        <Link className="btn btn-primary" to="/">回首页</Link>
        <Link className="btn" to="/lobby">去大厅找房间</Link>
      </div>
    </div>
  )
}

function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Layout>
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/lobby" element={<Lobby />} />
            <Route path="/scenarios" element={<ScenManager />} />
            <Route path="/new" element={<Wizard />} />
            <Route path="/saves" element={<SaveManager />} />
            <Route path="/grid" element={<GridManager />} />
            <Route path="/room/:code/:role" element={<RoomPage />} />
            <Route path="/admin" element={<Admin />} />
            <Route path="/login" element={<Login />} />
            <Route path="*" element={<NotFound />} />
          </Routes>
        </Layout>
        <ToastContainer />
      </BrowserRouter>
    </AuthProvider>
  )
}

export default App
