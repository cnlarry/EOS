import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'

interface BomRow { level: number; proNo: string; elementProNo: string; proName: string; proSpec: string; unitId: string; elementQty: number; baseQty: number; lostRate: number; requiredQty: number }
interface BomResult { proNo: string; qty: number; maxLevel: number; rows: BomRow[] }

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
          <div className="table-responsive">
            <table className="table table-sm table-vcenter card-table">
              <thead><tr><th>阶</th><th>父料号</th><th>元件料号</th><th>元件名称</th><th>规格</th><th>单位</th><th className="text-end">用量</th><th className="text-end">基准</th><th className="text-end">损耗率</th><th className="text-end">需求数量</th></tr></thead>
              <tbody>
                {result.data?.rows.map((row, index) => (
                  <tr key={index}>
                    <td className="text-center">{row.level}</td>
                    <td className="font-monospace small">{row.proNo}</td>
                    <td className="font-monospace small">{row.elementProNo}</td>
                    <td>{row.proName}</td>
                    <td className="small text-secondary">{row.proSpec}</td>
                    <td>{row.unitId}</td>
                    <td className="text-end">{row.elementQty}</td>
                    <td className="text-end">{row.baseQty}</td>
                    <td className="text-end">{row.lostRate}</td>
                    <td className="text-end fw-medium">{row.requiredQty}</td>
                  </tr>
                ))}
                {result.data?.rows.length === 0 && <tr><td colSpan={10} className="text-center text-secondary py-4">该产品暂无 BOM 或未找到。</td></tr>}
              </tbody>
            </table>
          </div>
        )}
      </ErpListCard>
    </div>
  )
}
