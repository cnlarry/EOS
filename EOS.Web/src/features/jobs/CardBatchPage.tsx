import { useId, useState } from 'react'
import { apiClient } from '../../services/api'
import { JobCard } from './JobCard'
import { todayInputValue } from './today'
import { useJobAction } from './useJobAction'

interface CardBatchResult {
  updated: number
  inserted: number
}

/** 员工批量发卡（模块 180218）：每行「员工号,卡号」，逐条新增或更新员工卡。 */
export function CardBatchPage() {
  const formId = useId()
  const [startDate, setStartDate] = useState(todayInputValue)
  const [cardList, setCardList] = useState('')

  const action = useJobAction(async () => {
    const cards = cardList
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean)
      .map((line) => {
        const [empId, cardId] = line.split(',').map((part) => part.trim())
        return { empId, cardId }
      })
      .filter((item) => item.empId && item.cardId)
    if (cards.length === 0) throw new Error('请按「员工号,卡号」每行一条填写。')
    return apiClient.post<CardBatchResult>('/jobs/card-batch', { startDate, cards })
  })

  return (
    <div className="erp-job-page">
      <JobCard
        ariaLabel="员工批量发卡"
        title="员工批量发卡"
        description="按生效日期批量新增或更新员工卡；同一员工已有更晚生效的卡时按既有规则闭合冲突卡。"
        actionLabel="执行发卡"
        running={action.running}
        onRun={() => void action.execute()}
        error={action.error}
        result={action.result ? <>发卡完成：新增 {action.result.inserted}，更新 {action.result.updated}。</> : null}
      >
        <div className="col-md-2">
          <label className="form-label mb-1 small" htmlFor={`${formId}-start-date`}>生效日期</label>
          <input
            id={`${formId}-start-date`}
            className="form-control form-control-sm"
            type="date"
            value={startDate}
            onChange={(event) => setStartDate(event.target.value)}
          />
        </div>
        <div className="col-md-10">
          <label className="form-label mb-1 small" htmlFor={`${formId}-cards`}>员工号,卡号（每行一条）</label>
          <textarea
            id={`${formId}-cards`}
            className="form-control form-control-sm font-monospace"
            rows={4}
            value={cardList}
            onChange={(event) => setCardList(event.target.value)}
          />
        </div>
      </JobCard>
    </div>
  )
}
