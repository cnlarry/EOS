import { IconKey, IconPlus, IconRefresh, IconReport, IconShield, IconUserOff, IconUserPlus, IconUsers } from '@tabler/icons-react'
import { useMutation, useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { useAuth } from '../auth/authContext'
import type { UserGroupItem, UserGroupSummary } from '../rights-admin/types'
import { apiClient } from '../../services/api'
import { type PageResponse } from '../../types/api'
import { NewUserModal } from './NewUserModal'
import { describeApiError } from '../../lib/errors'

export interface UserAdminSummary {
  userId: string
  employeeId: string
  employeeName: string
  departmentId: string
  departmentName: string
  companyId: string
  groups: string
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
    onError: (reason) => setError(describeApiError(reason, '保存失败，请稍后重试。')),
  })

  if (!user) return null
  const invalid = password.length < 8 || password.length > 64 || password !== password.trim() || password !== confirm

  return (
    <Modal
      title="设置密码"
      onClose={() => { setPassword(''); setConfirm(''); onClose() }}
      footer={<>
        <Button variant="ghost" onClick={() => { setPassword(''); setConfirm(''); onClose() }}>取消</Button>
        <Button variant="primary" loading={save.isPending} disabled={invalid} onClick={() => save.mutate(password)}>保存密码</Button>
      </>}
    >
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
    </Modal>
  )
}

const pageSize = 50

