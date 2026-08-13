import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import type { ColumnDef } from '@tanstack/react-table'

interface ReportOption { label: string; value: string }
interface ReportSelectSource { table: string; idColumn: string; valueColumn: string }
interface ReportCondition { serialNo: number; field: string | null; desc: string; type: number; expression: string | null; defaultValue: string | null; parameterName: string | null; options: ReportOption[]; selectSource: ReportSelectSource | null; defaultValueTo: string | null }
interface ReportColumn { key: string; label: string; dataType: string }
interface ReportDefinition { moduleId: number; title: string; masterTable: string; conditions: ReportCondition[]; columns: ReportColumn[]; masterPkOrder: string[]; spName: string | null; spParameters: ReportSpParameter[] }
interface ReportSpParameter { name: string; dataType: string; maxLength: number }
interface ReportQueryResult { rows: Record<string, unknown>[]; total: number; page: number; pageSize: number }
interface ReportPrintOption { reportId: string; reportName: string; headerId: string | null; tailId: string | null; footerText: string | null; isoNo: string | null; defaultPaper: string | null; isDefault: boolean }
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

export function ReportViewerPage() {
  const { moduleId = '' } = useParams()
  const [values, setValues] = useState<Record<number, string>>({})
  const [valuesTo, setValuesTo] = useState<Record<number, string>>({})
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [queryKey, setQueryKey] = useState(0)
  const [reportId, setReportId] = useState('')
  const [headerId, setHeaderId] = useState('')
  const [tailId, setTailId] = useState('')
  const [sortSerialNo, setSortSerialNo] = useState<number | null>(null)
  const [sortDirect, setSortDirect] = useState<'asc' | 'desc'>('asc')
  const [showGroup, setShowGroup] = useState(true)
  const [showDetail, setShowDetail] = useState(true)
  const [printing, setPrinting] = useState(false)
  const [printError, setPrintError] = useState<string | null>(null)
  const settingsApplied = useRef(false)
  const defaultsApplied = useRef(false)

  const definition = useQuery({
    queryKey: ['report', moduleId, 'definition'],
    queryFn: () => apiClient.get<ReportDefinition>(`/reports/${moduleId}/definition`),
  })
  const printSettings = useQuery({
    queryKey: ['report', moduleId, 'print-settings'],
    queryFn: () => apiClient.get<ReportPrintSettingsData>(`/reports/${moduleId}/print-settings`),
  })
  const result = useQuery({
    queryKey: ['report', moduleId, 'result', page, pageSize, queryKey],
    queryFn: () => apiClient.post<ReportQueryResult>(`/reports/${moduleId}/query?page=${page}&pageSize=${pageSize}`, { values, valuesTo }),
    enabled: definition.isSuccess && queryKey > 0,
    placeholderData: (previous: ReportQueryResult | undefined) => previous,
  })

  useEffect(() => {
    if (settingsApplied.current || !printSettings.data) return
    settingsApplied.current = true
    const user = printSettings.data.userSettings
    const report = user?.reportId
      ? printSettings.data.reports.find((item) => item.reportId === user.reportId)
      : printSettings.data.reports.find((item) => item.isDefault) ?? printSettings.data.reports[0]
    setReportId(report?.reportId ?? '')
    setHeaderId(user?.headerId ?? report?.headerId ?? '')
    setTailId(user?.tailId ?? report?.tailId ?? '')
    setSortSerialNo(user?.sortSerialNo ?? null)
    setSortDirect(user ? (user.sortAsc ? 'asc' : 'desc') : 'asc')
    setShowGroup(user?.showGroup ?? true)
    setShowDetail(user?.showDetail ?? true)
  }, [printSettings.data])

  useEffect(() => {
    if (defaultsApplied.current || !definition.data) return
    defaultsApplied.current = true
    const next: Record<number, string> = {}
    const nextTo: Record<number, string> = {}
    for (const condition of definition.data.conditions) {
      if (condition.defaultValue) next[condition.serialNo] = condition.defaultValue
      if (condition.defaultValueTo) nextTo[condition.serialNo] = condition.defaultValueTo
    }
    setValues(next)
    setValuesTo(nextTo)
  }, [definition.data])

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(
    () => (definition.data?.columns ?? []).map((column) => ({
      accessorKey: column.key,
      header: column.label,
      cell: (info) => String(info.getValue() ?? '—'),
      meta: { cellClassName: column.dataType.includes('float') || column.dataType.includes('int') ? 'text-end' : undefined },
    })),
    [definition.data],
  )

  const runQuery = () => { setPage(1); setQueryKey((current) => current + 1) }

  const handleExport = async () => {
    if (!definition.data) return
    try {
      const blob = await apiClient.postFile(`/reports/${moduleId}/export`, { values, valuesTo })
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${definition.data.title}.csv`
      document.body.appendChild(anchor)
      anchor.click()
      anchor.remove()
      URL.revokeObjectURL(url)
    } catch (error) {
      window.alert(error instanceof Error ? `导出失败：${error.message}` : '导出失败。')
    }
  }

  const selectedHeader = printSettings.data?.headers.find((header) => header.headerId === headerId)
  const sortSchemes = printSettings.data?.sortSchemesByReport[reportId] ?? []
  const hasPrintPermission = (printSettings.data?.reports.length ?? 0) > 0

  const handlePrint = async () => {
    if (!definition.data || !printSettings.data) return
    setPrinting(true)
    setPrintError(null)
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
      setPrintError(error instanceof ApiError ? error.body.message : 'PDF 生成失败，请重试。')
    } finally {
      setPrinting(false)
    }
  }

  if (definition.isPending) return <LoadingState label="正在加载报表定义…" />
  if (definition.isError) return <ErrorState message={definition.error instanceof ApiError ? definition.error.body.message : '报表定义加载失败。'} onRetry={() => void definition.refetch()} />
  const def = definition.data!
  const errorMessage = result.error instanceof ApiError ? result.error.body.message : '查询失败，请重试。'

  return (
    <div className="d-grid gap-2 erp-report-page">
      {hasPrintPermission && (
        <div className="card">
          <div className="card-header py-2 d-flex align-items-center gap-2">
            <span className="fw-semibold small">打印设置</span>
            <span className="text-secondary small">页头/表尾可随时更换，选择后即时预览</span>
          </div>
          <div className="card-body py-2">
            <div className="row g-2 align-items-end">
              <div className="col-md-3 col-lg-2">
                <label className="form-label small mb-1">报表</label>
                <select className="form-select form-select-sm" value={reportId} onChange={(event) => { setReportId(event.target.value); setSortSerialNo(null) }}>
                  {(printSettings.data?.reports ?? []).map((report) => <option key={report.reportId} value={report.reportId}>{report.reportName}</option>)}
                </select>
              </div>
              <div className="col-md-3 col-lg-2">
                <label className="form-label small mb-1">页头</label>
                <select className="form-select form-select-sm" value={headerId} onChange={(event) => setHeaderId(event.target.value)}>
                  <option value="">（报表默认）</option>
                  {(printSettings.data?.headers ?? []).map((header) => <option key={header.headerId} value={header.headerId}>{header.headerName}</option>)}
                </select>
              </div>
              <div className="col-md-3 col-lg-2">
                <label className="form-label small mb-1">表尾</label>
                <select className="form-select form-select-sm" value={tailId} onChange={(event) => setTailId(event.target.value)}>
                  <option value="">（无）</option>
                  {(printSettings.data?.tails ?? []).map((tail) => <option key={tail.tailId} value={tail.tailId}>{tail.tailName}</option>)}
                </select>
              </div>
              <div className="col-md-3 col-lg-2">
                <label className="form-label small mb-1">排序方案</label>
                <select className="form-select form-select-sm" value={sortSerialNo ?? ''} onChange={(event) => setSortSerialNo(event.target.value === '' ? null : Number(event.target.value))}>
                  <option value="">（默认）</option>
                  {sortSchemes.map((scheme) => <option key={scheme.serialNo} value={scheme.serialNo}>{scheme.sortName}</option>)}
                </select>
              </div>
              <div className="col-md-3 col-lg-2">
                <label className="form-label small mb-1">顺序</label>
                <div className="d-flex gap-3">
                  <label className="form-check small">
                    <input className="form-check-input" type="radio" name="sort-direct" checked={sortDirect === 'asc'} onChange={() => setSortDirect('asc')} />
                    <span className="form-check-label">升序</span>
                  </label>
                  <label className="form-check small">
                    <input className="form-check-input" type="radio" name="sort-direct" checked={sortDirect === 'desc'} onChange={() => setSortDirect('desc')} />
                    <span className="form-check-label">降序</span>
                  </label>
                </div>
              </div>
              <div className="col-md-3 col-lg-2">
                <div className="d-flex gap-3">
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
              <div className="col-md-3 col-lg-2">
                <Button size="sm" onClick={() => void handlePrint()} loading={printing}>打印 PDF</Button>
              </div>
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
            {printError && <div className="text-danger small mt-2">{printError}</div>}
          </div>
        </div>
      )}
      <ErpListCard
        ariaLabel={`${def.title}查询`}
        search={null}
        actions={<>
          <Button size="sm" onClick={runQuery}>查询</Button>
          <Button size="sm" onClick={() => void handleExport()}>导出 CSV</Button>
        </>}
        footer={<ErpPagination total={result.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} pageSizes={[50, 100, 200]} onPageSizeChange={(size) => { setPageSize(size); setPage(1) }} />}
      >
        {def.spName ? (
          <div className="card mb-2">
            <div className="card-body py-2">
              <div className="row g-2">
                {def.spParameters.map((parameter, index) => (
                  <div className="col-md-4 col-lg-3" key={parameter.name}>
                    <label className="form-label mb-1 small">{parameter.name}（{parameter.dataType}）</label>
                    <input
                      className="form-control form-control-sm"
                      type={parameter.dataType.includes('datetime') || parameter.dataType.includes('date') ? 'date' : 'text'}
                      value={values[index + 1] ?? ''}
                      onChange={(event) => setValues((current) => ({ ...current, [index + 1]: event.target.value }))}
                    />
                  </div>
                ))}
              </div>
            </div>
          </div>
        ) : def.conditions.length > 0 && (
          <div className="card mb-2">
            <div className="card-body py-2">
              <div className="row g-2">
                {def.conditions.map((condition) => (
                  <div className="col-md-4 col-lg-3" key={condition.serialNo}>
                    <label className="form-label mb-1 small">{condition.desc}</label>
                    {condition.type === 1 && (
                      <div className="d-flex gap-1">
                        <input className="form-control form-control-sm" placeholder="从" value={values[condition.serialNo] ?? ''} onChange={(event) => setValues((current) => ({ ...current, [condition.serialNo]: event.target.value }))} />
                        <input className="form-control form-control-sm" placeholder="到" value={valuesTo[condition.serialNo] ?? ''} onChange={(event) => setValuesTo((current) => ({ ...current, [condition.serialNo]: event.target.value }))} />
                      </div>
                    )}
                    {condition.type === 2 && (
                      <select className="form-select form-select-sm" value={values[condition.serialNo] ?? ''} onChange={(event) => setValues((current) => ({ ...current, [condition.serialNo]: event.target.value }))}>
                        <option value="">全部</option>
                        {condition.options.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                      </select>
                    )}
                    {condition.type === 3 && <DataSelectCondition moduleId={moduleId} condition={condition} value={values[condition.serialNo] ?? ''} onChange={(value) => setValues((current) => ({ ...current, [condition.serialNo]: value }))} />}
                    {condition.type === 4 && (
                      <div className="d-flex flex-wrap gap-2 small">
                        {condition.options.map((option) => (
                          <label className="form-check" key={option.value}>
                            <input className="form-check-input" type="checkbox" checked={(values[condition.serialNo] ?? '').split(',').includes(option.value)} onChange={(event) => {
                              const current = (values[condition.serialNo] ?? '').split(',').filter(Boolean)
                              const next = event.target.checked ? [...current, option.value] : current.filter((item) => item !== option.value)
                              setValues((state) => ({ ...state, [condition.serialNo]: next.join(',') }))
                            }} />
                            <span className="form-check-label">{option.label}</span>
                          </label>
                        ))}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </div>
          </div>
        )}
        {result.isPending ? <LoadingState label="正在查询…" /> : result.isError ? <ErrorState message={errorMessage} onRetry={() => void result.refetch()} /> : (
          <ErpTable columns={columns} data={result.data?.rows ?? []} getRowId={(row) => Object.values(row).slice(0, 2).join('-') || 'row'} empty={<div className="text-center text-secondary py-4">点击「查询」查看报表数据</div>} />
        )}
      </ErpListCard>
    </div>
  )
}

function DataSelectCondition({ moduleId, condition, value, onChange }: { moduleId: string; condition: ReportCondition; value: string; onChange: (value: string) => void }) {
  const options = useQuery({
    queryKey: ['report', moduleId, 'condition-options', condition.serialNo],
    queryFn: () => apiClient.get<ReportOption[]>(`/reports/${moduleId}/condition-options/${condition.serialNo}`),
    enabled: condition.selectSource != null,
  })
  return (
    <select className="form-select form-select-sm" value={value} onChange={(event) => onChange(event.target.value)}>
      <option value="">全部</option>
      {(options.data ?? []).map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
    </select>
  )
}
