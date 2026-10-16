import { useParams } from 'react-router-dom'

export default function SideView() {
  const { side } = useParams<{ side: string }>()
  const isRed = side === 'red'

  return (
    <div className="page side-view-page">
      <h2>{isRed ? '红方' : '蓝方'}视角</h2>
      <div className="side-view">
        <p>己方单位全知，敌方仅显示已暴露目标</p>
        <div className="map-placeholder">
          地图渲染区域（待集成）
        </div>
      </div>
    </div>
  )
}
