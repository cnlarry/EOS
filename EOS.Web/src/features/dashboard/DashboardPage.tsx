import { IconArrowUpRight, IconChecklist, IconClockHour4, IconFolder, IconGitBranch } from '@tabler/icons-react'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { navigationIcons } from '../../components/layout/navigationIcons'
import { Button } from '../../components/ui/Button'
import { useAuth } from '../auth/authContext'
import { moduleReadPermission } from '../auth/modulePermissions'
import { workbenchList } from '../document-workbench/workbenchPath'
import type { NavigationItem } from '../auth/types'
import { apiClient } from '../../services/api'
import { RECENT_MODULES_KEY } from '../../lib/storageKeys'
import { describeApiError } from '../../lib/errors'

interface MyTask {
  moduleId: number
  title: string
  pending: number
}

interface MyTasksResult {
  tasks: MyTask[]
  flowTasks: unknown[]
  engineEnabled: boolean
  note: string
}

interface MyStartedFlow {
  wfId: number
  moduleId: number
  title: string
  keyValue: string
  keyValueDesc: string
  startDate: string | null
  step: string
  stepDesc: string
}

/** 深度展开导航树，返回全部叶子（快捷入口按路由匹配）。 */
function flattenLeaves(items: NavigationItem[]): NavigationItem[] {
  return items.flatMap((item) => (item.children?.length ? flattenLeaves(item.children) : [item]))
}

function readRecent(): string[] {
  try {
    const saved = JSON.parse(localStorage.getItem(RECENT_MODULES_KEY) ?? '[]') as unknown
    return Array.isArray(saved) ? (saved as string[]) : []
  } catch {
    return []
  }
}

