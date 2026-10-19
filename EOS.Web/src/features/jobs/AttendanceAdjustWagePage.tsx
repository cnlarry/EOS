import { useId, useState } from 'react'
import { apiClient } from '../../services/api'
import { JobCard } from './JobCard'
import { currentMonthValue } from './today'
import { useJobAction } from './useJobAction'

interface AttendanceAdjustWageResult {
  month: string
  wageCalcRuns: number
  adjustedEmployees: number
  clearedDiaryRows: number
}

/** 依薪资调整考勤（模块 180505）：按工资表扣款项清空对应考勤字段。 */
export function AttendanceAdjustWagePage() {
  const formId = useId()
  const [month, setMonth] = useState(currentMonthValue)

  const action = useJobAction(async () => {
    const value = month.trim()
    if (!/^\d{6}$/.test(value)) throw new Error('月份格式应为 yyyyMM。')
    return apiClient.post<AttendanceAdjustWageResult>('/jobs/attendance-adjust-wage', { month: value })
  })

  return (
    <div className="erp-job-page">
      <JobCard
        ariaLabel="依薪资调整考勤"
        title="依薪资调整考勤"
        description="按当月工资表扣款项（WAGE_ADD<0）从节假日、休息日、平时加班到正常工时依次清空考勤字段，调整前后各重算一次工资。需先在考勤系统设置中配置薪资调整项目。"
        actionLabel="执行调整"
        running={action.running}
        onRun={() => void action.execute()}
        error={action.error}
        result={action.result
          ? <>调整完成：工资计算 {action.result.wageCalcRuns} 次，员工 {action.result.adjustedEmployees} 人，清空考勤 {action.result.clearedDiaryRows} 行。</>
          : null}
      >
        <div className="col-md-2">
          <label className="form-label mb-1 small" htmlFor={`${formId}-month`}>月份（yyyyMM）</label>
          <input
            id={`${formId}-month`}
            className="form-control form-control-sm"
            value={month}
            onChange={(event) => setMonth(event.target.value)}
          />
        </div>
      </JobCard>
    </div>
  )
}