/** 用户权限设定（2306 定制页）：用户列表（无感翻页）+ 行级操作；权限/报表权限为完整子页面，所属组为弹窗。 */
export function UserAdminPage() {
  const navigate = useNavigate()
  const { bootstrap } = useAuth()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [items, setItems] = useState<UserAdminSummary[]>([])
  const [hasMore, setHasMore] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const itemsRef = useRef<UserAdminSummary[]>([])
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [passwordTarget, setPasswordTarget] = useState<UserAdminSummary | null>(null)
  const [groupsTarget, setGroupsTarget] = useState<UserAdminSummary | null>(null)
  const [newUserOpen, setNewUserOpen] = useState(false)
  const [groupsSelection, setGroupsSelection] = useState<RowSelectionState>({})
  const [groupSaveError, setGroupSaveError] = useState('')

  const users = useQuery({
    queryKey: ['user-admin', 'users', keyword, page],
    queryFn: () => apiClient.get<PageResponse<UserAdminSummary>>('/admin/users', { query: { keyword, page, pageSize } }),
    placeholderData: (previous) => previous,
    // 刷新/重建列表时 items 会先清空再回填：关闭结构共享保证每次 fetch 都产生新引用，
    // 否则数据未变化时 users.data 引用不变，回填 effect 不会重跑，列表停留在空态
    structuralSharing: false,
  })
  const groups = useQuery({
    queryKey: ['rights-admin', 'groups'],
    queryFn: () => apiClient.get<UserGroupSummary[]>('/admin/groups'),
  })
  const userGroups = useQuery({
    queryKey: ['rights-admin', 'user-groups', groupsTarget?.userId],
    queryFn: () => apiClient.get<UserGroupItem[]>(`/admin/users/${encodeURIComponent(groupsTarget!.userId.trim())}/groups`),
    enabled: groupsTarget !== null,
  })
  const saveGroups = useMutation({
    mutationFn: async (ids: string[]) => {
      await apiClient.put(`/admin/users/${encodeURIComponent(groupsTarget!.userId.trim())}/groups`, { ids })
    },
    onSuccess: () => {
      setGroupSaveError('')
      setGroupsTarget(null)
      void users.refetch()
      void userGroups.refetch()
    },
    onError: (reason) => setGroupSaveError(describeApiError(reason, '保存失败，请稍后重试。')),
  })

  // 打开所属组选择器时，把当前已选组回填为初始选中（受控选中键 = G_IDX.trim()）
  useEffect(() => {
    if (!groupsTarget) {
      setGroupsSelection({})
      return
    }
    if (!userGroups.data) return
    const next: RowSelectionState = {}
    for (const group of userGroups.data) next[group.groupId.trim()] = true
    setGroupsSelection(next)
  }, [groupsTarget, userGroups.data])
  const status = useMutation({
    mutationFn: async ({ userId, isActive }: { userId: string; isActive: boolean }) => {
      await apiClient.put(`/admin/users/${encodeURIComponent(userId.trim())}/status`, { isActive })
    },
    onSuccess: () => void users.refetch(),
  })

  // 无感翻页：累积已加载行；关键字或页码变化时重建当前页数据
  useEffect(() => {
    if (!users.data) return
    const current = users.data.items ?? []
    const next = page === 1
      ? current
      : [...itemsRef.current, ...current.filter((user) => !itemsRef.current.some((prev) => prev.userId.trim() === user.userId.trim()))]
    itemsRef.current = next
    setItems(next)
    setHasMore(next.length < (users.data.total ?? 0))
    setLoadingMore(false)
  }, [users.data, page])

  const loadMore = () => {
    if (hasMore && !loadingMore && !users.isFetching) {
      setLoadingMore(true)
      setPage((current) => current + 1)
    }
  }

  const onSearchChange = (value: string) => {
    setKeyword(value)
    setPage(1)
    itemsRef.current = []
    setItems([])
    setHasMore(false)
  }

  const errorMessage = describeApiError(users.error, '发生未知错误，请稍后重试。')
  const currentUserId = bootstrap?.user.id.trim().toLowerCase()
  const total = users.data?.total ?? items.length

  const columns = useMemo<ColumnDef<UserAdminSummary, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    { accessorKey: 'userId', header: '用户名', minSize: 100, cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue()).trim()}</span>, meta: { minWidth: 100 } },
    { accessorKey: 'employeeId', header: '员工号', minSize: 80, cell: (info) => <span className="font-monospace">{String(info.getValue()).trim()}</span>, meta: { minWidth: 80 } },
    { accessorKey: 'employeeName', header: '姓名', minSize: 120, cell: (info) => <span>{String(info.getValue())}</span>, meta: { minWidth: 120 } },
    { accessorKey: 'departmentName', header: '部门', minSize: 140, cell: (info) => <span className="text-secondary">{String(info.getValue() || '—')}</span>, meta: { minWidth: 140 } },
    { accessorKey: 'companyId', header: '公司', minSize: 80, cell: (info) => <span className="text-secondary">{String(info.getValue() || '—')}</span>, meta: { minWidth: 80 } },
    {
      accessorKey: 'groups',
      header: '所属组',
      minSize: 200,
      cell: (info) => {
        const value = String(info.getValue() || '')
        const parts = value.split('、').map((item) => item.trim()).filter(Boolean)
        return parts.length === 0
          ? <span className="text-secondary">—</span>
          : (
            <div className="d-flex flex-wrap gap-1">
              {parts.map((group) => <span key={group} className="badge bg-secondary-subtle text-secondary">{group}</span>)}
            </div>
          )
      },
      meta: { minWidth: 200, truncate: false },
    },
    {
      accessorKey: 'isActive',
      header: '状态',
      cell: (info) => info.getValue() ? <span className="badge bg-success-subtle text-success">启用</span> : <span className="badge bg-danger-subtle text-danger">停用</span>,
      meta: { truncate: false, minWidth: 80 },
    },
    {
      accessorKey: 'hasPassword',
      header: '密码',
      cell: (info) => info.getValue() ? <span className="badge bg-secondary-subtle text-secondary">已设置</span> : <span className="badge bg-warning-subtle text-warning">未设置</span>,
      meta: { truncate: false, minWidth: 80 },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 356, minWidthFloor: true },
      cell: ({ row }) => {
        const user = row.original
        const id = user.userId.trim()
        const isSelf = id.toLowerCase() === currentUserId
        return (
          <div className="d-inline-flex gap-1">
            <Button size="sm" variant="ghost" icon={<IconKey size={14} />} onClick={() => setPasswordTarget(user)}>设置密码</Button>
            <Button size="sm" variant="ghost" icon={<IconShield size={14} />} onClick={() => navigate(`/admin/users/${encodeURIComponent(id)}/rights`)}>权限</Button>
            <Button size="sm" variant="ghost" icon={<IconReport size={14} />} onClick={() => navigate(`/admin/users/${encodeURIComponent(id)}/report-rights`)}>报表权限</Button>
            <Button size="sm" variant="ghost" icon={<IconUsers size={14} />} onClick={() => setGroupsTarget(user)}>所属组</Button>
            <Button
              size="sm"
              variant={user.isActive ? 'ghost' : 'secondary'}
              icon={user.isActive ? <IconUserOff size={14} /> : <IconUserPlus size={14} />}
              disabled={isSelf || status.isPending}
              title={isSelf ? '不能停用当前登录账号' : undefined}
              onClick={() => {
                const next = !user.isActive
                if (!next && !window.confirm(`确定停用账号 ${id} 吗？停用后该账号将无法登录。`)) return
                status.mutate({ userId: user.userId, isActive: next })
              }}
            >
              {user.isActive ? '停用' : '启用'}
            </Button>
          </div>
        )
      },
    },
  ], [currentUserId, status, navigate])

  return (
    <div className="erp-full-list-page">
      {groupSaveError && <div className="alert alert-danger py-2 mb-2" role="alert">{groupSaveError}</div>}
      <ErpListCard
        ariaLabel="用户管理查询"
        search={<ErpSearchBox value={keyword} onChange={onSearchChange} debounceMs={300} placeholder="搜索用户名、员工号或姓名" ariaLabel="搜索用户" />}
        actions={<div className="d-flex gap-2 align-items-center">
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => { itemsRef.current = []; setItems([]); setPage(1); void users.refetch() }}>刷新</Button>
          <Button size="sm" variant="primary" icon={<IconPlus size={16} />} onClick={() => setNewUserOpen(true)}>新增用户</Button>
        </div>}
        header={users.data ? <div className="erp-list-header text-secondary small px-3 pt-2">共 {total} 个账号；向下滚动自动加载更多；「新增用户」为员工开户（选择器选员工）；密码为空的账号需由管理员分配密码后才能登录。</div> : undefined}
      >
        {users.isPending && page === 1 ? <LoadingState label="正在加载用户…" /> : users.isError ? <ErrorState message={errorMessage} onRetry={() => void users.refetch()} /> : (
          <ErpTable
            columns={columns}
            data={items}
            getRowId={(row) => row.userId.trim()}
            resizable
            storageKey="user-admin-users"
            clientSideSorting
            rowClickSingleSelect
            rowSelection={rowSelection}
            onRowSelectionChange={setRowSelection}
            onEndReached={loadMore}
            hasMore={hasMore}
            loadingMore={loadingMore}
            empty={<EmptyState title="没有找到用户" description="请调整搜索条件后重试。" />}
          />
        )}
      </ErpListCard>
      <SetPasswordModal
        user={passwordTarget}
        onClose={() => setPasswordTarget(null)}
        onSaved={() => { setPasswordTarget(null); void users.refetch() }}
      />
      {groupsTarget !== null && (
        <UnifiedChooser
          open
          title={`用户所属组：${groupsTarget.userId.trim()}`}
          source={{ kind: 'sourceKey', key: 'rights-admin.groups' }}
          mode="multi"
          getRowId={(row) => String((row as { G_IDX?: unknown }).G_IDX ?? '').trim()}
          selectedKeys={groupsSelection}
          onSelectedKeysChange={setGroupsSelection}
          extra={<div className="small text-secondary">用户所属组按用户全量替换保存（个人权限存在时完全覆盖组权限）。</div>}
          onPick={(rows) => {
            const ids = rows.map((row) => String((row as { G_IDX?: unknown }).G_IDX ?? '').trim()).filter(Boolean)
            void saveGroups.mutateAsync(ids)
          }}
          onClose={() => setGroupsTarget(null)}
          searchPlaceholder="按组ID/组名搜索"
          storageKey="user-admin-groups-chooser"
        />
      )}
      {newUserOpen && (
        <NewUserModal
          groups={groups.data ?? []}
          onClose={() => setNewUserOpen(false)}
          onSaved={() => { setNewUserOpen(false); itemsRef.current = []; setItems([]); setPage(1); void users.refetch() }}
        />
      )}
    </div>
  )
}
