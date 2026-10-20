import { useState, type FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../components/Auth'
import { netError } from '../services/api'

/** 登录 / 注册页。注册一律观战角色，角色由超级管理员分配。 */
export default function Login() {
  const { login, register } = useAuth()
  const nav = useNavigate()
  const location = useLocation()
  const from = (location.state as { from?: string } | null)?.from ?? '/'

  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (busy) return
    setErr('')
    setBusy(true)
    try {
      if (mode === 'login') await login(username.trim(), password)
      else await register(username.trim(), password, displayName.trim() || undefined)
      nav(from, { replace: true })
    } catch (e2) {
      setErr(netError(e2))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page" style={{ display: 'flex', justifyContent: 'center', paddingTop: 60 }}>
      <form className="login-card" onSubmit={submit}>
        <h2 style={{ marginTop: 0, textAlign: 'center' }}>{mode === 'login' ? '登录' : '注册'}</h2>
        <label className="scm-f">
          <span>用户名</span>
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoFocus autoComplete="username" />
        </label>
        <label className="scm-f">
          <span>密码{mode === 'register' && '（≥6 位）'}</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
          />
        </label>
        {mode === 'register' && (
          <label className="scm-f">
            <span>昵称（可选）</span>
            <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </label>
        )}
        {err && <div className="scm-errors">{err}</div>}
        <button className="btn btn-primary" style={{ width: '100%', marginTop: 10 }} disabled={busy || !username || !password}>
          {busy ? '请稍候…' : mode === 'login' ? '登录' : '注册并登录'}
        </button>
        <button
          type="button"
          className="btn"
          style={{ width: '100%', marginTop: 8 }}
          onClick={() => {
            setMode(mode === 'login' ? 'register' : 'login')
            setErr('')
          }}
        >
          {mode === 'login' ? '没有账号？去注册' : '已有账号？去登录'}
        </button>
        <p className="muted" style={{ fontSize: 12, marginBottom: 0, textAlign: 'center' }}>
          注册默认为观战角色，席位与权限由管理员分配
        </p>
      </form>
    </div>
  )
}
