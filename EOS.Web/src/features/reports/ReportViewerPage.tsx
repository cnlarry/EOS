import { useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  IconArrowLeft,
  IconPrinter,
  IconRotateClockwise,
  IconSearch,
  IconX,
} from '@tabler/icons-react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { ErpCommandBar } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { alignClass, formatFieldValue } from '../document-workbench/fieldFormat'
import type { ColumnDef, VisibilityState } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'

interface ReportOption { label: string; value: string }
interface ReportSelectSource { table: string; idColumn: string; valueColumn: string }
interface ReportCondition { serialNo: number; field: string | null; desc: string; type: number; expression: string | null; defaultValue: string | null; parameterName: string | null; options: ReportOption[]; selectSource: ReportSelectSource | null; defaultValueTo: string | null }
interface ReportColumn { key: string; label: string; dataType: string; displayFormat?: string | null }
interface ReportDefinition { moduleId: number; title: string; masterTable: string; conditions: ReportCondition[]; columns: ReportColumn[]; masterPkOrder: string[]; dataSource: 'table' | 'aggregate'; parameters: ReportParameter[] }
interface ReportParameter { name: string; dataType: string; maxLength: number; serialNo: number; isTo: boolean; constant: string | null }
interface ReportQueryResult { rows: Record<string, unknown>[]; total: number; page: number; pageSize: number }
interface ReportPrintOption { reportId: string; reportName: string; headerId: string | null; tailId: string | null; footerText: string | null; isoNo: string | null; isDefault: boolean }
interface ReportHeaderOption { headerId: string; headerName: string; companyName: string; headerText: string | null; logoUrl: string | null }
interface ReportTailOption { tailId: string; tailName: string; tailText: string }
interface ReportSortScheme { serialNo: number; sortName: string; sortFields: string | null; groupName: string | null; groupFields: string | null }
interface ReportUserPrintSettings { reportId: string | null; headerId: string | null; tailId: string | null; sortSerialNo: number | null; sortAsc: boolean; showGroup: boolean; showDetail: boolean }
interface ReportPrintSettingsData {
  moduleId: number
  reports: ReportPrintOption[]
  headers: ReportHeaderOption[]
  tails: ReportTailOption[]
  sortSchemesByReport: Record<string, ReportSortScheme[]>
  userSettings: ReportUserPrintSettings | null
}

/** 结果列定义：既驱动表格列，也驱动「选择列」与本地列显隐。 */
interface ResultColumn {
  key: string
  label: string
  dataType: string
  displayFormat: string | null
}

const DEFAULT_PAGE_SIZE = 50
const PAGE_SIZES = [50, 100, 200]

/**
 * 报表查看器。
 *
 * 两种打开方式共用这一个组件：
 *   · 老的模块地址 `/reports/:moduleId`——模块号来自路由，报表身份随后解析出来，
 *     解析完即把地址**换成报表身份地址**（`/report/:reportId`，见下方 effect）；
 *   · 报表身份地址 `/report/:reportId`——模块号与报表编号由外层解析后经 props 传入。
 * 这么分是因为两者拿参数的来源不同，但取数、条件、打印面板这些状态必须完全一致，
 * 拆成两个组件就会出现"一个改了一个没改"的漂移。
 */
