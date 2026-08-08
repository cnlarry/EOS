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

interface MyTasksResult {
  tasks: MyTask[]
  engineEnabled: boolean
  note: string
}

/** 我的任务（2102 最小可用版）：各业务模块待批核单据清单 + 工作台直达入口 */
export function MyTasksPage() {
  const navigate = useNavigate()
  const result = useQuery({ queryKey: ['my-tasks'], queryFn: () => apiClient.get<MyTasksResult>('/workflow/my-tasks') })

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
      meta: { className: 'text-end', frozenRight: true, resizable: false },
      cell: ({ row }) => <Button size="sm" className="erp-table-action" onClick={() => navigate(`/document-workbench/${row.original.moduleId}`)}>去处理</Button>,
    },
  ], [navigate])

  const errorMessage = result.error instanceof ApiError ? result.error.body.message : '发生未知错误，请稍后重试。'
  const totalPending = (result.data?.tasks ?? []).reduce((sum, task) => sum + task.pending, 0)

  return (
    <div className="d-grid gap-2">
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
