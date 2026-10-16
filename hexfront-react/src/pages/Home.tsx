import { useNavigate } from 'react-router-dom'

export default function Home() {
  const navigate = useNavigate()

  return (
    <div className="page home-page">
      <div className="home">
        <button
          className="home-tile"
          onClick={() => navigate('/new')}
        >
          <b>创建副本</b>
          <span className="hint">按剧本自定义部队与战场环境</span>
        </button>

        <button
          className="home-tile"
          onClick={() => navigate('/lobby')}
        >
          <b>一键开局</b>
          <span className="hint">默认剧本 · 零配置 · 直接进导演部</span>
        </button>

        <button
          className="home-tile"
          onClick={() => navigate('/lobby')}
        >
          <b>存档副本</b>
          <span className="hint">暂无存档</span>
        </button>
      </div>
    </div>
  )
}
