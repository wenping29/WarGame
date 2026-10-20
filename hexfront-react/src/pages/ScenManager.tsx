/**
 * 剧本管理页：左栏剧本列表（按分期分组 + 内容反馈徽标），
 * 右栏分节编辑器（基本信息 / 环境 / 模式与日夜 / 天气 / 胜负 / 部队编制 / JSON 源）。
 * 编辑基于剧本全文深拷贝原地改，保存前规范化数字字段并走服务端校验，落盘自动备份；
 * 内容错误反馈走服务端 SQLite 应用库（/api/scenadmin/feedback 提交/列表/删除）。
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, netError, type FeedbackEntry, type HexGridMeta, type ScenarioInfo } from '../services/api'
import { useMeta } from '../room/useUnitFull'
import { TERRAINS } from '../components/GeoMapView'
import { toast } from '../components/Toast'

const ERAS = ['', '抗日战争', '解放战争', '抗美援朝', '第二次世界大战', '其他']
const VICTORY_KINDS = ['annihilate', 'capture', 'nodes', 'survive'] as const
const FB_KINDS = ['部署错误', '史实错误', '数据错误', '地形问题', '天气问题', '其他']
const FLAGS = [
  ['fog', '战争迷雾'],
  ['air_superiority', '制空权'],
  ['strict_supply', '严格后勤'],
  ['night', '夜间作战'],
] as const

type Doc = Record<string, any>

function getIn(obj: Doc | null, path: string): any {
  let v: any = obj
  for (const k of path.split('.')) v = v?.[k]
  return v == null ? '' : v
}
function setIn(obj: Doc, path: string, value: any) {
  const keys = path.split('.')
  let cur: any = obj
  for (let i = 0; i < keys.length - 1; i++) {
    const k = keys[i]
    if (cur[k] == null) cur[k] = /^\d+$/.test(keys[i + 1]) ? [] : {}
    cur = cur[k]
  }
  cur[keys[keys.length - 1]] = value
}
const toInt = (v: unknown, def = 0) => {
  const n = Math.round(+(v as number))
  return v === '' || v == null || isNaN(n) ? def : n
}
const toFloat = (v: unknown, def = 0) => {
  const n = +(v as number)
  return v === '' || v == null || isNaN(n) ? def : n
}

/** 保存前规范化：表单里的字符串数字转回数值，空 commander 删除 */
function normalizeDoc(d: Doc) {
  const env = (d.environment ??= {})
  env.width = toInt(env.width, 20)
  env.height = toInt(env.height, 16)
  env.cell_km = toFloat(env.cell_km, 2.5)
  if (env.center_lonlat == null || !Array.isArray(env.center_lonlat)) env.center_lonlat = [0, 0]
  env.center_lonlat[0] = toFloat(env.center_lonlat[0])
  env.center_lonlat[1] = toFloat(env.center_lonlat[1])
  if (env.height_seed === '' || env.height_seed == null) delete env.height_seed
  else env.height_seed = toInt(env.height_seed)
  // 六角格网络包引用：剧本内只保留 id 字符串
  if (d.hexgrid == null || d.hexgrid === '') delete d.hexgrid
  else d.hexgrid = String(d.hexgrid)
  d.max_turns = toInt(d.max_turns, 12)

  const ck = (d.clock ??= {})
  ck.start_hour = toInt(ck.start_hour, 6)
  ck.season = ck.season || ''
  ck.night_from = ck.night_from === '' ? null : toInt(ck.night_from, 0)
  ck.night_to = ck.night_to === '' ? null : toInt(ck.night_to, 0)

  const wx = (d.weather ??= {})
  wx.initial = wx.initial || ''
  wx.mode = wx.mode || ''
  if (Array.isArray(wx.schedule)) {
    for (const p of wx.schedule) {
      p.from = toInt(p.from, 1)
      p.to = toInt(p.to, 1)
      p.weather = p.weather || ''
    }
  }

  if (Array.isArray(d.victory)) {
    for (const v of d.victory) {
      v.side = v.side || 'red'
      v.kind = v.kind || 'survive'
      v.turns = v.turns === '' || v.turns == null ? 0 : toInt(v.turns)
      v.amount = v.amount === '' || v.amount == null ? 0 : toInt(v.amount)
      v.target = v.target === '' ? null : v.target
    }
  }

  for (const side of ['red', 'blue'] as const) {
    const sc = (d.sides ??= {})[side] ??= {}
    sc.name = sc.name || side
    if (sc.points_budget === '' || sc.points_budget == null) delete sc.points_budget
    else sc.points_budget = toInt(sc.points_budget)
    if (!Array.isArray(sc.oob)) sc.oob = []
    for (const e of sc.oob) {
      e.type = e.type || 'inf'
      e.qty = toInt(e.qty, 1)
      if (!Array.isArray(e.at) || e.at.length < 2) e.at = [2, 2]
      e.at[0] = toInt(e.at[0], 2)
      e.at[1] = toInt(e.at[1], 2)
      e.training = toInt(e.training, 60)
      e.experience = toInt(e.experience, 60)
      if (e.morale !== '') e.morale = toInt(e.morale, 80)
      const cmd = e.commander
      if (cmd && (cmd.name || cmd.quality !== '')) {
        e.commander = { name: cmd.name || '', quality: cmd.quality === '' || cmd.quality == null ? 0.7 : toFloat(cmd.quality, 0.7) }
      } else delete e.commander
    }
  }
  return d
}

