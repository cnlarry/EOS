import type { ReactNode } from 'react'
import { Button } from '../ui/Button'
import { COMMAND_ACTIONS } from './commandActions'

export interface ErpCommandItem {
  /** 动作键：匹配 COMMAND_ACTIONS 取默认图标/标题；或自定义键（此时必须给 icon/title/label）。 */
  action: string
  /** 自定义图标（覆盖注册表）。 */
  icon?: ReactNode
  /** 悬停标题（覆盖注册表）。 */
  title?: string
  /** 文字按钮文本：提供后渲染文字按钮（如表单保存/取消），否则渲染纯图标按钮。 */
  label?: string
  /** 纯图标按钮的视觉变体（仅对无 label 的图标按钮有意义；默认 secondary）。 */
  variant?: 'primary' | 'secondary' | 'danger' | 'ghost'
  /** 显隐控制：返回 false 则不渲染（供页面按权限/选中行/状态声明）。 */
  visible?: boolean
  /** 禁用。 */
  disabled?: boolean
  /** 加载中（图标按钮显示 spinner）。 */
  loading?: boolean
  onClick?: () => void
  /** 自定义渲染（如导出下拉、分组下拉），提供后忽略其余字段。 */
  render?: () => ReactNode
}

interface ErpCommandBarProps {
  /** 命令项列表，按数组顺序渲染（业务按钮在前、工具按钮在后）。 */
  items: ErpCommandItem[]
  className?: string
  ariaLabel?: string
}

/**
 * 统一命令栏：
 * - 图标+文字按钮：所有动作按钮统一为「图标 + 动作名」样式（工作台/表单/列表统一表意），
 *   悬停 title 仍保留动作名说明；
 * - label 按钮：通过 label 提供自定义文字（如保存/取消），自动复用 COMMAND_ACTIONS 注册表图标，
 *   与图标+文字按钮样式一致；
 * - 每个动作由页面按权限/选中状态声明 visible，不在组件内写业务分支。
 */
export function ErpCommandBar({ items, className = '', ariaLabel = '命令栏' }: ErpCommandBarProps) {
  return (
    <div className={`d-flex align-items-center gap-2 ${className}`.trim()} role="toolbar" aria-label={ariaLabel}>
      {items.map((item) => {
        if (item.render) return <span key={item.action}>{item.render()}</span>
        const action = COMMAND_ACTIONS[item.action]
        const icon = item.icon ?? action?.icon
        const title = item.title ?? action?.title ?? item.action
        if (item.visible === false) return null
        if (item.label) {
          return (
            <Button key={item.action} size="sm" className="erp-command-btn" icon={icon} variant={item.variant ?? 'secondary'} disabled={item.disabled} loading={item.loading} onClick={item.onClick}>
              {item.label}
            </Button>
          )
        }
        return (
          <Button key={item.action} size="sm" className="erp-command-btn" icon={icon} title={title} aria-label={title} variant={item.variant ?? 'secondary'} disabled={item.disabled} loading={item.loading} onClick={item.onClick}>
            {title}
          </Button>
        )
      })}
    </div>
  )
}