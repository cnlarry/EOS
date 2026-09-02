import { IconEye, IconEyeOff, IconLock, IconUser } from '@tabler/icons-react'
import { useState, type FormEvent } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from './authContext'
import { REMEMBERED_USER_KEY } from '../../lib/storageKeys'
import { describeApiError } from '../../lib/errors'

export function LoginPage() {
  const { bootstrap, loading, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [username, setUsername] = useState(localStorage.getItem(REMEMBERED_USER_KEY) ?? '')
  const [password, setPassword] = useState('')
  const [remember, setRemember] = useState(Boolean(localStorage.getItem(REMEMBERED_USER_KEY)))
  const [showPassword, setShowPassword] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState('')
  const destination = (location.state as { from?: string } | null)?.from ?? '/dashboard'

  if (loading) return <main className="erp-login-page erp-login-loading" aria-live="polite"><div className="spinner-border text-primary" role="status" /><span>正在恢复登录状态…</span></main>
  if (bootstrap) return <Navigate to={destination} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSubmitting(true)
    setError('')
    try {
      await login({ userId: username, password, rememberMe: remember })
      if (remember) localStorage.setItem(REMEMBERED_USER_KEY, username)
      else localStorage.removeItem(REMEMBERED_USER_KEY)
      navigate(destination, { replace: true })
    } catch (reason) {
      setError(describeApiError(reason, '登录失败，请稍后重试。'))
    } finally {
      setSubmitting(false)
    }
  }

  return <main className="erp-login-page">
    <section className="erp-login-brand" aria-label="EOS 企业操作系统">
      <div className="erp-login-brand-heading"><div className="erp-login-mark">E</div><div><h1>EOS</h1><p>企业操作系统</p></div></div>
      <div className="erp-login-slogan">让业务流程清晰，让企业运营高效</div>
    </section>
    <section className="card erp-login-card">
      <div className="erp-login-mobile-brand"><div className="erp-login-mark">E</div><div><strong>EOS</strong><small>企业操作系统</small></div></div>
      <div className="card-body">
        <div className="mb-4"><h2 className="mb-1">登录系统</h2><p className="text-secondary mb-0">请输入您的企业账号</p></div>
        <form onSubmit={submit} className="d-grid gap-3">
          <div>
            <label className="form-label" htmlFor="login-username">用户名</label>
            <div className="input-group">
              <span className="input-group-text"><IconUser size={18} /></span>
              <input id="login-username" name="username" className="form-control" value={username} onChange={(event)=>setUsername(event.target.value)} autoComplete="username" autoFocus required />
            </div>
          </div>
          <div>
            <label className="form-label" htmlFor="login-password">密码</label>
            <div className="input-group">
              <span className="input-group-text"><IconLock size={18} /></span>
              <input id="login-password" name="password" className="form-control" type={showPassword?'text':'password'} value={password} onChange={(event)=>setPassword(event.target.value)} autoComplete="current-password" required />
              <button className="btn btn-icon" type="button" onClick={()=>setShowPassword(!showPassword)} aria-label={showPassword?'隐藏密码':'显示密码'}>{showPassword?<IconEyeOff size={18}/>:<IconEye size={18}/>}</button>
            </div>
          </div>
          <label className="form-check"><input className="form-check-input" type="checkbox" checked={remember} onChange={(event)=>setRemember(event.target.checked)}/><span className="form-check-label">记住用户名</span></label>
          {error && <div className="alert alert-danger py-2 mb-0" role="alert">{error}</div>}
          <button className="btn btn-primary" disabled={submitting} type="submit">{submitting?'正在登录…':'登录'}</button>
        </form>
        {import.meta.env.DEV && <details className="erp-login-test-accounts"><summary>开发测试账号</summary><div>admin / purchaser / sales / viewer</div><div>统一密码：erp123</div></details>}
      </div>
    </section>
  </main>
}
