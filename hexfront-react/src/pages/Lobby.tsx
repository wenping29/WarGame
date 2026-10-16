import { useNavigate } from 'react-router-dom'

export default function Lobby() {
  const navigate = useNavigate()

  return (
    <div className="page lobby-page">
      <h2>大厅</h2>
      <div className="lobby-actions">
        <button onClick={() => navigate('/director')}>
          进入导演部
        </button>
        <button onClick={() => navigate('/side/red')}>
          红方视角
        </button>
        <button onClick={() => navigate('/side/blue')}>
          蓝方视角
        </button>
        <button onClick={() => navigate('/spectator')}>
          观战视角
        </button>
      </div>
    </div>
  )
}
