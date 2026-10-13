import { IconKey } from '@tabler/icons-react'
import { useState } from 'react'
import { Modal } from '../../components/ui/Modal'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

/**
 * 修改登录密码弹窗（侧栏底部用户菜单 → 修改密码）。
 *
 * 改密码是账号凭据操作：单次、需确认、成功后无需留在页面上，故就地弹窗完成；
 * 个人设置页只承载界面偏好，不再放密码表单。
 *
 * 前端校验口径与服务端 `PasswordPolicy` 一致（8-64 位、首尾不留空格），
 * 作用是让按钮在明显不合规时就置灰；服务端仍是唯一权威，失败原文照回显。
 *
 * 这里不走 react-query：它没有要失效的缓存，且弹窗由外壳直接挂载——
 * 走全局 mutation 错误出口的话，同一次失败会既弹轻提示又在弹窗里回显一遍。
 */
export function PasswordChangeModal({ onClose }: { onClose: () => void }) {
  const { notify } = useToast()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async () => {
    setBusy(true)
    setError(null)
    try {
      await apiClient.put('/auth/password', { currentPassword, newPassword })
      notify({ message: '密码修改成功。', variant: 'success' })
      onClose()
    } catch (reason) {
      setError(describeApiError(reason, '修改失败，请稍后重试。'))
    } finally {
      setBusy(false)
    }
  }

  const passwordInvalid =
    newPassword.length < 8 || newPassword.length > 64 || newPassword !== newPassword.trim()
    || newPassword !== confirmPassword || currentPassword.length === 0

  return (
    <Modal
      title="修改密码"
      onClose={onClose}
      footer={(
        <>
          <Button variant="secondary" onClick={onClose}>取消</Button>
          <Button
            variant="primary"
            icon={<IconKey size={16} />}
            loading={busy}
            disabled={passwordInvalid}
            onClick={() => void submit()}
          >
            修改密码
          </Button>
        </>
      )}
    >
      <div className="alert alert-info">密码长度需为 8-64 个字符，且不能以空格开头或结尾。修改后当前会话保持有效。</div>
      <div className="row g-3">
        <div className="col-12">
          <label className="form-label" htmlFor="current-password">当前密码</label>
          <input
            id="current-password"
            type="password"
            autoComplete="current-password"
            className="form-control"
            value={currentPassword}
            onChange={(event) => { setCurrentPassword(event.target.value); setError(null) }}
          />
        </div>
        <div className="col-12">
          <label className="form-label" htmlFor="new-password">新密码</label>
          <input
            id="new-password"
            type="password"
            autoComplete="new-password"
            className="form-control"
            value={newPassword}
            onChange={(event) => { setNewPassword(event.target.value); setError(null) }}
          />
        </div>
        <div className="col-12">
          <label className="form-label" htmlFor="confirm-password">确认新密码</label>
          <input
            id="confirm-password"
            type="password"
            autoComplete="new-password"
            className="form-control"
            value={confirmPassword}
            onChange={(event) => { setConfirmPassword(event.target.value); setError(null) }}
          />
        </div>
      </div>
      {newPassword && confirmPassword && newPassword !== confirmPassword && (
        <div className="text-danger small mt-2">两次输入的新密码不一致。</div>
      )}
      {error && <div className="alert alert-danger py-2 mt-3 mb-0" role="alert">{error}</div>}
    </Modal>
  )
}
