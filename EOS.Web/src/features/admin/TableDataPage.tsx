import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

interface TableInfo {
  table: string
  desc: string
}

interface TableDataResult {
  table: string
  primaryKeys: string[]
  columns: string[]
  rows: Record<string, unknown>[]
  total: number
  page: number
  pageSize: number
}

/**
 * 数据表数据维护（2310/2312 受控只读版）：
 * 从 TABLES 白名单中选表，分页查看主表数据；写操作经各模块统一表单/领域服务。
 */
export function TableDataPage() {
  const [table, setTable] = useState('')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)

  const tables = useQuery({ queryKey: ['table-data', 'tables'], queryFn: () => apiClient.get<TableInfo[]>('/table-data/tables') })
  const allTables = tables.data ?? []
  const selected = allTables.find((item) => item.table === table)
  const keywordLower = keyword.toLowerCase()
  const filtered = allTables.filter((item) =>
    !keywordLower || item.table.toLowerCase().includes(keywordLower) || item.desc.toLowerCase().includes(keywordLower))
  const options = selected && !filtered.some((item) => item.table === selected.table) ? [...filtered, selected] : filtered

  const data = useQuery({
    queryKey: ['table-data', table, page, pageSize],
    queryFn: () => apiClient.get<TableDataResult>(`/table-data/${encodeURIComponent(table)}?page=${page}&pageSize=${pageSize}`),
    enabled: table !== '',
    placeholderData: (previous: TableDataResult | undefined) => previous,
  })

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(() =>
    (data.data?.columns ?? []).map((column) => ({
      accessorKey: column,
      header: () => (
        <span className="font-monospace small">
          {column}
          {data.data?.primaryKeys.includes(column) && <span className="badge text-bg-warning ms-1" title="主键">PK</span>}
        </span>
      ),
    })), [data.data])

  const switchTable = (next: string) => {
    setTable(next)
    setPage(1)
  }

  const errorMessage = data.error instanceof ApiError ? data.error.body.message : '发生未知错误，请稍后重试。'

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="数据表数据维护"
        search={(
          <div className="d-flex gap-2 align-items-center">
            <select
              className="form-select form-select-sm w-auto"
              value={table}
              aria-label="选择数据表"
              onChange={(event) => switchTable(event.target.value)}
            >
              <option value="">请选择数据表</option>
              {options.map((item) => <option key={item.table} value={item.table}>{item.desc}（{item.table}）</option>)}
            </select>
            <ErpSearchBox value={keyword} onChange={setKeyword} placeholder="搜索表名或描述" ariaLabel="搜索数据表" />
          </div>
        )}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void tables.refetch()}>刷新</Button>}
        header={selected ? (
          <div className="px-3 pt-2 small text-secondary">
            当前表：<span className="font-monospace fw-semibold">{selected.table}</span>（{selected.desc}） · 主键：
            <span className="font-monospace">{data.data?.primaryKeys.join('、') || '—'}</span> · 只读查看
          </div>
        ) : undefined}
        footer={selected ? (
          <ErpPagination
            total={data.data?.total ?? 0}
            page={page}
            pageSize={pageSize}
            onPageChange={setPage}
            pageSizes={[50, 100, 200]}
            onPageSizeChange={(size) => { setPageSize(size); setPage(1) }}
          />
        ) : undefined}
      >
        {tables.isPending ? <LoadingState label="正在加载数据表清单…" /> : tables.isError ? (
          <ErrorState message="数据表清单加载失败。" onRetry={() => void tables.refetch()} />
        ) : !selected ? (
          <EmptyState title="请选择数据表" description="从 TABLES 白名单中选择要查看的表。" />
        ) : data.isPending ? (
          <LoadingState label="正在加载表数据…" />
        ) : data.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void data.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={data.data?.rows ?? []}
            getRowId={(row) => (data.data?.primaryKeys ?? []).map((key) => String(row[key] ?? '')).join('|') || 'row'}
            empty={<EmptyState title="该表暂无数据" description="切换其他数据表或调整筛选后重试。" />}
            resizable
            storageKey="table-data-browser"
          />
        )}
      </ErpListCard>
    </div>
  )
}
