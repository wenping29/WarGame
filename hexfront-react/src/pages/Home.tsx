/**
 * 首页：三个入口 - 创建副本 / 一键开局 / 存档副本
 */

import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, type SaveInfo } from '../services/api'
import { loadMeta } from '../room/useUnitFull'
import { toast } from '../components/Toast'

export default function Home() {
  const navigate = useNavigate()
  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [showSaves, setShowSaves] = useState(false)

  const quickStart = async () => {
    toast('一键开局中…')
    try {
      const meta = await loadMeta(true)
      const s = meta.scenarios[0]
      if (!s) return toast('没有可用剧本', 'err')
      const res = await api.createInstance({ scenario: s.id || s.file })
      if (res.code) {
        toast('开局成功 · 房间 ' + res.code)
        navigate(`/room/${res.code}/director`)
      } else {
        toast(res.error || res.errors?.join(';') || '创建失败', 'err')
      }
    } catch (e) {
      toast('一键开局失败：' + e, 'err')
    }
  }

  const toggleSaves = async () => {
    const next = !showSaves
    setShowSaves(next)
    if (next) {
      try {
        const meta = await loadMeta()
        setSaves(meta.saves || [])
      } catch {
        setSaves([])
      }
    }
  }

  const loadSave = async (s: SaveInfo) => {
    const res = await api.loadSave(s.instance)
    if (res.code) {
      const replayed = res.replayed_turns?.length ?? 0
      toast(`已载入房间 ${res.code}（回合 ${res.resumed_turn}${replayed ? ` · 重放 ${replayed} 回合` : ''}）`)
      if (res.replay_warnings?.length) toast(`重放指纹与存档不符：${res.replay_warnings[0]}`, 'err')
      navigate(`/room/${res.code}/director`)
    } else {
      toast(res.error || '载入失败', 'err')
    }
  }

  // 删除已存副本：二次确认（不可恢复）；活跃房间占用时后端拒删（409）
  const removeSave = async (s: SaveInfo) => {
    const label = s.name ? `${s.name}（${s.instance}）` : s.instance
    if (!window.confirm(`删除副本 ${label}？将同时删除其存档目录与全部历史数据，不可恢复。`)) return
    try {
      const res = await api.deleteSave(s.instance)
      if (res.deleted) {
        toast('已删除副本 ' + s.instance)
        const meta = await loadMeta()
        setSaves(meta.saves || [])
      } else {
        toast(res.error || '删除失败', 'err')
      }
    } catch (e) {
      toast('删除失败：' + e, 'err')
    }
  }

  return (
    <div className="page home-page">
      <div className="home-hero">
        <h2>引擎权威的 Web 多人兵棋</h2>
        <p className="muted">
          战争迷雾隔离信息 · 指令双盲 · 服务端裁决。四种终端角色：
          <span className="tag">导演部</span>全知控局、
          <span className="tag red">红方</span>/<span className="tag blue">蓝方</span>对抗推演、
          <span className="tag">观战</span>复盘学习。
          同一浏览器可多开做双人同桌。
        </p>
      </div>
      <div className="home">
        <button className="home-tile" onClick={() => navigate('/new')}>
          <b>创建副本</b>
          <span className="hint">按剧本自定义部队与战场环境</span>
        </button>

        <button className="home-tile" onClick={quickStart}>
          <b>一键开局</b>
          <span className="hint">默认剧本 · 零配置 · 直接进导演部</span>
        </button>

        <button className="home-tile" onClick={() => navigate('/grid')}>
          <b>六角格管理</b>
          <span className="hint">创建 / 删除 / 修改六角格网络 · 行列数与对边距</span>
        </button>

        <button className="home-tile" onClick={toggleSaves}>
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
                <div key={save.instance} className="card scn-card">
                  <div><b>{save.name}</b></div>
                  <div className="kv">
                    <span>{save.scenario_name || save.scenario}</span>
                    <span>回合 {save.last_turn}</span>
                    <span>{new Date(save.created_at).toLocaleString('zh-CN')}</span>
                  </div>
                  <div className="foot">
                    <button className="btn btn-sm btn-primary" onClick={() => loadSave(save)}>
                      载入并导调
                    </button>
                    <button className="btn btn-sm btn-red" onClick={() => removeSave(save)}>
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
