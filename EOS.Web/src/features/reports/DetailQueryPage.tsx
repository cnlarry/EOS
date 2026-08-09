import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'

interface DetailColumn { key: string; label: string; dataType: string }
interface DetailQueryResult {
  moduleId: number
  title: string
  columns: DetailColumn[]
  rows: Record<string, unknown>[]
  total: number
  page: number
  pageSize: number
}

const numericTypes = new Set(['int', 'decimal', 'float', 'money', 'numeric', 'double', 'bigint', 'smallint'])

const formatValue = (value: unknown, dataType: string): string => {
  if (value === null || value === undefined) return ''
  if (value instanceof Date) return isNaN(value.getTime()) ? '' : value.toISOString().slice(0, 10)
  const text = String(value).trim()
  if (text.length === 0) return ''
  if (dataType === 'datetime' && /^\d{4}-\d{2}-\d{2}/.test(text)) return text.slice(0, 10)
  return text
}

/** 跨表明细查询（14996/14998/170297）：受控只读列表，服务端固定 SQL + 参数化日期边界。 */
export function DetailQueryPage() {
  const { moduleId = '' } = useParams()
  const { hasPermission } = useAuth()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const canRead = hasPermission(`legacy-module.${moduleId}.read`)
  const result = useQuery({
    queryKey: ['detail-query', moduleId, page, pageSize],
    queryFn: () => apiClient.get<DetailQueryResult>(`/detail-query/${moduleId}`, { query: { page, pageSize } }),
    enabled: canRead,
  })
  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(() =>
    (result.data?.columns ?? []).map((field) => ({
      accessorKey: field.key,
      header: field.label,
      meta: {
        cellClassName: numericTypes.has(field.dataType.toLowerCase()) ? 'text-end' : undefined,
        minWidth: field.key === 'REMARK' ? 180 : 100,
      },
      cell: (info) => {
        const text = formatValue(info.getValue(), field.dataType)
        return text ? <span className={text.length > 24 ? 'erp-cell-ellipsis' : undefined} title={text.length > 24 ? text : undefined}>{text}</span> : '—'
      },
    })), [result.data])
  const errorMessage = result.error instanceof ApiError ? result.error.body.message : '发生未知错误，请稍后重试。'
  if (!canRead) {
    return <section className="card"><div className="card-body text-center py-5 text-danger">当前账号无权访问此模块。</div></section>
  }
  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="明细查询"
        search={null}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void result.refetch()}>刷新</Button>}
        footer={<ErpPagination total={result.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} pageSizes={[10, 20, 50, 100]} onPageSizeChange={(size) => { setPageSize(size); setPage(1) }} />}
      >
        {result.isPending ? <LoadingState label="正在加载明细数据…" /> : result.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void result.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={result.data?.rows ?? []}
            getRowId={(row) => JSON.stringify(row)}
            empty={<div className="text-center text-secondary py-4">没有符合条件的数据。</div>}
            resizable
            storageKey={`detail-query-${moduleId}`}
          />
        )}
      </ErpListCard>
    </div>
  )
}
