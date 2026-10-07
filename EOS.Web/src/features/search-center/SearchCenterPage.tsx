import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { formatFieldValue } from '../document-workbench/fieldFormat'
import type { ColumnDef } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'

interface SearchableModule { moduleId: number; title: string; masterTable: string; detailTable: string | null; searchMaster: boolean; searchDetail: boolean }
interface SearchField { key: string; label: string; dataType: string; displayFormat?: string | null }
interface SearchDefinition { moduleId: number; title: string; table: string; fields: SearchField[]; columns: SearchField[]; pkOrder: string[] }
interface SearchResult { rows: Record<string, unknown>[]; total: number; page: number; pageSize: number }

export function SearchCenterPage() {
  const { moduleId = '' } = useParams()
  const [selectedModule, setSelectedModule] = useState(moduleId || '')
  const [table, setTable] = useState('master')
  const [field, setField] = useState('')
  const [value, setValue] = useState('')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [queryKey, setQueryKey] = useState(0)

  const modules = useQuery({ queryKey: ['search-center', 'modules'], queryFn: () => apiClient.get<SearchableModule[]>('/search-center/modules') })
  const activeModule = modules.data?.find((item) => String(item.moduleId) === (selectedModule || moduleId))
  const effectiveTable = activeModule && !activeModule.searchMaster && activeModule.searchDetail ? 'detail' : table
  const definition = useQuery({
    queryKey: ['search-center', activeModule?.moduleId, effectiveTable, 'definition'],
    queryFn: () => apiClient.get<SearchDefinition>(`/search-center/${activeModule?.moduleId}/definition?table=${effectiveTable === 'detail' ? activeModule?.detailTable : activeModule?.masterTable}`),
    enabled: activeModule != null,
  })
  const result = useQuery({
    queryKey: ['search-center', activeModule?.moduleId, effectiveTable, field, value, keyword, page, pageSize, queryKey],
    queryFn: () => apiClient.post<SearchResult>(`/search-center/${activeModule?.moduleId}/query?page=${page}&pageSize=${pageSize}`, {
      table: effectiveTable === 'detail' ? activeModule?.detailTable : activeModule?.masterTable,
      field: field || null,
      value: value || null,
      keyword: keyword || null,
    }),
    enabled: activeModule != null && queryKey > 0,
    placeholderData: (previous: SearchResult | undefined) => previous,
  })

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(
    () => (definition.data?.columns ?? []).map((column) => ({
      accessorKey: column.key,
      header: column.label,
      cell: (info) => formatFieldValue(info.getValue(), column.dataType, column.displayFormat ?? null) || '—',
      meta: { cellClassName: column.dataType.includes('float') || column.dataType.includes('int') ? 'text-end' : undefined },
    })),
    [definition.data],
  )

  const runQuery = () => { setPage(1); setQueryKey((current) => current + 1) }
  const switchModule = (moduleId: string) => {
    setSelectedModule(moduleId)
    setTable('master')
    setField('')
    setValue('')
    setQueryKey(0)
  }

  if (modules.isPending) return <LoadingState label="正在加载查询中心…" />
  if (modules.isError) return <ErrorState message="查询中心加载失败。" onRetry={() => void modules.refetch()} />

  return (
    <div className="d-grid erp-search-center-page">
      <ErpListCard
        ariaLabel="通用查询"
        search={null}
        actions={<>
          <Button size="sm" onClick={runQuery} disabled={!activeModule}>查询</Button>
        </>}
        footer={<ErpPagination total={result.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} pageSizes={[50, 100, 200]} onPageSizeChange={(size) => { setPageSize(size); setPage(1) }} />}
      >
        <div className="card mb-2">
          <div className="card-body py-2">
            <div className="row g-2 align-items-end">
              <div className="col-md-3">
                <label className="form-label mb-1 small">查询模块</label>
                <select className="form-select form-select-sm" value={selectedModule || moduleId} onChange={(event) => switchModule(event.target.value)}>
                  <option value="">请选择</option>
                  {modules.data?.map((item) => <option key={item.moduleId} value={item.moduleId}>{item.title}（{item.moduleId}）</option>)}
                </select>
              </div>
              {activeModule?.searchMaster && activeModule.searchDetail && (
                <div className="col-md-2">
                  <label className="form-label mb-1 small">数据表</label>
                  <select className="form-select form-select-sm" value={table} onChange={(event) => { setTable(event.target.value); setField(''); setQueryKey(0) }}>
                    <option value="master">主表</option>
                    <option value="detail">明细表</option>
                  </select>
                </div>
              )}
              <div className="col-md-3">
                <label className="form-label mb-1 small">字段</label>
                <select className="form-select form-select-sm" value={field} onChange={(event) => setField(event.target.value)}>
                  <option value="">全部字段</option>
                  {definition.data?.fields.map((item) => <option key={item.key} value={item.key}>{item.label}</option>)}
                </select>
              </div>
              <div className="col-md-2">
                <label className="form-label mb-1 small">字段值</label>
                <input className="form-control form-control-sm" value={value} onChange={(event) => setValue(event.target.value)} />
              </div>
              <div className="col-md-2">
                <label className="form-label mb-1 small">关键字</label>
                <input className="form-control form-control-sm" value={keyword} onChange={(event) => setKeyword(event.target.value)} placeholder="跨字段模糊搜索" />
              </div>
            </div>
          </div>
        </div>
        {result.isPending ? <LoadingState label="正在查询…" /> : result.isError ? <ErrorState message={describeApiError(result.error, '查询失败。')} onRetry={() => void result.refetch()} /> : (
          <ErpTable columns={columns} data={result.data?.rows ?? []} getRowId={(row) => Object.values(row).slice(0, 2).join('-') || 'row'} empty={<div className="text-center text-secondary py-4">{activeModule ? '点击「查询」开始搜索' : '请先选择查询模块'}</div>} />
        )}
      </ErpListCard>
    </div>
  )
}
