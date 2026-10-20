import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, netError, type SaveDocMeta, type SaveInfo } from '../services/api'
import { loadMeta } from '../room/useUnitFull'
import { useAuth } from '../components/Auth'
import { toast } from '../components/Toast'

/** 存档内容文件名（对齐 AppDbService.StoreSaveFile 的 kind 语义） */
const KIND_LABEL: Record<string, string> = {
  meta: '元信息 meta.json',
  init: '开局全量 init.json',
  snapshot: '回合快照 snapshot_tN.json',
  turns: '指令记录 turns.jsonl',
}

const fmtTime = (iso?: string) => {
  if (!iso) return '—'
  const d = new Date(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString('zh-CN', { hour12: false })
}

const fmtBytes = (n: number) =>
  n >= 1024 * 1024 ? (n / 1024 / 1024).toFixed(1) + ' MB' : n >= 1024 ? (n / 1024).toFixed(1) + ' KB' : n + ' B'

/** 尽量格式化 JSON（turns 是 JSONL/解析失败时原文返回） */
const pretty = (text: string) => {
  try { return JSON.stringify(JSON.parse(text), null, 2) } catch { return text }
}

/** 原文预览上限：init/snapshot 动辄几十 MB，超出只展示片段 */
const PREVIEW_LIMIT = 64 * 1024

export default function SaveManager() {
  const navigate = useNavigate()
  const { user, ready } = useAuth()
  const isSuper = user?.role === 'super_admin'

  const [saves, setSaves] = useState<SaveInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [q, setQ] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  // 内容查看：instance → 文件清单；选中文件 → 原文
  const [docsInst, setDocsInst] = useState<string | null>(null)
  const [docs, setDocs] = useState<SaveDocMeta[] | null>(null)
  const [docView, setDocView] = useState<{ kind: string; turn: number | null; text: string; truncated: boolean } | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      const meta = await loadMeta(true) // 强制绕过缓存，拿到最新列表
      setSaves(meta.saves || [])
    } catch (e) {
      toast(netError(e) || '载入副本列表失败', 'err')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    if (ready) void refresh()
  }, [ready, refresh])

  const filtered = useMemo(() => {
    const kw = q.trim().toLowerCase()
    if (!kw) return saves
    return saves.filter((s) =>
      [s.name, s.instance, s.scenario, s.scenario_name, s.description]
        .some((v) => (v || '').toLowerCase().includes(kw)))
  }, [saves, q])

  const stats = useMemo(() => ({
    total: saves.length,
    turns: saves.reduce((a, s) => a + (s.turns_recorded || 0), 0),
  }), [saves])

  const loadSave = async (s: SaveInfo) => {
    setBusy(s.instance)
    try {
      const res = await api.loadSave(s.instance)
      if (res.code) {
        toast(`已载入房间 ${res.code}（回合 ${res.resumed_turn}）`)
        navigate(`/room/${res.code}/director`)
      } else toast(res.error || '载入失败', 'err')
    } catch (e) {
      toast(netError(e) || '载入失败', 'err')
    } finally {
      setBusy(null)
    }
  }

  const removeSave = async (s: SaveInfo) => {
    const label = s.name || s.instance
    if (!window.confirm(`删除副本「${label}」？\n\n将同时删除其全部存档数据（${s.turns_recorded || 0} 回合记录）且不可恢复。`)) return
    setBusy(s.instance)
    try {
      const res = await api.deleteSave(s.instance)
      if (res.deleted) {
        toast('已删除副本 ' + label)
        if (docsInst === s.instance) setDocsInst(null)
        await refresh()
      } else toast(res.error || '删除失败', 'err')
    } catch (e) {
      toast(netError(e) || '删除失败', 'err')
    } finally {
      setBusy(null)
    }
  }

  const toggleDocs = async (instance: string) => {
    if (docsInst === instance) { setDocsInst(null); setDocView(null); return }
    setDocsInst(instance)
    setDocs(null)
    setDocView(null)
    try {
      const res = await api.adminSaveDocs(instance)
      setDocs(res.files || [])
    } catch (e) {
      setDocs([])
      toast(netError(e) || '内容清单读取失败（需要超级管理员）', 'err')
    }
  }

  const viewDoc = async (instance: string, kind: string, turn: number | null) => {
    setDocView({ kind, turn, text: '加载中…', truncated: false })
    try {
      const text = await api.adminSaveDoc(instance, kind, turn)
      const truncated = text.length > PREVIEW_LIMIT
      setDocView({ kind, turn, text: truncated ? text.slice(0, PREVIEW_LIMIT) : text, truncated })
    } catch (e) {
      setDocView({ kind, turn, text: '读取失败：' + netError(e), truncated: false })
    }
  }

  return (
    <div className="page sv-page">
      <div className="sv-head">
        <div>
          <h2 style={{ margin: 0 }}>已存副本管理</h2>
          <p className="muted" style={{ margin: '4px 0 0' }}>
            共 {stats.total} 个副本 · {stats.turns} 条回合记录 · 数据以 SQLite 为唯一真源
          </p>
        </div>
        <div className="sv-tools">
          <input
            className="sv-search"
            placeholder="搜索 名称 / 实例 / 剧本…"
            value={q}
            onChange={(e) => setQ(e.target.value)}
          />
          <button className="btn" onClick={() => void refresh()} disabled={loading}>
            {loading ? '刷新中…' : '刷新'}
          </button>
        </div>
      </div>

      {!loading && filtered.length === 0 && (
        <div className="panel" style={{ textAlign: 'center', padding: 40 }}>
          {saves.length === 0 ? '暂无已存副本——去创建一局推演后这里就会出现记录。' : '没有匹配的副本。'}
        </div>
      )}

      {filtered.length > 0 && (
        <div className="panel">
          <table className="adm-table sv-table">
            <thead>
              <tr>
                <th>副本名称</th>
                <th>剧本</th>
                <th>进度</th>
                <th>回合记录</th>
                <th>更新时间</th>
                <th>实例</th>
                <th style={{ width: 260 }}>操作</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((s) => (
                <SaveRow
                  key={s.instance}
                  s={s}
                  busy={busy === s.instance}
                  isSuper={isSuper}
                  docsOpen={docsInst === s.instance}
                  docs={docsInst === s.instance ? docs : undefined}
                  docView={docsInst === s.instance ? docView : null}
                  onLoad={() => void loadSave(s)}
                  onDelete={() => void removeSave(s)}
                  onToggleDocs={() => void toggleDocs(s.instance)}
                  onViewDoc={(k, t) => void viewDoc(s.instance, k, t)}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// ==================== 行 + 内容展开区 ====================

interface RowProps {
  s: SaveInfo
  busy: boolean
  isSuper: boolean
  docsOpen: boolean
  docs?: SaveDocMeta[] | null | undefined // undefined=未打开 null=加载中
  docView: { kind: string; turn: number | null; text: string; truncated: boolean } | null
  onLoad: () => void
  onDelete: () => void
  onToggleDocs: () => void
  onViewDoc: (kind: string, turn: number | null) => void
}

function SaveRow({ s, busy, isSuper, docsOpen, docs, docView, onLoad, onDelete, onToggleDocs, onViewDoc }: RowProps) {
  const prog = s.max_turns ? `${s.last_turn}/${s.max_turns}` : String(s.last_turn)
  return (
    <>
      <tr>
        <td>
          <b>{s.name || '(未命名)'}</b>
          {s.description && <div className="muted" style={{ fontSize: 12 }}>{s.description}</div>}
        </td>
        <td>{s.scenario_name || s.scenario}</td>
        <td className="mono">回合 {prog}</td>
        <td>{s.turns_recorded ?? '—'}</td>
        <td className="muted">{fmtTime(s.updated_at || s.created_at)}</td>
        <td className="mono muted" style={{ fontSize: 12 }}>{s.instance}</td>
        <td>
          <div className="sv-actions">
            <button className="btn btn-sm btn-primary" onClick={onLoad} disabled={busy}>载入并导调</button>
            {isSuper && (
              <button className="btn btn-sm" onClick={onToggleDocs}>
                {docsOpen ? '收起内容' : '内容'}
              </button>
            )}
            <button className="btn btn-sm btn-red" onClick={onDelete} disabled={busy}>删除</button>
          </div>
        </td>
      </tr>
      {docsOpen && (
        <tr>
          <td colSpan={7} style={{ background: 'rgba(255,255,255,.015)' }}>
            {docs === undefined || docs === null ? (
              <span className="muted">读取内容清单中…</span>
            ) : docs.length === 0 ? (
              <span className="muted">该副本暂无入库内容文件。</span>
            ) : (
              <div className="sv-docs">
                <div className="sv-doc-list">
                  {docs.map((f, i) => (
                    <button
                      key={i}
                      className={'sv-doc-item' + (docView && docView.kind === f.kind && docView.turn === f.turn ? ' on' : '')}
                      onClick={() => onViewDoc(f.kind, f.turn)}
                    >
                      <b>{KIND_LABEL[f.kind] || f.kind}{f.turn != null ? ` t${f.turn}` : ''}</b>
                      <span className="muted">{fmtBytes(f.bytes)}</span>
                    </button>
                  ))}
                </div>
                {docView && (
                  <pre className="sv-doc-view">
                    {docView.kind === 'turns' ? docView.text : pretty(docView.text)}
                    {docView.truncated && '\n\n…（内容过大已截断，完整原文请通过 API 查看）'}
                  </pre>
                )}
              </div>
            )}
          </td>
        </tr>
      )}
    </>
  )
}