const numInput = (v: unknown) => (v == null ? '' : String(v))

export default function ScenManager() {
  const meta = useMeta()
  const [selId, setSelId] = useState('')
  const [file, setFile] = useState('')
  const [doc, setDoc] = useState<Doc | null>(null)
  const schedN = ((doc?.weather?.schedule || []) as Doc[]).length
  const [dirty, setDirty] = useState(false)
  const [errors, setErrors] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [search, setSearch] = useState('')
  const [jsonText, setJsonText] = useState('')
  const [fbOpen, setFbOpen] = useState(false)
  const [fbAll, setFbAll] = useState<FeedbackEntry[]>([])
  const [fbKind, setFbKind] = useState(FB_KINDS[0])
  const [fbDetail, setFbDetail] = useState('')
  const [fbContact, setFbContact] = useState('')

  const refreshFeedback = useCallback(() => {
    api.feedbackList().then(r => setFbAll(r.feedback || [])).catch(() => {})
  }, [])
  useEffect(refreshFeedback, [refreshFeedback])

  /* ---------- 六角格网络包（战场环境内创建，剧本只存 hexgrid id） ---------- */
  const [hgList, setHgList] = useState<HexGridMeta[]>([])
  const [hgBusy, setHgBusy] = useState(false)
  const [hgPick, setHgPick] = useState('')
  const [hgForm, setHgForm] = useState({ name: '', rows: '10', cols: '14', ftf: '2500', west: '107', south: '33', terrain: 'clear' })

  const refreshHexgrids = useCallback(() => {
    api.hexgrids().then(r => setHgList(r.grids || [])).catch(() => {})
  }, [])
  useEffect(refreshHexgrids, [refreshHexgrids])

  // 生成六角格包（zip 落服务器 data/hexgrids/）并把 id 绑进剧本，保存时落盘
  const hgCreate = async () => {
    const f = hgForm
    const rows = Math.round(+f.rows), cols = Math.round(+f.cols), ftf = +f.ftf
    const west = +f.west, south = +f.south
    if (!(rows >= 1 && rows <= 200) || !(cols >= 1 && cols <= 200)) return toast('行列需在 1–200 之间', 'err')
    if (!(ftf >= 10 && ftf <= 1000000)) return toast('对边距需在 10–1000000 米之间', 'err')
    if (!Number.isFinite(west) || !Number.isFinite(south)) return toast('锚点经纬度无效', 'err')
    setHgBusy(true)
    try {
      const r = await api.hexgridCreate({
        name: f.name.trim() || undefined, rows, cols, flat_to_flat: ftf, west, south,
        default_terrain: f.terrain || undefined, source: 'scenmgr',
      })
      toast(`已生成六角格包 ${r.grid.id}（${rows} 行 × ${cols} 列）`, 'ok')
      refreshHexgrids()
      upd(d => { d.hexgrid = r.grid.id })
    } catch (e) {
      toast('生成失败：' + netError(e), 'err')
    } finally {
      setHgBusy(false)
    }
  }

  const hgBind = (id: string) => {
    if (!id) return
    upd(d => { d.hexgrid = id })
    toast('已绑定网格包 ' + id + '，记得保存剧本')
  }

  // 默认选中第一个剧本
  useEffect(() => {
    if (!selId && meta?.scenarios.length) openScenario(meta.scenarios[0])
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [meta])

  const openScenario = async (s: ScenarioInfo) => {
    const id = s.id || s.file
    try {
      const r = await api.scenDetail(id)
      setSelId(id)
      setFile(r.file)
      setDoc(r.doc)
      setDirty(false)
      setErrors([])
      setJsonText(JSON.stringify(r.doc, null, 2))
    } catch (e) {
      toast('载入剧本失败：' + netError(e), 'err')
    }
  }

  const upd = (fn: (d: Doc) => void) => {
    setDoc(prev => {
      if (!prev) return prev
      const d: Doc = structuredClone(prev)
      fn(d)
      setJsonText(JSON.stringify(d, null, 2))
      return d
    })
    setDirty(true)
  }

  const validateOnly = async () => {
    if (!doc || !selId) return
    setBusy(true)
    try {
      const r = await api.scenSave(selId, normalizeDoc(structuredClone(doc)), true)
      setErrors(r.errors || [])
      toast(r.errors?.length ? `校验发现 ${r.errors.length} 个问题` : '校验通过', r.errors?.length ? 'err' : 'ok')
    } catch (e) {
      toast('校验失败：' + netError(e), 'err')
    } finally {
      setBusy(false)
    }
  }

  const save = async () => {
    if (!doc || !selId) return
    setBusy(true)
    try {
      const payload = normalizeDoc(structuredClone(doc))
      const r = await api.scenSave(selId, payload)
      if (r.errors?.length) {
        setErrors(r.errors)
        toast(`保存被拒：校验发现 ${r.errors.length} 个问题`, 'err')
      } else {
        setErrors([])
        setDirty(false)
        toast(`已保存${r.backup ? `（备份 ${r.backup}）` : ''}`, 'ok')
      }
    } catch (e) {
      toast('保存失败：' + netError(e), 'err')
    } finally {
      setBusy(false)
    }
  }

  const applyJson = () => {
    try {
      const d = JSON.parse(jsonText)
      setDoc(d)
      setDirty(true)
      toast('JSON 源已应用到编辑器')
    } catch (e) {
      toast('JSON 解析失败：' + (e as Error).message, 'err')
    }
  }

  const submitFeedback = async () => {
    if (!selId) return
    if (fbDetail.trim().length < 5) return toast('问题描述至少 5 个字', 'warn')
    try {
      await api.feedbackAdd({ scenario: selId, kind: fbKind, detail: fbDetail.trim(), contact: fbContact.trim() })
      setFbDetail('')
      setFbContact('')
      refreshFeedback()
      toast('反馈已提交，感谢纠错', 'ok')
    } catch (e) {
      toast('提交失败：' + netError(e), 'err')
    }
  }

  const removeFeedback = async (fid: string) => {
    try {
      await api.feedbackRemove(fid)
      refreshFeedback()
    } catch (e) {
      toast('删除失败：' + netError(e), 'err')
    }
  }

  const fbCount = useMemo(() => {
    const m: Record<string, number> = {}
    for (const f of fbAll) m[f.scenario] = (m[f.scenario] || 0) + 1
    return m
  }, [fbAll])

  const groups = useMemo(() => {
    const m = new Map<string, ScenarioInfo[]>()
    for (const s of meta?.scenarios || []) {
      const q = search.trim().toLowerCase()
      if (q && !(`${s.name} ${s.id} ${s.era || ''}`.toLowerCase().includes(q))) continue
      const era = s.era || '其他'
      if (!m.has(era)) m.set(era, [])
      m.get(era)!.push(s)
    }
    return [...m.entries()]
  }, [meta, search])

  const unitIds = useMemo(
    () => Object.keys(meta?.unit_types || ({} as Record<string, unknown>)),
    [meta],
  )
  const curFb = fbAll.filter(f => f.scenario === selId)

  // ==================== 编辑器小控件 ====================

  const Text = ({ label, path, ph }: { label: string; path: string; ph?: string }) => (
    <label className="scm-f">
      <span>{label}</span>
      <input value={numInput(getIn(doc, path))} placeholder={ph}
        onChange={e => upd(d => setIn(d, path, e.target.value))} />
    </label>
  )

  const Area = ({ label, path }: { label: string; path: string }) => (
    <label className="scm-f scm-f-wide">
      <span>{label}</span>
      <textarea rows={3} value={getIn(doc, path)}
        onChange={e => upd(d => setIn(d, path, e.target.value))} />
    </label>
  )

  const Select = ({ label, path, options }: { label: string; path: string; options: Array<[string, string]> }) => (
    <label className="scm-f">
      <span>{label}</span>
      <select value={getIn(doc, path)} onChange={e => upd(d => setIn(d, path, e.target.value))}>
        {options.map(([v, t]) => <option key={v} value={v}>{t}</option>)}
      </select>
    </label>
  )

  const OobTable = ({ side }: { side: 'red' | 'blue' }) => {
    const rows: Doc[] = doc?.sides?.[side]?.oob || []
    return (
      <div className="scm-oob">
        <div className="scm-oob-head">
          <b style={{ color: side === 'red' ? 'var(--red)' : 'var(--blue)' }}>
            {side === 'red' ? '红方' : '蓝方'}编制（{rows.length} 条）
          </b>
          <label className="scm-f scm-f-mini">
            <span>点数预算</span>
            <input value={numInput(doc?.sides?.[side]?.points_budget)}
              onChange={e => upd(d => setIn(d, `sides.${side}.points_budget`, e.target.value))} />
          </label>
          <button className="btn btn-sm" onClick={() => upd(d => {
            (d.sides ??= {})[side] ??= {};
            ((d.sides[side].oob ??= []) as Doc[]).push({
              type: 'inf', qty: 1, at: [2, 2], training: 60, experience: 60, morale: 80,
              commander: { name: '', quality: 0.7 },
            })
          })}>＋ 增一行</button>
        </div>
        <div className="scm-oob-scroll">
          <table className="oob-table">
            <thead><tr>
              <th>兵种</th><th>数量</th><th>q</th><th>r</th><th>训练</th><th>经验</th><th>士气</th>
              <th>指挥官</th><th>素质</th><th></th>
            </tr></thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={i}>
                  {(['type', 'qty', 'at.0', 'at.1', 'training', 'experience', 'morale'] as const).map(p => (
                    <td key={p}>
                      {p === 'type' ? (
                        <select value={getIn(row, p)} onChange={e => upd(d => setIn(d, `sides.${side}.oob.${i}.${p}`, e.target.value))}>
                          {(unitIds.length ? unitIds : [getIn(row, p) || 'inf']).map(t =>
                            <option key={t} value={t}>{t}</option>)}
                        </select>
                      ) : (
                        <input style={{ width: 52 }} value={numInput(getIn(row, p))}
                          onChange={e => upd(d => setIn(d, `sides.${side}.oob.${i}.${p}`, e.target.value))} />
                      )}
                    </td>
                  ))}
                  <td>
                    <input style={{ width: 130 }} value={getIn(row, 'commander.name')}
                      onChange={e => upd(d => setIn(d, `sides.${side}.oob.${i}.commander.name`, e.target.value))} />
                  </td>
                  <td>
                    <input style={{ width: 48 }} value={numInput(getIn(row, 'commander.quality'))}
                      onChange={e => upd(d => setIn(d, `sides.${side}.oob.${i}.commander.quality`, e.target.value))} />
                  </td>
                  <td>
                    <button className="btn btn-sm danger" title="删除该行"
                      onClick={() => upd(d => { d.sides[side].oob.splice(i, 1) })}>✕</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    )
  }

  // ==================== 渲染 ====================

  return (
    <div className="scm">
      {/* 左栏：剧本列表 */}
      <aside className="scm-list">
        <input className="scm-search" placeholder="🔍 搜索剧本…" value={search}
          onChange={e => setSearch(e.target.value)} />
        <div className="scm-list-scroll">
          {groups.map(([era, list]) => (
            <div key={era}>
              <div className="scn-era">{era}</div>
              {list.map(s => {
                const id = s.id || s.file
                const n = fbCount[id] || 0
                return (
                  <button key={id} type="button"
                    className={`scn-card${id === selId ? ' on' : ''}`}
                    onClick={() => openScenario(s)}>
                    <b className="scn-card-name">
                      {s.name || id}
                      {n > 0 && <span className="scm-badge" title={`${n} 条内容反馈`}>{n}</span>}
                    </b>
                    <span className="scn-card-meta">
                      {s.scale ? s.scale + ' · ' : ''}回合 {s.max_turns ?? '?'} · 单位 {s.unit_count ?? '?'}
                    </span>
                  </button>
                )
              })}
            </div>
          ))}
        </div>
      </aside>

      {/* 右栏：编辑器 */}
      <section className="scm-main">
        {!doc ? (
          <p className="muted">← 从左侧选择要编辑的剧本</p>
        ) : (
          <>
            <div className="scm-head">
              <div>
                <b>{getIn(doc, 'name') || selId}</b>
                <span className="mono muted small"> {file}</span>
                {dirty && <span className="scm-dirty">未保存</span>}
              </div>
              <div className="scm-actions">
                <button className="btn btn-sm" disabled={busy} onClick={validateOnly}>✓ 校验</button>
                <button className="btn btn-sm primary" disabled={busy} onClick={save}>💾 保存</button>
                <button className="btn btn-sm" onClick={() => { setFbOpen(true) }}>
                  ⚠ 反馈问题{curFb.length ? `（${curFb.length}）` : ''}
                </button>
              </div>
            </div>

            {errors.length > 0 && (
              <div className="scm-errors">
                {errors.map((e, i) => <div key={i}>✕ {e}</div>)}
              </div>
            )}

            <div className="scm-scroll">
              <details className="scm-sec" open>
                <summary>基本信息</summary>
                <div className="scm-grid">
                  <Text label="剧本 ID" path="id" />
                  <Text label="名称" path="name" />
                  <Select label="战争分期" path="era"
                    options={ERAS.map(x => [x, x || '（其他）'])} />
                  <Text label="规模" path="scale" />
                  <Text label="回合数" path="max_turns" />
                  <Text label="GIS 数据包" path="gis_package" ph="留空 = 程序化地形" />
                  <Area label="描述" path="description" />
                </div>
              </details>

              <details className="scm-sec" open>
                <summary>战场环境</summary>
                <div className="scm-grid">
                  <Text label="图宽（列）" path="environment.width" />
                  <Text label="图高（行）" path="environment.height" />
                  <Text label="对边距 km" path="environment.cell_km" />
                  <Text label="中心经度" path="environment.center_lonlat.0" />
                  <Text label="中心纬度" path="environment.center_lonlat.1" />
                  <Text label="高程种子" path="environment.height_seed" />
                </div>

                <div className="scm-hex">
                  <div className="scm-hex-head">
                    <b>六角格网络包</b>
                    <span className="muted small">
                      数据以 zip 存服务器，剧本只引用 id；绑定后优先于上方 environment 与 gis_package
                    </span>
                  </div>

                  <div className="scm-hex-bind">
                    <span>当前绑定：</span>
                    {doc.hexgrid ? (
                      <>
                        <code className="mono">{String(doc.hexgrid)}</code>
                        <span className="muted small">{hgList.find(g => g.id === doc.hexgrid)?.name || ''}</span>
                        <button className="btn btn-sm danger" disabled={hgBusy}
                          onClick={() => upd(d => { delete d.hexgrid })}>解除绑定</button>
                      </>
                    ) : (
                      <span className="muted small">未绑定（用 environment 程序化地形或 gis_package）</span>
                    )}
                  </div>

                  {hgList.length > 0 && (
                    <div className="scm-hex-bind">
                      <select value={hgPick} onChange={e => setHgPick(e.target.value)} style={{ maxWidth: 260 }}>
                        <option value="">选择已有网格包…</option>
                        {hgList.map(g => (
                          <option key={g.id} value={g.id}>
                            {g.name}（{g.rows}×{g.cols} · {g.id}{g.used_by.length ? ' · 已被引用' : ''}）
                          </option>
                        ))}
                      </select>
                      <button className="btn btn-sm" disabled={!hgPick || hgBusy} onClick={() => { hgBind(hgPick); setHgPick('') }}>
                        绑定所选
                      </button>
                    </div>
                  )}

                  <div className="scm-grid">
                    <label className="scm-f"><span>网格名称</span>
                      <input value={hgForm.name} placeholder="留空自动命名"
                        onChange={e => setHgForm({ ...hgForm, name: e.target.value })} />
                    </label>
                    <label className="scm-f"><span>行数</span>
                      <input type="number" min={1} max={200} value={hgForm.rows}
                        onChange={e => setHgForm({ ...hgForm, rows: e.target.value })} />
                    </label>
                    <label className="scm-f"><span>列数</span>
                      <input type="number" min={1} max={200} value={hgForm.cols}
                        onChange={e => setHgForm({ ...hgForm, cols: e.target.value })} />
                    </label>
                    <label className="scm-f"><span>对边距（米）</span>
                      <input type="number" min={10} value={hgForm.ftf}
                        onChange={e => setHgForm({ ...hgForm, ftf: e.target.value })} />
                    </label>
                    <label className="scm-f"><span>锚点西经</span>
                      <input type="number" step="0.0001" value={hgForm.west}
                        onChange={e => setHgForm({ ...hgForm, west: e.target.value })} />
                    </label>
                    <label className="scm-f"><span>锚点南纬</span>
                      <input type="number" step="0.0001" value={hgForm.south}
                        onChange={e => setHgForm({ ...hgForm, south: e.target.value })} />
                    </label>
                    <label className="scm-f"><span>默认地形</span>
                      <select value={hgForm.terrain}
                        onChange={e => setHgForm({ ...hgForm, terrain: e.target.value })}>
                        {TERRAINS.map(([k, n]) => <option key={k} value={k}>{n}</option>)}
                      </select>
                    </label>
                  </div>
                  <button className="btn btn-sm primary" disabled={hgBusy} onClick={hgCreate}>
                    {hgBusy ? '生成中…' : '⚙ 生成六角格包并绑定'}
                  </button>
                </div>
              </details>

              <details className="scm-sec">
                <summary>模式与日夜</summary>
                <div className="scm-flags">
                  {FLAGS.map(([k, t]) => (
                    <label key={k} className="scm-flag">
                      <input type="checkbox" checked={!!getIn(doc, 'flags.' + k)}
                        onChange={e => upd(d => setIn(d, 'flags.' + k, e.target.checked))} />
                      {t}
                    </label>
                  ))}
                </div>
                <div className="scm-grid">
                  <Text label="起始时刻（时）" path="clock.start_hour" />
                  <Select label="季节" path="clock.season"
                    options={[['', '（默认）'], ['春', '春'], ['夏', '夏'], ['秋', '秋'], ['冬', '冬']]} />
                  <Text label="夜幕开始（时）" path="clock.night_from" />
                  <Text label="夜幕结束（时）" path="clock.night_to" />
                </div>
              </details>

              <details className="scm-sec">
                <summary>天气{schedN > 0 && <span className="cnt">已排 {schedN} 段</span>}</summary>
                <div className="scm-grid">
                  <Select label="初始天气" path="weather.initial"
                    options={[['', '（默认）'], ['clear', '晴'], ['overcast', '阴'], ['rain', '雨'], ['snow', '雪'], ['storm', '暴风']]}/>
                  <Select label="演变模式" path="weather.mode"
                    options={[['', '（继承默认）'], ['fixed', '固定'], ['stochastic', '逐回合概率'], ['director', '导演部手控']]} />
                </div>
                {(doc.weather?.schedule || []).length > 0 && (
                  <div className="scm-oob">
                    <div className="scm-oob-head"><b>分段天气计划</b>
                      <button className="btn btn-sm" onClick={() => upd(d => {
                        (d.weather.schedule ??= []).push({ from: 1, to: 3, weather: 'rain' })
                      })}>＋</button>
                    </div>
                    <table className="oob-table">
                      <thead><tr><th>从回合</th><th>到回合</th><th>天气</th><th></th></tr></thead>
                      <tbody>
                        {(doc.weather.schedule as Doc[]).map((p, i) => (
                          <tr key={i}>
                            <td><input style={{ width: 60 }} value={numInput(p.from)}
                              onChange={e => upd(d => setIn(d, `weather.schedule.${i}.from`, e.target.value))} /></td>
                            <td><input style={{ width: 60 }} value={numInput(p.to)}
                              onChange={e => upd(d => setIn(d, `weather.schedule.${i}.to`, e.target.value))} /></td>
                            <td>
                              <select value={p.weather || ''}
                                onChange={e => upd(d => setIn(d, `weather.schedule.${i}.weather`, e.target.value))}>
                                {['clear', 'overcast', 'rain', 'snow', 'storm'].map(w =>
                                  <option key={w} value={w}>{w}</option>)}
                              </select>
                            </td>
                            <td><button className="btn btn-sm danger"
                              onClick={() => upd(d => { d.weather.schedule.splice(i, 1) })}>✕</button></td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                {(doc.weather?.schedule || []).length === 0 && (
                  <button className="btn btn-sm" onClick={() => upd(d => {
                    d.weather.schedule = [{ from: 1, to: 3, weather: 'rain' }]
                  })}>＋ 添加分段天气计划</button>
                )}
              </details>

              <details className="scm-sec">
                <summary>胜负判定{((doc.victory || []) as Doc[]).length > 0 && <span className="cnt">{((doc.victory || []) as Doc[]).length} 条</span>}</summary>
                <table className="oob-table">
                  <thead><tr><th>阵营</th><th>种类</th><th>目标格（capture）</th><th>回合/数值</th><th></th></tr></thead>
                  <tbody>
                    {((doc.victory || []) as Doc[]).map((v, i) => (
                      <tr key={i}>
                        <td>
                          <select value={v.side || 'red'}
                            onChange={e => upd(d => setIn(d, `victory.${i}.side`, e.target.value))}>
                            <option value="red">红方</option><option value="blue">蓝方</option>
                          </select>
                        </td>
                        <td>
                          <select value={v.kind || 'survive'}
                            onChange={e => upd(d => setIn(d, `victory.${i}.kind`, e.target.value))}>
                            {VICTORY_KINDS.map(k => <option key={k} value={k}>{k}</option>)}
                          </select>
                        </td>
                        <td><input style={{ width: 70 }} value={v.target || ''}
                          onChange={e => upd(d => setIn(d, `victory.${i}.target`, e.target.value))} /></td>
                        <td><input style={{ width: 60 }} value={numInput(v.turns)}
                          onChange={e => upd(d => setIn(d, `victory.${i}.turns`, e.target.value))} /></td>
                        <td><button className="btn btn-sm danger"
                          onClick={() => upd(d => { d.victory.splice(i, 1) })}>✕</button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <button className="btn btn-sm" style={{ marginTop: 6 }} onClick={() => upd(d => {
                  (d.victory ??= []).push({ kind: 'survive', side: 'red', target: null, amount: 0, turns: 12 })
                })}>＋ 增一条判定</button>
              </details>

              <details className="scm-sec">
                <summary>部队编制</summary>
                <OobTable side="red" />
                <OobTable side="blue" />
              </details>

              <details className="scm-sec">
                <summary>JSON 源（高级）</summary>
                <div>
                  <textarea className="scm-json" rows={16} value={jsonText}
                    onChange={e => setJsonText(e.target.value)} spellCheck={false} />
                  <div style={{ marginTop: 6 }}>
                    <button className="btn btn-sm" onClick={applyJson}>应用源码到编辑器</button>
                    <span className="muted small" style={{ marginLeft: 8 }}>
                      改动其余高级字段（rules / unit_type_defs / gis 等）后应用，再点上方「保存」
                    </span>
                  </div>
                </div>
              </details>
            </div>
          </>
        )}
      </section>

      {/* 反馈弹窗 */}
      {fbOpen && (
        <div className="scm-modal-mask" onClick={() => setFbOpen(false)}>
          <div className="scm-modal" onClick={e => e.stopPropagation()}>
            <h3>⚠ 剧本内容反馈 — {getIn(doc, 'name') || selId}</h3>
            <div className="scm-grid">
              <label className="scm-f">
                <span>问题类型</span>
                <select value={fbKind} onChange={e => setFbKind(e.target.value)}>
                  {FB_KINDS.map(k => <option key={k} value={k}>{k}</option>)}
                </select>
              </label>
              <label className="scm-f">
                <span>联系方式（选填）</span>
                <input value={fbContact} onChange={e => setFbContact(e.target.value)} />
              </label>
            </div>
            <label className="scm-f" style={{ display: 'block', marginTop: 8 }}>
              <span>问题描述</span>
              <textarea rows={4} style={{ width: '100%' }} value={fbDetail}
                placeholder="例如：第 3 回合 63 军位置应为…（写明回合、部队、位置/数值与依据）"
                onChange={e => setFbDetail(e.target.value)} />
            </label>
            <div style={{ marginTop: 10, display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
              <button className="btn btn-sm" onClick={() => setFbOpen(false)}>关闭</button>
              <button className="btn btn-sm primary" onClick={submitFeedback}>提交反馈</button>
            </div>

            {curFb.length > 0 && (
              <div className="scm-fb-list">
                <b>已有反馈（{curFb.length}）</b>
                {curFb.map(f => (
                  <div key={f.id} className="scm-fb-item">
                    <div className="scm-fb-meta">
                      <span className="tag">{f.kind}</span>
                      <span className="mini">{f.created_at}</span>
                      <button className="btn btn-sm danger" style={{ marginLeft: 'auto' }}
                        onClick={() => removeFeedback(f.id)}>✕</button>
                    </div>
                    <div className="small">{f.detail}</div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
