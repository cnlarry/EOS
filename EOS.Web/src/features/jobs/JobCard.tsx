import type { ReactNode } from 'react'
import { ErpListCard } from '../../components/common/ErpListCard'
import { Button } from '../../components/ui/Button'

export interface JobCardProps {
  /** 命令栏无障碍名（读屏用），与作业名一致 */
  ariaLabel: string
  title: string
  description: ReactNode
  /** 执行按钮文案 */
  actionLabel: string
  running: boolean
  onRun: () => void
  error: string | null
  result: ReactNode
  /** 本作业自己的参数行 */
  children?: ReactNode
}

/**
 * 单个后台作业的页面外壳：命令栏只放本作业的执行按钮，参数、结果与错误都在卡内。
 * 一个页面只承载一个作业（模块），作业之间不共享执行状态。
 */
export function JobCard({ ariaLabel, title, description, actionLabel, running, onRun, error, result, children }: JobCardProps) {
  return (
    <ErpListCard
      ariaLabel={ariaLabel}
      search={null}
      actions={<Button size="sm" onClick={onRun} loading={running}>{actionLabel}</Button>}
    >
      <div className="p-2">
        <h2 className="card-title fs-6">{title}</h2>
        <p className="text-secondary small mb-2">{description}</p>
        {children && <div className="row g-2 align-items-end">{children}</div>}
        {error && <div className="alert alert-danger py-2 mb-0 mt-2">{error}</div>}
        {result && <div className="alert alert-success py-2 mb-0 mt-2">{result}</div>}
      </div>
    </ErpListCard>
  )
}
