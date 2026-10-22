import { IconRefresh } from '@tabler/icons-react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { useEffect, useMemo, useState } from 'react'
import { EmptyState } from '../../components/common/AsyncState'
import { ErpListGrid } from '../../components/common/ErpListGrid'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

interface FieldAuditRow {
  T_ID: string
  F_ID: string
  F_TYPE: string | null
  F_DESC?: string | null
}

interface FieldAuditResult {
  kind: string
  rows: FieldAuditRow[]
  total: number
  limited: boolean
}

const kinds = [
  { key: 'unmanaged', label: '未受管理字段', desc: '物理表存在但 FIELDS 无元数据的列' },
  { key: 'orphan', label: '未知管理字段', desc: 'FIELDS 有元数据但物理表不存在的列（虚拟字段没有物理列，不计）' },
] as const

/** 读写数据表信息（2303 受控只读版）：物理列与 FIELDS 元数据差集审计 */
export function FieldAuditPage() {
  const [kind, setKind] = useState<'unmanaged' | 'orphan'>('unmanaged')
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const result = useQuery({
    queryKey: ['field-audit', kind],
    // 切视图时保留上一份结果，避免表格整块消失再出现
    placeholderData: keepPreviousData,
    queryFn: () => apiClient.get<FieldAuditResult>(`/table-data/field-audit/${kind}`),
  })

  // 切换视图时清空行选择，避免上一视图的选中行残留
  useEffect(() => { setRowSelection({}) }, [kind])

  const columns = useMemo<ColumnDef<FieldAuditRow, unknown>[]>(() => [
    {
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
    },
    { accessorKey: 'T_ID', header: '数据表名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'F_ID', header: '字段名', cell: (info) => <span className="font-monospace">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'F_TYPE', header: '字段类型', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'F_DESC', header: '字段说明' },
  ], [])

  const errorMessage = describeApiError(result.error, '发生未知错误，请稍后重试。')

  return (
    <div className="erp-base-page erp-page-fill erp-full-list-page">
      <ErpListGrid
        ariaLabel="字段元数据审计"
        search={(
          <div className="d-flex gap-2 align-items-center">
            <div className="btn-group btn-group-sm">
              {kinds.map((item) => (
                <button
                  key={item.key}
                  type="button"
                  className={`btn ${kind === item.key ? 'btn-primary' : 'btn-outline-primary'}`}
                  onClick={() => setKind(item.key)}
                >
                  {item.label}
                </button>
              ))}
            </div>
            <span className="text-secondary small">{kinds.find((item) => item.key === kind)?.desc}</span>
          </div>
        )}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void result.refetch()}>刷新</Button>}
        header={(
          <div className="px-3 pt-2 small text-secondary">
            共 {result.data?.total ?? 0} 行{result.data?.limited ? `（仅显示前 ${result.data.rows.length} 行）` : ''}
          </div>
        )}
        listId="field-audit"
        columns={columns}
        rows={result.data?.rows}
        rowKey={(row: FieldAuditRow) => `${row.T_ID}.${row.F_ID}`}
        loading={result.isPending}
        loadingLabel="正在加载字段审计…"
        fetching={result.isFetching}
        error={result.isError ? errorMessage : null}
        onRetry={() => void result.refetch()}
        sorting={{ mode: 'client' }}
        selection={{ value: rowSelection, onChange: setRowSelection }}
        empty={<EmptyState title="该视图无差异数据" description="物理列与 FIELDS 元数据一致。" />}
      />
    </div>
  )
}