export function ReportViewerPage({
  moduleIdOverride,
  reportIdOverride,
}: { moduleIdOverride?: string; reportIdOverride?: string } = {}) {
  const navigate = useNavigate()
  const routeParams = useParams()
  const moduleId = moduleIdOverride ?? routeParams.moduleId ?? ''
  const baseId = useId()
  const [searchParams, setSearchParams] = useSearchParams()
  /** 首次渲染时的 URL 参数：条件初值来源，只读一次（后续变化由页面回写 URL） */
  const initialParams = useRef(searchParams)
  const [values, setValues] = useState<Record<number, string>>({})
  const [valuesTo, setValuesTo] = useState<Record<number, string>>({})
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [queryKey, setQueryKey] = useState(0)
  const [reportId, setReportId] = useState('')
  const [headerId, setHeaderId] = useState('')
  const [tailId, setTailId] = useState('')
  const [sortSerialNo, setSortSerialNo] = useState<number | null>(null)
  const [sortDirect, setSortDirect] = useState<'asc' | 'desc'>('asc')
  const [showGroup, setShowGroup] = useState(true)
  const [showDetail, setShowDetail] = useState(true)
  const [printing, setPrinting] = useState(false)
  const [printOpen, setPrintOpen] = useState(false)
  const [columnSelectorOpen, setColumnSelectorOpen] = useState(false)
  const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({})
  const [actionError, setActionError] = useState<string | null>(null)
  /** 无列定义的报表（空主表聚合/过程报表）查询后的结果集动态列 */
  const [spResultColumns, setSpResultColumns] = useState<ResultColumn[] | null>(null)
  const settingsApplied = useRef(false)
  const defaultsApplied = useRef(false)

  const printSettings = useQuery({
    queryKey: ['report', moduleId, 'print-settings'],
    queryFn: () => apiClient.get<ReportPrintSettingsData>(`/reports/${moduleId}/print-settings`),
  })
  const definition = useQuery({
    queryKey: ['report', moduleId, 'definition', reportId],
    queryFn: () => apiClient.get<ReportDefinition>(`/reports/${moduleId}/definition`, { query: { reportId: reportId || undefined } }),
    // 等待打印设置加载完成 reportId 初始化（一模块多报表按 REPORT_ID 解析 SP/列）
    enabled: printSettings.isSuccess && reportId !== '',
  })
  const result = useQuery({
    queryKey: ['report', moduleId, 'result', page, pageSize, queryKey, reportId],
    queryFn: () => apiClient.post<ReportQueryResult>(`/reports/${moduleId}/query?page=${page}&pageSize=${pageSize}${reportId ? `&reportId=${encodeURIComponent(reportId)}` : ''}`, { values, valuesTo }),
    enabled: definition.isSuccess && queryKey > 0,
    placeholderData: (previous: ReportQueryResult | undefined) => previous,
  })

  useEffect(() => {
    if (settingsApplied.current || !printSettings.data) return
    settingsApplied.current = true
    const user = printSettings.data.userSettings
    // 地址里点名了报表就认地址（深链/分享/收藏必须打开同一张）；
    // 没点名才轮到"最近用过"与"默认报表"——这是页内切换与老地址的兜底。
    const pointed = reportIdOverride
      ? printSettings.data.reports.find((item) => item.reportId === reportIdOverride)
      : undefined
    const report = pointed
      ?? (user?.reportId
        ? printSettings.data.reports.find((item) => item.reportId === user.reportId)
        : printSettings.data.reports.find((item) => item.isDefault) ?? printSettings.data.reports[0])
    setReportId(report?.reportId ?? '')
    // 地址点名的那张若不在这张模块的可见清单里（改过归属、或被例外行隐藏），
    // 不要静默换成默认报表——那是"点了 A 看到 B"。交给外层报错，这里只标记。
    if (reportIdOverride && !pointed) setActionError(`报表 ${reportIdOverride} 在当前账号下不可见。`)
    setHeaderId(user?.headerId ?? report?.headerId ?? '')
    setTailId(user?.tailId ?? report?.tailId ?? '')
    setSortSerialNo(user?.sortSerialNo ?? null)
    setSortDirect(user ? (user.sortAsc ? 'asc' : 'desc') : 'asc')
    setShowGroup(user?.showGroup ?? true)
    setShowDetail(user?.showDetail ?? true)
  }, [printSettings.data, reportIdOverride])

  // 条件初值：URL 参数优先（支持带条件深链），缺失时回落报表定义默认值；
  // 由 URL 带入条件的深链直接出结果，避免落在一张空白报表上
  useEffect(() => {
    if (defaultsApplied.current || !definition.data) return
    defaultsApplied.current = true
    const params = initialParams.current
    const next: Record<number, string> = {}
    const nextTo: Record<number, string> = {}
    let fromUrl = false
    for (const condition of definition.data.conditions) {
      const urlValue = params.get(`f${condition.serialNo}`)
      const urlValueTo = params.get(`t${condition.serialNo}`)
      if (urlValue != null) {
        next[condition.serialNo] = urlValue
        fromUrl = true
      } else if (condition.defaultValue) {
        next[condition.serialNo] = condition.defaultValue
      }
      if (urlValueTo != null) {
        nextTo[condition.serialNo] = urlValueTo
        fromUrl = true
      } else if (condition.defaultValueTo) {
        nextTo[condition.serialNo] = condition.defaultValueTo
      }
    }
    setValues(next)
    setValuesTo(nextTo)
    const urlPage = Number.parseInt(params.get('page') ?? '', 10)
    if (Number.isInteger(urlPage) && urlPage > 0) setPage(urlPage)
    const urlPageSize = Number.parseInt(params.get('pageSize') ?? '', 10)
    if (Number.isInteger(urlPageSize) && urlPageSize > 0) setPageSize(urlPageSize)
    if (fromUrl) setQueryKey((current) => current + 1)
  }, [definition.data])

  // 查询状态回写 URL：刷新/分享/浏览器后退都能回到同一张报表的同一条件。
  // 定义就绪前不回写——否则初始的空状态会把深链带来的条件参数先抹掉
  useEffect(() => {
    if (!definition.isSuccess) return
    const next = new URLSearchParams()
    if (reportId) next.set('reportId', reportId)
    for (const [serialNo, value] of Object.entries(values)) if (value) next.set(`f${serialNo}`, value)
    for (const [serialNo, value] of Object.entries(valuesTo)) if (value) next.set(`t${serialNo}`, value)
    if (page > 1) next.set('page', String(page))
    if (pageSize !== DEFAULT_PAGE_SIZE) next.set('pageSize', String(pageSize))
    setSearchParams(next, { replace: true })
  }, [definition.isSuccess, values, valuesTo, reportId, page, pageSize, setSearchParams])

  const resultColumns = useMemo<ResultColumn[]>(() => {
    if (spResultColumns) return spResultColumns
    return (definition.data?.columns ?? []).map((column) => ({
      key: column.key,
      label: column.label,
      dataType: column.dataType,
      displayFormat: column.displayFormat ?? null,
    }))
  }, [definition.data, spResultColumns])

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(
    () => resultColumns.map((column) => ({
      accessorKey: column.key,
      header: column.label,
      cell: (info) => formatFieldValue(info.getValue(), column.dataType, column.displayFormat) || '—',
      meta: { cellClassName: alignClass(null, column.dataType) },
    })),
    [resultColumns],
  )

  // 无列定义的报表：结果集行键即权威列（definition.columns 为空的模块）
  useEffect(() => {
    if ((definition.data?.columns?.length ?? 0) > 0) {
      setSpResultColumns(null)
      return
    }
    const first = result.data?.rows?.[0]
    if (!first) return
    const keys = Object.keys(first)
    if (keys.length === 0) return
    setSpResultColumns(keys.map((key) => ({ key, label: key, dataType: 'string', displayFormat: null })))
  }, [definition.data?.columns, result.data])

  // 列显隐本地持久化（按模块 + 报表）：不进服务端字段元数据
  const columnStorageKey = `eos.report.columns.${moduleId}.${reportId}`
  useEffect(() => {
    const stored = readStoredColumns(columnStorageKey)
    if (!stored) {
      setColumnVisibility({})
      return
    }
    const visible = new Set(stored)
    const next: VisibilityState = {}
    for (const column of resultColumns) next[column.key] = visible.has(column.key)
    setColumnVisibility(next)
  }, [columnStorageKey, resultColumns])

  const columnGroups = useMemo<ColumnSelectorGroup[]>(() => [{
    id: 'result',
    label: '报表列',
    fields: resultColumns.map((column) => ({ key: column.key, label: column.label })),
    visibleKeys: resultColumns.filter((column) => columnVisibility[column.key] !== false).map((column) => column.key),
    defaultKeys: resultColumns.map((column) => column.key),
  }], [resultColumns, columnVisibility])

  const pkOrder = useMemo(() => definition.data?.masterPkOrder ?? [], [definition.data])
  const getRowId = useCallback((row: Record<string, unknown>, index: number) => {
    if (pkOrder.length > 0) {
      const parts = pkOrder.map((key) => row[key])
      if (parts.every((value) => value != null && value !== '')) return parts.map((value) => String(value)).join('~')
    }
    return `row-${index}`
  }, [pkOrder])

  const runQuery = () => { setPage(1); setQueryKey((current) => current + 1); setActionError(null) }

  const resetConditions = () => {
    const next: Record<number, string> = {}
    const nextTo: Record<number, string> = {}
    for (const condition of definition.data?.conditions ?? []) {
      if (condition.defaultValue) next[condition.serialNo] = condition.defaultValue
      if (condition.defaultValueTo) nextTo[condition.serialNo] = condition.defaultValueTo
    }
    setValues(next)
    setValuesTo(nextTo)
    setPage(1)
  }

  const saveVisibleColumns = (selection: Record<string, string[]>) => {
    const visibleKeys = selection.result ?? []
    const visible = new Set(visibleKeys)
    const next: VisibilityState = {}
    for (const column of resultColumns) next[column.key] = visible.has(column.key)
    setColumnVisibility(next)
    storeColumns(columnStorageKey, visibleKeys)
  }

  const handleExport = async () => {
    if (!definition.data) return
    setActionError(null)
    try {
      const blob = await apiClient.postFile(`/reports/${moduleId}/export${reportId ? `?reportId=${encodeURIComponent(reportId)}` : ''}`, { values, valuesTo })
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${definition.data.title}.csv`
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      URL.revokeObjectURL(url)
    } catch (error) {
      setActionError(describeApiError(error, '导出失败，请重试。'))
    }
  }

  const selectedHeader = printSettings.data?.headers.find((header) => header.headerId === headerId)
  const sortSchemes = printSettings.data?.sortSchemesByReport[reportId] ?? []
  const hasPrintPermission = (printSettings.data?.reports.length ?? 0) > 0

  const handlePrint = async () => {
    if (!definition.data || !printSettings.data) return
    setPrinting(true)
    setActionError(null)
    try {
      await apiClient.post(`/reports/${moduleId}/print-settings`, {
        reportId: reportId || null,
        headerId: headerId || null,
        tailId: tailId || null,
        sortSerialNo,
        sortAsc: sortDirect === 'asc',
        showGroup,
        showDetail,
        values,
        valuesTo,
      }).catch(() => {})
      const blob = await apiClient.postFile(`/reports/${moduleId}/pdf`, {
        reportId: reportId || null,
        headerId: headerId || null,
        tailId: tailId || null,
        values,
        valuesTo,
        sortSerialNo,
        sortDirect: sortDirect === 'desc',
        showGroup,
        showDetail,
      })
      const url = URL.createObjectURL(blob)
      window.open(url, '_blank')
    } catch (error) {
      setActionError(describeApiError(error, 'PDF 生成失败，请重试。'))
    } finally {
      setPrinting(false)
    }
  }

  if (definition.isPending) return <LoadingState label="正在加载报表定义…" />
  if (definition.isError) return <ErrorState message={describeApiError(definition.error, '报表定义加载失败。')} onRetry={() => void definition.refetch()} />
  const def = definition.data!
  const errorMessage = describeApiError(result.error, '查询失败，请重试。')
  const selectedReport = printSettings.data?.reports.find((report) => report.reportId === reportId)
  const multipleReports = (printSettings.data?.reports.length ?? 0) > 1
  const rowCount = result.data?.rows?.length ?? 0

  return (
    <div className="erp-report-page">
      <header className="erp-report-head">
        <Button variant="ghost" size="sm" icon={<IconArrowLeft size={16} />} onClick={() => navigate('/report-center')}>
          报表中心
        </Button>
        <div className="erp-report-head-title">
          <h1>{def.title}</h1>
          <span>模块 {moduleId}{multipleReports && selectedReport ? ` · ${selectedReport.reportName}` : ''}</span>
        </div>
        <div className="erp-report-head-actions">
          {hasPrintPermission && (
            <Button
              size="sm"
              variant={printOpen ? 'primary' : 'secondary'}
              className="erp-command-btn"
              icon={<IconPrinter size={16} />}
              aria-expanded={printOpen}
              title="打印"
              onClick={() => setPrintOpen((current) => !current)}
            >
              打印
            </Button>
          )}
        </div>
      </header>

      {actionError && (
        <div className="erp-report-alert" role="alert">
          <span>{actionError}</span>
          <button type="button" aria-label="关闭提示" title="关闭提示" onClick={() => setActionError(null)}>
            <IconX size={14} />
          </button>
        </div>
      )}

      {hasPrintPermission && printOpen && (
        <section className="card erp-report-print-panel">
          <div className="card-header py-2 d-flex align-items-center gap-2">
            <span className="fw-semibold small">打印设置</span>
            <span className="text-secondary small">页头/表尾可随时更换，选择后即时预览</span>
          </div>
          <div className="card-body py-2">
            <div className="erp-query-grid">
              <div className="erp-query-field">
                <span className="erp-query-label" id={`${baseId}-print-report`}>报表</span>
                <div className="erp-query-control">
                  <select
                    className="form-select form-select-sm"
                    aria-labelledby={`${baseId}-print-report`}
                    value={reportId}
                    onChange={(event) => { setReportId(event.target.value); setSortSerialNo(null) }}
                  >
                    {(printSettings.data?.reports ?? []).map((report) => <option key={report.reportId} value={report.reportId}>{report.reportName}</option>)}
                  </select>
                </div>
              </div>
              <div className="erp-query-field">
                <span className="erp-query-label" id={`${baseId}-print-header`}>页头</span>
                <div className="erp-query-control">
                  <select
                    className="form-select form-select-sm"
                    aria-labelledby={`${baseId}-print-header`}
                    value={headerId}
                    onChange={(event) => setHeaderId(event.target.value)}
                  >
                    <option value="">（报表默认）</option>
                    {(printSettings.data?.headers ?? []).map((header) => <option key={header.headerId} value={header.headerId}>{header.headerName}</option>)}
                  </select>
                </div>
              </div>
              <div className="erp-query-field">
                <span className="erp-query-label" id={`${baseId}-print-tail`}>表尾</span>
                <div className="erp-query-control">
                  <select
                    className="form-select form-select-sm"
                    aria-labelledby={`${baseId}-print-tail`}
                    value={tailId}
                    onChange={(event) => setTailId(event.target.value)}
                  >
                    <option value="">（无）</option>
                    {(printSettings.data?.tails ?? []).map((tail) => <option key={tail.tailId} value={tail.tailId}>{tail.tailName}</option>)}
                  </select>
                </div>
              </div>
              <div className="erp-query-field">
                <span className="erp-query-label" id={`${baseId}-print-sort`}>排序方案</span>
                <div className="erp-query-control">
                  <select
                    className="form-select form-select-sm"
                    aria-labelledby={`${baseId}-print-sort`}
                    value={sortSerialNo ?? ''}
                    onChange={(event) => setSortSerialNo(event.target.value === '' ? null : Number(event.target.value))}
                  >
                    <option value="">（默认）</option>
                    {sortSchemes.map((scheme) => <option key={scheme.serialNo} value={scheme.serialNo}>{scheme.sortName}</option>)}
                  </select>
                </div>
              </div>
              <div className="erp-query-field">
                <span className="erp-query-label" id={`${baseId}-print-direct`}>顺序</span>
                <div className="erp-query-control" role="radiogroup" aria-labelledby={`${baseId}-print-direct`}>
                  <label className="form-check small">
                    <input className="form-check-input" type="radio" name={`${baseId}-sort-direct`} checked={sortDirect === 'asc'} onChange={() => setSortDirect('asc')} />
                    <span className="form-check-label">升序</span>
                  </label>
                  <label className="form-check small">
                    <input className="form-check-input" type="radio" name={`${baseId}-sort-direct`} checked={sortDirect === 'desc'} onChange={() => setSortDirect('desc')} />
                    <span className="form-check-label">降序</span>
                  </label>
                </div>
              </div>
              <div className="erp-query-field">
                <span className="erp-query-label" id={`${baseId}-print-scope`}>输出</span>
                <div className="erp-query-control" role="group" aria-labelledby={`${baseId}-print-scope`}>
                  <label className="form-check small">
                    <input className="form-check-input" type="checkbox" checked={showGroup} onChange={(event) => setShowGroup(event.target.checked)} />
                    <span className="form-check-label">显示分组</span>
                  </label>
                  <label className="form-check small">
                    <input className="form-check-input" type="checkbox" checked={showDetail} onChange={(event) => setShowDetail(event.target.checked)} />
                    <span className="form-check-label">显示明细</span>
                  </label>
                </div>
              </div>
            </div>
            <div className="d-flex align-items-center gap-2 mt-2">
              <Button size="sm" variant="primary" className="erp-command-btn" icon={<IconPrinter size={16} />} loading={printing} onClick={() => void handlePrint()}>
                生成 PDF
              </Button>
              <span className="text-secondary small">按当前查询条件输出整份报表</span>
            </div>
            {selectedHeader && (
              <div className="border rounded bg-light mt-2 px-3 py-2 d-flex align-items-center gap-3">
                {selectedHeader.logoUrl && <img src={selectedHeader.logoUrl} alt="页头 LOGO" style={{ maxHeight: 40 }} onError={(event) => { event.currentTarget.style.display = 'none' }} />}
                <div>
                  {selectedHeader.companyName && <div className="fw-bold">{selectedHeader.companyName}</div>}
                  {selectedHeader.headerText && <div className="small text-secondary">{selectedHeader.headerText}</div>}
                </div>
              </div>
            )}
          </div>
        </section>
      )}

      {def.conditions.length > 0 && (
        <section className="card erp-report-conditions">
          <div className="card-header d-flex align-items-center gap-2">
            <span className="fw-semibold small">查询条件</span>
            <span className="text-secondary small">{def.conditions.length} 项</span>
            <div className="ms-auto d-flex align-items-center gap-2">
              <Button size="sm" variant="ghost" className="erp-command-btn" icon={<IconRotateClockwise size={16} />} onClick={resetConditions}>
                重置
              </Button>
              <Button size="sm" variant="primary" className="erp-command-btn" icon={<IconSearch size={16} />} onClick={runQuery}>
                查询
              </Button>
            </div>
          </div>
          <div className="card-body">
            <div className="erp-query-grid">
              {def.conditions.map((condition) => {
                const labelId = `${baseId}-cond-${condition.serialNo}`
                return (
                  <div className="erp-query-field" key={condition.serialNo}>
                    <span className="erp-query-label" id={labelId}>{condition.desc}</span>
                    <div className="erp-query-control">
                      {condition.type === 1 && (
                        <>
                          <input
                            className="form-control form-control-sm erp-query-range"
                            placeholder="从"
                            aria-label={`${condition.desc} 从`}
                            value={values[condition.serialNo] ?? ''}
                            onChange={(event) => setValues((current) => ({ ...current, [condition.serialNo]: event.target.value }))}
                          />
                          <input
                            className="form-control form-control-sm erp-query-range"
                            placeholder="到"
                            aria-label={`${condition.desc} 到`}
                            value={valuesTo[condition.serialNo] ?? ''}
                            onChange={(event) => setValuesTo((current) => ({ ...current, [condition.serialNo]: event.target.value }))}
                          />
                        </>
                      )}
                      {condition.type === 2 && (
                        <select
                          className="form-select form-select-sm"
                          aria-labelledby={labelId}
                          value={values[condition.serialNo] ?? ''}
                          onChange={(event) => setValues((current) => ({ ...current, [condition.serialNo]: event.target.value }))}
                        >
                          <option value="">全部</option>
                          {condition.options.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                        </select>
                      )}
                      {condition.type === 3 && (
                        <DataSelectCondition
                          moduleId={moduleId}
                          reportId={reportId}
                          condition={condition}
                          labelId={labelId}
                          value={values[condition.serialNo] ?? ''}
                          onChange={(value) => setValues((current) => ({ ...current, [condition.serialNo]: value }))}
                        />
                      )}
                      {condition.type === 5 && (
                        <DataMultiSelectCondition
                          moduleId={moduleId}
                          condition={condition}
                          labelId={labelId}
                          value={values[condition.serialNo] ?? ''}
                          onChange={(value) => setValues((current) => ({ ...current, [condition.serialNo]: value }))}
                        />
                      )}
                      {condition.type === 4 && (
                        <div className="d-flex flex-wrap gap-2 small" role="group" aria-labelledby={labelId}>
                          {condition.options.map((option) => (
                            <label className="form-check" key={option.value}>
                              <input
                                className="form-check-input"
                                type="checkbox"
                                checked={(values[condition.serialNo] ?? '').split(',').includes(option.value)}
                                onChange={(event) => {
                                  const current = (values[condition.serialNo] ?? '').split(',').filter(Boolean)
                                  const next = event.target.checked ? [...current, option.value] : current.filter((item) => item !== option.value)
                                  setValues((state) => ({ ...state, [condition.serialNo]: next.join(',') }))
                                }}
                              />
                              <span className="form-check-label">{option.label}</span>
                            </label>
                          ))}
                        </div>
                      )}
                    </div>
                  </div>
                )
              })}
            </div>
          </div>
        </section>
      )}

      <ErpListCard
        ariaLabel={`${def.title}查询结果`}
        search={null}
        actions={(
          <ErpCommandBar
            ariaLabel="报表结果工具栏"
            items={[
              { action: 'columns', label: '选择列', visible: resultColumns.length > 1, onClick: () => setColumnSelectorOpen(true) },
              { action: 'export', label: '导出 CSV', visible: queryKey > 0, onClick: () => void handleExport() },
              { action: 'refresh', label: '刷新', visible: queryKey > 0, loading: result.isFetching, onClick: () => void result.refetch() },
            ]}
          />
        )}
        footer={<ErpPagination total={result.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} pageSizes={PAGE_SIZES} onPageSizeChange={(size) => { setPageSize(size); setPage(1) }} />}
      >
        {queryKey === 0 ? (
          <EmptyState title="尚未查询" description="设置查询条件后点击「查询」，结果将显示在这里。" />
        ) : result.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void result.refetch()} />
        ) : result.isPending ? (
          <LoadingState label="正在查询…" />
        ) : rowCount === 0 ? (
          <EmptyState title="没有符合条件的数据" description="请放宽查询条件后重新查询。" />
        ) : (
          <ErpTable
            columns={columns}
            data={result.data?.rows ?? []}
            getRowId={getRowId}
            columnVisibility={columnVisibility}
            resizable
            storageKey={`report-viewer-${moduleId}`}
          />
        )}
      </ErpListCard>

      <ErpColumnSelector
        open={columnSelectorOpen}
        title="选择列"
        groups={columnGroups}
        onClose={() => setColumnSelectorOpen(false)}
        onSave={saveVisibleColumns}
      />
    </div>
  )
}

function DataSelectCondition({ moduleId, reportId, condition, labelId, value, onChange }: { moduleId: string; reportId: string; condition: ReportCondition; labelId: string; value: string; onChange: (value: string) => void }) {
  const options = useQuery({
    queryKey: ['report', moduleId, 'condition-options', condition.serialNo, reportId],
    // 带上报表身份：一模块多报表时，选项可能随报表的数据源不同（丢掉它就等于按默认报表回答）
    queryFn: () => apiClient.get<ReportOption[]>(`/reports/${moduleId}/condition-options/${condition.serialNo}${reportId ? `?reportId=${encodeURIComponent(reportId)}` : ''}`),
    enabled: condition.selectSource != null,
  })
  return (
    <select className="form-select form-select-sm" aria-labelledby={labelId} value={value} onChange={(event) => onChange(event.target.value)}>
      <option value="">全部</option>
      {(options.data ?? []).map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
    </select>
  )
}

/**
 * F_TYPE 5 数据源多选：选项来自数据源（condition-options 接口，同 type 3），
 * 取值为逗号分隔的实际值（同 type 4 固定多选语义）。
 */
function DataMultiSelectCondition({ moduleId, condition, labelId, value, onChange }: { moduleId: string; condition: ReportCondition; labelId: string; value: string; onChange: (value: string) => void }) {
  const options = useQuery({
    queryKey: ['report', moduleId, 'condition-options', condition.serialNo],
    queryFn: () => apiClient.get<ReportOption[]>(`/reports/${moduleId}/condition-options/${condition.serialNo}`),
    enabled: condition.selectSource != null,
  })
  const selected = value.split(',').filter(Boolean)
  return (
    <div className="d-flex flex-wrap gap-2 small" role="group" aria-labelledby={labelId}>
      {(options.data ?? []).map((option) => (
        <label className="form-check" key={option.value}>
          <input
            className="form-check-input"
            type="checkbox"
            checked={selected.includes(option.value)}
            onChange={(event) => {
              const next = event.target.checked ? [...selected, option.value] : selected.filter((item) => item !== option.value)
              onChange(next.join(','))
            }}
          />
          <span className="form-check-label">{option.label}</span>
        </label>
      ))}
    </div>
  )
}

/** 读取本地列显隐（按模块 + 报表）。解析失败或未存过返回 null。 */
function readStoredColumns(key: string): string[] | null {
  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return null
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return null
    return parsed.filter((item): item is string => typeof item === 'string')
  } catch {
    return null
  }
}

/** 写入本地列显隐；隐私模式/配额受限时静默降级（仅本次会话生效）。 */
function storeColumns(key: string, visibleKeys: string[]): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(visibleKeys))
  } catch {
    // 忽略：列显隐是本地偏好，写不进去不影响本次使用
  }
}
