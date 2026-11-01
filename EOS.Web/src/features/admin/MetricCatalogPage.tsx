import { IconPencil, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { Button } from '../../components/ui/Button'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

/**
 * 指标口径（模块 2317，定制承载页 `/admin/metrics`）：`REPORT_METRIC` 的维护面。
 *
 * <para>
 * 同一条口径被报表与助手共同消费，所以它的权限是**独立能力**（2317），不挂在报表设置（2201）
 * 或元数据管理（2302）上——否则"谁能改口径"会取决于"谁能改报表/改元数据"。
 * </para>
 *
 * <para>
 * **校验全在服务端**（受控表达式解析、来源表与维度的物理列、行过滤的表归属），界面只做必填提示：
 * 前端自行判断"这条表达式能不能用"会与服务端长出第二套标准。**删除只对候选口径开放**——
 * 已确认的可能被报表与结论引用，要先显式改回候选，这一步本身就是一次决策。
 * </para>
 */
type MetricRow = {
  metricId: string
  metricName: string
  definition: string
  sourceTable: string
  dimensionKeys: string
  domain: string
  version: number
  confirmStatus: string
  rowFilter: string | null
  description: string | null
  createdBy: string
  modifiedTime: string | null
  approvedBy: string | null
  approvedTime: string | null
}

type EditorState = { mode: 'new' } | { mode: 'edit'; row: MetricRow }

const EMPTY_FORM = {
  metricId: '', metricName: '', definition: '', sourceTable: 'COP_ORDER_D',
  dimensionKeys: '', domain: '', rowFilter: '', description: '', confirmStatus: 'CANDIDATE',
}

export function MetricCatalogPage() {
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [form, setForm] = useState({ ...EMPTY_FORM })
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const metrics = useQuery({
    queryKey: ['metric-catalog'],
    queryFn: () => apiClient.get<MetricRow[]>('/report-admin/metrics'),
    placeholderData: keepPreviousData,
  })

  const rows = useMemo(() => {
    const all = metrics.data ?? []
    const text = keyword.trim().toLowerCase()
    if (!text) return all
    return all.filter(row =>
      row.metricId.toLowerCase().includes(text)
      || row.metricName.toLowerCase().includes(text)
      || row.domain.toLowerCase().includes(text)
      || row.sourceTable.toLowerCase().includes(text))
  }, [metrics.data, keyword])

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ['metric-catalog'] })

  const save = useMutation({
    mutationFn: async () => {
      const payload = {
        metricId: form.metricId.trim(),
        metricName: form.metricName.trim(),
        definition: form.definition.trim(),
        sourceTable: form.sourceTable.trim(),
        dimensionKeys: form.dimensionKeys.trim(),
        domain: form.domain.trim(),
        rowFilter: form.rowFilter.trim() ? form.rowFilter.trim() : null,
        description: form.description.trim() ? form.description.trim() : null,
        confirmStatus: form.confirmStatus,
      }
      if (editor?.mode === 'new') {
        await apiClient.post('/report-admin/metrics', payload)
      } else {
        await apiClient.put(`/report-admin/metrics/${encodeURIComponent(payload.metricId)}`, payload)
      }
    },
    onSuccess: async () => {
      await invalidate()
      setEditor(null)
      setError(null)
      setNotice('已保存。改完立即生效（口径由 resolve_metric 与报表现读）。')
    },
    onError: (exception) => setError(describeApiError(exception, '保存失败。')),
  })

  const setStatus = useMutation({
    mutationFn: async (target: { row: MetricRow; status: 'CANDIDATE' | 'CONFIRMED' }) => {
      await apiClient.put(`/report-admin/metrics/${encodeURIComponent(target.row.metricId)}`, {
        metricId: target.row.metricId,
        metricName: target.row.metricName,
        definition: target.row.definition,
        sourceTable: target.row.sourceTable,
        dimensionKeys: target.row.dimensionKeys,
        domain: target.row.domain,
        rowFilter: target.row.rowFilter,
        description: target.row.description,
        confirmStatus: target.status,
      })
    },
    onSuccess: async (_result, target) => {
      await invalidate()
      setNotice(target.status === 'CONFIRMED'
        ? `已确认口径「${target.row.metricId}」，助手与报表现在可用于取数。`
        : `已把「${target.row.metricId}」改回候选：它暂时不可用于取数。`)
    },
    onError: (exception) => setNotice(describeApiError(exception, '状态变更失败。')),
  })

  const remove = useMutation({
    mutationFn: async (row: MetricRow) => {
      if (!window.confirm(`确定删除候选口径「${row.metricId}」吗？已确认口径不允许删除。`)) return
      await apiClient.delete(`/report-admin/metrics/${encodeURIComponent(row.metricId)}`)
      await invalidate()
      setNotice(`已删除「${row.metricId}」。`)
    },
    onError: (exception) => setNotice(describeApiError(exception, '删除失败。')),
  })

  const openNew = useCallback(() => {
    setError(null)
    setForm({ ...EMPTY_FORM })
    setEditor({ mode: 'new' })
  }, [])

  const openEdit = useCallback((row: MetricRow) => {
    setError(null)
    setForm({
      metricId: row.metricId,
      metricName: row.metricName,
      definition: row.definition,
      sourceTable: row.sourceTable,
      dimensionKeys: row.dimensionKeys,
      domain: row.domain,
      rowFilter: row.rowFilter ?? '',
      description: row.description ?? '',
      confirmStatus: row.confirmStatus === 'CONFIRMED' ? 'CONFIRMED' : 'CANDIDATE',
    })
    setEditor({ mode: 'edit', row })
  }, [])

  const columns = useMemo<ColumnDef<MetricRow, unknown>[]>(() => [
    { accessorKey: 'metricId', header: '标识', size: 170, cell: ({ row }) => (
      <span className="font-monospace">{row.original.metricId}</span>
    ) },
    { accessorKey: 'metricName', header: '名称', size: 140 },
    { accessorKey: 'domain', header: '业务域', size: 90 },
    { accessorKey: 'sourceTable', header: '来源表', size: 150, cell: ({ row }) => (
      <span className="font-monospace">{row.original.sourceTable}</span>
    ) },
    { accessorKey: 'dimensionKeys', header: '维度', size: 180, cell: ({ row }) => (
      <span className="text-secondary">{row.original.dimensionKeys || '（无）'}</span>
    ) },
    {
      id: 'status',
      header: '确认状态',
      size: 110,
      enableSorting: false,
      cell: ({ row }) => row.original.confirmStatus === 'CONFIRMED'
        ? <span className="badge text-bg-success">已确认</span>
        : <span className="badge text-bg-secondary">候选</span>,
    },
    { accessorKey: 'version', header: '版本', size: 70, cell: ({ row }) => `v${row.original.version}` },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      size: 240,
      cell: ({ row }) => (
        <div className="d-flex gap-1">
          <Button size="sm" variant="ghost" icon={<IconPencil size={14} />}
            aria-label={`编辑 ${row.original.metricId}`}
            onClick={() => openEdit(row.original)}>编辑</Button>
          {row.original.confirmStatus === 'CONFIRMED'
            ? <Button size="sm" variant="ghost" disabled={setStatus.isPending}
                aria-label={`改回候选 ${row.original.metricId}`}
                onClick={() => setStatus.mutate({ row: row.original, status: 'CANDIDATE' })}>改回候选</Button>
            : <Button size="sm" variant="ghost" disabled={setStatus.isPending}
                aria-label={`确认 ${row.original.metricId}`}
                onClick={() => setStatus.mutate({ row: row.original, status: 'CONFIRMED' })}>确认</Button>}
          <Button size="sm" variant="ghost" icon={<IconTrash size={14} />}
            aria-label={`删除 ${row.original.metricId}`}
            disabled={row.original.confirmStatus === 'CONFIRMED' || remove.isPending}
            title={row.original.confirmStatus === 'CONFIRMED' ? '已确认口径需先改回候选' : '删除候选口径'}
            onClick={() => remove.mutate(row.original)}>删除</Button>
        </div>
      ),
    },
  ], [openEdit, remove, setStatus])

  return (
    <div className="erp-base-page erp-page-fill erp-full-list-page">
      <ErpListCard
        ariaLabel="指标口径维护"
        search={<ErpSearchBox value={keyword} onChange={setKeyword} debounceMs={300}
          placeholder="搜索标识 / 名称 / 业务域 / 来源表" ariaLabel="搜索指标口径" />}
        actions={<>
          <Button size="sm" variant="primary" icon={<IconPlus size={16} />} onClick={openNew}
            aria-label="新增口径" />
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void metrics.refetch()}
            aria-label="刷新" />
        </>}
        header={<div className="card-header erp-card-toolbar">
          <h2 className="card-title mb-0">指标口径</h2>
          <span className="text-secondary small">共 {rows.length} 条</span>
          <span className="ms-auto text-secondary small">校验在服务端；删除只对候选开放</span>
        </div>}
      >
        {notice && <div className="alert alert-info mb-0 rounded-0 py-2" role="status">{notice}</div>}

        <ErpListGrid
          plain
          listId="metric-catalog"
          columns={columns}
          rows={rows}
          rowKey={(row) => row.metricId}
          loading={metrics.isPending}
          loadingLabel="正在加载指标口径…"
          fetching={metrics.isFetching}
          error={metrics.isError ? describeApiError(metrics.error, '加载口径失败。') : null}
          onRetry={() => void metrics.refetch()}
          empty={<EmptyState title="还没有口径" description="口径登记后，助手与报表才能按它取数。" />}
        />
      </ErpListCard>

      {editor !== null && (<Modal
        title={editor?.mode === 'new' ? '新增口径' : `修改口径 · ${form.metricId}`}
        onClose={() => setEditor(null)}
        footer={(
          <div className="d-flex gap-2 justify-content-end">
            <Button variant="secondary" onClick={() => setEditor(null)}>取消</Button>
            <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>保存</Button>
          </div>
        )}
      >
        <div className="row g-2">
          <div className="col-md-6">
            <label className="form-label" htmlFor="metric-id">标识</label>
            <input id="metric-id" className="form-control form-control-sm font-monospace"
              value={form.metricId} aria-label="口径标识"
              disabled={editor?.mode === 'edit'}
              onChange={(event) => setForm({ ...form, metricId: event.target.value })} />
            <div className="form-hint">小写字母开头，只含小写字母/数字/下划线；**标识是身份，保存后不可改**。</div>
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="metric-name">名称</label>
            <input id="metric-name" className="form-control form-control-sm"
              value={form.metricName} aria-label="口径名称"
              onChange={(event) => setForm({ ...form, metricName: event.target.value })} />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="metric-source">来源表</label>
            <input id="metric-source" className="form-control form-control-sm font-monospace"
              value={form.sourceTable} aria-label="来源表"
              onChange={(event) => setForm({ ...form, sourceTable: event.target.value })} />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="metric-domain">业务域</label>
            <input id="metric-domain" className="form-control form-control-sm"
              value={form.domain} aria-label="业务域"
              onChange={(event) => setForm({ ...form, domain: event.target.value })} />
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="metric-definition">口径定义（受控表达式）</label>
            <input id="metric-definition" className="form-control form-control-sm font-monospace"
              value={form.definition} aria-label="口径定义"
              onChange={(event) => setForm({ ...form, definition: event.target.value })} />
            <div className="form-hint">如 <code>SUM(AMOUNT_TAX)</code>；除法分母须用 <code>NULLIF</code> 包裹。</div>
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="metric-dimensions">维度（逗号分隔）</label>
            <input id="metric-dimensions" className="form-control form-control-sm font-monospace"
              value={form.dimensionKeys} aria-label="维度"
              onChange={(event) => setForm({ ...form, dimensionKeys: event.target.value })} />
            <div className="form-hint">每个维度必须是来源表或行过滤表上的物理列。</div>
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="metric-filter">行过滤（JSON，可空）</label>
            <textarea id="metric-filter" className="form-control font-monospace" rows={3}
              value={form.rowFilter} aria-label="行过滤"
              onChange={(event) => setForm({ ...form, rowFilter: event.target.value })} />
            <div className="form-hint">
              形如 <code>{'{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[…]  }'}</code>；
              表必须是来源表或来源模块的主表。
            </div>
          </div>
          <div className="col-12">
            <label className="form-label" htmlFor="metric-description">说明</label>
            <textarea id="metric-description" className="form-control" rows={2}
              value={form.description} aria-label="说明"
              onChange={(event) => setForm({ ...form, description: event.target.value })} />
          </div>
          <div className="col-md-6">
            <label className="form-label" htmlFor="metric-status">确认状态</label>
            <select id="metric-status" className="form-select form-select-sm"
              value={form.confirmStatus} aria-label="确认状态"
              onChange={(event) => setForm({ ...form, confirmStatus: event.target.value })}>
              <option value="CANDIDATE">候选（不可用于取数）</option>
              <option value="CONFIRMED">已确认（可用于取数）</option>
            </select>
          </div>
        </div>
        {error && <div className="alert alert-danger mt-3 mb-0 py-2" role="alert">{error}</div>}
      </Modal>)}
    </div>
  )
}
