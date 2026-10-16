import { useState } from 'react'
import { useNavigate } from 'react-router-dom'

export default function Wizard() {
  const navigate = useNavigate()
  const [scenario, setScenario] = useState('default')
  const [mapSize, setMapSize] = useState('medium')

  return (
    <div className="page wizard-page">
      <h2>创建新游戏</h2>
      <div className="wizard-form">
        <div className="form-group">
          <label>剧本选择</label>
          <select value={scenario} onChange={(e) => setScenario(e.target.value)}>
            <option value="default">默认剧本</option>
            <option value="custom">自定义剧本</option>
          </select>
        </div>

        <div className="form-group">
          <label>地图尺寸</label>
          <select value={mapSize} onChange={(e) => setMapSize(e.target.value)}>
            <option value="small">小型 (20x15)</option>
            <option value="medium">中型 (30x20)</option>
            <option value="large">大型 (40x30)</option>
          </select>
        </div>

        <div className="wizard-actions">
          <button onClick={() => navigate('/lobby')}>取消</button>
          <button className="primary" onClick={() => navigate('/director')}>
            开始游戏
          </button>
        </div>
      </div>
    </div>
  )
}
