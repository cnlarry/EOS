import { IconKey, IconRefresh } from '@tabler/icons-react'
import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../auth/authContext'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

const avatarPalette = ['#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6']

function avatarColor(username: string): string {
  let hash = 0
  for (let index = 0; index < username.length; index += 1) {
    hash = (hash * 31 + username.charCodeAt(index)) >>> 0
  }
  return avatarPalette[hash % avatarPalette.length]
}

export function ProfilePage() {
  const { bootstrap } = useAuth()
  const user = bootstrap?.user

  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [message, setMessage] = useState<{ type: 'success' | 'error'; text: string } | null>(null)

  const changePassword = useMutation({
    mutationFn: async () => {
      await apiClient.put('/auth/password', { currentPassword, newPassword })
    },
    onSuccess: () => {
      setMessage({ type: 'success', text: '密码修改成功。' })
      setCurrentPassword('')
      setNewPassword('')
      setConfirmPassword('')
    },
    onError: (reason) => {
      setMessage({ type: 'error', text: describeApiError(reason, '修改失败，请稍后重试。') })
    },
  })

  const passwordInvalid =
    newPassword.length < 8 || newPassword.length > 64 || newPassword !== newPassword.trim()
    || newPassword !== confirmPassword || currentPassword.length === 0

  return (
    <div className="row row-cards">
      <div className="col-lg-8">
        <section className="card">
          <div className="card-header"><h2 className="card-title">个人资料</h2></div>
          <div className="card-body">
            <div className="d-flex align-items-center gap-3 mb-4">
              {user?.avatarUrl ? (
                <span className="avatar avatar-lg"><img src={user.avatarUrl} alt="" /></span>
              ) : (
                <span className="avatar avatar-lg" style={{ backgroundColor: avatarColor(user?.username ?? 'user') }}>{user?.avatarText}</span>
              )}
              <div>
                <div className="fw-semibold fs-4">{user?.displayName}</div>
                <div className="text-secondary">{user?.username}</div>
              </div>
            </div>
            <div className="row g-3">
              <div className="col-md-6"><label className="form-label">登录账号</label><input className="form-control" value={user?.username ?? ''} disabled /></div>
              <div className="col-md-6"><label className="form-label">员工号</label><input className="form-control" value={user?.employeeId ?? ''} disabled /></div>
              <div className="col-md-6"><label className="form-label">姓名</label><input className="form-control" value={user?.displayName ?? ''} disabled /></div>
              <div className="col-md-6"><label className="form-label">所属部门</label><input className="form-control" value={user?.organization.name ?? ''} disabled /></div>
              <div className="col-md-6"><label className="form-label">角色</label><input className="form-control" value={user?.roleName ?? ''} disabled /></div>
            </div>
          </div>
        </section>

        <section className="card mt-3">
          <div className="card-header"><h2 className="card-title">修改登录密码</h2></div>
          <div className="card-body">
            <div className="alert alert-info">密码长度需为 8-64 个字符，且不能以空格开头或结尾。修改后当前会话保持有效。</div>
            <div className="row g-3">
              <div className="col-md-6">
                <label className="form-label" htmlFor="current-password">当前密码</label>
                <input id="current-password" type="password" autoComplete="current-password" className="form-control" value={currentPassword} onChange={(event) => { setCurrentPassword(event.target.value); setMessage(null) }} />
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="new-password">新密码</label>
                <input id="new-password" type="password" autoComplete="new-password" className="form-control" value={newPassword} onChange={(event) => { setNewPassword(event.target.value); setMessage(null) }} />
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="confirm-password">确认新密码</label>
                <input id="confirm-password" type="password" autoComplete="new-password" className="form-control" value={confirmPassword} onChange={(event) => { setConfirmPassword(event.target.value); setMessage(null) }} />
              </div>
            </div>
            {newPassword && confirmPassword && newPassword !== confirmPassword && <div className="text-danger small mt-2">两次输入的新密码不一致。</div>}
            {message && <div className={`alert alert-${message.type === 'success' ? 'success' : 'danger'} py-2 mt-3 mb-0`} role="alert">{message.text}</div>}
          </div>
          <div className="card-footer text-end">
            <Button variant="primary" icon={<IconKey size={16} />} loading={changePassword.isPending} disabled={passwordInvalid} onClick={() => changePassword.mutate()}>修改密码</Button>
          </div>
        </section>
      </div>
      <div className="col-lg-4">
        <section className="card">
          <div className="card-header"><h2 className="card-title">会话信息</h2></div>
          <div className="card-body">
            <div className="text-secondary small">当前登录会话由服务端 Cookie 维护（HttpOnly + SameSite），不会在浏览器本地存储令牌。</div>
            <div className="mt-3">
              <Button variant="ghost" size="sm" icon={<IconRefresh size={15} />} onClick={() => location.reload()}>刷新用户信息</Button>
            </div>
          </div>
        </section>
      </div>
    </div>
  )
}
