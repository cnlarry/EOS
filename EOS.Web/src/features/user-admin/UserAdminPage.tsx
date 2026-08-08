import { IconKey, IconRefresh, IconUserOff, IconUserPlus } from '@tabler/icons-react'
import { useMutation, useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../auth/authContext'
import { apiClient } from '../../services/api'
import { ApiError, type PageResponse } from '../../types/api'

export interface UserAdminSummary {
  userId: string
  employeeId: string
  employeeName: string
  departmentId: string
  departmentName: string
  companyId: string
  groupId: string
  isActive: boolean
  hasPassword: boolean
  lastUpdatedBy: string | null
  lastUpdatedAt: string | null
}

interface SetPasswordModalProps {
  user: UserAdminSummary | null
  onClose: () => void
  onSaved: () => void
}

function SetPasswordModal({ user, onClose, onSaved }: SetPasswordModalProps) {
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState('')
  const save = useMutation({
    mutationFn: async (value: string) => {
      await apiClient.put(`/admin/users/${encodeURIComponent(user!.userId.trim())}/password`, { newPassword: value })
    },
    onSuccess: () => {
      setPassword('')
      setConfirm('')
      onSaved()
    },
    onError: (reason) => setError(reason instanceof ApiError ? reason.body.message : '保存失败，请稍后重试。'),
  })

  if (!user) return null
  const invalid = password.length < 8 || password.length > 64 || password !== password.trim() || password !== confirm

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">设置密码</h2>
            <button className="btn-close" aria-label="关闭" onClick={() => { setPassword(''); setConfirm(''); onClose() }} />
          </div>
          <div className="modal-body">
            <div className="alert alert-info">为账号 <strong>{user.userId.trim()}</strong>（{user.employeeName}）分配新密码。密码长度需为 8-64 个字符，且不能以空格开头或结尾。</div>
            <div className="d-grid gap-3">
              <div>
                <label className="form-label" htmlFor="new-password">新密码</label>
                <input id="new-password" type="password" autoComplete="new-password" className="form-control" value={password} onChange={(event) => setPassword(event.target.value)} />
              </div>
              <div>
                <label className="form-label" htmlFor="confirm-password">确认新密码</label>
                <input id="confirm-password" type="password" autoComplete="new-password" className="form-control" value={confirm} onChange={(event) => setConfirm(event.target.value)} />
              </div>
              {password && confirm && password !== confirm && <div className="text-danger small">两次输入的密码不一致。</div>}
              {error && <div className="alert alert-danger py-2 mb-0" role="alert">{error}</div>}
            </div>
          </div>
          <div className="modal-footer">
            <Button variant="ghost" onClick={() => { setPassword(''); setConfirm(''); onClose() }}>取消</Button>
            <Button variant="primary" loading={save.isPending} disabled={invalid} onClick={() => save.mutate(password)}>保存密码</Button>
          </div>
        </div>
      </div>
    </div>
  )
}

const pageSize = 10

export function UserAdminPage() {
  const { bootstrap } = useAuth()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [passwordTarget, setPasswordTarget] = useState<UserAdminSummary | null>(null)
  const users = useQuery({
    queryKey: ['user-admin', 'users', keyword, page],
    queryFn: () => apiClient.get<PageResponse<UserAdminSummary>>('/admin/users', { query: { keyword, page, pageSize } }),
  })
  const status = useMutation({
    mutationFn: async ({ userId, isActive }: { userId: string; isActive: boolean }) => {
      await apiClient.put(`/admin/users/${encodeURIComponent(userId.trim())}/status`, { isActive })
    },
    onSuccess: () => void users.refetch(),
  })

  const errorMessage = users.error instanceof ApiError ? users.error.body.message : '发生未知错误，请稍后重试。'
  const currentUserId = bootstrap?.user.id.trim().toLowerCase()

  const columns = useMemo<ColumnDef<UserAdminSummary, unknown>[]>(() => [
    { accessorKey: 'userId', header: '用户名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue()).trim()}</span> },
    { accessorKey: 'employeeId', header: '员工号', cell: (info) => <span className="font-monospace">{String(info.getValue()).trim()}</span> },
    { accessorKey: 'employeeName', header: '姓名' },
    { accessorKey: 'departmentName', header: '部门', cell: (info) => <span className="text-secondary">{String(info.getValue() || '—')}</span> },
    { accessorKey: 'companyId', header: '公司', cell: (info) => <span className="text-secondary">{String(info.getValue() || '—')}</span> },
    {
      accessorKey: 'isActive',
      header: '状态',
      cell: (info) => info.getValue() ? <span className="badge bg-success-subtle text-success">启用</span> : <span className="badge bg-danger-subtle text-danger">停用</span>,
    },
    {
      accessorKey: 'hasPassword',
      header: '密码',
      cell: (info) => info.getValue() ? <span className="badge bg-secondary-subtle text-secondary">已设置</span> : <span className="badge bg-warning-subtle text-warning">未设置</span>,
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true },
      cell: ({ row }) => {
        const user = row.original
        const isSelf = user.userId.trim().toLowerCase() === currentUserId
        return (
          <div className="d-inline-flex gap-1">
            <Button size="sm" icon={<IconKey size={15} />} onClick={() => setPasswordTarget(user)}>设置密码</Button>
            <Button
              size="sm"
              variant={user.isActive ? 'ghost' : 'secondary'}
              icon={user.isActive ? <IconUserOff size={15} /> : <IconUserPlus size={15} />}
              disabled={isSelf || status.isPending}
              title={isSelf ? '不能停用当前登录账号' : undefined}
              onClick={() => {
                const next = !user.isActive
                if (!next && !window.confirm(`确定停用账号 ${user.userId.trim()} 吗？停用后该账号将无法登录。`)) return
                status.mutate({ userId: user.userId, isActive: next })
              }}
            >
              {user.isActive ? '停用' : '启用'}
            </Button>
          </div>
        )
      },
    },
  ], [currentUserId, status])

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="用户管理查询"
        search={<ErpSearchBox value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }} debounceMs={300} placeholder="搜索用户名、员工号或姓名" ariaLabel="搜索用户" />}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void users.refetch()}>刷新</Button>}
        header={users.data ? <div className="erp-list-header text-secondary small px-3 pt-2">共 {users.data.total} 个账号；密码为空的账号需由管理员分配密码后才能登录。</div> : undefined}
        footer={!users.isPending && !users.isError ? <ErpPagination total={users.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} /> : undefined}
      >
        {users.isPending ? <LoadingState label="正在加载用户…" /> : users.isError ? <ErrorState message={errorMessage} onRetry={() => void users.refetch()} /> : (
          <ErpTable columns={columns} data={users.data?.items ?? []} resizable storageKey="user-admin-users" empty={<EmptyState title="没有找到用户" description="请调整搜索条件后重试。" />} />
        )}
      </ErpListCard>
      <SetPasswordModal
        user={passwordTarget}
        onClose={() => setPasswordTarget(null)}
        onSaved={() => { setPasswordTarget(null); void users.refetch() }}
      />
    </div>
  )
}
