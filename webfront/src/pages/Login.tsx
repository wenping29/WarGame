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
    <div className="page login-wrap">
      <form className="login-card" onSubmit={submit}>
        <div className="login-brand">
          <div className="login-logo">⚔</div>
          <h1>兵棋对抗</h1>
          <span className="muted">HEXFRONT · 兵棋推演系统</span>
        </div>

        <div className="login-tabs">
          <button type="button" className={mode === 'login' ? 'on' : ''} onClick={() => { setMode('login'); setErr('') }}>
            登 录
          </button>
          <button type="button" className={mode === 'register' ? 'on' : ''} onClick={() => { setMode('register'); setErr('') }}>
            注 册
          </button>
        </div>

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

        <button className="btn btn-primary login-submit" disabled={busy || !username || !password}>
          {busy ? '请稍候…' : mode === 'login' ? '登录' : '注册并登录'}
        </button>

        <p className="muted login-note">
          注册默认为观战角色，席位与权限由管理员分配
        </p>
      </form>
    </div>
  )
}
