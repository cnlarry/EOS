import { IconArchive, IconArchiveOff, IconEdit, IconExternalLink, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { useCallback, useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { useToast } from '../../components/ui/toastContext'
import { describeApiError } from '../../lib/errors'
import { archiveSession, deleteSession, listSessions, renameSession } from './api'
import { useAssistant } from './assistantContext'
import type { AssistantSession } from './types'

const pageSize = 16

/** 视图三态，与后端 `state` 参数一一对应（前端不自己筛——筛过的结果配服务端分页会算错总数）。 */
type SessionView = 'active' | 'archived' | 'all'

const VIEWS: Array<{ value: SessionView; label: string; title: string }> = [
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
 * 会话管理：历史会话一览，归档 / 取消归档、重命名、打开、以及**只对已归档开放**的永久删除。
 *
 * <para>
 * 它是普通工作区标签页（路由 `/assistant/sessions`），与助手全屏页同构：可并存、可深链、
 * 随工作区持久化。与助手下拉共用同一份会话状态（`AssistantProvider`），所以任何一处改动
 * 都会先把另一处刷掉，不会出现"管理页删了、下拉里还在"。
 * </para>
 *
 * <para>
 * **归档与删除是两件事**：归档＝从「在列」收起来但历史一行不动，随时可取消；
 * 删除＝连消息一起抹掉、不可恢复，因此只对**已归档**会话开放（服务端也强制，
 * 未归档一律 400 `SESSION_NOT_ARCHIVED`），避免在列表里手滑删掉还在用的会话。
 * </para>
 */
export function SessionAdminPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const assistant = useAssistant()
  const [view, setView] = useState<SessionView>('active')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [renaming, setRenaming] = useState<{ id: string; title: string } | null>(null)

  const sessions = useQuery({
    queryKey: ['assistant-sessions', view, keyword, page],
    queryFn: () => listSessions({ offset: (page - 1) * pageSize, limit: pageSize, state: view, keyword }),
  })

  const items = sessions.data?.items ?? []
  const total = sessions.data?.total ?? 0
  const errorMessage = describeApiError(sessions.error, '加载会话列表失败，请稍后重试。')

  /** 管理页与助手下拉是两份状态：这边的改动要顺手把那边刷掉，否则两处显示会不一致。 */
  const refreshBoth = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['assistant-sessions'] })
    void assistant.refreshSessions()
  }, [assistant, queryClient])

  const archive = useMutation({
    mutationFn: ({ id, archived }: { id: string; archived: boolean }) => archiveSession(id, archived),
    onSuccess: (_result, variables) => {
      refreshBoth()
      toast.notify({
        message: variables.archived
          ? '已归档。会话不再出现在「在列」里，历史完整保留，可随时取消归档。'
          : '已取消归档，会话回到「在列」。',
        variant: 'success',
      })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '操作失败。'), variant: 'danger' }),
  })

  const rename = useMutation({
    mutationFn: ({ id, title }: { id: string; title: string }) => renameSession(id, title),
    onSuccess: (_result, variables) => {
      setRenaming(null)
      refreshBoth()
      toast.notify({ message: `已改名为「${variables.title}」。`, variant: 'success' })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '重命名失败。'), variant: 'danger' }),
  })

  const remove = useMutation({
    mutationFn: (id: string) => deleteSession(id),
    onSuccess: (_result, id) => {
      refreshBoth()
      // 删掉的正是助手当前挂着的会话时，把它切回"未选中"，下次提问会自动开新会话，
      // 而不是停在一个已不存在的会话上。
      if (assistant.sessionId === id) assistant.setSessionId(null)
      toast.notify({ message: '已永久删除，该会话的消息不再保留。', variant: 'success' })
    },
    onError: (error) => toast.notify({ message: describeApiError(error, '删除失败。'), variant: 'danger' }),
  })

  const openSession = useCallback((session: AssistantSession) => {
    assistant.setSessionId(session.id)
    assistant.setOpen(true)
    toast.notify({ message: `已切换到「${session.title}」，助手已打开。`, variant: 'info' })
  }, [assistant, toast])

  const confirmDelete = useCallback((session: AssistantSession) => {
    const count = session.messageCount ?? 0
    // 二次确认：归档是收起来（可恢复），删除不可恢复——这句话必须落到用户眼前
    if (window.confirm(
      `确定永久删除会话「${session.title}」吗？\n\n`
      + `将连同它的 ${count} 条消息一起抹掉，删除后无法恢复（归档可以恢复，删除不能）。`)) {
      remove.mutate(session.id)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<AssistantSession, unknown>[]>(() => [
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
      // 消息数是判断"这条还值不值得留"的主要依据，所以给最小宽度、别被挤没
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
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 300, minWidthFloor: true, resizable: false },
      cell: ({ row }) => {
        const session = row.original
        const archived = Boolean(session.archivedAt)
        return (
          <div className="d-flex gap-1 justify-content-end">
            <Button size="sm" variant="ghost" icon={<IconExternalLink size={14} />}
              title="切换到这条会话并打开助手" onClick={() => openSession(session)}>打开</Button>
            <Button size="sm" variant="ghost" icon={<IconEdit size={14} />}
              title="重命名" onClick={() => setRenaming({ id: session.id, title: session.title })}>重命名</Button>
            <Button size="sm" variant="ghost" icon={archived ? <IconArchiveOff size={14} /> : <IconArchive size={14} />}
              title={archived ? '取消归档，回到「在列」' : '归档：从「在列」收起来，历史完整保留、可取消'}
              loading={archive.isPending && archive.variables?.id === session.id}
              disabled={archive.isPending}
              onClick={() => archive.mutate({ id: session.id, archived: !archived })}>
              {archived ? '取消归档' : '归档'}
            </Button>
            {/* 删除只对已归档开放：服务端也会拒（400 SESSION_NOT_ARCHIVED），
                这里干脆不给按钮——不该出现"点了才被告知不行"的动作 */}
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
  ], [archive, confirmDelete, openSession, remove])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="会话管理查询与操作"
        search={<ErpSearchBox value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }}
          debounceMs={300} placeholder="搜索会话标题" ariaLabel="搜索会话" />}
        actions={<>
          {VIEWS.map(item => (
            <Button key={item.value} size="sm" variant={view === item.value ? 'primary' : 'secondary'}
              title={item.title} onClick={() => { setView(item.value); setPage(1) }}>{item.label}</Button>
          ))}
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void sessions.refetch()}>刷新</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">会话管理</h2>
          <span className="text-secondary small">共 {total} 个{view === 'archived' ? '已归档' : view === 'active' ? '在列' : ''}会话</span>
          <span className="ms-auto text-secondary small">归档＝收起来（可恢复）；删除只对已归档开放，且不可恢复</span>
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
            storageKey="assistant-sessions"
            empty={<EmptyState title="没有找到会话"
              description={view === 'archived' ? '还没有已归档的会话。' : '换个关键词，或直接开始一次新的对话。'} />}
          />
        )}
      </ErpListCard>

      {renaming && (
        <Modal
          title="重命名会话"
          size="sm"
          onClose={() => setRenaming(null)}
          footer={<>
            <Button size="sm" variant="secondary" onClick={() => setRenaming(null)}>取消</Button>
            <Button size="sm" loading={rename.isPending} disabled={!renaming.title.trim()}
              onClick={() => rename.mutate({ id: renaming.id, title: renaming.title.trim() })}>保存</Button>
          </>}
        >
          <label className="form-label" htmlFor="session-admin-rename">会话名称</label>
          <input
            id="session-admin-rename"
            className="form-control"
            autoFocus
            maxLength={200}
            value={renaming.title}
            onChange={(event) => setRenaming({ ...renaming, title: event.target.value })}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && renaming.title.trim()) {
                event.preventDefault()
                rename.mutate({ id: renaming.id, title: renaming.title.trim() })
              }
            }}
          />
          <div className="form-hint mt-2">名称必填，超过 200 字会被截断。</div>
        </Modal>
      )}
    </div>
  )
}
