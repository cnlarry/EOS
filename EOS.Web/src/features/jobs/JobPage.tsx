import { useState } from 'react'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'

export function JobPage() {
  const [running, setRunning] = useState(false)
  const [result, setResult] = useState<{ sproc: string; elapsedMs: number } | null>(null)
  const [cardStartDate, setCardStartDate] = useState('2026-08-09')
  const [cardList, setCardList] = useState('admin,CARD001\nuser02,CARD002')
  const [cardResult, setCardResult] = useState<{ updated: number; inserted: number } | null>(null)

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

  const runCardBatch = async () => {
    setRunning(true)
    setCardResult(null)
    try {
      const cards = cardList.split('\n').map((line) => line.trim()).filter(Boolean).map((line) => {
        const [empId, cardId] = line.split(',').map((part) => part.trim())
        return { empId, cardId }
      }).filter((item) => item.empId && item.cardId)
      const data = await apiClient.post<{ updated: number; inserted: number }>('/jobs/card-batch', {
        startDate: cardStartDate,
        cards,
      })
      setCardResult(data)
    } catch (error) {
      window.alert(error instanceof Error ? `发卡失败：${error.message}` : '发卡失败。')
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
        <div className="card m-2">
          <div className="card-body py-2">
            <h2 className="card-title fs-6">员工批量发卡（180218）</h2>
            <div className="row g-2 align-items-end">
              <div className="col-md-2">
                <label className="form-label mb-1 small">生效日期</label>
                <input className="form-control form-control-sm" type="date" value={cardStartDate} onChange={(event) => setCardStartDate(event.target.value)} />
              </div>
              <div className="col-md-8">
                <label className="form-label mb-1 small">员工号,卡号（每行一条）</label>
                <textarea className="form-control form-control-sm font-monospace" rows={3} value={cardList} onChange={(event) => setCardList(event.target.value)} />
              </div>
              <div className="col-md-2">
                <Button size="sm" onClick={() => void runCardBatch()} loading={running}>执行发卡</Button>
              </div>
            </div>
            {cardResult && <div className="alert alert-success py-2 mb-0 mt-2">发卡完成：新增 {cardResult.inserted}，更新 {cardResult.updated}。</div>}
          </div>
        </div>
      </ErpListCard>
    </div>
  )
}
