import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { workbenchList, workbenchView } from '../document-workbench/workbenchPath'
import { ApiError } from '../../types/api'
import { describeApiError } from '../../lib/errors'

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
  keyValues: string[]
  title: string
  approvePower: boolean
  forwardPower: boolean
  isSign: boolean
  passPercent: number
  steps: { step: string; stepDesc: string }[]
}

interface MyStartedFlow {
  wfId: number
  moduleId: number
  title: string
  keyValue: string
  keyValues: string[]
  keyValueDesc: string
  startDate: string | null
  step: string
  stepDesc: string
}

interface MyTasksResult {
  tasks: MyTask[]
  flowTasks: FlowTask[]
  engineEnabled: boolean
  note: string
}

type ApproveState = 'Y' | 'N'

/** 我的任务（2102）：真实流程待办（同意/驳回）+ 直接批核模型单据计数 */
export function MyTasksPage() {
  const navigate = useNavigate()
  const result = useQuery({
    queryKey: ['my-tasks'],
    queryFn: () => apiClient.get<MyTasksResult>('/workflow/my-tasks'),
  })
  const started = useQuery({
    queryKey: ['my-started'],
    queryFn: () => apiClient.get<{ rows: MyStartedFlow[] }>('/workflow/my-started'),
  })
  const [pending, setPending] = useState<{ task: FlowTask; state: ApproveState } | null>(null)
  const [jumpNo, setJumpNo] = useState('')
  const [opinion, setOpinion] = useState('')

  const approveTask = async (myTaskId: number, approveState: ApproveState, message?: string, jump?: string) => {
    try {
      const response = await apiClient.post<{ flowFinished: boolean; message?: string }>(
        `/workflow/tasks/${myTaskId}/approve`,
        { approveState, message, jumpNo: jump },
      )
      window.alert(response.message ?? (approveState === 'Y' ? '已同意' : '已驳回'))
      void result.refetch()
    } catch (error) {
      const body = error instanceof ApiError ? error.body : undefined
      window.alert(body?.message ?? '审批失败，请稍后重试。')
    }
  }

  const openApprove = (task: FlowTask, state: ApproveState) => {
    // 同意且无跳转权：直接提交；否则打开弹窗选择跳转/退回目标与意见
    if (state === 'Y' && !task.forwardPower) {
      void approveTask(task.myTaskId, state)
      return
    }
    setPending({ task, state })
    setJumpNo('')
    setOpinion('')
  }

  const withdrawFlow = async (flow: MyStartedFlow) => {
    if (!window.confirm(`确认撤回「${flow.title}」（${flow.keyValueDesc || flow.keyValue}）？撤回后单据可修改并重新提交。`)) {
      return
    }
    try {
      const response = await apiClient.post<{ message?: string }>('/workflow/withdraw', {
        moduleId: flow.moduleId,
        key: flow.keyValues.length > 0 ? flow.keyValues : null,
      })
      window.alert(response.message ?? '流程已撤回')
      void result.refetch()
      void started.refetch()
    } catch (error) {
      const body = error instanceof ApiError ? error.body : undefined
      window.alert(body?.message ?? '撤回失败，请稍后重试。')
    }
  }

  const confirmApprove = async () => {
    if (!pending) return
    const task = pending.task
    const message = opinion.trim() || undefined
    const jump = jumpNo || undefined
    setPending(null)
    await approveTask(task.myTaskId, pending.state, message, jump)
  }

  const forwardTargets = pending?.task.steps.filter((step) => step.step > pending.task.step) ?? []
  const backTargets = pending?.task.steps.filter((step) => step.step <= pending.task.step) ?? []

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
      cell: ({ row }) => <Button size="sm" className="erp-table-action" onClick={() => navigate(workbenchList(row.original.moduleId))}>去处理</Button>,
    },
  ], [navigate])

  const errorMessage = describeApiError(result.error, '发生未知错误，请稍后重试。')
  const totalPending = (result.data?.tasks ?? []).reduce((sum, task) => sum + task.pending, 0)
  const flowTasks = result.data?.flowTasks ?? []

  const flowColumns: ColumnDef<FlowTask, unknown>[] = [
    { accessorKey: 'title', header: '单据类型', cell: (info) => <span className="fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'step', header: '步骤', cell: (info) => <span className="font-monospace">{String(info.getValue())}</span> },
    {
      accessorKey: 'stepDesc',
      header: '审批步骤',
      cell: (info) => (
        <span>
          {String(info.getValue() ?? '—')}
          {info.row.original.isSign && <span className="badge text-bg-info ms-1">会签</span>}
          {info.row.original.forwardPower && <span className="badge text-bg-secondary ms-1">可跳转</span>}
        </span>
      ),
    },
    { accessorKey: 'moduleId', header: '模块号', cell: (info) => <span className="font-monospace text-secondary">{String(info.getValue())}</span> },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true, resizable: false, truncate: false },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          <Button
            size="sm"
            className="erp-table-action"
            title="查看单据内容"
            onClick={() => navigate(workbenchView(row.original.moduleId, row.original.keyValues.length > 0 ? row.original.keyValues : null))}
          >
            查看
          </Button>
          <Button size="sm" className="erp-table-action" onClick={() => openApprove(row.original, 'Y')}>同意</Button>
          <Button size="sm" variant="danger" className="erp-table-action" onClick={() => openApprove(row.original, 'N')}>驳回</Button>
        </div>
      ),
    },
  ]

  return (
    <div className="d-grid gap-2">
      {pending && (
        <Modal
          title={pending.state === 'Y' ? '同意并处理' : '驳回退回'}
          onClose={() => setPending(null)}
          dialogClassName="erp-dialog-sm"
          ariaLabel="流程审批"
          footer={<>
            <Button variant="secondary" onClick={() => setPending(null)}>取消</Button>
            <Button variant={pending.state === 'Y' ? 'primary' : 'danger'} onClick={() => void confirmApprove()}>
              {pending.state === 'Y' ? '确认同意' : '确认驳回'}
            </Button>
          </>}
        >
                <div className="mb-2 text-secondary small">
                  {pending.task.title} · 步骤 {pending.task.step} {pending.task.stepDesc}
                </div>
                {pending.state === 'N' && (
                  <div className="mb-2">
                    <label className="form-label">退回至</label>
                    <select className="form-select" value={jumpNo} onChange={(event) => setJumpNo(event.target.value)}>
                      <option value="">退回第一步（默认）</option>
                      {backTargets.map((target) => (
                        <option key={target.step} value={target.step}>
                          退回至 {target.step} {target.stepDesc}
                        </option>
                      ))}
                    </select>
                  </div>
                )}
                {pending.state === 'Y' && pending.task.forwardPower && (
                  <div className="mb-2">
                    <label className="form-label">跳转目标（跳过中间步骤）</label>
                    <select className="form-select" value={jumpNo} onChange={(event) => setJumpNo(event.target.value)}>
                      <option value="">不跳转（按顺序进入下一步）</option>
                      <option value="0">直接结束流程</option>
                      {forwardTargets.map((target) => (
                        <option key={target.step} value={target.step}>
                          跳至 {target.step} {target.stepDesc}
                        </option>
                      ))}
                    </select>
                  </div>
                )}
                <div>
                  <label className="form-label">{pending.state === 'N' ? '驳回意见' : '审批意见'}（可选）</label>
                  <input
                    className="form-control"
                    value={opinion}
                    onChange={(event) => setOpinion(event.target.value)}
                    placeholder={pending.state === 'N' ? '填写驳回原因…' : '填写审批意见…'}
                  />
                </div>
        </Modal>
      )}
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
      {(started.data?.rows ?? []).length > 0 && (
        <ErpListCard
          ariaLabel="我发起的"
          search={null}
          actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void started.refetch()}>刷新</Button>}
          header={<div className="px-3 pt-2 small text-secondary">我发起的在途流程 {started.data?.rows.length ?? 0} 项（可撤回后修改重新提交）</div>}
        >
          <ErpTable
            columns={[
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
                  <Button size="sm" variant="danger" className="erp-table-action" onClick={() => void withdrawFlow(row.original as MyStartedFlow)}>
                    撤回
                  </Button>
                ),
              },
            ]}
            data={started.data?.rows ?? []}
            getRowId={(row) => String(row.wfId)}
            empty={<div className="text-center text-secondary py-4">当前无我发起的在途流程</div>}
            resizable
            storageKey="my-started"
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
