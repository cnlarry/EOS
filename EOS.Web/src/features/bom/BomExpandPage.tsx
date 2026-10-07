import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'

interface BomRow { level: number; proNo: string; elementProNo: string; proName: string; proSpec: string; unitId: string; elementQty: number; baseQty: number; lostRate: number; requiredQty: number }
interface BomResult { proNo: string; qty: number; maxLevel: number; rows: BomRow[] }

const bomColumns: ColumnDef<BomRow, unknown>[] = [
  { accessorKey: 'level', header: '阶', cell: (info) => <span>{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'proNo', header: '父料号', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'elementProNo', header: '元件料号', cell: (info) => <span className="font-monospace small">{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'proName', header: '元件名称', cell: (info) => <span>{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'proSpec', header: '规格', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'unitId', header: '单位', cell: (info) => <span>{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'elementQty', header: '用量', meta: { className: 'text-end' }, cell: (info) => <span>{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'baseQty', header: '基准', meta: { className: 'text-end' }, cell: (info) => <span>{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'lostRate', header: '损耗率', meta: { className: 'text-end' }, cell: (info) => <span>{String(info.getValue() ?? '')}</span> },
  { accessorKey: 'requiredQty', header: '需求数量', meta: { className: 'text-end' }, cell: (info) => <span className="fw-medium">{String(info.getValue() ?? '')}</span> },
]

export function BomExpandPage() {
  const [proNo, setProNo] = useState('')
  const [qty, setQty] = useState('1')
  const [maxLevel, setMaxLevel] = useState('99')
  const [queryKey, setQueryKey] = useState(0)
  const result = useQuery({
    queryKey: ['bom', 'expand', proNo, qty, maxLevel, queryKey],
    queryFn: () => apiClient.get<BomResult>(`/bom/expand?proNo=${encodeURIComponent(proNo)}&qty=${qty}&maxLevel=${maxLevel}`),
    enabled: queryKey > 0,
  })

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="BOM 展开表"
        search={null}
        actions={<>
          <Button size="sm" onClick={() => setQueryKey((current) => current + 1)} disabled={!proNo.trim()}>展开</Button>
        </>}
      >
        <div className="card mb-2">
          <div className="card-body py-2">
            <div className="row g-2 align-items-end">
              <div className="col-md-4">
                <label className="form-label mb-1 small">产品编号</label>
                <input className="form-control form-control-sm" value={proNo} onChange={(event) => setProNo(event.target.value)} placeholder="如 23-66-2365" />
              </div>
              <div className="col-md-2">
                <label className="form-label mb-1 small">产品数量</label>
                <input className="form-control form-control-sm" type="number" value={qty} onChange={(event) => setQty(event.target.value)} />
              </div>
              <div className="col-md-2">
                <label className="form-label mb-1 small">展开阶数</label>
                <input className="form-control form-control-sm" type="number" value={maxLevel} onChange={(event) => setMaxLevel(event.target.value)} />
              </div>
            </div>
          </div>
        </div>
        {result.isPending ? <LoadingState label="正在展开 BOM…" /> : result.isError ? <ErrorState message="展开失败，请检查产品编号。" onRetry={() => void result.refetch()} /> : (
          <ErpTable
            columns={bomColumns}
            data={result.data?.rows ?? []}
            getRowId={(row, index) => `${row.elementProNo}-${index}`}
            resizable
            dense
            storageKey="bom-expand"
            empty={<div className="text-center text-secondary py-4">该产品暂无 BOM 或未找到。</div>}
          />
        )}
      </ErpListCard>
    </div>
  )
}
