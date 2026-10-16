import { useNavigate } from 'react-router-dom'
import { useGameStore } from '../store/gameStore'

export default function Lobby() {
  const navigate = useNavigate()
  const { setViewMode, setCurrentSide } = useGameStore()

  const enterDirector = () => {
    setViewMode('director')
    navigate('/director')
  }

  const enterSide = (side: 'red' | 'blue') => {
    setViewMode('side')
    setCurrentSide(side)
    navigate(`/side/${side}`)
  }

  const enterSpectator = () => {
    setViewMode('spectator')
    navigate('/spectator')
  }

  return (
    <div className="page lobby-page">
      <h2>大厅</h2>
      <div className="lobby-actions">
        <button onClick={enterDirector}>
          进入导演部
        </button>
        <button onClick={() => enterSide('red')}>
          红方视角
        </button>
        <button onClick={() => enterSide('blue')}>
          蓝方视角
        </button>
        <button onClick={enterSpectator}>
          观战视角
        </button>
      </div>
    </div>
  )
}
