/**
 * 首页：三个入口 - 创建副本 / 一键开局 / 存档副本
 */

import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { listSaves } from '../services/api'

interface SaveInfo {
  id: string
  name: string
  savedAt: string
}

export default function Home() {
  const navigate = useNavigate()
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [showSaves, setShowSaves] = useState(false)

  useEffect(() => {
    loadSaves()
  }, [])

  const loadSaves = async () => {
    try {
      const list = await listSaves()
      setSaves(list)
    } catch (e) {
      console.error('Failed to load saves:', e)
    }
  }

  const toggleSaves = () => {
    setShowSaves(!showSaves)
  }

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
          onClick={toggleSaves}
        >
          <b>存档副本</b>
          <span className="hint">
            {saves.length ? `${saves.length} 个副本可载入` : '暂无存档'}
          </span>
        </button>
      </div>
      
      {showSaves && (
        <div className="home-list">
          <h3>已存副本 ({saves.length})</h3>
          {saves.length === 0 ? (
            <div className="muted small">暂无存档</div>
          ) : (
            <div className="grid-3">
              {saves.map((save) => (
                <div key={save.id} className="card scn-card">
                  <div>
                    <b>{save.name}</b>
                  </div>
                  <div className="kv">
                    <span>保存于: {new Date(save.savedAt).toLocaleString('zh-CN')}</span>
                  </div>
                  <div className="foot">
                    <button 
                      className="btn btn-sm btn-primary"
                      onClick={() => navigate(`/director?load=${save.id}`)}
                    >
                      载入
                    </button>
                    <button 
                      className="btn btn-sm"
                      onClick={() => {/* TODO: delete save */}}
                    >
                      删除
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
