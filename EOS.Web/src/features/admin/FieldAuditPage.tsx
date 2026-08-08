import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

interface FieldAuditRow {
  T_ID: string
  F_ID: string
  F_TYPE: string | null
  F_DESC?: string | null
  IS_VIRTUAL?: boolean | null
}

interface FieldAuditResult {
  kind: string
  rows: FieldAuditRow[]
  total: number
  limited: boolean
}

const kinds = [
  { key: 'unmanaged', label: '未受管理字段', desc: '物理表存在但 FIELDS 无元数据的列' },
  { key: 'orphan', label: '未知管理字段', desc: 'FIELDS 有元数据但物理表不存在的列' },
] as const

/** 读写数据表信息（2303 受控只读版）：物理列与 FIELDS 元数据差集审计 */
export function FieldAuditPage() {
  const [kind, setKind] = useState<'unmanaged' | 'orphan'>('unmanaged')
  const result = useQuery({
    queryKey: ['field-audit', kind],
    queryFn: () => apiClient.get<FieldAuditResult>(`/table-data/field-audit/${kind}`),
  })

  const columns = useMemo<ColumnDef<FieldAuditRow, unknown>[]>(() => [
    { accessorKey: 'T_ID', header: '数据表名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'F_ID', header: '字段名', cell: (info) => <span className="font-monospace">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'F_TYPE', header: '字段类型', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '—')}</span> },
    { accessorKey: 'F_DESC', header: '字段说明' },
    { accessorKey: 'IS_VIRTUAL', header: '虚拟字段', cell: (info) => (info.getValue() ? '是' : '—') },
  ], [])

  const errorMessage = result.error instanceof ApiError ? result.error.body.message : '发生未知错误，请稍后重试。'

  return (
    <div className="d-grid gap-2">
      <ErpListCard
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
      >
        {result.isPending ? <LoadingState label="正在加载字段审计…" /> : result.isError ? (
          <ErrorState message={errorMessage} onRetry={() => void result.refetch()} />
        ) : (
          <ErpTable
            columns={columns}
            data={result.data?.rows ?? []}
            getRowId={(row: FieldAuditRow) => `${row.T_ID}.${row.F_ID}`}
            empty={<div className="text-center text-secondary py-4">该视图无差异数据</div>}
            resizable
            storageKey="field-audit"
          />
        )}
      </ErpListCard>
    </div>
  )
}
