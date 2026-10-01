import { IconArchive, IconArchiveOff, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import type { AssistantSession } from '../assistant/types'
import { archiveAnySession, deleteAnySession, listAllSessions, listSessionOwners } from './api'
import type { AdminSessionView } from './api'

const pageSize = 16

const VIEWS: Array<{ value: AdminSessionView; label: string; title: string }> = [
  { value: 'active', label: '在列', title: '只显示在列的会话' },
  { value: 'archived', label: '已归档', title: '只显示已归档的会话；永久删除在这里开放' },
  { value: 'all', label: '全部', title: '在列与已归档都显示' },
]

/** 列表要能一眼比出先后，所以显示到分钟。 */
function formatTime(value: string) {
  const date = new Date(value)
  if (!value || Number.isNaN(date.getTime())) return '—'
  return date.toLocaleString('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
  })
}

/**
 * 工作助手管理 → **会话管理**（菜单组 31 / 模块 3101，路由 `/admin/assistant/sessions`）。
 *
 * <para>
 * 与助手自己的 `/assistant/sessions` 是**两个东西**：那一侧是个人视角（服务端按 `USER_ID` 隔离，
 * 只能看自己的），这一侧面向管理员、**跨用户**看全系统会话，权限门是 3101 的
 * `CanBrowse` / `CanEdit`（见 ADR-030 §4）。
 * </para>
 *
 * <para>
 * **只看元数据，不提供对话正文**（ADR-030 §2）：列表给出归属用户 / 标题 / 消息数 / 时间 / 归档状态，
 * 管理动作只有归档·取消归档与"仅对已归档开放"的永久删除。要看正文是另一个需要独立权限位与
 * 审计留痕的决定，不要因为"反正有读权限"就把它加进来。
 * </para>
 */
