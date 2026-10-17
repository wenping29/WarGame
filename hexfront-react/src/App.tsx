import { BrowserRouter, Routes, Route } from 'react-router-dom'
import Home from './pages/Home'
import Lobby from './pages/Lobby'
import Wizard from './pages/Wizard'
import SideView from './pages/SideView'
import DirectorView from './pages/DirectorView'
import SpectatorView from './pages/SpectatorView'
import Layout from './components/Layout'
import ToastContainer from './components/Toast'

function App() {
  return (
    <BrowserRouter>
      <Layout>
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/lobby" element={<Lobby />} />
          <Route path="/new" element={<Wizard />} />
          <Route path="/side/:side" element={<SideView />} />
          <Route path="/director" element={<DirectorView />} />
          <Route path="/spectator" element={<SpectatorView />} />
        </Routes>
      </Layout>
      <ToastContainer />
    </BrowserRouter>
  )
}

export default App
