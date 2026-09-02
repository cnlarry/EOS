import { IconEdit, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpFieldChooser } from '../../components/common/ErpFieldChooser'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpQueryBuilder } from '../../components/common/ErpQueryBuilder'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { emptyQueryCondition, type QueryCondition } from '../../components/common/queryCondition'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { serializeReportFilter } from './reportFilter'
import { describeApiError } from '../../lib/errors'

interface ModuleOption { moduleId: number; description: string }
interface IdNameOption { id: string; name: string }
interface ReportRow {
  reportId: string; reportName: string | null; moduleId: number; isoNo: string | null;
  headerId: string | null; tailId: string | null; footerText: string | null;
  isDefault: boolean; reportFilter: string | null;
  remark: string | null
}
interface SortRow {
  serialNo: number; sortName: string | null; sortFields: string | null; sortDesc: string | null;
  groupName: string | null; groupFields: string | null; groupDesc: string | null
}

interface ReportAdminFieldOption { table: string; column: string; label: string }

type ReportEditorState = { mode: 'new' } | { mode: 'edit'; row: ReportRow }
type SortEditorState = { mode: 'new' } | { mode: 'edit'; row: SortRow }

function selectColumn<T>(): ColumnDef<T, unknown> {
  return {
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
  }
}

interface ReportEditorModalProps {
  open: boolean
  mode: 'new' | 'edit'
  row?: ReportRow
  moduleId: number
  headers: IdNameOption[]
  tails: IdNameOption[]
  onClose: () => void
  onSaved: () => void
}

