import { IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

interface ModuleOption { moduleId: number; description: string }
interface IdNameOption { id: string; name: string }
interface ReportRow {
  reportId: string; reportName: string | null; moduleId: number; isoNo: string | null;
  headerId: string | null; tailId: string | null; footerText: string | null;
  defaultPaper: string | null; isDefault: boolean; reportFilter: string | null;
  defaultPrinter: string | null; remark: string | null
}
interface SortRow {
  serialNo: number; sortName: string | null; sortFields: string | null; sortDesc: string | null;
  groupName: string | null; groupFields: string | null; groupDesc: string | null
}

const PAPERS = ['', 'A4', 'A3', 'A5', 'LETTER', 'LEGAL']

/**
 * 报表排序/分组字段选择器：复用统一选择器（report-admin.fields 数据源）。
 * 已选字段按「表.列」token 保持顺序，勾选/取消/移除后经左上角「确认」回写逗号分隔串。
 */
function ReportFieldPicker({
  open,
  title,
  moduleId,
  value,
  onSave,
  onClose,
}: {
  open: boolean
  title: string
  moduleId: string
  value: string
  onSave: (value: string) => void
  onClose: () => void
}) {
  const [tokens, setTokens] = useState<string[]>([])

  useEffect(() => {
    if (open) setTokens(value.split(',').map(token => token.trim()).filter(Boolean))
  }, [open, value])

  const rowSelection = useMemo(() => Object.fromEntries(tokens.map(token => [token, true])), [tokens])

  const reconcileSelection = (next: Record<string, boolean>) => {
    setTokens(current => {
      const nextKeys = Object.keys(next).filter(key => next[key])
      const currentSet = new Set(current)
      if (nextKeys.length === current.length && nextKeys.every(key => currentSet.has(key))) return current
      const kept = current.filter(key => nextKeys.includes(key))
      const added = nextKeys.filter(key => !currentSet.has(key))
      return [...kept, ...added]
    })
  }

  const removeToken = (token: string) => {
    reconcileSelection({ ...rowSelection, [token]: false })
  }

  return (
    <UnifiedChooser
      open={open}
      title={title}
      source={{ kind: 'sourceKey', key: 'report-admin.fields', args: { moduleId } }}
      mode="multi"
      getRowId={(row) => `${String(row.T_ID)}.${String(row.F_ID)}`}
      selectedKeys={rowSelection}
      onSelectedKeysChange={reconcileSelection}
      onPick={() => { onSave(tokens.join(',')); onClose() }}
      onClose={onClose}
      dialogSize="md"
      emptyText="该模块没有可用字段。"
      extra={
        <div className="mt-2">
          <label className="form-label">已选字段（顺序即保存顺序）</label>
          <div className="erp-field-picker-list">
            {tokens.map((token, index) => (
              <div key={token} className="erp-field-picker-row">
                <span className="erp-field-picker-order">{index + 1}</span>
                <span className="erp-menu-table-id">{token}</span>
                <span className="ms-auto d-flex align-items-center gap-1">
                  <button type="button" className="erp-field-mini" aria-label={`移除 ${token}`} onClick={() => removeToken(token)}>
                    <IconTrash size={14} />
                  </button>
                </span>
              </div>
            ))}
            {tokens.length === 0 && <div className="text-secondary small p-2">尚未选择字段。</div>}
          </div>
        </div>
      }
    />
  )
}

export function ReportAdminPage() {
  const [moduleId, setModuleId] = useState('')
  const [selectedReport, setSelectedReport] = useState('')
  const [reportDraft, setReportDraft] = useState<Record<string, string | boolean>>({})
  const [editingReport, setEditingReport] = useState<string | null>(null)
  const [sortDraft, setSortDraft] = useState<Record<string, string>>({})
  const [editingSort, setEditingSort] = useState<number | null>(null)
  const [fieldPicker, setFieldPicker] = useState<{ target: 'sortFields' | 'groupFields' } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const modules = useQuery({ queryKey: ['report-admin', 'modules'], queryFn: () => apiClient.get<ModuleOption[]>('/report-admin/modules') })
  const reports = useQuery({
    queryKey: ['report-admin', 'reports', moduleId],
    queryFn: () => apiClient.get<ReportRow[]>(`/report-admin/reports?moduleId=${moduleId}`),
    enabled: moduleId !== '',
  })
  const headers = useQuery({ queryKey: ['report-admin', 'headers'], queryFn: () => apiClient.get<IdNameOption[]>('/report-admin/headers') })
  const tails = useQuery({ queryKey: ['report-admin', 'tails'], queryFn: () => apiClient.get<IdNameOption[]>('/report-admin/tails') })
  const sorts = useQuery({
    queryKey: ['report-admin', 'sorts', selectedReport],
    queryFn: () => apiClient.get<SortRow[]>(`/report-admin/sorts?reportId=${encodeURIComponent(selectedReport)}`),
    enabled: selectedReport !== '',
  })

  const rows = reports.data ?? []
  const sortRows = sorts.data ?? []

  useEffect(() => {
    if (!reports.data) return
    if (selectedReport && reports.data.some((item) => item.reportId === selectedReport)) return
    setSelectedReport(reports.data[0]?.reportId ?? '')
  }, [reports.data, selectedReport])

  const resetReportForm = () => {
    setEditingReport(null)
    setReportDraft({})
    setError(null)
  }
  const startEditReport = (row: ReportRow) => {
    setEditingReport(row.reportId)
    setReportDraft({
      reportId: row.reportId, reportName: row.reportName ?? '', moduleId: String(row.moduleId), isoNo: row.isoNo ?? '',
      headerId: row.headerId ?? '', tailId: row.tailId ?? '', footerText: row.footerText ?? '',
      defaultPaper: row.defaultPaper ?? '', isDefault: row.isDefault, reportFilter: row.reportFilter ?? '',
      defaultPrinter: row.defaultPrinter ?? '', remark: row.remark ?? '',
    })
    setError(null)
  }
  const submitReport = async () => {
    const id = String(reportDraft.reportId ?? '').trim()
    if (!id) { setError('报表编号不能为空。'); return }
    if (!moduleId) { setError('请先选择模块。'); return }
    setSaving(true); setError(null)
    try {
      const body = {
        reportId: id, reportName: String(reportDraft.reportName ?? ''), moduleId: Number(moduleId),
        isoNo: String(reportDraft.isoNo ?? ''), headerId: String(reportDraft.headerId ?? ''), tailId: String(reportDraft.tailId ?? ''),
        footerText: String(reportDraft.footerText ?? ''), defaultPaper: String(reportDraft.defaultPaper ?? ''),
        isDefault: Boolean(reportDraft.isDefault), reportFilter: String(reportDraft.reportFilter ?? ''),
        defaultPrinter: String(reportDraft.defaultPrinter ?? ''), remark: String(reportDraft.remark ?? ''),
      }
      if (editingReport) await apiClient.put(`/report-admin/reports/${encodeURIComponent(editingReport)}`, body)
      else await apiClient.post('/report-admin/reports', body)
      await reports.refetch()
      resetReportForm()
    } catch (err) { setError(err instanceof ApiError ? err.body.message : '保存失败。') } finally { setSaving(false) }
  }
  const deleteReport = async (row: ReportRow) => {
    if (!window.confirm(`确定删除报表「${row.reportName ?? row.reportId}」及其排序方案？`)) return
    setError(null)
    try {
      await apiClient.delete(`/report-admin/reports/${encodeURIComponent(row.reportId)}`)
      if (selectedReport === row.reportId) setSelectedReport('')
      await reports.refetch()
    } catch (err) { setError(err instanceof ApiError ? err.body.message : '删除失败。') }
  }

  const resetSortForm = () => { setEditingSort(null); setSortDraft({}); setError(null) }
  const startEditSort = (row: SortRow) => {
    setEditingSort(row.serialNo)
    setSortDraft({
      serialNo: String(row.serialNo), sortName: row.sortName ?? '', sortFields: row.sortFields ?? '',
      sortDesc: row.sortDesc ?? '', groupName: row.groupName ?? '', groupFields: row.groupFields ?? '', groupDesc: row.groupDesc ?? '',
    })
    setError(null)
  }
  const submitSort = async () => {
    const serialNo = Number(sortDraft.serialNo)
    if (!serialNo || !selectedReport) { setError('排序方案序号无效。'); return }
    setSaving(true); setError(null)
    try {
      const body = {
        serialNo, sortName: String(sortDraft.sortName ?? ''), sortFields: String(sortDraft.sortFields ?? ''),
        sortDesc: String(sortDraft.sortDesc ?? ''), groupName: String(sortDraft.groupName ?? ''),
        groupFields: String(sortDraft.groupFields ?? ''), groupDesc: String(sortDraft.groupDesc ?? ''),
      }
      if (editingSort !== null) await apiClient.put(`/report-admin/sorts/${encodeURIComponent(selectedReport)}/${editingSort}`, body)
      else await apiClient.post(`/report-admin/sorts?reportId=${encodeURIComponent(selectedReport)}`, body)
      await sorts.refetch()
      resetSortForm()
    } catch (err) { setError(err instanceof ApiError ? err.body.message : '保存失败。') } finally { setSaving(false) }
  }
  const deleteSort = async (row: SortRow) => {
    if (!window.confirm(`确定删除排序方案「${row.sortName ?? row.serialNo}」？`)) return
    setError(null)
    try {
      await apiClient.delete(`/report-admin/sorts/${encodeURIComponent(selectedReport)}/${row.serialNo}`)
      await sorts.refetch()
    } catch (err) { setError(err instanceof ApiError ? err.body.message : '删除失败。') }
  }

  if (modules.isPending) return <LoadingState label="正在加载模块…" />
  if (modules.isError) return <ErrorState message={modules.error instanceof ApiError ? modules.error.body.message : '加载失败。'} onRetry={() => void modules.refetch()} />

  return (
    <div className="d-grid gap-2">
      <div className="card">
        <div className="card-header py-2 d-flex align-items-center gap-2">
          <span className="fw-semibold small">报表定义维护（2201）</span>
          <select className="form-select form-select-sm w-auto ms-2" value={moduleId} onChange={(event) => { setModuleId(event.target.value); setSelectedReport(''); resetReportForm(); resetSortForm() }}>
            <option value="">请选择模块</option>
            {modules.data.map((item) => <option key={item.moduleId} value={item.moduleId}>{item.moduleId} {item.description}</option>)}
          </select>
          <span className="text-secondary small">维护 REPORT 报表定义与 REPORT_SORT 排序/分组方案</span>
        </div>
        <div className="card-body py-2">
          {error && <div className="text-danger small mb-2">{error}</div>}
          {!moduleId ? <div className="text-secondary small py-3">先选择模块，再维护该模块的报表定义。</div> : reports.isPending ? <LoadingState label="正在加载报表…" /> : reports.isError ? (
            <ErrorState message={reports.error instanceof ApiError ? reports.error.body.message : '加载失败。'} onRetry={() => void reports.refetch()} />
          ) : (
            <div className="table-responsive">
              <table className="table table-sm align-middle">
                <thead><tr>
                  <th className="small">报表编号</th><th className="small">报表名称</th><th className="small">页头</th>
                  <th className="small">表尾</th><th className="small">纸张</th><th className="small">默认</th><th className="small">操作</th>
                </tr></thead>
                <tbody>
                  {rows.map((row) => (
                    <tr key={row.reportId} className={selectedReport === row.reportId ? 'table-active' : ''}>
                      <td className="small font-monospace">{row.reportId}</td>
                      <td className="small">{row.reportName ?? '—'}</td>
                      <td className="small">{headers.data?.find((item) => item.id === row.headerId)?.name ?? row.headerId ?? '—'}</td>
                      <td className="small">{tails.data?.find((item) => item.id === row.tailId)?.name ?? row.tailId ?? '—'}</td>
                      <td className="small">{row.defaultPaper || 'A4'}</td>
                      <td className="small">{row.isDefault ? '是' : '否'}</td>
                      <td className="small text-nowrap">
                        <Button size="sm" className="me-1" onClick={() => { setSelectedReport(row.reportId); startEditReport(row) }}>编辑</Button>
                        <Button size="sm" className="me-1" variant="ghost" onClick={() => { setSelectedReport(row.reportId); resetSortForm() }}>排序方案</Button>
                        <Button size="sm" variant="danger" onClick={() => void deleteReport(row)}>删除</Button>
                      </td>
                    </tr>
                  ))}
                  {rows.length === 0 && <tr><td colSpan={7} className="text-center text-secondary py-3 small">该模块暂无报表定义</td></tr>}
                </tbody>
              </table>
            </div>
          )}
          <div className="border-top pt-2 mt-2">
            <div className="d-flex align-items-center gap-2 mb-2">
              <span className="small fw-semibold">{editingReport ? `编辑报表：${editingReport}` : '新增报表定义'}</span>
              {editingReport && <Button size="sm" variant="ghost" onClick={resetReportForm}>取消</Button>}
            </div>
            <div className="row g-2">
              <div className="col-md-3"><label className="form-label small mb-1">报表编号</label>
                <input className="form-control form-control-sm" value={String(reportDraft.reportId ?? '')} disabled={editingReport !== null} onChange={(event) => setReportDraft((state) => ({ ...state, reportId: event.target.value }))} /></div>
              <div className="col-md-3"><label className="form-label small mb-1">报表名称</label>
                <input className="form-control form-control-sm" value={String(reportDraft.reportName ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, reportName: event.target.value }))} /></div>
              <div className="col-md-3"><label className="form-label small mb-1">页头</label>
                <select className="form-select form-select-sm" value={String(reportDraft.headerId ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, headerId: event.target.value }))}>
                  <option value="">（默认）</option>
                  {(headers.data ?? []).map((item) => <option key={item.id} value={item.id}>{item.name}（{item.id}）</option>)}
                </select></div>
              <div className="col-md-3"><label className="form-label small mb-1">表尾</label>
                <select className="form-select form-select-sm" value={String(reportDraft.tailId ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, tailId: event.target.value }))}>
                  <option value="">（无）</option>
                  {(tails.data ?? []).map((item) => <option key={item.id} value={item.id}>{item.name}（{item.id}）</option>)}
                </select></div>
              <div className="col-md-2"><label className="form-label small mb-1">纸张</label>
                <select className="form-select form-select-sm" value={String(reportDraft.defaultPaper ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, defaultPaper: event.target.value }))}>
                  {PAPERS.map((paper) => <option key={paper} value={paper}>{paper || 'A4（默认）'}</option>)}
                </select></div>
              <div className="col-md-2"><label className="form-label small mb-1">默认报表</label>
                <input className="form-check-input ms-2 mt-2" type="checkbox" checked={Boolean(reportDraft.isDefault)} onChange={(event) => setReportDraft((state) => ({ ...state, isDefault: event.target.checked }))} /></div>
              <div className="col-md-3"><label className="form-label small mb-1">ISO 编号</label>
                <input className="form-control form-control-sm" value={String(reportDraft.isoNo ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, isoNo: event.target.value }))} /></div>
              <div className="col-md-3"><label className="form-label small mb-1">默认打印机</label>
                <input className="form-control form-control-sm" value={String(reportDraft.defaultPrinter ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, defaultPrinter: event.target.value }))} /></div>
              <div className="col-md-4"><label className="form-label small mb-1">备注</label>
                <input className="form-control form-control-sm" value={String(reportDraft.remark ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, remark: event.target.value }))} /></div>
              <div className="col-md-6"><label className="form-label small mb-1">报表过滤（受控解析，不可解析时跳过）</label>
                <input className="form-control form-control-sm" value={String(reportDraft.reportFilter ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, reportFilter: event.target.value }))} /></div>
              <div className="col-md-6"><label className="form-label small mb-1">页脚文字</label>
                <textarea className="form-control form-control-sm" rows={1} value={String(reportDraft.footerText ?? '')} onChange={(event) => setReportDraft((state) => ({ ...state, footerText: event.target.value }))} /></div>
            </div>
            <div className="mt-2"><Button size="sm" onClick={() => void submitReport()} loading={saving}>{editingReport ? '保存修改' : '新增报表'}</Button></div>
          </div>
          {selectedReport && (
            <div className="border-top pt-2 mt-3">
              <div className="small fw-semibold mb-2">排序/分组方案（REPORT_SORT）——报表：{selectedReport}</div>
              {sorts.isPending ? <LoadingState label="正在加载排序方案…" /> : (
                <div className="table-responsive">
                  <table className="table table-sm align-middle">
                    <thead><tr>
                      <th className="small">序号</th><th className="small">方案名</th><th className="small">排序字段</th>
                      <th className="small">分组名</th><th className="small">分组字段</th><th className="small">操作</th>
                    </tr></thead>
                    <tbody>
                      {sortRows.map((row) => (
                        <tr key={row.serialNo}>
                          <td className="small">{row.serialNo}</td>
                          <td className="small">{row.sortName ?? '—'}</td>
                          <td className="small font-monospace">{row.sortFields ?? '—'}</td>
                          <td className="small">{row.groupName ?? '—'}</td>
                          <td className="small font-monospace">{row.groupFields ?? '—'}</td>
                          <td className="small text-nowrap">
                            <Button size="sm" className="me-1" onClick={() => startEditSort(row)}>编辑</Button>
                            <Button size="sm" variant="danger" onClick={() => void deleteSort(row)}>删除</Button>
                          </td>
                        </tr>
                      ))}
                      {sortRows.length === 0 && <tr><td colSpan={6} className="text-center text-secondary py-2 small">暂无排序方案</td></tr>}
                    </tbody>
                  </table>
                </div>
              )}
              <div className="row g-2 align-items-end">
                <div className="col-md-2"><label className="form-label small mb-1">序号</label>
                  <input className="form-control form-control-sm" type="number" value={String(sortDraft.serialNo ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, serialNo: event.target.value }))} /></div>
                <div className="col-md-2"><label className="form-label small mb-1">方案名</label>
                  <input className="form-control form-control-sm" value={String(sortDraft.sortName ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, sortName: event.target.value }))} /></div>
                <div className="col-md-4"><label className="form-label small mb-1">排序字段（表.列，逗号分隔）</label>
                  <div className="d-flex gap-1">
                    <input className="form-control form-control-sm" value={String(sortDraft.sortFields ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, sortFields: event.target.value }))} />
                    <Button size="sm" onClick={() => setFieldPicker({ target: 'sortFields' })}>选择字段…</Button>
                  </div></div>
                <div className="col-md-2"><label className="form-label small mb-1">分组名</label>
                  <input className="form-control form-control-sm" value={String(sortDraft.groupName ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, groupName: event.target.value }))} /></div>
                <div className="col-md-4"><label className="form-label small mb-1">分组字段（表.列，逗号分隔）</label>
                  <div className="d-flex gap-1">
                    <input className="form-control form-control-sm" value={String(sortDraft.groupFields ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, groupFields: event.target.value }))} />
                    <Button size="sm" onClick={() => setFieldPicker({ target: 'groupFields' })}>选择字段…</Button>
                  </div></div>
                <div className="col-md-6"><label className="form-label small mb-1">排序说明</label>
                  <input className="form-control form-control-sm" value={String(sortDraft.sortDesc ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, sortDesc: event.target.value }))} /></div>
                <div className="col-md-6"><label className="form-label small mb-1">分组说明</label>
                  <input className="form-control form-control-sm" value={String(sortDraft.groupDesc ?? '')} onChange={(event) => setSortDraft((state) => ({ ...state, groupDesc: event.target.value }))} /></div>
              </div>
              <div className="mt-2 d-flex gap-2 align-items-center">
                <Button size="sm" onClick={() => void submitSort()} loading={saving}>{editingSort !== null ? '保存修改' : '新增方案'}</Button>
                {editingSort !== null && <Button size="sm" variant="ghost" onClick={resetSortForm}>取消</Button>}
              </div>
            </div>
          )}
        </div>
      </div>
      {fieldPicker ? (
        <ReportFieldPicker
          open
          title={fieldPicker.target === 'sortFields' ? '选择排序字段' : '选择分组字段'}
          moduleId={moduleId}
          value={String(sortDraft[fieldPicker.target] ?? '')}
          onSave={(value) => setSortDraft((state) => ({ ...state, [fieldPicker.target]: value }))}
          onClose={() => setFieldPicker(null)}
        />
      ) : null}
    </div>
  )
}
