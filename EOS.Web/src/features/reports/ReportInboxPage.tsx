import { IconArrowLeft, IconDownload, IconMail, IconMailOpened, IconPlus, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import type { ColumnDef } from '@tanstack/react-table'

interface Subscription {
  id: number
  userId: string
  moduleId: number
  reportId: string
  scheduleType: string
  runHour: number
  runMinute: number
  weekday: number | null
  monthDay: number | null
  enabled: boolean
  lastRunAt: string | null
  lastUpdateBy: string | null
  lastUpdateDate: string | null
}

interface InboxItem {
  id: number
  userId: string
  moduleId: number
  reportId: string
  title: string
  pdfPath: string
  generatedAt: string
  read: boolean
}

export function ReportInboxPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const subscriptions = useQuery({
    queryKey: ['report-center', 'subscriptions'],
    queryFn: () => apiClient.get<Subscription[]>('/report-center/subscriptions'),
  })
  const inbox = useQuery({
    queryKey: ['report-center', 'inbox'],
    queryFn: () => apiClient.get<InboxItem[]>('/report-center/inbox', { query: { limit: '100' } }),
  })

  const deleteSubscription = useMutation({
    mutationFn: (id: number) => apiClient.delete(`/report-center/subscriptions/${id}`),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['report-center', 'subscriptions'] }),
  })
  const markRead = useMutation({
    mutationFn: (id: number) => apiClient.post(`/report-center/inbox/${id}/read`),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['report-center', 'inbox'] }),
  })

  const [showNew, setShowNew] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [draft, setDraft] = useState({
    moduleId: 0, reportId: '', scheduleType: 'DAILY', runHour: 8, runMinute: 0, weekday: null as number | null, monthDay: null as number | null, enabled: true,
  })
  const createSubscription = useMutation({
    mutationFn: () => apiClient.post('/report-center/subscriptions', draft),
    onSuccess: () => {
      setShowNew(false)
      setError(null)
      setDraft({ moduleId: 0, reportId: '', scheduleType: 'DAILY', runHour: 8, runMinute: 0, weekday: null, monthDay: null, enabled: true })
      void queryClient.invalidateQueries({ queryKey: ['report-center', 'subscriptions'] })
    },
    onError: (reason) => setError(reason instanceof ApiError ? reason.body.message : '新增订阅失败，请重试。'),
  })

  const subscriptionColumns = useMemo(() => {
    const pending = deleteSubscription.isPending
    const run = deleteSubscription.mutate
    const columns: ColumnDef<Subscription, unknown>[] = [
      { accessorKey: 'reportId', header: '报表', cell: ({ row }) => <span className="font-monospace small">{row.original.reportId}</span> },
      { accessorKey: 'moduleId', header: '模块', cell: ({ row }) => <span className="small">{row.original.moduleId}</span> },
      { accessorKey: 'scheduleType', header: '周期', cell: ({ row }) => <span className="small">{scheduleLabel(row.original.scheduleType)}</span> },
      {
        id: 'runAt', header: '执行时刻',
        cell: ({ row }) => <span className="small">{`${String(row.original.runHour).padStart(2, '0')}:${String(row.original.runMinute).padStart(2, '0')}`}{row.original.scheduleType === 'WEEKLY' && row.original.weekday ? `（周${row.original.weekday}）` : ''}{row.original.scheduleType === 'MONTHLY' && row.original.monthDay ? `（${row.original.monthDay}日）` : ''}</span>,
      },
      { accessorKey: 'enabled', header: '启用', cell: ({ row }) => <span className={`small ${row.original.enabled ? 'text-success' : 'text-secondary'}`}>{row.original.enabled ? '是' : '否'}</span> },
      { accessorKey: 'lastRunAt', header: '上次执行', cell: ({ row }) => <span className="small text-secondary">{row.original.lastRunAt ? new Date(row.original.lastRunAt).toLocaleString('zh-CN') : '—'}</span> },
      {
        id: 'actions', header: '', meta: { truncate: false },
        cell: ({ row }) => (
          <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} title="删除订阅" aria-label={`删除订阅 ${row.original.reportId}`}
            disabled={pending} onClick={() => void run(row.original.id)}>删除</Button>
        ),
      },
    ]
    return columns
  }, [deleteSubscription])

  const inboxColumns = useMemo(() => {
    const pending = markRead.isPending
    const run = markRead.mutate
    const columns: ColumnDef<InboxItem, unknown>[] = [
      { accessorKey: 'title', header: '报表', cell: ({ row }) => <span className="fw-medium">{row.original.title}</span> },
      { accessorKey: 'generatedAt', header: '生成时间', cell: ({ row }) => <span className="small">{new Date(row.original.generatedAt).toLocaleString('zh-CN')}</span> },
      { accessorKey: 'read', header: '状态', cell: ({ row }) => <span className={`small ${row.original.read ? 'text-secondary' : 'text-primary fw-medium'}`}>{row.original.read ? '已读' : '未读'}</span> },
      {
        id: 'actions', header: '', meta: { truncate: false },
        cell: ({ row }) => (
          <div className="d-inline-flex gap-1">
            {!row.original.read && (
              <Button size="sm" variant="ghost" icon={<IconMailOpened size={14} />} title="标记已读" aria-label={`标记已读 ${row.original.title}`}
                disabled={pending} onClick={() => void run(row.original.id)}>已读</Button>
            )}
            <Button size="sm" variant="ghost" icon={<IconDownload size={14} />} title="下载 PDF" aria-label={`下载 ${row.original.title}`}
              onClick={() => { window.open(`/api/v1/report-center/inbox/${row.original.id}/download`, '_blank') }}>下载</Button>
          </div>
        ),
      },
    ]
    return columns
  }, [markRead])

  return (
    <div className="erp-full-list-page">
      <section className="card erp-list-card mb-3">
        <section className="erp-list-command-bar" aria-label="报表订阅工具栏">
          <div className="erp-list-actions d-flex gap-2 align-items-center ms-auto">
            <Button size="sm" variant="ghost" icon={<IconArrowLeft size={16} />} onClick={() => navigate('/report-center')}>返回报表中心</Button>
            <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setShowNew((v) => !v)}>新增订阅</Button>
          </div>
        </section>
        <div className="erp-report-inbox-body p-2">
          {showNew && (
            <div className="border rounded p-2 mb-2 bg-light">
              <div className="row g-2">
                <div className="col-auto">
                  <label className="form-label mb-1 small">模块号</label>
                  <input type="number" className="form-control form-control-sm" value={draft.moduleId || ''}
                    onChange={(e) => setDraft((d) => ({ ...d, moduleId: Number(e.target.value) }))} />
                </div>
                <div className="col-auto">
                  <label className="form-label mb-1 small">报表编号</label>
                  <input className="form-control form-control-sm" value={draft.reportId}
                    onChange={(e) => setDraft((d) => ({ ...d, reportId: e.target.value }))} placeholder="如 COP_SEND_M_149808" />
                </div>
                <div className="col-auto">
                  <label className="form-label mb-1 small">周期</label>
                  <select className="form-select form-select-sm" value={draft.scheduleType}
                    onChange={(e) => setDraft((d) => ({ ...d, scheduleType: e.target.value }))}>
                    <option value="DAILY">每日</option>
                    <option value="WEEKLY">每周</option>
                    <option value="MONTHLY">每月</option>
                  </select>
                </div>
                <div className="col-auto">
                  <label className="form-label mb-1 small">时刻</label>
                  <div className="d-flex gap-1">
                    <input type="number" min={0} max={23} className="form-control form-control-sm" style={{ width: 70 }} value={draft.runHour}
                      onChange={(e) => setDraft((d) => ({ ...d, runHour: Number(e.target.value) }))} />
                    <span className="align-self-center">:</span>
                    <input type="number" min={0} max={59} className="form-control form-control-sm" style={{ width: 70 }} value={draft.runMinute}
                      onChange={(e) => setDraft((d) => ({ ...d, runMinute: Number(e.target.value) }))} />
                  </div>
                </div>
                {draft.scheduleType === 'WEEKLY' && (
                  <div className="col-auto">
                    <label className="form-label mb-1 small">星期</label>
                    <select className="form-select form-select-sm" value={draft.weekday ?? 1}
                      onChange={(e) => setDraft((d) => ({ ...d, weekday: Number(e.target.value) }))}>
                      {[1, 2, 3, 4, 5, 6, 7].map((w) => <option key={w} value={w}>周{w}</option>)}
                    </select>
                  </div>
                )}
                {draft.scheduleType === 'MONTHLY' && (
                  <div className="col-auto">
                    <label className="form-label mb-1 small">日</label>
                    <input type="number" min={1} max={31} className="form-control form-control-sm" style={{ width: 70 }} value={draft.monthDay ?? 1}
                      onChange={(e) => setDraft((d) => ({ ...d, monthDay: Number(e.target.value) }))} />
                  </div>
                )}
                <div className="col-auto d-flex align-items-end gap-2">
                  <Button size="sm" variant="primary" onClick={() => void createSubscription.mutate()} loading={createSubscription.isPending}>保存</Button>
                  <Button size="sm" variant="secondary" onClick={() => setShowNew(false)}>取消</Button>
                </div>
              </div>
              {error && <div className="alert alert-danger py-1 px-2 small mt-2 mb-0">{error}</div>}
            </div>
          )}
          {subscriptions.isPending ? <LoadingState label="正在加载订阅…" /> : subscriptions.isError ? (
            <div className="alert alert-danger d-flex align-items-center justify-content-between">
              <span>{subscriptions.error instanceof ApiError ? subscriptions.error.body.message : '订阅加载失败。'}</span>
              <Button size="sm" variant="danger" onClick={() => void subscriptions.refetch()}>重试</Button>
            </div>
          ) : subscriptions.data.length === 0 ? (
            <EmptyState title="暂无订阅" description="点击右上角「新增订阅」按周期自动生成报表。" />
          ) : (
            <ErpTable columns={subscriptionColumns} data={subscriptions.data} getRowId={(r) => String(r.id)} clientSideSorting empty={<EmptyState title="暂无订阅" />} />
          )}
        </div>
      </section>

      <section className="card erp-list-card">
        <section className="erp-list-command-bar" aria-label="报表收件箱工具栏">
          <div className="d-flex align-items-center gap-2"><IconMail size={16} /><span className="fw-semibold small">收件箱</span></div>
        </section>
        <div className="erp-report-inbox-body p-2">
          {inbox.isPending ? <LoadingState label="正在加载收件箱…" /> : inbox.isError ? (
            <div className="alert alert-danger d-flex align-items-center justify-content-between">
              <span>{inbox.error instanceof ApiError ? inbox.error.body.message : '收件箱加载失败。'}</span>
              <Button size="sm" variant="danger" onClick={() => void inbox.refetch()}>重试</Button>
            </div>
          ) : inbox.data.length === 0 ? (
            <EmptyState title="收件箱为空" description="订阅的报表将在到点后自动生成到这里。" />
          ) : (
            <ErpTable columns={inboxColumns} data={inbox.data} getRowId={(r) => String(r.id)} clientSideSorting empty={<EmptyState title="收件箱为空" />} />
          )}
        </div>
      </section>
    </div>
  )
}

function scheduleLabel(type: string): string {
  switch (type) {
    case 'WEEKLY': return '每周'
    case 'MONTHLY': return '每月'
    default: return '每日'
  }
}
