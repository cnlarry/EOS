import { useEffect, useState } from 'react'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import type { UserGroupSummary } from './types'

interface GroupEditorModalProps {
  open: boolean
  mode: 'new' | 'edit'
  group?: UserGroupSummary | null
  onClose: () => void
  onSaved: () => void
}

/** 用户组主档新增/编辑弹窗（2305 定制页，字段少故用弹窗承载）。 */
export function GroupEditorModal({ open, mode, group, onClose, onSaved }: GroupEditorModalProps) {
  const editingId = mode === 'edit' ? (group?.groupId ?? '').trim() : ''
  const [draft, setDraft] = useState({ groupId: '', groupDescription: '', remark: '' })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setDraft({
      groupId: editingId,
      groupDescription: group?.groupDescription ?? '',
      remark: group?.remark ?? '',
    })
    setError(null)
  }, [open, mode, group, editingId])

  if (!open) return null

  const submit = async () => {
    const groupId = draft.groupId.trim()
    const groupDescription = draft.groupDescription.trim()
    const remark = draft.remark.trim() || null
    if (!groupId) { setError('组ID不能为空。'); return }
    if (!groupDescription) { setError('组描述不能为空。'); return }
    setSaving(true)
    setError(null)
    try {
      if (mode === 'new') {
        await apiClient.post('/admin/groups', { groupId, groupDescription, remark })
      } else {
        await apiClient.put(`/admin/groups/${encodeURIComponent(editingId)}`, { groupDescription, remark })
      }
      onSaved()
    } catch (reason) {
      setError(reason instanceof ApiError ? reason.body.message : '保存失败，请稍后重试。')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{mode === 'new' ? '新增用户组' : `编辑用户组：${editingId}`}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body d-grid gap-3">
            <div>
              <label className="form-label" htmlFor="group-id">组ID</label>
              <input
                id="group-id"
                className="form-control"
                value={draft.groupId}
                disabled={mode === 'edit'}
                maxLength={10}
                onChange={(event) => setDraft((current) => ({ ...current, groupId: event.target.value }))}
                placeholder="如 001 / HR（最多 10 个字符）"
              />
              {mode === 'edit' && <div className="form-hint">组ID 不可修改</div>}
            </div>
            <div>
              <label className="form-label" htmlFor="group-description">组描述</label>
              <input
                id="group-description"
                className="form-control"
                value={draft.groupDescription}
                maxLength={100}
                onChange={(event) => setDraft((current) => ({ ...current, groupDescription: event.target.value }))}
                placeholder="用户组名称"
              />
            </div>
            <div>
              <label className="form-label" htmlFor="group-remark">备注</label>
              <textarea
                id="group-remark"
                className="form-control"
                rows={3}
                value={draft.remark}
                onChange={(event) => setDraft((current) => ({ ...current, remark: event.target.value }))}
                maxLength={1000}
                placeholder="选填"
              />
            </div>
            {error && <div className="alert alert-danger py-2 mb-0" role="alert">{error}</div>}
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            <Button variant="primary" onClick={() => void submit()} loading={saving}>保存</Button>
          </div>
        </div>
      </div>
    </div>
  )
}