export function AdminSessionsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [view, setView] = useState<AdminSessionView>('active')
  const [keyword, setKeyword] = useState('')
  const [owner, setOwner] = useState('')
  const [page, setPage] = useState(1)

  const sessions = useQuery({
    queryKey: ['assistant-admin-sessions', view, keyword, owner, page],
    queryFn: () => listAllSessions({ offset: (page - 1) * pageSize, limit: pageSize, state: view, keyword, owner }),
  })

  const owners = useQuery({
    queryKey: ['assistant-admin-session-owners'],
    queryFn: listSessionOwners,
    staleTime: 60_000,
  })

  const items = sessions.data?.items ?? []
  const total = sessions.data?.total ?? 0
  const errorMessage = describeApiError(sessions.error, '加载会话列表失败，请稍后重试。')

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-sessions'] })
  }, [queryClient])

  const archive = useMutation({
    mutationFn: ({ id, archived }: { id: string; archived: boolean }) => archiveAnySession(id, archived),
    onSuccess: (_result, variables) => {
      refresh()
      toast.notify({
        message: variables.archived
          ? '已归档。该用户不再在助手里看到它，历史完整保留、可随时取消归档。'
          : '已取消归档。',
        variant: 'success',
      })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '操作失败。'), variant: 'danger' }),
  })

  const remove = useMutation({
    mutationFn: (id: string) => deleteAnySession(id),
    onSuccess: () => {
      refresh()
      toast.notify({ message: '已永久删除，该会话的消息不再保留。', variant: 'success' })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '删除失败。'), variant: 'danger' }),
  })

  const confirmDelete = useCallback((session: AssistantSession) => {
    const count = session.messageCount ?? 0
    // 二次确认：这条会在**别人的**会话上生效，所以把"谁的多大损失"写清楚
    if (window.confirm(
      `确定永久删除「${session.userId}」的会话「${session.title}」吗？\n\n`
      + `将连同它的 ${count} 条消息一起抹掉，删除后无法恢复（归档可以恢复，删除不能）。`)) {
      remove.mutate(session.id)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<AssistantSession, unknown>[]>(() => [
    {
      accessorKey: 'userId',
      header: '归属用户',
      meta: { className: 'text-nowrap', minWidth: 110 },
      cell: (info) => <span className="font-monospace">{String(info.getValue())}</span>,
    },
    {
      accessorKey: 'title',
      header: '会话',
      cell: (info) => (
        <span className="d-inline-flex align-items-center gap-2">
          <span className="fw-semibold">{String(info.getValue())}</span>
          {info.row.original.archivedAt && <span className="badge bg-secondary-lt">已归档</span>}
        </span>
      ),
    },
    {
      accessorKey: 'messageCount',
      header: '消息',
      meta: { className: 'text-end text-nowrap', minWidth: 72 },
      cell: (info) => <span className="text-secondary">{info.getValue() as number | undefined ?? 0}</span>,
    },
    {
      accessorKey: 'lastActiveAt',
      header: '最近活跃',
      meta: { className: 'text-nowrap' },
      cell: (info) => <span className="text-secondary">{formatTime(String(info.getValue()))}</span>,
    },
    {
      accessorKey: 'createdAt',
      header: '创建时间',
      meta: { className: 'text-nowrap' },
      cell: (info) => <span className="text-secondary">{formatTime(String(info.getValue()))}</span>,
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 220, minWidthFloor: true, resizable: false },
      cell: ({ row }) => {
        const session = row.original
        const archived = Boolean(session.archivedAt)
        return (
          <div className="d-flex gap-1 justify-content-end">
            <Button size="sm" variant="ghost" icon={archived ? <IconArchiveOff size={14} /> : <IconArchive size={14} />}
              title={archived ? '取消归档，回到该用户的「在列」' : '归档：从该用户的「在列」收起来，历史完整保留、可取消'}
              loading={archive.isPending && archive.variables?.id === session.id}
              disabled={archive.isPending}
              onClick={() => archive.mutate({ id: session.id, archived: !archived })}>
              {archived ? '取消归档' : '归档'}
            </Button>
            {/* 删除只对已归档开放（服务端也拒），所以按钮只在已归档行出现 */}
            {archived && (
              <Button size="sm" variant="ghost" icon={<IconTrash size={14} />}
                title="永久删除（不可恢复）"
                loading={remove.isPending && remove.variables === session.id}
                disabled={remove.isPending}
                onClick={() => confirmDelete(session)}>删除</Button>
            )}
          </div>
        )
      },
    },
    // 依赖整个 mutation 对象：列定义读的是它的 mutate/isPending/variables，只列字段会漏
  ], [archive, confirmDelete, remove])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="助手会话管理查询与操作"
        search={<ErpSearchBox value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }}
          debounceMs={300} placeholder="搜索会话标题" ariaLabel="搜索会话" />}
        actions={<>
          <select className="form-select form-select-sm" style={{ width: 150 }} value={owner}
            aria-label="按归属用户筛选"
            onChange={(event) => { setOwner(event.target.value); setPage(1) }}>
            <option value="">全部用户</option>
            {(owners.data ?? []).map(item => <option key={item} value={item}>{item}</option>)}
          </select>
          {VIEWS.map(item => (
            <Button key={item.value} size="sm" variant={view === item.value ? 'primary' : 'secondary'}
              title={item.title} onClick={() => { setView(item.value); setPage(1) }}>{item.label}</Button>
          ))}
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void sessions.refetch()}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">会话管理</h2>
          <span className="text-secondary small">共 {total} 个{view === 'archived' ? '已归档' : view === 'active' ? '在列' : ''}会话</span>
          <span className="ms-auto text-secondary small">
            跨用户查看会话元数据；**不含对话正文**，归档＝可恢复，删除只对已归档开放
          </span>
        </div>}
        footer={!sessions.isPending && !sessions.isError
          ? <ErpPagination total={total} page={page} pageSize={pageSize} onPageChange={setPage} />
          : undefined}
      >
        {sessions.isPending ? <LoadingState label="正在加载会话…" /> : sessions.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void sessions.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={items}
            getRowId={(row) => row.id}
            resizable
            storageKey="assistant-admin-sessions"
            empty={<EmptyState title="没有找到会话"
              description={view === 'archived' ? '还没有已归档的会话。' : '换个关键词或用户，看看别处。'} />}
          />
        )}
      </ErpListCard>
    </div>
  )
}
