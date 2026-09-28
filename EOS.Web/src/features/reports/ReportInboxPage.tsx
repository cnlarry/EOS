import { IconArrowLeft, IconDownload, IconMailOpened, IconPlus, IconTrash, IconX } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { TabbedPanel } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import type { ColumnDef } from '@tanstack/react-table'
import { describeApiError } from '../../lib/errors'

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

type InboxTab = 'inbox' | 'subscriptions'

/**
 * 报表订阅与收件箱。
 *
 * 两者是同一件事的两端（订阅负责产出、收件箱负责接收），因此并为一个页面的两个页签，
 * 而不是上下堆叠两张表——堆叠时两块区域等权重、长度互相挤压，读起来分不清主次。
 */
export function ReportInboxPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [tab, setTab] = useState<InboxTab>('inbox')

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
  const [formError, setFormError] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const [draft, setDraft] = useState({
    moduleId: 0, reportId: '', scheduleType: 'DAILY', runHour: 8, runMinute: 0, weekday: null as number | null, monthDay: null as number | null, enabled: true,
  })
  const createSubscription = useMutation({
    mutationFn: () => apiClient.post('/report-center/subscriptions', draft),
    onSuccess: () => {
      setShowNew(false)
      setFormError(null)
      setDraft({ moduleId: 0, reportId: '', scheduleType: 'DAILY', runHour: 8, runMinute: 0, weekday: null, monthDay: null, enabled: true })
      void queryClient.invalidateQueries({ queryKey: ['report-center', 'subscriptions'] })
    },
    onError: (reason) => setFormError(describeApiError(reason, '新增订阅失败，请重试。')),
  })

  /** 下载经统一传输层取 blob：失败时能拿到服务端错误信息，而不是浏览器原生错误页。 */
  const downloadInbox = async (item: InboxItem) => {
    setPageError(null)
    try {
      const blob = await apiClient.getFile(`/report-center/inbox/${item.id}/download`)
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${item.title}.pdf`
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      URL.revokeObjectURL(url)
    } catch (error) {
      setPageError(describeApiError(error, '下载失败，请重试。'))
    }
  }

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
              onClick={() => void downloadInbox(row.original)}>下载</Button>
          </div>
        ),
      },
    ]
    return columns
  }, [markRead])

  const unreadCount = (inbox.data ?? []).filter((item) => !item.read).length

  return (
    <div className="erp-report-inbox-page">
      <div className="erp-inbox-toolbar">
        <Button variant="ghost" size="sm" icon={<IconArrowLeft size={16} />} onClick={() => navigate('/report-center')}>
          报表中心
        </Button>
        <span className="text-secondary small">
          订阅按周期自动生成报表，产出进收件箱
        </span>
        <div className="ms-auto d-flex align-items-center gap-2">
          {tab === 'subscriptions' && (
            <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setShowNew((value) => !value)}>
              新增订阅
            </Button>
          )}
        </div>
      </div>

      {pageError && (
        <div className="erp-report-alert" role="alert">
          <span>{pageError}</span>
          <button type="button" aria-label="关闭提示" title="关闭提示" onClick={() => setPageError(null)}>
            <IconX size={14} />
          </button>
        </div>
      )}

      <TabbedPanel
        label="报表订阅与收件箱"
        activeKey={tab}
        onActiveKeyChange={setTab}
        tabs={[
          { key: 'inbox', label: `收件箱 (${inbox.data?.length ?? 0})` },
          { key: 'subscriptions', label: `订阅 (${subscriptions.data?.length ?? 0})` },
        ]}
      >
        {tab === 'inbox' ? (
          <>
            <div className="erp-inbox-toolbar">
              <span className="text-secondary small">
                共 {inbox.data?.length ?? 0} 份，未读 {unreadCount} 份
              </span>
            </div>
            {inbox.isPending ? (
              <LoadingState label="正在加载收件箱…" />
            ) : inbox.isError ? (
              <ErrorState message={describeApiError(inbox.error, '收件箱加载失败。')} onRetry={() => void inbox.refetch()} />
            ) : (
              <ErpTable
                columns={inboxColumns}
                data={inbox.data ?? []}
                getRowId={(row) => String(row.id)}
                clientSideSorting
                empty={<EmptyState title="收件箱为空" description="订阅的报表将在到点后自动生成到这里。" />}
              />
            )}
          </>
        ) : (
          <>
            <div className="erp-inbox-toolbar">
              <span className="text-secondary small">
                共 {subscriptions.data?.length ?? 0} 条订阅
              </span>
            </div>
            {showNew && (
              <div className="border rounded p-2 mb-2 bg-light">
                <div className="row g-2">
                  <div className="col-auto">
                    <label className="form-label mb-1 small" htmlFor="report-subscription-module">模块号</label>
                    <input id="report-subscription-module" type="number" className="form-control form-control-sm erp-narrow-input" value={draft.moduleId || ''}
                      onChange={(event) => setDraft((current) => ({ ...current, moduleId: Number(event.target.value) }))} />
                  </div>
                  <div className="col-auto">
                    <label className="form-label mb-1 small" htmlFor="report-subscription-report">报表编号</label>
                    <input id="report-subscription-report" className="form-control form-control-sm" value={draft.reportId}
                      onChange={(event) => setDraft((current) => ({ ...current, reportId: event.target.value }))} placeholder="如 COP_SEND_M_149808" />
                  </div>
                  <div className="col-auto">
                    <label className="form-label mb-1 small" htmlFor="report-subscription-cycle">周期</label>
                    <select id="report-subscription-cycle" className="form-select form-select-sm" value={draft.scheduleType}
                      onChange={(event) => setDraft((current) => ({ ...current, scheduleType: event.target.value }))}>
                      <option value="DAILY">每日</option>
                      <option value="WEEKLY">每周</option>
                      <option value="MONTHLY">每月</option>
                    </select>
                  </div>
                  <div className="col-auto">
                    <label className="form-label mb-1 small" htmlFor="report-subscription-hour">时刻</label>
                    <div className="d-flex gap-1">
                      <input id="report-subscription-hour" type="number" min={0} max={23} className="form-control form-control-sm erp-narrow-input" value={draft.runHour}
                        onChange={(event) => setDraft((current) => ({ ...current, runHour: Number(event.target.value) }))} />
                      <span className="align-self-center">:</span>
                      <input type="number" min={0} max={59} className="form-control form-control-sm erp-narrow-input" aria-label="分" value={draft.runMinute}
                        onChange={(event) => setDraft((current) => ({ ...current, runMinute: Number(event.target.value) }))} />
                    </div>
                  </div>
                  {draft.scheduleType === 'WEEKLY' && (
                    <div className="col-auto">
                      <label className="form-label mb-1 small" htmlFor="report-subscription-weekday">星期</label>
                      <select id="report-subscription-weekday" className="form-select form-select-sm" value={draft.weekday ?? 1}
                        onChange={(event) => setDraft((current) => ({ ...current, weekday: Number(event.target.value) }))}>
                        {[1, 2, 3, 4, 5, 6, 7].map((weekday) => <option key={weekday} value={weekday}>周{weekday}</option>)}
                      </select>
                    </div>
                  )}
                  {draft.scheduleType === 'MONTHLY' && (
                    <div className="col-auto">
                      <label className="form-label mb-1 small" htmlFor="report-subscription-monthday">日</label>
                      <input id="report-subscription-monthday" type="number" min={1} max={31} className="form-control form-control-sm erp-narrow-input" value={draft.monthDay ?? 1}
                        onChange={(event) => setDraft((current) => ({ ...current, monthDay: Number(event.target.value) }))} />
                    </div>
                  )}
                  <div className="col-auto d-flex align-items-end gap-2">
                    <Button size="sm" variant="primary" onClick={() => void createSubscription.mutate()} loading={createSubscription.isPending}>保存</Button>
                    <Button size="sm" variant="secondary" onClick={() => setShowNew(false)}>取消</Button>
                  </div>
                </div>
                {formError && <div className="alert alert-danger py-1 px-2 small mt-2 mb-0">{formError}</div>}
              </div>
            )}
            {subscriptions.isPending ? (
              <LoadingState label="正在加载订阅…" />
            ) : subscriptions.isError ? (
              <ErrorState message={describeApiError(subscriptions.error, '订阅加载失败。')} onRetry={() => void subscriptions.refetch()} />
            ) : (
              <ErpTable
                columns={subscriptionColumns}
                data={subscriptions.data ?? []}
                getRowId={(row) => String(row.id)}
                clientSideSorting
                empty={<EmptyState title="暂无订阅" description="点击「新增订阅」按周期自动生成报表。" />}
              />
            )}
          </>
        )}
      </TabbedPanel>
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
