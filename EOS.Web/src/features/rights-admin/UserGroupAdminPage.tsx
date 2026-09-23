import { IconClick, IconEdit, IconPlus, IconRefresh, IconReport, IconShield, IconTrash, IconUsers } from '@tabler/icons-react'
import { useMutation, useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { GroupEditorModal } from './GroupEditorModal'
import type { UserGroupSummary } from './types'
import { describeApiError } from '../../lib/errors'

type EditorState = { mode: 'new' } | { mode: 'edit'; group: UserGroupSummary }

/**
 * 用户组管理（2305 定制页）：
 * 组主档新增/编辑用弹窗，删除带严格守卫（服务端拒绝删除仍有关联成员的组）；
 * 组权限 / 报表权限 / 成员为完整子页面，不经工作台承载。
 */
export function UserGroupAdminPage() {
  const navigate = useNavigate()
  const groups = useQuery({
    queryKey: ['rights-admin', 'groups'],
    queryFn: () => apiClient.get<UserGroupSummary[]>('/admin/groups'),
  })
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [keyword, setKeyword] = useState('')

  const remove = useMutation({
    mutationFn: async (groupId: string) => {
      await apiClient.delete(`/admin/groups/${encodeURIComponent(groupId)}`)
    },
    onSuccess: () => {
      setRowSelection({})
      void groups.refetch()
    },
  })

  const confirmRemove = useCallback((group: UserGroupSummary) => {
    const id = group.groupId.trim()
    const name = group.groupDescription || id
    if (!window.confirm(
      `确定删除用户组「${name}」（${id}）吗？\n将同时删除该组的模块权限与报表权限；仍关联成员的组会被拒绝删除。`,
    )) return
    remove.mutate(id)
  }, [remove])

  const errorMessage = describeApiError(groups.error, '发生未知错误，请稍后重试。')
  const filtered = useMemo(() => {
    const text = keyword.trim().toLowerCase()
    if (!text) return groups.data ?? []
    return (groups.data ?? []).filter((group) =>
      group.groupId.toLowerCase().includes(text)
      || group.groupDescription.toLowerCase().includes(text))
  }, [groups.data, keyword])

  const columns = useMemo<ColumnDef<UserGroupSummary, unknown>[]>(() => [
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
    { accessorKey: 'groupId', header: '组ID', minSize: 90, cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue()).trim()}</span>, meta: { minWidth: 90 } },
    { accessorKey: 'groupDescription', header: '组描述', minSize: 220, cell: (info) => <span>{String(info.getValue() || '—')}</span>, meta: { minWidth: 220 } },
    {
      accessorKey: 'memberCount',
      header: '成员数',
      cell: (info) => {
        const count = Number(info.getValue())
        return <span className={`badge ${count > 0 ? 'bg-success-subtle text-success' : 'bg-secondary-subtle text-secondary'}`}>{count}</span>
      },
      meta: { truncate: false, minWidth: 80 },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 332, minWidthFloor: true },
      cell: ({ row }) => {
        const group = row.original
        const id = group.groupId.trim()
        return (
          <div className="d-inline-flex gap-1">
            <Button size="sm" variant="ghost" icon={<IconShield size={14} />} title="组权限" aria-label="组权限" onClick={() => navigate(`/admin/groups/${encodeURIComponent(id)}/rights`)}>组权限</Button>
            <Button size="sm" variant="ghost" icon={<IconReport size={14} />} title="报表权限" aria-label="报表权限" onClick={() => navigate(`/admin/groups/${encodeURIComponent(id)}/report-rights`)}>报表权限</Button>
            <Button size="sm" variant="ghost" icon={<IconClick size={14} />} title="按钮权限" aria-label="按钮权限" onClick={() => navigate(`/admin/groups/${encodeURIComponent(id)}/button-rights`)}>按钮权限</Button>
            <Button size="sm" variant="ghost" icon={<IconUsers size={14} />} title="成员" aria-label="成员" onClick={() => navigate(`/admin/groups/${encodeURIComponent(id)}/members`)}>成员</Button>
            <Button size="sm" variant="ghost" icon={<IconEdit size={14} />} title="编辑" aria-label="编辑" onClick={() => setEditor({ mode: 'edit', group })}>编辑</Button>
            <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} title="删除" aria-label="删除" onClick={() => confirmRemove(group)} loading={remove.isPending}>删除</Button>
          </div>
        )
      },
    },
  ], [navigate, remove.isPending, confirmRemove])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="用户组管理查询"
        search={<ErpSearchBox value={keyword} onChange={setKeyword} debounceMs={300} placeholder="搜索组ID或组描述" ariaLabel="搜索用户组" />}
        actions={<div className="d-flex gap-2 align-items-center">
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void groups.refetch()}>刷新</Button>
          <Button size="sm" variant="primary" icon={<IconPlus size={16} />} onClick={() => setEditor({ mode: 'new' })}>新增</Button>
        </div>}
        header={groups.data ? <div className="erp-list-header text-secondary small px-3 pt-2">共 {groups.data.length} 个用户组{keyword.trim() ? `，筛选后 ${filtered.length} 个` : ''}；操作列提供组权限/报表权限/成员/编辑/删除，双击行也可编辑。</div> : undefined}
      >
        {groups.isPending ? <LoadingState label="正在加载用户组…" /> : groups.isError ? <ErrorState message={errorMessage} onRetry={() => void groups.refetch()} /> : (
          <ErpTable
            columns={columns}
            data={filtered}
            getRowId={(row) => row.groupId.trim()}
            resizable
            storageKey="user-group-admin-groups"
            clientSideSorting
            rowClickSingleSelect
            rowSelection={rowSelection}
            onRowSelectionChange={setRowSelection}
            onRowDoubleClick={(row) => setEditor({ mode: 'edit', group: row })}
            empty={<EmptyState title="没有用户组" description="点击「新增」建立第一个用户组。" />}
          />
        )}
      </ErpListCard>
      {remove.isError && (
        <div className="alert alert-danger mb-0" role="alert">
          {describeApiError(remove.error, '删除失败，请稍后重试。')}
        </div>
      )}
      {editor && (
        <GroupEditorModal
          open
          mode={editor.mode}
          group={editor.mode === 'edit' ? editor.group : null}
          onClose={() => setEditor(null)}
          onSaved={() => { setEditor(null); void groups.refetch() }}
        />
      )}
    </div>
  )
}
