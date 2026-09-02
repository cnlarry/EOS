import { IconRefresh } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

interface CarSummaryResult {
  columns: string[]
  rows: Record<string, unknown>[]
  count: number
}

/** 车辆汇总分析表（199901）：车牌/月份范围 → RPT_CAR_SUMMARY 受控执行 → HTML 表格 */
export function CarSummaryPage() {
  const [carIdFrom, setCarIdFrom] = useState('')
  const [carIdTo, setCarIdTo] = useState('')
  const [monthFrom, setMonthFrom] = useState('2026-01')
  const [monthTo, setMonthTo] = useState('2026-12')
  const [queryKey, setQueryKey] = useState(0)

  const result = useQuery({
    queryKey: ['car-summary', carIdFrom, carIdTo, monthFrom, monthTo, queryKey],
    queryFn: () => apiClient.get<CarSummaryResult>('/car-summary', {
      query: { carIdFrom, carIdTo, monthFrom, monthTo },
    }),
    enabled: queryKey > 0,
    placeholderData: (previous: CarSummaryResult | undefined) => previous,
  })

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(() =>
    (result.data?.columns ?? []).map((column) => ({
      accessorKey: column,
      header: () => <span className="font-monospace small">{column}</span>,
      cell: (info) => String(info.getValue() ?? '—'),
      meta: { cellClassName: column.includes('AMOUNT') || column.includes('METER') || column.includes('TOTAL') ? 'text-end' : undefined },
    })), [result.data])

  const runQuery = () => setQueryKey((current) => current + 1)
  const errorMessage = describeApiError(result.error, '发生未知错误，请稍后重试。')

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="车辆汇总分析表"
        search={null}
        actions={<Button size="sm" icon={<IconRefresh size={16} />} onClick={runQuery} disabled={queryKey === 0}>查询</Button>}
        header={(
          <div className="card mb-2">
            <div className="card-body py-2">
              <div className="row g-2 align-items-end">
                <div className="col-md-2">
                  <label className="form-label mb-1 small">车牌起</label>
                  <input className="form-control form-control-sm" value={carIdFrom} onChange={(event) => setCarIdFrom(event.target.value)} />
                </div>
                <div className="col-md-2">
                  <label className="form-label mb-1 small">车牌止</label>
                  <input className="form-control form-control-sm" value={carIdTo} onChange={(event) => setCarIdTo(event.target.value)} />
                </div>
                <div className="col-md-2">
                  <label className="form-label mb-1 small">月份起（yyyy-MM）</label>
                  <input className="form-control form-control-sm" value={monthFrom} onChange={(event) => setMonthFrom(event.target.value)} />
                </div>
                <div className="col-md-2">
                  <label className="form-label mb-1 small">月份止（yyyy-MM）</label>
                  <input className="form-control form-control-sm" value={monthTo} onChange={(event) => setMonthTo(event.target.value)} />
                </div>
                <div className="col-md-2">
                  <Button size="sm" onClick={runQuery}>查询汇总</Button>
                </div>
                <div className="col-md-2 text-secondary small">共 {result.data?.count ?? 0} 行</div>
              </div>
            </div>
          </div>
        )}
      >
        {queryKey === 0 ? <div className="text-center text-secondary py-4">设置车牌与月份范围后点击「查询汇总」。</div> : result.isPending ? (
          <LoadingState label="正在汇总车辆数据…" />
        ) : result.isError ? (
          <ErrorState message={errorMessage} onRetry={runQuery} />
        ) : (
          <ErpTable
            columns={columns}
            data={result.data?.rows ?? []}
            getRowId={(row) => `${String(row.CAR_ID ?? '')}.${String(row.YM_RPT ?? '')}`}
            empty={<div className="text-center text-secondary py-4">该范围内无车辆汇总数据</div>}
            resizable
            storageKey="car-summary"
          />
        )}
      </ErpListCard>
    </div>
  )
}