export function DashboardPage() {
  const navigate = useNavigate()
  const { bootstrap, hasPermission } = useAuth()
  const canSeeTasks = hasPermission(moduleReadPermission(2102))
  const [pendingSelection, setPendingSelection] = useState<RowSelectionState>({})
  const [startedSelection, setStartedSelection] = useState<RowSelectionState>({})

  const myTasks = useQuery({
    queryKey: ['dashboard-my-tasks'],
    queryFn: () => apiClient.get<MyTasksResult>('/workflow/my-tasks'),
    enabled: canSeeTasks,
  })
  const myStarted = useQuery({
    queryKey: ['dashboard-my-started'],
    queryFn: () => apiClient.get<{ rows: MyStartedFlow[] }>('/workflow/my-started'),
    enabled: canSeeTasks,
  })

  const leaves = useMemo(() => flattenLeaves(bootstrap?.navigation ?? []), [bootstrap?.navigation])
  const recent = useMemo(() => {
    const saved = readRecent()
    return saved
      .map((route) => leaves.find((leaf) => leaf.route === route))
      .filter((leaf): leaf is NavigationItem => Boolean(leaf))
  }, [leaves])

  const tasksError = describeApiError(myTasks.error, '发生未知错误，请稍后重试。')
  const startedRows = myStarted.data?.rows ?? []
  const pendingTasks = (myTasks.data?.tasks ?? []).filter((task) => task.pending > 0)
  const totalPending = pendingTasks.reduce((sum, task) => sum + task.pending, 0)
  const flowCount = myTasks.data?.flowTasks.length ?? 0

  const pendingColumns: ColumnDef<MyTask, unknown>[] = [
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
    { accessorKey: 'title', header: '单据类型', cell: (info) => <span className="fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'moduleId', header: '模块号', cell: (info) => <span className="font-monospace text-secondary">{String(info.getValue())}</span> },
    {
      accessorKey: 'pending',
      header: '待批核',
      cell: (info) => {
        const count = Number(info.getValue() ?? 0)
        return count > 0 ? <span className="badge text-bg-warning">{count}</span> : <span className="text-secondary">0</span>
      },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true, resizable: false, truncate: false },
      cell: ({ row }) => (
        <Button size="sm" className="erp-table-action" onClick={() => navigate(workbenchList(row.original.moduleId))}>去处理</Button>
      ),
    },
  ]

  const startedColumns: ColumnDef<MyStartedFlow, unknown>[] = [
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
    { accessorKey: 'title', header: '单据类型', cell: (info) => <span className="fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'keyValueDesc', header: '单据', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'step',
      header: '当前步骤',
      cell: (info) => (
        <span className="font-monospace">
          {String(info.getValue() ?? '—')} {String((info.row.original as MyStartedFlow).stepDesc ?? '')}
        </span>
      ),
    },
    {
      accessorKey: 'startDate',
      header: '发起时间',
      cell: (info) => {
        const raw = info.getValue()
        return raw ? new Date(String(raw)).toLocaleString() : '—'
      },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true, resizable: false, truncate: false },
      cell: ({ row }) => (
        <Button size="sm" className="erp-table-action" onClick={() => navigate(workbenchList(row.original.moduleId))}>查看</Button>
      ),
    },
  ]

  return (
    <div className="d-grid erp-dashboard">
      <div className="row row-deck row-cards">
        <div className="col-sm-6 col-xl-4">
          <article className="card erp-metric-card">
            <div className="card-body">
              <div className="d-flex align-items-start">
                <div>
                  <div className="text-secondary fw-medium">待批核单据</div>
                  <div className="h1 mb-1 mt-2">{canSeeTasks ? (myTasks.isPending ? '…' : totalPending) : '—'}</div>
                  <div className="text-secondary small">{canSeeTasks ? '未确认单据合计，含已提交流程' : '无「我的任务」模块权限'}</div>
                </div>
                <span className="erp-metric-icon ms-auto">
                  <IconChecklist size={23} stroke={1.7} />
                </span>
              </div>
            </div>
          </article>
        </div>
        <div className="col-sm-6 col-xl-4">
          <article className="card erp-metric-card">
            <div className="card-body">
              <div className="d-flex align-items-start">
                <div>
                  <div className="text-secondary fw-medium">流程审批待办</div>
                  <div className="h1 mb-1 mt-2">{canSeeTasks ? (myTasks.isPending ? '…' : flowCount) : '—'}</div>
                  <div className="text-secondary small">{canSeeTasks ? '工作流引擎待我处理的任务' : '无「我的任务」模块权限'}</div>
                </div>
                <span className="erp-metric-icon ms-auto">
                  <IconGitBranch size={23} stroke={1.7} />
                </span>
              </div>
            </div>
          </article>
        </div>
        <div className="col-sm-6 col-xl-4">
          <article className="card erp-metric-card">
            <div className="card-body">
              <div className="d-flex align-items-start">
                <div>
                  <div className="text-secondary fw-medium">我发起的流程</div>
                  <div className="h1 mb-1 mt-2">{canSeeTasks ? (myStarted.isPending ? '…' : startedRows.length) : '—'}</div>
                  <div className="text-secondary small">{canSeeTasks ? '在途流程，可撤回后修改重新提交' : '无「我的任务」模块权限'}</div>
                </div>
                <span className="erp-metric-icon ms-auto">
                  <IconClockHour4 size={23} stroke={1.7} />
                </span>
              </div>
            </div>
          </article>
        </div>
      </div>

      <div className="row row-cards">
        <div className="col-lg-8">
          <ErpListCard
            ariaLabel="待批核单据"
            search={null}
            actions={canSeeTasks ? (
              <Button size="sm" icon={<IconArrowUpRight size={16} />} onClick={() => navigate('/my-tasks')}>查看全部</Button>
            ) : undefined}
            header={<div className="px-3 pt-2 small text-secondary">待批核单据 {canSeeTasks ? pendingTasks.length : '—'} 项</div>}
          >
            {!canSeeTasks ? (
              <div className="text-center text-secondary py-4">无「我的任务」模块浏览权限，无法展示待办明细。</div>
            ) : myTasks.isPending ? (
              <LoadingState label="正在加载待办任务…" />
            ) : myTasks.isError ? (
              <ErrorState message={tasksError} onRetry={() => void myTasks.refetch()} />
            ) : (
              <ErpTable
                columns={pendingColumns}
                data={pendingTasks}
                getRowId={(row) => String(row.moduleId)}
                empty={<div className="text-center text-secondary py-4">当前无待批核单据</div>}
                resizable
                storageKey="dashboard-pending"
                clientSideSorting
                rowClickSingleSelect
                rowSelection={pendingSelection}
                onRowSelectionChange={setPendingSelection}
              />
            )}
          </ErpListCard>
        </div>
        <div className="col-lg-4">
          <section className="card h-100">
            <div className="card-header">
              <h2 className="card-title">快捷入口</h2>
            </div>
            <div className="list-group list-group-flush">
              {recent.length === 0 ? (
                <div className="list-group-item text-secondary small">从左侧菜单访问过的工作台/报表会自动出现在这里。</div>
              ) : (
                recent.map((item) => {
                  const Icon = navigationIcons[item.icon] ?? IconFolder
                  return (
                    <Link className="list-group-item list-group-item-action d-flex align-items-center gap-2" to={item.route!} key={item.route}>
                      <Icon size={18} stroke={1.7} className="text-blue" />
                      <span className="fw-semibold text-truncate">{item.label}</span>
                    </Link>
                  )
                })
              )}
            </div>
          </section>
        </div>
      </div>

      {canSeeTasks && startedRows.length > 0 && (
        <ErpListCard
          ariaLabel="我发起的在途流程"
          search={null}
          actions={<Button size="sm" icon={<IconArrowUpRight size={16} />} onClick={() => navigate('/my-tasks')}>查看全部</Button>}
          header={<div className="px-3 pt-2 small text-secondary">我发起的在途流程 {startedRows.length} 项</div>}
        >
          {myStarted.isPending ? (
            <LoadingState label="正在加载流程…" />
          ) : myStarted.isError ? (
            <ErrorState message={describeApiError(myStarted.error, '发生未知错误，请稍后重试。')} onRetry={() => void myStarted.refetch()} />
          ) : (
            <ErpTable
              columns={startedColumns}
              data={startedRows}
              getRowId={(row) => String(row.wfId)}
              empty={<div className="text-center text-secondary py-4">当前无我发起的在途流程</div>}
              resizable
              storageKey="dashboard-started"
              clientSideSorting
              rowClickSingleSelect
              rowSelection={startedSelection}
              onRowSelectionChange={setStartedSelection}
            />
          )}
        </ErpListCard>
      )}
    </div>
  )
}
