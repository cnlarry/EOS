import { IconExternalLink, IconRefresh, IconUsers, IconReport, IconShield } from '@tabler/icons-react'
import { useMutation, useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { ReportRightsMatrix } from './ReportRightsMatrix'
import { RightsMatrix } from './RightsMatrix'
import { RightsMemberPicker, type PickerOption } from './RightsMemberPicker'
import type { GroupMemberSummary, UserGroupSummary } from './types'

export function UserGroupAdminPage() {
  const groups = useQuery({
    queryKey: ['rights-admin', 'groups'],
    queryFn: () => apiClient.get<UserGroupSummary[]>('/admin/groups'),
  })
  const [rightsTarget, setRightsTarget] = useState<string | null>(null)
  const [reportTarget, setReportTarget] = useState<string | null>(null)
  const [memberTarget, setMemberTarget] = useState<string | null>(null)

  const members = useQuery({
    queryKey: ['rights-admin', 'members', memberTarget],
    queryFn: () => apiClient.get<GroupMemberSummary[]>(`/admin/groups/${encodeURIComponent(memberTarget!.trim())}/members`),
    enabled: memberTarget !== null,
  })
  const users = useQuery({
    queryKey: ['rights-admin', 'users-options'],
    queryFn: () => apiClient.get<{ items: { userId: string; employeeName: string }[] }>('/admin/users', { query: { page: 1, pageSize: 100 } }),
  })

  const memberOptions = useMemo<PickerOption[]>(
    () => (users.data?.items ?? []).map((user) => ({ id: user.userId.trim(), label: user.employeeName })),
    [users.data],
  )
  const memberSelected = useMemo(
    () => (members.data ?? []).map((member) => member.userId.trim()),
    [members.data],
  )
  const saveMembers = useMutation({
    mutationFn: async (ids: string[]) => {
      await apiClient.put(`/admin/groups/${encodeURIComponent(memberTarget!.trim())}/members`, { ids })
    },
    onSuccess: () => {
      setMemberTarget(null)
      void groups.refetch()
    },
  })

  const errorMessage = groups.error instanceof ApiError ? groups.error.body.message : '发生未知错误，请稍后重试。'
  const columns = useMemo<ColumnDef<UserGroupSummary, unknown>[]>(() => [
    { accessorKey: 'groupId', header: '组ID', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue()).trim()}</span> },
    { accessorKey: 'groupDescription', header: '组描述', cell: (info) => <span>{String(info.getValue() || '—')}</span> },
    {
      accessorKey: 'memberCount',
      header: '成员数',
      cell: (info) => <span className="badge bg-secondary-subtle text-secondary">{String(info.getValue())}</span>,
      meta: { truncate: false },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true, truncate: false },
      cell: ({ row }) => {
        const group = row.original
        const id = group.groupId.trim()
        return (
          <div className="d-inline-flex gap-1">
            <Button size="sm" icon={<IconShield size={15} />} onClick={() => setRightsTarget(id)}>组权限</Button>
            <Button size="sm" icon={<IconReport size={15} />} onClick={() => setReportTarget(id)}>报表权限</Button>
            <Button size="sm" variant="secondary" icon={<IconUsers size={15} />} onClick={() => setMemberTarget(id)}>成员</Button>
            <Button size="sm" variant="ghost" icon={<IconExternalLink size={15} />} title="在统一表单工作台中维护组主档" onClick={() => { window.location.href = '/document-workbench/2305' }}>
              主档
            </Button>
          </div>
        )
      },
    },
  ], [])

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="用户组管理查询"
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void groups.refetch()}>刷新</Button>}
        header={groups.data ? <div className="erp-list-header text-secondary small px-3 pt-2">共 {groups.data.length} 个用户组；组主档新增/编辑请通过「主档」进入工作台维护。</div> : undefined}
      >
        {groups.isPending ? <LoadingState label="正在加载用户组…" /> : groups.isError ? <ErrorState message={errorMessage} onRetry={() => void groups.refetch()} /> : (
          <ErpTable columns={columns} data={groups.data ?? []} resizable storageKey="user-group-admin-groups" empty={<EmptyState title="没有用户组" description="请先在工作台中建立用户组主档。" />} />
        )}
      </ErpListCard>
      <RightsMatrix
        open={rightsTarget !== null}
        mode="group"
        targetId={rightsTarget ?? ''}
        onClose={() => setRightsTarget(null)}
      />
      <ReportRightsMatrix
        open={reportTarget !== null}
        mode="group"
        targetId={reportTarget ?? ''}
        onClose={() => setReportTarget(null)}
      />
      <RightsMemberPicker
        open={memberTarget !== null}
        title={`组成员：${memberTarget ?? ''}`}
        hint="成员关系按组全量替换保存（取消勾选 = 移出该组）。"
        options={memberOptions}
        selected={memberSelected}
        loading={memberTarget !== null && (members.isPending || users.isPending)}
        onClose={() => setMemberTarget(null)}
        onSave={(ids) => saveMembers.mutateAsync(ids)}
      />
    </div>
  )
}
