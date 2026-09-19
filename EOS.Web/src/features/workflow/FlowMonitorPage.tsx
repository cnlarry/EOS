import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { IconEye, IconRefresh } from '@tabler/icons-react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { workbenchView } from '../document-workbench/workbenchPath'
import { describeApiError } from '../../lib/errors'
import { WorkflowTimeline, type WorkflowTimelineRow } from './WorkflowTimeline'

interface MonitorRow {
  wfId: number
  moduleId: number
  title: string
  keyValueDesc: string
  keyValue: string
  keyValues: string[]
  startUser: string
  startDate: string | null
  ageDays: number | null
  step: string
  stepDesc: string
  state: string
  stateLabel: string
  overdue: boolean
}

interface MonitorTask {
  myTaskId: number
  step: string
  stepDesc: string
  approver: string
  state: string
  message: string
  approveDate: string | null
  approvePower: boolean
  isCurrent: boolean
  isSign: boolean
  isMustSign: boolean
  isAutoExec: boolean
  passPercent: number
}

interface MonitorLog {
  step: string
  stepDesc: string
  approver: string
  state: string
  message: string
  date: string | null
}

interface MonitorDetail {
  monitor: MonitorRow
  tasks: MonitorTask[]
  logs: MonitorLog[]
}

const TASK_STATE_LABEL: Record<string, string> = {
  '': '待批',
  Y: '同意',
  N: '驳回',
  S: '跳过',
  W: '撤回',
  A: '送审',
}

function stateBadge(state: string, overdue = false): { label: string; cls: string } {
  switch (state) {
    case '0': return { label: '在途', cls: overdue ? 'text-bg-danger' : 'text-bg-warning' }
    case '1': return { label: '已完成', cls: 'text-bg-success' }
    case '2': return { label: '已撤回', cls: 'text-bg-secondary' }
    default: return { label: state, cls: 'text-bg-secondary' }
  }
}

