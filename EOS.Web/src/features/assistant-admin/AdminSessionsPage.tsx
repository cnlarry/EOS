import { IconArchive, IconArchiveOff, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
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
import type { AdminSessionQuery, AdminSessionView } from './api'

const pageSize = 16

const VIEWS: Array<{ value: AdminSessionView; label: string; title: string }> = [
  { value: 'active', label: '在列', title: '只显示在列的会话' },
  { value: 'archived', label: '已归档', title: '只显示已归档的会话；永久删除在这里开放' },
  { value: 'all', label: '全部', title: '在列与已归档都显示' },
]

/** 表格列键 → 服务端排序列。排序必须由服务端做：列表是服务端分页的，前端排只会排当前这一页。 */
const SORT_KEYS: Record<string, NonNullable<AdminSessionQuery['sortBy']>> = {
  lastActiveAt: 'lastActive',
  createdAt: 'created',
  title: 'title',
  messageCount: 'messages',
}

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
 * 管理动作是归档·取消归档与"仅对已归档开放"的永久删除（单行与批量同一口径）。要看正文是另一个
 * 需要独立权限位与审计留痕的决定，不要因为"反正有读权限"就把它加进来。
 * </para>
 */
export function AdminSessionsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const [view, setView] = useState<AdminSessionView>('active')
  const [keyword, setKeyword] = useState('')
  const [owner, setOwner] = useState('')
  const [page, setPage] = useState(1)
  // 默认最近活跃在前（与服务端默认一致，首次加载不会出现"界面没箭头但顺序却在变"）
  const [sorting, setSorting] = useState<SortingState>([{ id: 'lastActiveAt', desc: true }])
  const [selected, setSelected] = useState<RowSelectionState>({})

  const sortParams = useMemo(() => {
    const first = sorting[0]
    const sortBy = (first && SORT_KEYS[first.id]) || 'lastActive'
    const sortDir: 'asc' | 'desc' = !first || first.desc ? 'desc' : 'asc'
    return { sortBy, sortDir }
  }, [sorting])

  const sessions = useQuery({
    queryKey: ['assistant-admin-sessions', view, keyword, owner, page, sortParams.sortBy, sortParams.sortDir],
    queryFn: () => listAllSessions({
      offset: (page - 1) * pageSize,
      limit: pageSize,
      state: view,
      keyword,
      owner,
      sortBy: sortParams.sortBy,
      sortDir: sortParams.sortDir,
    }),
  })

  const owners = useQuery({
    queryKey: ['assistant-admin-session-owners'],
    queryFn: listSessionOwners,
    staleTime: 60_000,
  })

  // 依赖 sessions.data 而不是"每次现取 items"：后者每渲染都是新数组，
  // 会让下面按选中集过滤的 useMemo 每帧重算（lint 也会提示）。
  const items = useMemo(() => sessions.data?.items ?? [], [sessions.data])
  const total = sessions.data?.total ?? 0
  const errorMessage = describeApiError(sessions.error, '加载会话列表失败，请稍后重试。')

  // 选中集只对"当前这一页看得见的行"有意义：翻页/换筛选/换排序后清空，
  // 否则批量操作会作用到用户已经看不见的行上。
  const resetSelection = useCallback(() => setSelected({}), [])
  const selectedRows = useMemo(
    () => items.filter(item => selected[item.id]),
    [items, selected],
  )

  const refresh = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-admin-sessions'] })
  }, [queryClient])

  const archive = useMutation({
    mutationFn: (variables: { id: string; archived: boolean }) => archiveAnySession(variables.id, variables.archived),
    onSuccess: (_result, variables) => {
      resetSelection()
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
      resetSelection()
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

  /** 批量归档 / 取消归档：作用在选中的那些行上，逐条调用（服务端逐条幂等）。 */
  const batchArchive = useCallback(async (archived: boolean) => {
    const targets = selectedRows.filter(item => Boolean(item.archivedAt) !== archived)
    if (targets.length === 0) {
      toast.notify({ message: '选中的会话没有需要改状态的。', variant: 'info' })
      return
    }
    for (const item of targets) {
      archive.mutate({ id: item.id, archived })
    }
  }, [archive, selectedRows, toast])

  /** 批量删除：**只对已归档的**（服务端也拒在列的），所以这里先把在列的挑出来告知。 */
  const batchDelete = useCallback(() => {
    const deletable = selectedRows.filter(item => Boolean(item.archivedAt))
    const skipped = selectedRows.length - deletable.length
    if (deletable.length === 0) {
      toast.notify({ message: '选中的会话都还在「在列」——只有已归档的可以永久删除。', variant: 'warning' })
      return
    }
    const messages = deletable.reduce((sum, item) => sum + (item.messageCount ?? 0), 0)
    if (window.confirm(
      `确定永久删除选中的 ${deletable.length} 个会话吗？\n\n`
      + `将连同它们的 ${messages} 条消息一起抹掉，删除后无法恢复。`
      + (skipped > 0 ? `\n\n（另有 ${skipped} 个还在「在列」，会被跳过。）` : ''))) {
      for (const item of deletable) remove.mutate(item.id)
    }
  }, [remove, selectedRows, toast])

  const columns = useMemo<ColumnDef<AssistantSession, unknown>[]>(() => [
    {
      id: 'select',
      header: ({ table }) => (
        <input
          type="checkbox"
          className="form-check-input"
          aria-label="全选本页"
          checked={table.getIsAllPageRowsSelected()}
          ref={(node) => {
            if (node) node.indeterminate = table.getIsSomePageRowsSelected() && !table.getIsAllPageRowsSelected()
          }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          type="checkbox"
          className="form-check-input"
          aria-label={`选择 ${row.original.title}`}
          checked={row.getIsSelected()}
          disabled={!row.getCanSelect()}
          onChange={row.getToggleSelectedHandler()}
        />
      ),
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-center', minWidth: 44, minWidthFloor: true, resizable: false, truncate: false },
    },
    {
      accessorKey: 'userId',
      header: '归属用户',
      enableSorting: false,
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
        search={<ErpSearchBox value={keyword} onChange={(value) => { resetSelection(); setKeyword(value); setPage(1) }}
          debounceMs={300} placeholder="搜索会话标题" ariaLabel="搜索会话" />}
        actions={<>
          <select className="form-select form-select-sm" style={{ width: 150 }} value={owner}
            aria-label="按归属用户筛选"
            onChange={(event) => { resetSelection(); setOwner(event.target.value); setPage(1) }}>
            <option value="">全部用户</option>
            {(owners.data ?? []).map(item => <option key={item} value={item}>{item}</option>)}
          </select>
          {VIEWS.map(item => (
            <Button key={item.value} size="sm" variant={view === item.value ? 'primary' : 'secondary'}
              title={item.title}
              onClick={() => { resetSelection(); setView(item.value); setPage(1) }}>{item.label}</Button>
          ))}
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void sessions.refetch()}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">会话管理</h2>
          <span className="text-secondary small">共 {total} 个{view === 'archived' ? '已归档' : view === 'active' ? '在列' : ''}会话</span>
          {selectedRows.length > 0 && (
            <span className="d-inline-flex align-items-center gap-2">
              <span className="badge bg-blue-lt">已选 {selectedRows.length} 项</span>
              <Button size="sm" icon={<IconArchive size={14} />} disabled={archive.isPending}
                onClick={() => void batchArchive(true)}>批量归档</Button>
              <Button size="sm" icon={<IconArchiveOff size={14} />} disabled={archive.isPending}
                onClick={() => void batchArchive(false)}>批量取消归档</Button>
              <Button size="sm" variant="danger" icon={<IconTrash size={14} />} disabled={remove.isPending}
                onClick={batchDelete}>批量删除</Button>
            </span>
          )}
          <span className="ms-auto text-secondary small">
            跨用户查看会话元数据；不含对话正文，归档＝可恢复，删除只对已归档开放
          </span>
        </div>}
        footer={!sessions.isPending && !sessions.isError
          ? <ErpPagination total={total} page={page} pageSize={pageSize}
            onPageChange={(next) => { resetSelection(); setPage(next) }} />
          : undefined}
      >
        {sessions.isPending ? <LoadingState label="正在加载会话…" /> : sessions.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void sessions.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={items}
            getRowId={(row) => row.id}
            // 不传 clientSideSorting：列表是服务端分页的，排序交给服务端（ErpTable 的 manualSorting）
            sorting={sorting}
            onSortingChange={(next) => { resetSelection(); setSorting(next); setPage(1) }}
            rowSelection={selected}
            onRowSelectionChange={setSelected}
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
