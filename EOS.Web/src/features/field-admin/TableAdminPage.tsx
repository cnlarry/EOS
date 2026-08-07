import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

export interface FieldAdminTable {
  tableId: string
  description: string
  kind: string | null
  type: string | null
}

const pageSize = 16

export function TableAdminPage() {
  const navigate = useNavigate()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const tables = useQuery({ queryKey: ['field-admin', 'tables'], queryFn: () => apiClient.get<FieldAdminTable[]>('/admin/tables') })

  const filtered = (tables.data ?? []).filter((item) =>
    !keyword || item.tableId.toLowerCase().includes(keyword.toLowerCase()) || item.description.toLowerCase().includes(keyword.toLowerCase()))
  const rows = filtered.slice((page - 1) * pageSize, page * pageSize)

  const errorMessage = tables.error instanceof ApiError ? tables.error.body.message : '发生未知错误，请稍后重试。'

  const columns = useMemo<ColumnDef<FieldAdminTable, unknown>[]>(() => [
    { accessorKey: 'tableId', header: '表名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'description', header: '描述' },
    { accessorKey: 'type', header: '类型', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'kind', header: '性质', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '—')}</span> },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end' },
      cell: ({ row }) => <Button size="sm" className="erp-table-action" onClick={() => navigate(`/admin/tables/${encodeURIComponent(row.original.tableId)}/fields`)}>管理字段</Button>,
    },
  ], [navigate])

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="数据表维护查询"
        search={<ErpSearchBox value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }} debounceMs={300} placeholder="搜索表名或描述" ariaLabel="搜索数据表" />}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void tables.refetch()}>刷新</Button>}
        footer={!tables.isPending && !tables.isError ? <ErpPagination total={filtered.length} page={page} pageSize={pageSize} onPageChange={setPage} /> : undefined}
      >
        {tables.isPending ? <LoadingState label="正在加载数据表…" /> : tables.isError ? <ErrorState message={errorMessage} onRetry={() => void tables.refetch()} /> : (
          <ErpTable columns={columns} data={rows} resizable storageKey="field-admin-tables" empty={<EmptyState title="没有找到数据表" description="请调整搜索条件后重试。" />} />
        )}
      </ErpListCard>
    </div>
  )
}
