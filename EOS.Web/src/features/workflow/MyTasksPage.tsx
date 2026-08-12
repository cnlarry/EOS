import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

interface MyTask {
  moduleId: number
  title: string
  pending: number
}

interface FlowTask {
  myTaskId: number
  wfId: number
  step: string
  stepDesc: string
  moduleId: number
  keyValue: string
  title: string
}

interface MyTasksResult {
  tasks: MyTask[]
  flowTasks: FlowTask[]
  engineEnabled: boolean
  note: string
}

/** 我的任务（2102）：真实流程待办（同意/驳回）+ 直接批核模型单据计数 */
export function MyTasksPage() {
  const navigate = useNavigate()
  const result = useQuery({
    queryKey: ['my-tasks'],
    queryFn: () => apiClient.get<MyTasksResult>('/workflow/my-tasks'),
  })
  const approveTask = async (myTaskId: number, approveState: 'Y' | 'N') => {
    const message = approveState === 'N' ? window.prompt('驳回意见（可选）：') : undefined
    if (approveState === 'N' && message === null) return
    try {
      const response = await apiClient.post<{ flowFinished: boolean; message?: string }>(
        `/workflow/tasks/${myTaskId}/approve`,
        { approveState, message: message?.trim() || undefined },
      )
      window.alert(response.message ?? (approveState === 'Y' ? '已同意' : '已驳回'))
      void result.refetch()
    } catch (error) {
      const body = error instanceof ApiError ? error.body : undefined
      window.alert(body?.message ?? '审批失败，请稍后重试。')
    }
  }

  const columns = useMemo<ColumnDef<MyTask, unknown>[]>(() => [
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
      cell: ({ row }) => <Button size="sm" className="erp-table-action" onClick={() => navigate(`/document-workbench/${row.original.moduleId}`)}>去处理</Button>,
    },
  ], [navigate])

  const errorMessage = result.error instanceof ApiError ? result.error.body.message : '发生未知错误，请稍后重试。'
  const totalPending = (result.data?.tasks ?? []).reduce((sum, task) => sum + task.pending, 0)
  const flowTasks = result.data?.flowTasks ?? []

  const flowColumns: ColumnDef<FlowTask, unknown>[] = [
    { accessorKey: 'title', header: '单据类型', cell: (info) => <span className="fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'step', header: '步骤', cell: (info) => <span className="font-monospace">{String(info.getValue())}</span> },
    { accessorKey: 'stepDesc', header: '审批步骤', cell: (info) => <span>{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'moduleId', header: '模块号', cell: (info) => <span className="font-monospace text-secondary">{String(info.getValue())}</span> },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true, resizable: false, truncate: false },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          <Button size="sm" className="erp-table-action" onClick={() => void approveTask(row.original.myTaskId, 'Y')}>同意</Button>
          <Button size="sm" variant="danger" className="erp-table-action" onClick={() => void approveTask(row.original.myTaskId, 'N')}>驳回</Button>
        </div>
      ),
    },
  ]

  return (
    <div className="d-grid gap-2">
      {flowTasks.length > 0 && (
        <ErpListCard
          ariaLabel="流程审批待办"
          search={null}
          actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void result.refetch()}>刷新</Button>}
          header={<div className="px-3 pt-2 small text-secondary">流程审批待办 {flowTasks.length} 项（多级审批链）</div>}
        >
          <ErpTable
            columns={flowColumns}
            data={flowTasks}
            getRowId={(row) => String(row.myTaskId)}
            empty={<div className="text-center text-secondary py-4">当前无流程审批待办</div>}
            resizable
            storageKey="my-tasks-flow"
          />
        </ErpListCard>
      )}
      <ErpListCard
        ariaLabel="我的任务"
        search={null}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void result.refetch()}>刷新</Button>}
        header={(
          <div className="px-3 pt-2 small text-secondary">
            待办总数 {totalPending} · {result.data?.note ?? ''}
          </div>
        )}
      >
        {result.isPending ? <LoadingState label="正在加载待办任务…" /> : result.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void result.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={result.data?.tasks ?? []}
            getRowId={(row) => String(row.moduleId)}
            empty={<div className="text-center text-secondary py-4">当前无待批核单据</div>}
            resizable
            storageKey="my-tasks"
          />
        )}
      </ErpListCard>
    </div>
  )
}