/** 流程监控（2103）：在途/完成/撤回流程实例全局视图 + 超时标记 + 明细下钻。 */
export function FlowMonitorPage() {
  const navigate = useNavigate()
  const [status, setStatus] = useState('')
  const [keyword, setKeyword] = useState('')
  const [selectedWfId, setSelectedWfId] = useState<number | null>(null)

  const list = useQuery({
    queryKey: ['flow-monitor', status, keyword.trim()],
    queryFn: () => apiClient.get<{ rows: MonitorRow[]; overdueDays: number }>(
      `/workflow/monitor?status=${status}&keyword=${encodeURIComponent(keyword.trim())}`,
    ),
  })

  const detail = useQuery({
    queryKey: ['flow-monitor-detail', selectedWfId],
    queryFn: () => apiClient.get<MonitorDetail>(`/workflow/monitor/${selectedWfId}`),
    enabled: selectedWfId != null,
  })

  const rows = list.data?.rows ?? []
  const overdueDays = list.data?.overdueDays ?? 3

  const columns = useMemo<ColumnDef<MonitorRow, unknown>[]>(() => [
    {
      accessorKey: 'title',
      header: '单据类型',
      cell: (info) => <span className="fw-semibold">{String(info.getValue() ?? '—')}</span>,
    },
    {
      accessorKey: 'keyValueDesc',
      header: '单据',
      cell: (info) => {
        const desc = String(info.getValue() ?? '')
        const row = info.row.original
        return <span className="text-secondary" title={desc || row.keyValue}>{desc || row.keyValue || '—'}</span>
      },
    },
    { accessorKey: 'startUser', header: '发起人', cell: (info) => <span className="font-monospace">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'startDate',
      header: '发起时间',
      cell: (info) => {
        const raw = info.getValue()
        return raw ? new Date(String(raw)).toLocaleString() : '—'
      },
    },
    {
      id: 'step',
      header: '当前步骤',
      cell: (info) => {
        const step = String(info.row.original.step ?? '')
        const desc = String(info.row.original.stepDesc ?? '')
        return <span className="font-monospace">{step ? `${step} ${desc}` : '—'}</span>
      },
    },
    {
      id: 'state',
      header: '状态',
      cell: (info) => {
        const { label, cls } = stateBadge(info.row.original.state, info.row.original.overdue)
        return <span className={`badge ${cls}`}>{label}</span>
      },
    },
    {
      id: 'age',
      header: '在途天数',
      cell: (info) => {
        const row = info.row.original
        if (row.state !== '0' || row.ageDays == null) return <span className="text-secondary">—</span>
        const days = Math.floor(row.ageDays)
        return row.overdue
          ? <span className="text-danger fw-semibold">{days} 天（超时）</span>
          : <span>{days} 天</span>
      },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end', frozenRight: true, resizable: false, truncate: false },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          <Button size="sm" className="erp-table-action" title="查看流程明细" onClick={() => setSelectedWfId(row.original.wfId)}>
            <IconEye size={14} className="me-1" />明细
          </Button>
          {row.original.keyValues.length > 0 && (
            <Button size="sm" className="erp-table-action" title="查看单据内容" onClick={() => navigate(workbenchView(row.original.moduleId, row.original.keyValues))}>
              单据
            </Button>
          )}
        </div>
      ),
    },
  ], [navigate])

  const errorMessage = describeApiError(list.error, '加载失败，请稍后重试。')
  const inFlightCount = rows.filter((row) => row.state === '0').length
  const overdueCount = rows.filter((row) => row.overdue).length
  const selected = detail.data?.monitor

  return (
    <div className="d-flex flex-column erp-full-list-page" style={{ height: 'calc(100dvh - 96px)' }}>
      <ErpListCard
        ariaLabel="流程监控"
        search={null}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void list.refetch()}>刷新</Button>}
        header={(
          <div className="d-flex flex-wrap gap-2 align-items-center px-3 pt-2 pb-2">
            <select className="form-select form-select-sm" style={{ width: 150 }} value={status} onChange={(event) => setStatus(event.target.value)} aria-label="状态筛选">
              <option value="">全部状态</option>
              <option value="0">在途</option>
              <option value="1">已完成</option>
              <option value="2">已撤回</option>
            </select>
            <input
              className="form-control form-control-sm"
              style={{ width: 260 }}
              value={keyword}
              onChange={(event) => setKeyword(event.target.value)}
              placeholder="搜索单据/发起人/模块标题…"
              aria-label="搜索"
            />
            <span className="small text-secondary">
              共 {rows.length} 个实例 · 在途 {inFlightCount} · 超时 {overdueCount}（阈值 {overdueDays} 天）
            </span>
          </div>
        )}
      >
        {list.isPending ? <LoadingState label="正在加载流程实例…" /> : list.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void list.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={rows}
            getRowId={(row) => String(row.wfId)}
            resizable
            storageKey="flow-monitor"
            empty={<div className="text-center text-secondary py-4">当前无流程实例</div>}
          />
        )}
      </ErpListCard>

      {selectedWfId != null && (
        <Modal
          title="流程明细"
          onClose={() => setSelectedWfId(null)}
          size="xl"
          ariaLabel="流程明细"
          footer={<Button variant="secondary" onClick={() => setSelectedWfId(null)}>关闭</Button>}
        >
                {detail.isPending ? <LoadingState label="正在加载流程明细…" /> : selected ? (
                  <div className="d-grid gap-3">
                    <div className="d-flex flex-wrap gap-2 align-items-center">
                      <span className="fw-semibold">{selected.title}</span>
                      <span className="font-monospace text-secondary">{selected.keyValueDesc || selected.keyValue}</span>
                      {(() => {
                        const { label, cls } = stateBadge(selected.state, selected.overdue)
                        return <span className={`badge ${cls}`}>{label}</span>
                      })()}
                      {selected.overdue && <span className="badge text-bg-danger">超时</span>}
                    </div>
                    <div className="row g-2 small text-secondary">
                      <div className="col-auto">发起人：<span className="font-monospace">{selected.startUser}</span></div>
                      <div className="col-auto">发起时间：{selected.startDate ? new Date(String(selected.startDate)).toLocaleString() : '—'}</div>
                      {selected.ageDays != null && <div className="col-auto">在途 {Math.floor(selected.ageDays)} 天</div>}
                      {selected.keyValues.length > 0 && (
                        <div className="col-auto">
                          <Button size="sm" className="erp-table-action" onClick={() => navigate(workbenchView(selected.moduleId, selected.keyValues))}>查看单据</Button>
                        </div>
                      )}
                    </div>
                    <div>
                      <h3 className="small fw-semibold mb-1">步骤任务</h3>
                      <ErpTable
                        columns={[
                          { accessorKey: 'step', header: '步骤', cell: (info) => <span className="font-monospace">{String(info.getValue() ?? '')}</span> },
                          { accessorKey: 'stepDesc', header: '步骤名称' },
                          { accessorKey: 'approver', header: '审批人', cell: (info) => <span className="font-monospace">{String(info.getValue() ?? '—')}</span> },
                          {
                            id: 'state',
                            header: '状态',
                            cell: (info) => {
                              const state = String(info.row.original.state ?? '')
                              const label = TASK_STATE_LABEL[state] ?? state
                              const cls = state === 'Y' ? 'text-bg-success' : state === 'N' || state === 'W' ? 'text-bg-danger' : state === 'S' ? 'text-bg-secondary' : state === 'A' ? 'text-bg-info' : 'text-bg-warning'
                              return <span className={`badge ${cls}`}>{label}</span>
                            },
                          },
                          {
                            id: 'flags',
                            header: '属性',
                            cell: (info) => {
                              const task = info.row.original as MonitorTask
                              const flags: string[] = []
                              if (task.isCurrent) flags.push('当前')
                              if (task.isSign) flags.push('会签')
                              if (task.isMustSign) flags.push('必签')
                              if (task.isAutoExec) flags.push('自动')
                              return flags.length ? <span className="text-secondary">{flags.join(' · ')}</span> : <span>—</span>
                            },
                          },
                          {
                            id: 'msg',
                            header: '意见',
                            cell: (info) => {
                              const msg = String((info.row.original as MonitorTask).message ?? '')
                              return msg ? <span title={msg}>{msg}</span> : <span className="text-secondary">—</span>
                            },
                          },
                        ]}
                        data={detail.data?.tasks ?? []}
                        getRowId={(task) => String(task.myTaskId)}
                        empty={<div className="text-center text-secondary py-3">无任务</div>}
                      />
                    </div>
                    <div>
                      <h3 className="small fw-semibold mb-1">审批日志</h3>
                      <div className="border rounded p-2" style={{ maxHeight: 260, overflowY: 'auto' }}>
                        {(detail.data?.logs ?? []).length === 0 ? (
                          <div className="text-center text-secondary py-3">暂无日志</div>
                        ) : (
                          <WorkflowTimeline rows={(detail.data?.logs ?? []) as WorkflowTimelineRow[]} emptyText="暂无日志" />
                        )}
                      </div>
                    </div>
                  </div>
                ) : (
                  <ErrorState message="流程明细加载失败" onRetry={() => void detail.refetch()} />
                )}
        </Modal>
      )}
    </div>
  )
}
