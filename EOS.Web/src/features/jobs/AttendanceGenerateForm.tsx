import { useId, useState } from 'react'
import { apiClient } from '../../services/api'
import { JobCard } from './JobCard'
import { todayInputValue } from './today'
import { useJobAction } from './useJobAction'

interface AttendanceGenerateResult {
  mode: string
  employeeCount: number
  inserted: number
  filled: number
}

interface AttendanceGenerateFormProps {
  mode: 'simulate' | 'extract'
  title: string
  description: string
}

/**
 * 考勤生成作业的表单：模拟生成与真实抽取各是一个模块、一个页面，
 * 两者的差异只有作业模式；生成规则本身由服务端按 mode 分别授权。
 */
export function AttendanceGenerateForm({ mode, title, description }: AttendanceGenerateFormProps) {
  const formId = useId()
  const [startDate, setStartDate] = useState(todayInputValue)
  const [endDate, setEndDate] = useState(todayInputValue)
  const [employeeIds, setEmployeeIds] = useState('')

  const action = useJobAction(async () => {
    const empIds = employeeIds.split(/[\n,]/).map((part) => part.trim()).filter(Boolean)
    if (empIds.length === 0) throw new Error('请填写员工号（逗号或换行分隔）。')
    return apiClient.post<AttendanceGenerateResult>('/jobs/attendance-generate', {
      startDate,
      endDate,
      mode,
      empIds,
      deptId: null,
    })
  })

  return (
    <div className="erp-base-page">
      <JobCard
        ariaLabel={title}
        title={title}
        description={description}
        actionLabel="执行生成"
        running={action.running}
        onRun={() => void action.execute()}
        error={action.error}
        result={action.result
          ? <>生成完成：员工 {action.result.employeeCount} 人，新增考勤 {action.result.inserted} 行，按排班填充 {action.result.filled} 行。</>
          : null}
      >
        <div className="col-md-2">
          <label className="form-label mb-1 small" htmlFor={`${formId}-start-date`}>开始日期</label>
          <input
            id={`${formId}-start-date`}
            className="form-control form-control-sm"
            type="date"
            value={startDate}
            onChange={(event) => setStartDate(event.target.value)}
          />
        </div>
        <div className="col-md-2">
          <label className="form-label mb-1 small" htmlFor={`${formId}-end-date`}>结束日期</label>
          <input
            id={`${formId}-end-date`}
            className="form-control form-control-sm"
            type="date"
            value={endDate}
            onChange={(event) => setEndDate(event.target.value)}
          />
        </div>
        <div className="col-md-8">
          <label className="form-label mb-1 small" htmlFor={`${formId}-employees`}>员工号（逗号或换行分隔）</label>
          <input
            id={`${formId}-employees`}
            className="form-control form-control-sm"
            value={employeeIds}
            onChange={(event) => setEmployeeIds(event.target.value)}
          />
        </div>
      </JobCard>
    </div>
  )
}
