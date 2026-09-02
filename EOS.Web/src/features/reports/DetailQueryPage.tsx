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
import { formatFieldValue } from '../document-workbench/fieldFormat'
import { useAuth } from '../auth/authContext'
import { moduleReadPermission } from '../auth/modulePermissions'
import { describeApiError } from '../../lib/errors'

interface DetailColumn { key: string; label: string; dataType: string; displayFormat?: string | null }
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

/** 跨表明细查询（14996/14998/170297）：受控只读列表，服务端固定 SQL + 参数化日期边界。 */
export function DetailQueryPage() {
  const { moduleId = '' } = useParams()
  const { hasPermission } = useAuth()
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const canRead = hasPermission(moduleReadPermission(moduleId))
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
        const text = formatFieldValue(info.getValue(), field.dataType, field.displayFormat ?? null)
        return text || '—'
      },
    })), [result.data])
  const errorMessage = describeApiError(result.error, '发生未知错误，请稍后重试。')
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
