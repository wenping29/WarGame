import { useGameStore } from '../store/gameStore'

export default function DirectorView() {
  const { turn, maxTurns, units } = useGameStore()

  return (
    <div className="page director-page">
      <h2>导演部视角</h2>
      <div className="director-info">
        <span className="muted">回合: {turn}/{maxTurns}</span>
        <span className="muted">单位数: {units.length}</span>
      </div>
      <div className="director-view">
        <p>全局真实态 + 双方已受领指令</p>
        <div className="map-placeholder">
          地图渲染区域（待集成）
        </div>
      </div>
    </div>
  )
}
