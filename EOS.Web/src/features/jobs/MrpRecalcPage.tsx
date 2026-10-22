import { apiClient } from '../../services/api'
import { JobCard } from './JobCard'
import { useJobAction } from './useJobAction'

interface MrpRecalcResult {
  job: string
  elapsedMs: number
}

/** 产品可用库存重计（模块 230901）：重算全部产品的可用库存与 MRP 数量。 */
export function MrpRecalcPage() {
  const action = useJobAction(() => apiClient.post<MrpRecalcResult>('/jobs/mrp-recalc'))

  return (
    <div className="erp-base-page">
      <JobCard
        ariaLabel="产品可用库存重计"
        title="产品可用库存重计"
        description="重算全部产品的可用库存与 MRP 数量：库存量取 MRP 库别的结存，未送货、未入库、未到货、在购量按各自来源单据汇总回填。日常由库存移动自动维护；仅在数据异常、切换系统参数 PRO_MRP 或批量导入后手工执行一次。"
        actionLabel="执行重计"
        running={action.running}
        onRun={() => void action.execute()}
        error={action.error}
        result={action.result ? <>重计完成（{action.result.job}），耗时 {(action.result.elapsedMs / 1000).toFixed(1)} 秒。</> : null}
      />
    </div>
  )
}
