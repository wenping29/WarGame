export default function SpectatorView() {
  return (
    <div className="page spectator-page">
      <h2>观战视角</h2>
      <div className="spectator-view">
        <p>仅公开情报层（双方已暴露目标的并集）</p>
        <div className="map-placeholder">
          地图渲染区域（待集成）
        </div>
      </div>
    </div>
  )
}
