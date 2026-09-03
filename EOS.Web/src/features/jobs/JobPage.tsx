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
  const [attendanceStart, setAttendanceStart] = useState('2026-08-09')
  const [attendanceEnd, setAttendanceEnd] = useState('2026-08-09')
  const [attendanceMode, setAttendanceMode] = useState<'simulate' | 'extract'>('simulate')
  const [attendanceTargets, setAttendanceTargets] = useState('')
  const [attendanceResult, setAttendanceResult] = useState<{ mode: string; employeeCount: number; inserted: number; filled: number } | null>(null)
  const [adjustMonth, setAdjustMonth] = useState('202608')
  const [adjustResult, setAdjustResult] = useState<{ month: string; wageCalcRuns: number; adjustedEmployees: number; clearedDiaryRows: number } | null>(null)

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

  const runAttendanceGenerate = async () => {
    setRunning(true)
    setAttendanceResult(null)
    try {
      const empIds = attendanceTargets.split(/[\n,]/).map((part) => part.trim()).filter(Boolean)
      const data = await apiClient.post<{ mode: string; employeeCount: number; inserted: number; filled: number }>('/jobs/attendance-generate', {
        startDate: attendanceStart,
        endDate: attendanceEnd,
        mode: attendanceMode,
        empIds: empIds.length > 0 ? empIds : null,
        deptId: null,
      })
      setAttendanceResult(data)
    } catch (error) {
      window.alert(error instanceof Error ? `考勤生成失败：${error.message}` : '考勤生成失败。')
    } finally {
      setRunning(false)
    }
  }

  const runAttendanceAdjustWage = async () => {
    setRunning(true)
    setAdjustResult(null)
    try {
      const data = await apiClient.post<{ month: string; wageCalcRuns: number; adjustedEmployees: number; clearedDiaryRows: number }>('/jobs/attendance-adjust-wage', {
        month: adjustMonth,
      })
      setAdjustResult(data)
    } catch (error) {
      window.alert(error instanceof Error ? `依薪资调整考勤失败：${error.message}` : '依薪资调整考勤失败。')
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
          <p className="text-secondary small mb-2">产品可用库存重计（230901）：重算全部产品的可用库存/MRP 数量，由存储过程受控执行。</p>
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
        <div className="card m-2">
          <div className="card-body py-2">
            <h2 className="card-title fs-6">考勤生成（180654 模拟 / 180659 真实抽取）</h2>
            <p className="text-secondary small mb-2">按日期范围生成 HRM_DIARY 空白考勤记录，并按已批核排班的班次时间填充上下班（ON1/OUT1）。完整计算引擎（调休/请假/出差/签卡/随机模拟）已登记技术债。</p>
            <div className="row g-2 align-items-end">
              <div className="col-md-2">
                <label className="form-label mb-1 small">开始日期</label>
                <input className="form-control form-control-sm" type="date" value={attendanceStart} onChange={(event) => setAttendanceStart(event.target.value)} />
              </div>
              <div className="col-md-2">
                <label className="form-label mb-1 small">结束日期</label>
                <input className="form-control form-control-sm" type="date" value={attendanceEnd} onChange={(event) => setAttendanceEnd(event.target.value)} />
              </div>
              <div className="col-md-2">
                <label className="form-label mb-1 small">模式</label>
                <select className="form-select form-select-sm" value={attendanceMode} onChange={(event) => setAttendanceMode(event.target.value as 'simulate' | 'extract')}>
                  <option value="simulate">模拟生成</option>
                  <option value="extract">真实抽取</option>
                </select>
              </div>
              <div className="col-md-4">
                <label className="form-label mb-1 small">员工号（逗号/换行分隔，留空=全部在职）</label>
                <input className="form-control form-control-sm" value={attendanceTargets} onChange={(event) => setAttendanceTargets(event.target.value)} placeholder="如 admin" />
              </div>
              <div className="col-md-2">
                <Button size="sm" onClick={() => void runAttendanceGenerate()} loading={running}>执行生成</Button>
              </div>
            </div>
            {attendanceResult && <div className="alert alert-success py-2 mb-0 mt-2">生成完成（{attendanceResult.mode}）：员工 {attendanceResult.employeeCount} 人，新增考勤 {attendanceResult.inserted} 行，按排班填充 {attendanceResult.filled} 行。</div>}
          </div>
        </div>
        <div className="card m-2">
          <div className="card-body py-2">
            <h2 className="card-title fs-6">依薪资调整考勤（180505）</h2>
            <p className="text-secondary small mb-2">按当月工资表扣款项（WAGE_ADD&lt;0）从节假日→休息日→平时→正常工时依次清空 HRM_DIARY 对应字段，调整前后各重算一次工资（需先在考勤系统设置 HR_SETUP.WAGE_* 调整项目）。</p>
            <div className="row g-2 align-items-end">
              <div className="col-md-2">
                <label className="form-label mb-1 small">月份（yyyyMM）</label>
                <input className="form-control form-control-sm" value={adjustMonth} onChange={(event) => setAdjustMonth(event.target.value)} />
              </div>
              <div className="col-md-2">
                <Button size="sm" onClick={() => void runAttendanceAdjustWage()} loading={running}>执行调整</Button>
              </div>
              {adjustResult && <div className="col-md-8 text-success small">调整完成：工资计算 {adjustResult.wageCalcRuns} 次，员工 {adjustResult.adjustedEmployees} 人，清空考勤 {adjustResult.clearedDiaryRows} 行。</div>}
            </div>
          </div>
        </div>
      </ErpListCard>
    </div>
  )
}
