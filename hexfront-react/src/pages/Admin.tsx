import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../components/Auth'
import { api, netError, type AdminMenu, type AdminRole, type AdminUser } from '../services/api'
import { roleLabel } from '../services/auth'

type Tab = 'users' | 'roles' | 'menus' | 'logs'

const TABS: Array<{ key: Tab; label: string }> = [
  { key: 'users', label: '用户管理' },
  { key: 'roles', label: '角色管理' },
  { key: 'menus', label: '菜单管理' },
  { key: 'logs', label: '日志查询' },
]

/** 系统管理页：仅超级管理员可见（路由级 + 后端 SuperAdmin 双重校验）。 */
export default function Admin() {
  const { user, ready } = useAuth()
  const [tab, setTab] = useState<Tab>('users')

  if (!ready) return <div className="page muted">正在恢复会话…</div>
  if (!user)
    return (
      <div className="page" style={{ textAlign: 'center', paddingTop: 60 }}>
        <h2>请先登录</h2>
        <p className="muted">系统管理仅对超级管理员开放。</p>
        <Link className="btn btn-primary" to="/login" state={{ from: '/admin' }}>
          去登录
        </Link>
      </div>
    )
  if (user.role !== 'super_admin')
    return (
      <div className="page" style={{ textAlign: 'center', paddingTop: 60 }}>
        <h2>无权限</h2>
        <p className="muted">当前角色「{roleLabel(user.role)}」无系统管理权限。</p>
        <Link className="btn" to="/">
          回首页
        </Link>
      </div>
    )

  return (
    <div className="page adm-page">
      <div className="adm-tabs">
        {TABS.map((t) => (
          <button key={t.key} className={'adm-tab' + (tab === t.key ? ' on' : '')} onClick={() => setTab(t.key)}>
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'users' && <UsersTab />}
      {tab === 'roles' && <RolesTab />}
      {tab === 'menus' && <MenusTab />}
      {tab === 'logs' && <LogsTab />}
    </div>
  )
}

// ==================== 用户管理 ====================

function UsersTab() {
  const [users, setUsers] = useState<AdminUser[]>([])
  const [roles, setRoles] = useState<AdminRole[]>([])
  const [err, setErr] = useState('')
  const [msg, setMsg] = useState('')
  const [busy, setBusy] = useState(false)
  // 新建表单
  const [nu, setNu] = useState({ username: '', password: '', display_name: '', role: 'spectator' })

  const flash = (m: string) => {
    setMsg(m)
    setErr('')
    window.setTimeout(() => setMsg(''), 2500)
  }

  const load = useCallback(async () => {
    try {
      const [us, rs] = await Promise.all([api.adminUsers(), api.adminRoles()])
      setUsers(us.users)
      setRoles(rs.roles)
    } catch (e) {
      setErr(netError(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true)
    setErr('')
    try {
      await fn()
      flash(ok)
      await load()
    } catch (e) {
      setErr(netError(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <fieldset className="scm-sec">
        <legend>新建用户</legend>
        <div className="scm-grid">
          <label className="scm-f">
            <span>用户名</span>
            <input value={nu.username} onChange={(e) => setNu({ ...nu, username: e.target.value })} />
          </label>
          <label className="scm-f">
            <span>密码（≥6 位）</span>
            <input value={nu.password} onChange={(e) => setNu({ ...nu, password: e.target.value })} />
          </label>
          <label className="scm-f">
            <span>昵称</span>
            <input value={nu.display_name} onChange={(e) => setNu({ ...nu, display_name: e.target.value })} />
          </label>
          <label className="scm-f">
            <span>角色</span>
            <select value={nu.role} onChange={(e) => setNu({ ...nu, role: e.target.value })}>
              {roles.map((r) => (
                <option key={r.code} value={r.code}>
                  {r.name}（{r.code}）
                </option>
              ))}
            </select>
          </label>
          <label className="scm-f">
            <span>&nbsp;</span>
            <button
              className="btn btn-sm primary"
              disabled={busy || !nu.username || nu.password.length < 6}
              onClick={() =>
                run(async () => {
                  await api.adminCreateUser({
                    username: nu.username.trim(),
                    password: nu.password,
                    display_name: nu.display_name.trim() || undefined,
                    role: nu.role,
                  })
                  setNu({ username: '', password: '', display_name: '', role: 'spectator' })
                }, '用户已创建')
              }
            >
              ＋ 创建
            </button>
          </label>
        </div>
      </fieldset>

      {msg && <div className="adm-msg">{msg}</div>}
      {err && <div className="scm-errors">{err}</div>}

      <div className="scm-sec">
        <table className="adm-table">
          <thead>
            <tr>
              <th>ID</th>
              <th>用户名</th>
              <th>昵称</th>
              <th>角色</th>
              <th>状态</th>
              <th>最近登录</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            {users.map((u) => (
              <tr key={u.id} className={u.status === 'disabled' ? 'adm-dim' : ''}>
                <td>{u.id}</td>
                <td>{u.username}</td>
                <td>{u.display_name || '—'}</td>
                <td>
                  <select
                    value={u.role_code}
                    disabled={busy || (u.role_code === 'super_admin' && u.status === 'active')}
                    title={u.role_code === 'super_admin' ? '最后一个超级管理员不可在此降级' : ''}
                    onChange={(e) => run(() => api.adminUpdateUser(u.id, { role: e.target.value }), '角色已更新')}
                  >
                    {roles.map((r) => (
                      <option key={r.code} value={r.code}>
                        {r.name}
                      </option>
                    ))}
                  </select>
                </td>
                <td>{u.status === 'active' ? '正常' : '已停用'}</td>
                <td className="adm-time">{u.last_login_at ? new Date(u.last_login_at).toLocaleString() : '—'}</td>
                <td className="adm-ops">
                  <button
                    className="btn btn-sm"
                    disabled={busy}
                    onClick={() =>
                      run(
                        () => api.adminUpdateUser(u.id, { status: u.status === 'active' ? 'disabled' : 'active' }),
                        u.status === 'active' ? '已停用' : '已启用'
                      )
                    }
                  >
                    {u.status === 'active' ? '停用' : '启用'}
                  </button>
                  <button
                    className="btn btn-sm"
                    disabled={busy}
                    onClick={() => {
                      const p = window.prompt(`为 ${u.username} 设置新密码（≥6 位）：`)
                      if (p != null && p.length >= 6) void run(() => api.adminUpdateUser(u.id, { password: p }), '密码已重置')
                      else if (p != null) setErr('密码 ≥6 位')
                    }}
                  >
                    重置密码
                  </button>
                  <button
                    className="btn btn-sm danger"
                    disabled={busy}
                    onClick={() => {
                      if (window.confirm(`确认删除用户 ${u.username}？`)) void run(() => api.adminDeleteUser(u.id), '已删除')
                    }}
                  >
                    删除
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

// ==================== 角色管理 ====================

function RolesTab() {
  const [roles, setRoles] = useState<AdminRole[]>([])
  const [menus, setMenus] = useState<AdminMenu[]>([])
  const [roleMenus, setRoleMenus] = useState<Record<string, string[]>>({})
  const [editing, setEditing] = useState<string | null>(null) // 正在分配菜单的角色
  const [draft, setDraft] = useState<string[]>([])
  const [err, setErr] = useState('')
  const [msg, setMsg] = useState('')
  const [busy, setBusy] = useState(false)
  const [nr, setNr] = useState({ code: '', name: '', description: '' })

  const flash = (m: string) => {
    setMsg(m)
    setErr('')
    window.setTimeout(() => setMsg(''), 2500)
  }

  const load = useCallback(async () => {
    try {
      const [rs, ms] = await Promise.all([api.adminRoles(), api.adminMenus()])
      setRoles(rs.roles)
      setMenus(ms.menus)
      const map: Record<string, string[]> = {}
      for (const rm of ms.role_menus) (map[rm.role_code] ??= []).push(rm.menu_code)
      setRoleMenus(map)
    } catch (e) {
      setErr(netError(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true)
    setErr('')
    try {
      await fn()
      flash(ok)
      await load()
    } catch (e) {
      setErr(netError(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <fieldset className="scm-sec">
        <legend>新建角色</legend>
        <div className="scm-grid">
          <label className="scm-f">
            <span>code（小写字母/数字/下划线）</span>
            <input value={nr.code} onChange={(e) => setNr({ ...nr, code: e.target.value })} placeholder="observer" />
          </label>
          <label className="scm-f">
            <span>名称</span>
            <input value={nr.name} onChange={(e) => setNr({ ...nr, name: e.target.value })} placeholder="观察员" />
          </label>
          <label className="scm-f scm-f-wide">
            <span>描述</span>
            <input value={nr.description} onChange={(e) => setNr({ ...nr, description: e.target.value })} />
          </label>
          <label className="scm-f">
            <span>&nbsp;</span>
            <button
              className="btn btn-sm primary"
              disabled={busy || !nr.code || !nr.name}
              onClick={() =>
                run(async () => {
                  await api.adminCreateRole({ code: nr.code.trim(), name: nr.name.trim(), description: nr.description.trim() || undefined })
                  setNr({ code: '', name: '', description: '' })
                }, '角色已创建')
              }
            >
              ＋ 创建
            </button>
          </label>
        </div>
      </fieldset>

      {msg && <div className="adm-msg">{msg}</div>}
      {err && <div className="scm-errors">{err}</div>}

      <div className="scm-sec">
        <table className="adm-table">
          <thead>
            <tr>
              <th>code</th>
              <th>名称</th>
              <th>描述</th>
              <th>用户数</th>
              <th>菜单</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            {roles.map((r) => (
              <tr key={r.code}>
                <td>
                  <code>{r.code}</code>
                </td>
                <td>{r.name}</td>
                <td className="muted">{r.description || '—'}</td>
                <td>{r.user_count ?? 0}</td>
                <td>{(roleMenus[r.code] ?? []).length} 项</td>
                <td className="adm-ops">
                  <button
                    className="btn btn-sm"
                    disabled={busy}
                    onClick={() => {
                      setEditing(editing === r.code ? null : r.code)
                      setDraft([...(roleMenus[r.code] ?? [])])
                    }}
                  >
                    分配菜单
                  </button>
                  <button
                    className="btn btn-sm"
                    disabled={busy}
                    onClick={() => {
                      const name = window.prompt('修改角色名称：', r.name)
                      if (name) void run(() => api.adminUpdateRole(r.code, { name: name.trim(), description: r.description ?? undefined }), '角色已更新')
                    }}
                  >
                    改名
                  </button>
                  <button
                    className="btn btn-sm danger"
                    disabled={busy || r.code === 'super_admin' || (r.user_count ?? 0) > 0}
                    title={r.code === 'super_admin' ? '超级管理员不可删除' : (r.user_count ?? 0) > 0 ? '仍有用户引用' : ''}
                    onClick={() => {
                      if (window.confirm(`确认删除角色 ${r.name}？`)) void run(() => api.adminDeleteRole(r.code), '已删除')
                    }}
                  >
                    删除
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        {editing && (
          <div className="adm-menu-edit">
            <b>{roleLabel(editing)} 的可见菜单</b>
            <div className="adm-menu-flags">
              {menus.map((m) => (
                <label key={m.code} className="scm-flag">
                  <input
                    type="checkbox"
                    checked={draft.includes(m.code)}
                    onChange={(e) => setDraft(e.target.checked ? [...draft, m.code] : draft.filter((c) => c !== m.code))}
                  />
                  {m.name}（{m.code}）
                </label>
              ))}
            </div>
            <div className="adm-menu-ops">
              <button
                className="btn btn-sm primary"
                disabled={busy}
                onClick={() =>
                  run(async () => {
                    await api.adminSetRoleMenus(editing, draft)
                    setEditing(null)
                  }, '菜单授权已保存')
                }
              >
                💾 保存授权
              </button>
              <button className="btn btn-sm" onClick={() => setEditing(null)}>
                取消
              </button>
            </div>
          </div>
        )}
      </div>
    </>
  )
}

// ==================== 菜单管理 ====================

function MenusTab() {
  const [menus, setMenus] = useState<AdminMenu[]>([])
  const [err, setErr] = useState('')
  const [msg, setMsg] = useState('')
  const [busy, setBusy] = useState(false)
  const [nm, setNm] = useState({ code: '', name: '', path: '', sort: '9' })
  const [editId, setEditId] = useState<number | null>(null)
  const [ed, setEd] = useState({ name: '', path: '', sort: '0', visible: true })

  const flash = (m: string) => {
    setMsg(m)
    setErr('')
    window.setTimeout(() => setMsg(''), 2500)
  }

  const load = useCallback(async () => {
    try {
      setMenus((await api.adminMenus()).menus)
    } catch (e) {
      setErr(netError(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true)
    setErr('')
    try {
      await fn()
      flash(ok)
      await load()
    } catch (e) {
      setErr(netError(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <fieldset className="scm-sec">
        <legend>新建菜单</legend>
        <div className="scm-grid">
          <label className="scm-f">
            <span>code</span>
            <input value={nm.code} onChange={(e) => setNm({ ...nm, code: e.target.value })} placeholder="replay" />
          </label>
          <label className="scm-f">
            <span>名称</span>
            <input value={nm.name} onChange={(e) => setNm({ ...nm, name: e.target.value })} placeholder="复盘回放" />
          </label>
          <label className="scm-f">
            <span>路径</span>
            <input value={nm.path} onChange={(e) => setNm({ ...nm, path: e.target.value })} placeholder="/replay" />
          </label>
          <label className="scm-f">
            <span>排序</span>
            <input value={nm.sort} onChange={(e) => setNm({ ...nm, sort: e.target.value })} />
          </label>
          <label className="scm-f">
            <span>&nbsp;</span>
            <button
              className="btn btn-sm primary"
              disabled={busy || !nm.code || !nm.name}
              onClick={() =>
                run(async () => {
                  await api.adminCreateMenu({
                    code: nm.code.trim(),
                    name: nm.name.trim(),
                    path: nm.path.trim() || undefined,
                    sort: Number(nm.sort) || 0,
                    visible: true,
                  })
                  setNm({ code: '', name: '', path: '', sort: '9' })
                }, '菜单已创建')
              }
            >
              ＋ 创建
            </button>
          </label>
        </div>
      </fieldset>

      {msg && <div className="adm-msg">{msg}</div>}
      {err && <div className="scm-errors">{err}</div>}

      <div className="scm-sec">
        <table className="adm-table">
          <thead>
            <tr>
              <th>ID</th>
              <th>code</th>
              <th>名称</th>
              <th>路径</th>
              <th>排序</th>
              <th>可见</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            {menus.map((m) =>
              editId === m.id ? (
                <tr key={m.id}>
                  <td>{m.id}</td>
                  <td>
                    <code>{m.code}</code>
                  </td>
                  <td>
                    <input value={ed.name} onChange={(e) => setEd({ ...ed, name: e.target.value })} />
                  </td>
                  <td>
                    <input value={ed.path} onChange={(e) => setEd({ ...ed, path: e.target.value })} />
                  </td>
                  <td>
                    <input className="adm-sort" value={ed.sort} onChange={(e) => setEd({ ...ed, sort: e.target.value })} />
                  </td>
                  <td>
                    <input type="checkbox" checked={ed.visible} onChange={(e) => setEd({ ...ed, visible: e.target.checked })} />
                  </td>
                  <td className="adm-ops">
                    <button
                      className="btn btn-sm primary"
                      disabled={busy}
                      onClick={() =>
                        run(async () => {
                          await api.adminUpdateMenu(m.id, {
                            name: ed.name.trim(),
                            path: ed.path.trim() || undefined,
                            sort: Number(ed.sort) || 0,
                            visible: ed.visible,
                          })
                          setEditId(null)
                        }, '菜单已更新')
                      }
                    >
                      保存
                    </button>
                    <button className="btn btn-sm" onClick={() => setEditId(null)}>
                      取消
                    </button>
                  </td>
                </tr>
              ) : (
                <tr key={m.id}>
                  <td>{m.id}</td>
                  <td>
                    <code>{m.code}</code>
                  </td>
                  <td>{m.name}</td>
                  <td>{m.path || '—'}</td>
                  <td>{m.sort}</td>
                  <td>{m.visible ? '是' : '否'}</td>
                  <td className="adm-ops">
                    <button
                      className="btn btn-sm"
                      onClick={() => {
                        setEditId(m.id)
                        setEd({ name: m.name, path: m.path ?? '', sort: String(m.sort), visible: !!m.visible })
                      }}
                    >
                      编辑
                    </button>
                    <button
                      className="btn btn-sm danger"
                      disabled={busy || m.code === 'system'}
                      title={m.code === 'system' ? '系统管理菜单不可删除' : ''}
                      onClick={() => {
                        if (window.confirm(`确认删除菜单 ${m.name}？各角色授权将同步移除。`))
                          void run(() => api.adminDeleteMenu(m.id), '已删除')
                      }}
                    >
                      删除
                    </button>
                  </td>
                </tr>
              )
            )}
          </tbody>
        </table>
      </div>
    </>
  )
}

// ==================== 日志查询 ====================

type LogKind = 'login' | 'query' | 'error'

function LogsTab() {
  const [kind, setKind] = useState<LogKind>('login')
  const [rows, setRows] = useState<Array<Record<string, unknown>>>([])
  const [filter, setFilter] = useState('')
  const [err, setErr] = useState('')

  const load = useCallback(async () => {
    setErr('')
    try {
      if (kind === 'login') setRows((await api.adminLoginLogs(200, filter || undefined)).logs as never)
      else if (kind === 'query') setRows((await api.adminQueryLogs(300, filter || undefined)).logs as never)
      else setRows((await api.adminErrorLogs(200)).logs as never)
    } catch (e) {
      setErr(netError(e))
    }
  }, [kind, filter])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <div className="scm-sec">
      <div className="adm-log-bar">
        {(
          [
            ['login', '登录日志'],
            ['query', '查询日志'],
            ['error', '错误日志'],
          ] as Array<[LogKind, string]>
        ).map(([k, label]) => (
          <button key={k} className={'adm-tab' + (kind === k ? ' on' : '')} onClick={() => { setKind(k); setFilter('') }}>
            {label}
          </button>
        ))}
        {kind !== 'error' && (
          <input
            className="scm-search"
            style={{ marginLeft: 'auto', maxWidth: 220 }}
            placeholder={kind === 'login' ? '按用户名过滤…' : '按路径过滤…'}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
        )}
        <button className="btn btn-sm" onClick={() => void load()}>
          刷新
        </button>
      </div>
      {err && <div className="scm-errors">{err}</div>}
      <div className="adm-log-scroll">
        <table className="adm-table">
          {kind === 'login' && (
            <>
              <thead>
                <tr>
                  <th>时间</th>
                  <th>用户</th>
                  <th>动作</th>
                  <th>结果</th>
                  <th>IP</th>
                  <th>详情</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={String(r.id)}>
                    <td className="adm-time">{fmt(r.ts)}</td>
                    <td>{str(r.username)}</td>
                    <td>{str(r.action)}</td>
                    <td>{r.ok ? '✓' : '✗'}</td>
                    <td>{str(r.ip)}</td>
                    <td className="muted">{str(r.detail) || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </>
          )}
          {kind === 'query' && (
            <>
              <thead>
                <tr>
                  <th>时间</th>
                  <th>方法</th>
                  <th>路径</th>
                  <th>状态</th>
                  <th>耗时</th>
                  <th>用户</th>
                  <th>IP</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={String(r.id)}>
                    <td className="adm-time">{fmt(r.ts)}</td>
                    <td>{str(r.method)}</td>
                    <td className="adm-path">{str(r.path)}</td>
                    <td>{str(r.status)}</td>
                    <td>{str(r.duration_ms)} ms</td>
                    <td>{str(r.username)}</td>
                    <td>{str(r.ip)}</td>
                  </tr>
                ))}
              </tbody>
            </>
          )}
          {kind === 'error' && (
            <>
              <thead>
                <tr>
                  <th>时间</th>
                  <th>来源</th>
                  <th>路径</th>
                  <th>消息</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={String(r.id)}>
                    <td className="adm-time">{fmt(r.ts)}</td>
                    <td>{str(r.source)}</td>
                    <td className="adm-path">{str(r.path)}</td>
                    <td className="adm-path">{str(r.message)}</td>
                  </tr>
                ))}
              </tbody>
            </>
          )}
        </table>
        {rows.length === 0 && !err && <p className="muted" style={{ textAlign: 'center' }}>暂无记录</p>}
      </div>
    </div>
  )
}

const str = (v: unknown): string => (v == null ? '' : String(v))
const fmt = (v: unknown): string => {
  const s = str(v)
  const d = s ? new Date(s) : null
  return d && !isNaN(d.getTime()) ? d.toLocaleString() : s
}
