import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
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
interface ReportCondition { serialNo: number; field: string | null; desc: string; type: number; expression: string | null; defaultValue: string | null; parameterName: string | null; options: ReportOption[]; selectSource: ReportSelectSource | null }
interface ReportColumn { key: string; label: string; dataType: string }
interface ReportDefinition { moduleId: number; title: string; masterTable: string; conditions: ReportCondition[]; columns: ReportColumn[]; masterPkOrder: string[]; spName: string | null; spParameters: ReportSpParameter[] }
interface ReportSpParameter { name: string; dataType: string; maxLength: number }
interface ReportQueryResult { rows: Record<string, unknown>[]; total: number; page: number; pageSize: number }

export function ReportViewerPage() {
  const { moduleId = '' } = useParams()
  const [values, setValues] = useState<Record<number, string>>({})
  const [valuesTo, setValuesTo] = useState<Record<number, string>>({})
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [queryKey, setQueryKey] = useState(0)

  const definition = useQuery({
    queryKey: ['report', moduleId, 'definition'],
    queryFn: () => apiClient.get<ReportDefinition>(`/reports/${moduleId}/definition`),
  })
  const result = useQuery({
    queryKey: ['report', moduleId, 'result', page, pageSize, queryKey],
    queryFn: () => apiClient.post<ReportQueryResult>(`/reports/${moduleId}/query?page=${page}&pageSize=${pageSize}`, { values, valuesTo }),
    enabled: definition.isSuccess && queryKey > 0,
    placeholderData: (previous: ReportQueryResult | undefined) => previous,
  })

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

  if (definition.isPending) return <LoadingState label="正在加载报表定义…" />
  if (definition.isError) return <ErrorState message={definition.error instanceof ApiError ? definition.error.body.message : '报表定义加载失败。'} onRetry={() => void definition.refetch()} />
  const def = definition.data!
  const errorMessage = result.error instanceof ApiError ? result.error.body.message : '查询失败，请重试。'

  return (
    <div className="d-grid gap-2 erp-report-page">
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
