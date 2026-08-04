import { IconAlertCircle, IconInbox } from '@tabler/icons-react'
import { Button } from '../ui/Button'

export function LoadingState({ label = '正在加载数据…' }: { label?: string }) {
  return (
    <div className="erp-state" role="status">
      <span className="spinner-border text-primary" aria-hidden="true" />
      <span className="text-secondary">{label}</span>
    </div>
  )
}

export function EmptyState({ title = '暂无数据', description = '当前条件下没有找到记录。' }: { title?: string; description?: string }) {
  return (
    <div className="erp-state">
      <span className="erp-state-icon"><IconInbox size={28} /></span>
      <strong>{title}</strong>
      <span className="text-secondary">{description}</span>
    </div>
  )
}

export function ErrorState({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="erp-state" role="alert">
      <span className="erp-state-icon erp-state-icon-error"><IconAlertCircle size={28} /></span>
      <strong>数据加载失败</strong>
      <span className="text-secondary">{message}</span>
      {onRetry && <Button onClick={onRetry}>重新加载</Button>}
    </div>
  )
}
