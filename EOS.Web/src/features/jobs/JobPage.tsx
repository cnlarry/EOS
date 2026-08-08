import { useState } from 'react'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'

export function JobPage() {
  const [running, setRunning] = useState(false)
  const [result, setResult] = useState<{ sproc: string; elapsedMs: number } | null>(null)

  const runMrpRecalc = async () => {
    setRunning(true)
    setResult(null)
    try {
      const data = await apiClient.post<{ sproc: string; elapsedMs: number }>('/jobs/mrp-recalc')
      setResult(data)
    } catch (error) {
      window.alert(error instanceof Error ? `重算失败：${error.message}` : '重算失败。')
    } finally {
      setRunning(false)
    }
  }

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="后台作业"
        search={null}
        actions={<>
          <Button size="sm" onClick={() => void runMrpRecalc()} loading={running}>执行产品可用库存重计</Button>
        </>}
      >
        <div className="p-2">
          <p className="text-secondary small mb-2">产品可用库存重计（230901）：重算全部产品的可用库存/MRP 数量，由旧系统存储过程受控执行。</p>
          {result && <div className="alert alert-success py-2 mb-0">重计完成（{result.sproc}），耗时 {(result.elapsedMs / 1000).toFixed(1)} 秒。</div>}
        </div>
      </ErpListCard>
    </div>
  )
}
