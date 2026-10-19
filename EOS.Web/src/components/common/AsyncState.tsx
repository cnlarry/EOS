import { IconAlertCircle, IconInbox } from '@tabler/icons-react'
import { Button } from '../ui/Button'

/**
 * 状态块的两种高度：
 * - default：整页/整卡占位，18rem，居中留白；
 * - compact：弹窗与卡片内嵌，7.5rem——选择器弹窗高 640px，18rem 会占掉近一半，
 *   看起来像"只加载了一半"。
 */
type StateSize = 'default' | 'compact'

function stateClass(size: StateSize) {
  return size === 'compact' ? 'erp-state is-compact' : 'erp-state'
}

export function LoadingState({ label = '正在加载数据…', size = 'default' }: { label?: string; size?: StateSize }) {
  return (
    <div className={stateClass(size)} role="status">
      <span className="spinner-border text-primary" aria-hidden="true" />
      <span className="text-secondary">{label}</span>
    </div>
  )
}

export function EmptyState({ title = '暂无数据', description = '当前条件下没有找到记录。', size = 'default' }: { title?: string; description?: string; size?: StateSize }) {
  return (
    <div className={stateClass(size)}>
      <span className="erp-state-icon"><IconInbox size={28} /></span>
      <strong>{title}</strong>
      <span className="text-secondary">{description}</span>
    </div>
  )
}

export function ErrorState({ message, onRetry, size = 'default' }: { message: string; onRetry?: () => void; size?: StateSize }) {
  return (
    <div className={stateClass(size)} role="alert">
      <span className="erp-state-icon erp-state-icon-error"><IconAlertCircle size={28} /></span>
      <strong>数据加载失败</strong>
      <span className="text-secondary">{message}</span>
      {onRetry && <Button onClick={onRetry}>重新加载</Button>}
    </div>
  )
}
