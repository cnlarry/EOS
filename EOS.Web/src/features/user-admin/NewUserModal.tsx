import { IconShield, IconUsers } from '@tabler/icons-react'
import { useState } from 'react'
import type { RowSelectionState } from '@tanstack/react-table'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import type { UserGroupSummary } from '../rights-admin/types'
import { describeApiError } from '../../lib/errors'

interface NewUserModalProps {
  groups: UserGroupSummary[]
  onClose: () => void
  onSaved: () => void
}

/** 行内按元数据键大小写不敏感取值（服务端列键来自 110104 字段元数据，如 EMP_ID）。 */
function rowValue(row: Record<string, unknown>, ...keys: string[]): unknown {
  const entry = Object.entries(row).find(([key]) =>
    keys.some(candidate => candidate.toUpperCase() === key.toUpperCase()),
  )
  return entry?.[1]
}

interface SelectedEmployee {
  employeeId: string
  employeeName: string
  departmentName?: string
}

/** 新增用户（开户）弹窗：用户名 + 员工（统一选择器）+ 初始密码 + 可选所属组（多选）。 */
export function NewUserModal({ groups, onClose, onSaved }: NewUserModalProps) {
  const [userId, setUserId] = useState('')
  const [employee, setEmployee] = useState<SelectedEmployee | null>(null)
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [groupIds, setGroupIds] = useState<string[]>([])
  const [groupsOpen, setGroupsOpen] = useState(false)
  const [groupSelection, setGroupSelection] = useState<RowSelectionState>({})
  const [chooserOpen, setChooserOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  /** 已选组以「组ID（组名）」展示，组名取用户管理页已加载的组列表 */
  const groupLabel = (groupId: string) => {
    const hit = groups.find((group) => group.groupId.trim() === groupId)
    return hit ? `${groupId}（${hit.groupDescription}）` : groupId
  }

  const openGroupChooser = () => {
    setGroupSelection(Object.fromEntries(groupIds.map((groupId) => [groupId, true])))
    setGroupsOpen(true)
  }

  const passwordValid = password.length >= 8 && password.length <= 64 && password === password.trim()
  const canSubmit = userId.trim().length > 0
    && userId.trim().length <= 10
    && /^[A-Za-z0-9_-]+$/.test(userId.trim())
    && employee !== null
    && passwordValid
    && password === confirm

  const submit = async () => {
    if (!canSubmit || !employee) return
    setSaving(true)
    setError(null)
    try {
      await apiClient.post('/admin/users', {
        userId: userId.trim(),
        employeeId: employee.employeeId,
        password,
        groupIds,
      })
      onSaved()
    } catch (reason) {
      setError(describeApiError(reason, '开户失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <Modal
        title="新增用户（开户）"
        onClose={onClose}
        footer={<>
          <Button onClick={onClose}>取消</Button>
          <Button variant="primary" onClick={() => void submit()} loading={saving} disabled={!canSubmit}>开户</Button>
        </>}
      >
        <div className="d-grid gap-3">
            <div>
              <label className="form-label" htmlFor="new-user-id">用户名</label>
              <input
                id="new-user-id"
                className="form-control"
                value={userId}
                maxLength={10}
                onChange={(event) => setUserId(event.target.value)}
                placeholder="登录账号（1-10 位字母数字/下划线/连字符）"
              />
              {userId.trim().length > 0 && !/^[A-Za-z0-9_-]+$/.test(userId.trim()) && (
                <div className="text-danger small">用户名仅允许字母、数字、下划线或连字符。</div>
              )}
            </div>
            <div>
              <label className="form-label">员工</label>
              <div className="input-group">
                <input
                  className="form-control"
                  readOnly
                  value={employee ? `${employee.employeeId}（${employee.employeeName}${employee.departmentName ? ` / ${employee.departmentName}` : ''}）` : ''}
                  placeholder="点击右侧按钮选择员工"
                  aria-label="已选员工"
                />
                <Button size="md" icon={<IconUsers size={16} />} onClick={() => setChooserOpen(true)}>选择员工</Button>
              </div>
            </div>
            <div className="row g-2">
              <div className="col-6">
                <label className="form-label" htmlFor="new-user-password">初始密码</label>
                <input id="new-user-password" type="password" autoComplete="new-password" className="form-control" value={password} onChange={(event) => setPassword(event.target.value)} />
              </div>
              <div className="col-6">
                <label className="form-label" htmlFor="new-user-confirm">确认密码</label>
                <input id="new-user-confirm" type="password" autoComplete="new-password" className="form-control" value={confirm} onChange={(event) => setConfirm(event.target.value)} />
              </div>
            </div>
            {password && !passwordValid && <div className="text-danger small">密码长度需为 8-64 个字符，且不能以空格开头或结尾。</div>}
            {password && confirm && password !== confirm && <div className="text-danger small">两次输入的密码不一致。</div>}
            <div>
              <label className="form-label">所属组（选填，可多组）</label>
              <div className="input-group">
                <input
                  className="form-control"
                  readOnly
                  value={groupIds.map(groupLabel).join('、')}
                  placeholder="点击右侧按钮选择用户组"
                  aria-label="已选用户组"
                />
                <Button size="md" icon={<IconShield size={16} />} onClick={openGroupChooser}>选择用户组</Button>
              </div>
              <div className="small text-secondary mt-1">用户与用户组为多对多关系；该用户存在个人权限时，个人权限完全覆盖组权限。</div>
            </div>
            {error && <div className="alert alert-danger py-2 mb-0" role="alert">{error}</div>}
        </div>
      </Modal>
      <UnifiedChooser
        open={chooserOpen}
        title="选择员工"
        source={{ kind: 'sourceKey', key: 'user-admin.employees' }}
        mode="single"
        onPick={(rows) => {
          const picked = rows[0] as unknown as Record<string, unknown> | undefined
          const employeeId = picked ? rowValue(picked, 'EMP_ID') : null
          if (picked && employeeId != null) {
            const departmentName = rowValue(picked, 'DEPT_NAME')
            setEmployee({
              employeeId: String(employeeId),
              employeeName: String(rowValue(picked, 'EMP_NAME') ?? ''),
              departmentName: departmentName != null && String(departmentName).trim() !== '' ? String(departmentName) : undefined,
            })
          }
          setChooserOpen(false)
        }}
        onClose={() => setChooserOpen(false)}
        getRowId={(row, index) => String(rowValue(row as unknown as Record<string, unknown>, 'EMP_ID') ?? `row-${index}`)}
        searchPlaceholder="按员工号/姓名/部门搜索"
        serverPaging
        resizable
        storageKey="new-user-employee-chooser"
      />
      <UnifiedChooser
        open={groupsOpen}
        title="选择用户组"
        source={{ kind: 'sourceKey', key: 'rights-admin.groups' }}
        mode="multi"
        selectedKeys={groupSelection}
        onSelectedKeysChange={setGroupSelection}
        getRowId={(row) => String((row as { G_IDX?: unknown }).G_IDX ?? '').trim()}
        onPick={(rows) => {
          setGroupIds(rows.map((row) => String((row as { G_IDX?: unknown }).G_IDX ?? '').trim()).filter(Boolean))
          setGroupsOpen(false)
        }}
        onClose={() => setGroupsOpen(false)}
        searchPlaceholder="按组ID/组名搜索"
        extra={<div className="small text-secondary">可多选；开户后也可在用户管理的「所属组」中调整。</div>}
        storageKey="new-user-group-chooser"
      />
    </>
  )
}
