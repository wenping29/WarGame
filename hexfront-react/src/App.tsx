import { BrowserRouter, Routes, Route } from 'react-router-dom'
import Home from './pages/Home'
import Lobby from './pages/Lobby'
import Wizard from './pages/Wizard'
import RoomPage from './pages/RoomPage'
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
          <Route path="/room/:code/:role" element={<RoomPage />} />
        </Routes>
      </Layout>
      <ToastContainer />
    </BrowserRouter>
  )
}

export default App