/** 报表定义（REPORT）新增/编辑弹窗（2201 定制页，弹窗承载对齐 2305/2306）。 */
function ReportEditorModal({ open, mode, row, moduleId, headers, tails, onClose, onSaved }: ReportEditorModalProps) {
  const editingId = mode === 'edit' ? (row?.reportId ?? '').trim() : ''
  const [draft, setDraft] = useState<Record<string, string | boolean>>({})
  const [moduleSelection, setModuleSelection] = useState<number | ''>('')
  const [moduleDesc, setModuleDesc] = useState('')
  const [moduleChooserOpen, setModuleChooserOpen] = useState(false)
  const [filterBuilderOpen, setFilterBuilderOpen] = useState(false)
  const [filterConditions, setFilterConditions] = useState<QueryCondition[]>([emptyQueryCondition()])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setDraft({
      reportId: row?.reportId ?? '', reportName: row?.reportName ?? '', isoNo: row?.isoNo ?? '',
      headerId: row?.headerId ?? '', tailId: row?.tailId ?? '', footerText: row?.footerText ?? '',
      isDefault: row?.isDefault ?? false,
      reportFilter: row?.reportFilter ?? '', remark: row?.remark ?? '',
    })
    setModuleSelection(mode === 'edit' ? (row?.moduleId ?? '') : '')
    setModuleDesc('')
    setError(null)
    setFilterBuilderOpen(false)
    setFilterConditions([emptyQueryCondition()])
  }, [open, mode, row])

  const effectiveModule = mode === 'edit' ? (row?.moduleId ?? moduleId) : (moduleSelection || 0)

  const fieldOptions = useQuery({
    queryKey: ['report-admin', 'field-options', effectiveModule],
    queryFn: () => apiClient.get<ReportAdminFieldOption[]>(`/report-admin/field-options?moduleId=${effectiveModule}`),
    enabled: effectiveModule > 0,
  })

  if (!open) return null

  const submit = async () => {
    const id = String(draft.reportId ?? '').trim()
    if (!id) { setError('报表编号不能为空。'); return }
    const targetModule = mode === 'edit' ? (row?.moduleId ?? moduleId) : moduleSelection
    if (!targetModule) { setError('请选择所属模块。'); return }
    setSaving(true)
    setError(null)
    try {
      const body = {
        reportId: id, reportName: String(draft.reportName ?? ''), moduleId: targetModule,
        isoNo: String(draft.isoNo ?? ''), headerId: String(draft.headerId ?? ''), tailId: String(draft.tailId ?? ''),
        footerText: String(draft.footerText ?? ''),
        isDefault: Boolean(draft.isDefault), reportFilter: String(draft.reportFilter ?? ''),
        remark: String(draft.remark ?? ''),
      }
      if (mode === 'edit') await apiClient.put(`/report-admin/reports/${encodeURIComponent(editingId)}`, body)
      else await apiClient.post('/report-admin/reports', body)
      onSaved()
    } catch (reason) {
      setError(describeApiError(reason, '保存失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
    <Modal
      title={mode === 'new' ? '新增报表定义' : `编辑报表：${editingId}`}
      onClose={onClose}
      size="lg"
      footer={<>
        <Button onClick={onClose}>取消</Button>
        <Button variant="primary" onClick={() => void submit()} loading={saving}>{mode === 'edit' ? '保存修改' : '新增记录'}</Button>
      </>}
    >
            {error && <div className="alert alert-danger py-2 mb-3" role="alert">{error}</div>}
            <div className="row g-3">
              <div className="col-md-4">
                <label className="form-label" htmlFor="report-module">所属模块</label>
                <div className="d-flex gap-1">
                  <input id="report-module" className="form-control" readOnly value={mode === 'edit' ? String(row?.moduleId ?? '') : (moduleSelection ? `${moduleSelection} ${moduleDesc}` : '')} disabled={mode === 'edit'} />
                  {mode === 'new' && <Button size="sm" onClick={() => setModuleChooserOpen(true)}>选择模块…</Button>}
                </div>
                {mode === 'edit' && <div className="form-hint">所属模块不可修改</div>}
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="report-id">报表编号</label>
                <input id="report-id" className="form-control" value={String(draft.reportId ?? '')} disabled={mode === 'edit'} onChange={(event) => setDraft((state) => ({ ...state, reportId: event.target.value }))} />
                {mode === 'edit' && <div className="form-hint">报表编号不可修改</div>}
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="report-name">报表名称</label>
                <input id="report-name" className="form-control" value={String(draft.reportName ?? '')} onChange={(event) => setDraft((state) => ({ ...state, reportName: event.target.value }))} />
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="report-iso">ISO 编号</label>
                <input id="report-iso" className="form-control" value={String(draft.isoNo ?? '')} onChange={(event) => setDraft((state) => ({ ...state, isoNo: event.target.value }))} />
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="report-header">页头</label>
                <select id="report-header" className="form-select" value={String(draft.headerId ?? '')} onChange={(event) => setDraft((state) => ({ ...state, headerId: event.target.value }))}>
                  <option value="">（默认）</option>
                  {headers.map((item) => <option key={item.id} value={item.id}>{item.name}（{item.id}）</option>)}
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label" htmlFor="report-tail">表尾</label>
                <select id="report-tail" className="form-select" value={String(draft.tailId ?? '')} onChange={(event) => setDraft((state) => ({ ...state, tailId: event.target.value }))}>
                  <option value="">（无）</option>
                  {tails.map((item) => <option key={item.id} value={item.id}>{item.name}（{item.id}）</option>)}
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label d-block" htmlFor="report-default">默认报表</label>
                <input id="report-default" className="form-check-input" type="checkbox" checked={Boolean(draft.isDefault)} onChange={(event) => setDraft((state) => ({ ...state, isDefault: event.target.checked }))} />
              </div>
              <div className="col-md-12">
                <label className="form-label" htmlFor="report-filter">报表过滤（受控解析，不可解析时跳过）</label>
                <div className="d-flex gap-1">
                  <input id="report-filter" className="form-control" value={String(draft.reportFilter ?? '')} onChange={(event) => setDraft((state) => ({ ...state, reportFilter: event.target.value }))} />
                  <Button size="sm" onClick={() => setFilterBuilderOpen(true)} disabled={!effectiveModule}>构建…</Button>
                </div>
              </div>
              <div className="col-md-12">
                <label className="form-label" htmlFor="report-footer">页脚文字</label>
                <textarea id="report-footer" className="form-control" rows={2} value={String(draft.footerText ?? '')} onChange={(event) => setDraft((state) => ({ ...state, footerText: event.target.value }))} />
              </div>
              <div className="col-md-12">
                <label className="form-label" htmlFor="report-remark">备注</label>
                <input id="report-remark" className="form-control" value={String(draft.remark ?? '')} onChange={(event) => setDraft((state) => ({ ...state, remark: event.target.value }))} />
              </div>
            </div>
    </Modal>
      {moduleChooserOpen && (
        <UnifiedChooser
          open
          title="选择所属模块"
          source={{ kind: 'sourceKey', key: 'report-admin.modules' }}
          mode="single"
          getRowId={(chooserRow) => String(chooserRow.M_IDX)}
          onPick={(rows) => {
            const picked = rows[0] as { M_IDX?: unknown; M_DESC?: unknown } | undefined
            if (picked) {
              setModuleSelection(Number(picked.M_IDX))
              setModuleDesc(String(picked.M_DESC ?? ''))
            }
            setModuleChooserOpen(false)
          }}
          onClose={() => setModuleChooserOpen(false)}
          dialogSize="md"
          emptyText="没有可选择的报表模块。"
        />
      )}
      {filterBuilderOpen && (
        <ErpQueryBuilder
          open
          title="构建报表过滤条件"
          fields={(fieldOptions.data ?? []).map((option) => ({ key: `${option.table}.${option.column}`, label: option.label || `${option.table}.${option.column}` }))}
          conditions={filterConditions}
          onChange={setFilterConditions}
          onApply={() => {
            setDraft((state) => ({ ...state, reportFilter: serializeReportFilter(filterConditions) }))
            setFilterBuilderOpen(false)
          }}
          onClear={() => {
            setDraft((state) => ({ ...state, reportFilter: '' }))
            setFilterBuilderOpen(false)
          }}
          onClose={() => setFilterBuilderOpen(false)}
        />
      )}
    </>
  )
}

/**
 * 报表排序/分组字段选择器：统一字段选择器（report-admin.fields 数据源），
 * 已选字段按「表.列」token 保持顺序，右栏可上移/下移/移除后经「保存」回写逗号分隔串。
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
  return (
    <ErpFieldChooser
      open={open}
      title={title}
      source={{ kind: 'sourceKey', key: 'report-admin.fields', args: { moduleId } }}
      mode="multi"
      getRowId={(row) => `${String(row.T_ID)}.${String(row.F_ID)}`}
      valueFormat="comma"
      value={value}
      onSave={onSave}
      onClose={onClose}
      emptyText="该模块没有可用字段。"
    />
  )
}

interface SortEditorModalProps {
  open: boolean
  mode: 'new' | 'edit'
  row?: SortRow
  reportId: string
  moduleId: string
  onClose: () => void
  onSaved: () => void
}

/** 排序/分组方案（REPORT_SORT）新增/编辑弹窗。 */
function SortEditorModal({ open, mode, row, reportId, moduleId, onClose, onSaved }: SortEditorModalProps) {
  const editingSerial = mode === 'edit' ? (row?.serialNo ?? 0) : 0
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [fieldPicker, setFieldPicker] = useState<{ target: 'sortFields' | 'groupFields' } | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setDraft({
      serialNo: row?.serialNo != null ? String(row.serialNo) : '', sortName: row?.sortName ?? '',
      sortFields: row?.sortFields ?? '', sortDesc: row?.sortDesc ?? '',
      groupName: row?.groupName ?? '', groupFields: row?.groupFields ?? '', groupDesc: row?.groupDesc ?? '',
    })
    setError(null)
    setFieldPicker(null)
  }, [open, mode, row])

  if (!open) return null

  const submit = async () => {
    const serialNo = Number(draft.serialNo)
    if (!serialNo) { setError('排序方案序号无效。'); return }
    setSaving(true)
    setError(null)
    try {
      const body = {
        serialNo, sortName: String(draft.sortName ?? ''), sortFields: String(draft.sortFields ?? ''),
        sortDesc: String(draft.sortDesc ?? ''), groupName: String(draft.groupName ?? ''),
        groupFields: String(draft.groupFields ?? ''), groupDesc: String(draft.groupDesc ?? ''),
      }
      if (mode === 'edit') await apiClient.put(`/report-admin/sorts/${encodeURIComponent(reportId)}/${editingSerial}`, body)
      else await apiClient.post(`/report-admin/sorts?reportId=${encodeURIComponent(reportId)}`, body)
      onSaved()
    } catch (reason) {
      setError(describeApiError(reason, '保存失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  return (
      <>
      <Modal
        title={mode === 'new' ? `新增排序方案：${reportId}` : `编辑排序方案：${reportId}`}
        onClose={onClose}
        size="lg"
        footer={<>
          <Button onClick={onClose}>取消</Button>
          <Button variant="primary" onClick={() => void submit()} loading={saving}>{mode === 'edit' ? '保存修改' : '新增方案'}</Button>
        </>}
      >
            {error && <div className="alert alert-danger py-2 mb-3" role="alert">{error}</div>}
            <div className="row g-3">
              <div className="col-md-6">
                <label className="form-label" htmlFor="sort-serial">序号</label>
                <input id="sort-serial" className="form-control" type="number" value={String(draft.serialNo ?? '')} disabled={mode === 'edit'} onChange={(event) => setDraft((state) => ({ ...state, serialNo: event.target.value }))} />
                {mode === 'edit' && <div className="form-hint">序号不可修改</div>}
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="sort-name">方案名</label>
                <input id="sort-name" className="form-control" value={String(draft.sortName ?? '')} onChange={(event) => setDraft((state) => ({ ...state, sortName: event.target.value }))} />
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="sort-fields">排序字段（表.列，逗号分隔）</label>
                <div className="d-flex gap-1">
                  <input id="sort-fields" className="form-control" value={String(draft.sortFields ?? '')} onChange={(event) => setDraft((state) => ({ ...state, sortFields: event.target.value }))} />
                  <Button size="sm" onClick={() => setFieldPicker({ target: 'sortFields' })}>选择字段…</Button>
                </div>
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="sort-group-fields">分组字段（表.列，逗号分隔）</label>
                <div className="d-flex gap-1">
                  <input id="sort-group-fields" className="form-control" value={String(draft.groupFields ?? '')} onChange={(event) => setDraft((state) => ({ ...state, groupFields: event.target.value }))} />
                  <Button size="sm" onClick={() => setFieldPicker({ target: 'groupFields' })}>选择字段…</Button>
                </div>
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="sort-group-name">分组名</label>
                <input id="sort-group-name" className="form-control" value={String(draft.groupName ?? '')} onChange={(event) => setDraft((state) => ({ ...state, groupName: event.target.value }))} />
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="sort-desc">排序说明</label>
                <input id="sort-desc" className="form-control" value={String(draft.sortDesc ?? '')} onChange={(event) => setDraft((state) => ({ ...state, sortDesc: event.target.value }))} />
              </div>
              <div className="col-md-12">
                <label className="form-label" htmlFor="sort-group-desc">分组说明</label>
                <input id="sort-group-desc" className="form-control" value={String(draft.groupDesc ?? '')} onChange={(event) => setDraft((state) => ({ ...state, groupDesc: event.target.value }))} />
              </div>
            </div>
      </Modal>
      {fieldPicker && (
        <ReportFieldPicker
          open
          title={fieldPicker.target === 'sortFields' ? '选择排序字段' : '选择分组字段'}
          moduleId={moduleId}
          value={String(draft[fieldPicker.target] ?? '')}
          onSave={(value) => setDraft((state) => ({ ...state, [fieldPicker.target]: value }))}
          onClose={() => setFieldPicker(null)}
        />
      )}
      </>
  )
}

export function ReportAdminPage() {
  const [keyword, setKeyword] = useState('')
  const [reportSelection, setReportSelection] = useState<RowSelectionState>({})
  const [sortSelection, setSortSelection] = useState<RowSelectionState>({})
  const [selectedReport, setSelectedReport] = useState('')
  const [reportEditor, setReportEditor] = useState<ReportEditorState | null>(null)
  const [sortEditor, setSortEditor] = useState<SortEditorState | null>(null)

  const modules = useQuery({ queryKey: ['report-admin', 'modules'], queryFn: () => apiClient.get<ModuleOption[]>('/report-admin/modules') })
  const reports = useQuery({ queryKey: ['report-admin', 'reports'], queryFn: () => apiClient.get<ReportRow[]>('/report-admin/reports') })
  const headers = useQuery({ queryKey: ['report-admin', 'headers'], queryFn: () => apiClient.get<IdNameOption[]>('/report-admin/headers') })
  const tails = useQuery({ queryKey: ['report-admin', 'tails'], queryFn: () => apiClient.get<IdNameOption[]>('/report-admin/tails') })
  const sorts = useQuery({
    queryKey: ['report-admin', 'sorts', selectedReport],
    queryFn: () => apiClient.get<SortRow[]>(`/report-admin/sorts?reportId=${encodeURIComponent(selectedReport)}`),
    enabled: selectedReport !== '',
  })

  const rows = reports.data ?? []
  const sortRows = sorts.data ?? []

  const filteredRows = useMemo(() => {
    const text = keyword.trim().toLowerCase()
    const all = reports.data ?? []
    if (!text) return all
    const moduleDesc = (moduleId: number) => modules.data?.find((item) => item.moduleId === moduleId)?.description ?? ''
    return all.filter((row) =>
      row.reportId.toLowerCase().includes(text)
      || (row.reportName ?? '').toLowerCase().includes(text)
      || String(row.moduleId).includes(text)
      || moduleDesc(row.moduleId).toLowerCase().includes(text))
  }, [reports.data, keyword, modules.data])

  // 数据加载后无有效选中时默认选中第一条（刷新/过滤变化时同样自动回落）
  useEffect(() => {
    if (!reports.data) return
    setReportSelection(current => {
      const valid = Object.keys(current).filter(key => current[key] && reports.data!.some(item => item.reportId === key))
      if (valid.length > 0) return current
      const first = reports.data[0]?.reportId ?? ''
      setSelectedReport(first)
      setSortSelection({})
      return first ? { [first]: true } : {}
    })
  }, [reports.data])

  const deleteReport = useCallback(async (row: ReportRow) => {
    if (!window.confirm(`确定删除报表「${row.reportName ?? row.reportId}」及其排序方案？`)) return
    try {
      await apiClient.delete(`/report-admin/reports/${encodeURIComponent(row.reportId)}`)
      if (selectedReport === row.reportId) { setSelectedReport(''); setReportSelection({}); setSortEditor(null) }
      await reports.refetch()
    } catch { /* 删除失败静默：保持列表现场，错误由下次刷新体现 */ }
  }, [selectedReport, reports])

  const deleteSort = useCallback(async (row: SortRow) => {
    if (!window.confirm(`确定删除排序方案「${row.sortName ?? row.serialNo}」？`)) return
    try {
      await apiClient.delete(`/report-admin/sorts/${encodeURIComponent(selectedReport)}/${row.serialNo}`)
      await sorts.refetch()
    } catch { /* 删除失败静默 */ }
  }, [selectedReport, sorts])

  const handleReportSelectionChange = (next: RowSelectionState) => {
    setReportSelection(next)
    const keys = Object.keys(next).filter(key => next[key])
    const nextId = keys[0] ?? ''
    if (nextId !== selectedReport) {
      setSelectedReport(nextId)
      setSortSelection({})
      setSortEditor(null)
    }
  }

  const reportColumns = useMemo<ColumnDef<ReportRow, unknown>[]>(() => [
    selectColumn<ReportRow>(),
    { accessorKey: 'reportId', header: '报表编号', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'moduleId', header: '所属模块',
      cell: ({ row }) => {
        const m = modules.data?.find((item) => item.moduleId === row.original.moduleId)
        return m ? `${m.moduleId} ${m.description}` : String(row.original.moduleId)
      },
    },
    { accessorKey: 'reportName', header: '报表名称', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'headerId', header: '页头', cell: (info) => headers.data?.find((item) => item.id === info.getValue())?.name ?? (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'tailId', header: '表尾', cell: (info) => tails.data?.find((item) => item.id === info.getValue())?.name ?? (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'isDefault', header: '默认', cell: (info) => (info.getValue() ? '是' : '否') },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      meta: { className: 'text-end text-nowrap', frozenRight: true, truncate: false, minWidth: 132, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          <Button size="sm" variant="ghost" icon={<IconEdit size={14} />} onClick={() => setReportEditor({ mode: 'edit', row: row.original })}>编辑</Button>
          <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} onClick={() => void deleteReport(row.original)}>删除</Button>
        </div>
      ),
    },
  ], [headers.data, tails.data, modules.data, deleteReport])

  const sortColumns = useMemo<ColumnDef<SortRow, unknown>[]>(() => [
    selectColumn<SortRow>(),
    { accessorKey: 'serialNo', header: '序号' },
    { accessorKey: 'sortName', header: '方案名', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'sortFields', header: '排序字段', cell: (info) => <span className="font-monospace">{info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())}</span> },
    { accessorKey: 'groupName', header: '分组名', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'groupFields', header: '分组字段', cell: (info) => <span className="font-monospace">{info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())}</span> },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      meta: { className: 'text-end text-nowrap', frozenRight: true, truncate: false, minWidth: 132, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          <Button size="sm" variant="ghost" icon={<IconEdit size={14} />} onClick={() => setSortEditor({ mode: 'edit', row: row.original })}>编辑</Button>
          <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} onClick={() => void deleteSort(row.original)}>删除</Button>
        </div>
      ),
    },
  ], [deleteSort])

  if (modules.isPending) return <LoadingState label="正在加载模块…" />
  if (modules.isError) return <ErrorState message={describeApiError(modules.error, '加载失败。')} onRetry={() => void modules.refetch()} />

  const selectedRow = rows.find((item) => item.reportId === selectedReport)
  const reportsError = describeApiError(reports.error, '加载失败。')
  const sortsError = describeApiError(sorts.error, '加载失败。')

  return (
    <div className="erp-workbench-page">
      <ErpListCard
        ariaLabel="报表定义查询"
        search={<ErpSearchBox value={keyword} onChange={setKeyword} debounceMs={300} placeholder="搜索报表编号/名称/所属模块" ariaLabel="搜索报表" />}
        actions={<div className="d-flex gap-2 align-items-center">
          <Button size="sm" className="erp-command-btn" icon={<IconRefresh size={16} />} onClick={() => void reports.refetch()}>刷新</Button>
          <Button size="sm" variant="primary" className="erp-command-btn" icon={<IconPlus size={16} />} onClick={() => setReportEditor({ mode: 'new' })}>新增报表</Button>
        </div>}
        header={reports.data ? <div className="erp-list-header text-secondary small px-3 pt-2">共 {rows.length} 个报表{keyword.trim() ? `，筛选后 ${filteredRows.length} 个` : ''}；点击行选中报表，下方显示其排序/分组方案。</div> : undefined}
      >
        <div className="erp-master-table-region">
          {reports.isPending ? <LoadingState label="正在加载报表…" /> : reports.isError ? (
            <ErrorState message={reportsError} onRetry={() => void reports.refetch()} />
          ) : (
            <ErpTable
              columns={reportColumns}
              data={filteredRows}
              getRowId={(row: ReportRow) => row.reportId}
              empty={<EmptyState title={keyword.trim() ? '未找到匹配报表' : '暂无报表定义'} description={keyword.trim() ? '换一个关键词试试。' : '点击「新增报表」建立第一条报表定义。'} />}
              resizable
              storageKey="report-admin-reports"
              clientSideSorting
              rowClickSingleSelect
              rowSelection={reportSelection}
              onRowSelectionChange={handleReportSelectionChange}
            />
          )}
        </div>
      </ErpListCard>
      <section className="card erp-detail-card">
        <div className="card-header erp-detail-toolbar d-flex align-items-center gap-2">
          <span className="small fw-semibold">{selectedReport ? `排序/分组方案（REPORT_SORT）——报表：${selectedReport}` : '排序/分组方案（REPORT_SORT）'}</span>
          <div className="ms-auto">
            <Button size="sm" variant="primary" className="erp-command-btn" icon={<IconPlus size={16} />} disabled={!selectedReport} onClick={() => setSortEditor({ mode: 'new' })}>新增方案</Button>
          </div>
        </div>
        {selectedReport && sorts.isPending ? <LoadingState label="正在加载排序方案…" /> : sorts.isError ? (
          <ErrorState message={sortsError} onRetry={() => void sorts.refetch()} />
        ) : (
          <ErpTable
            columns={sortColumns}
            data={sortRows}
            getRowId={(row: SortRow) => String(row.serialNo)}
            empty={<EmptyState title={selectedReport ? '暂无排序方案' : '未选择报表'} description={selectedReport ? '点击「新增方案」建立排序/分组方案。' : '请先选择上方报表，再维护其排序/分组方案。'} />}
            resizable
            storageKey="report-admin-sorts"
            clientSideSorting
            rowClickSingleSelect
            rowSelection={sortSelection}
            onRowSelectionChange={setSortSelection}
          />
        )}
      </section>
      {reportEditor && (
        <ReportEditorModal
          open
          mode={reportEditor.mode}
          row={reportEditor.mode === 'edit' ? reportEditor.row : undefined}
          moduleId={Number(selectedRow?.moduleId ?? 0)}
          headers={headers.data ?? []}
          tails={tails.data ?? []}
          onClose={() => setReportEditor(null)}
          onSaved={() => { setReportEditor(null); void reports.refetch() }}
        />
      )}
      {sortEditor && selectedReport && (
        <SortEditorModal
          open
          mode={sortEditor.mode}
          row={sortEditor.mode === 'edit' ? sortEditor.row : undefined}
          reportId={selectedReport}
          moduleId={String(selectedRow?.moduleId ?? '')}
          onClose={() => setSortEditor(null)}
          onSaved={() => { setSortEditor(null); void sorts.refetch() }}
        />
      )}
    </div>
  )
}
