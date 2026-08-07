import type { ReactNode } from 'react'

interface ErpListCardProps {
  /** 全局搜索框（如 ErpSearchBox），渲染在命令栏左侧 */
  search?: ReactNode
  /** 操作按钮区，渲染在命令栏右侧 */
  actions?: ReactNode
  /** 表格上方的标题/统计等区域 */
  header?: ReactNode
  /** 表格主体（含加载/错误/空状态） */
  children: ReactNode
  /** 分页脚内容（如 ErpPagination） */
  footer?: ReactNode
  ariaLabel?: string
}

/**
 * 标准 ERP 列表页骨架（`.card.erp-list-card`）。
 *
 * 命令栏 + 主体 + 分页脚三段式布局；只负责结构，不感知业务。
 * 搜索框与分页分别使用 ErpSearchBox / ErpPagination 填充对应槽位。
 */
export function ErpListCard({ search, actions, header, children, footer, ariaLabel = '列表查询与操作' }: ErpListCardProps) {
  return (
    <section className="card erp-list-card">
      {(search || actions) && (
        <section className="erp-list-command-bar" aria-label={ariaLabel}>
          {search}
          {actions && <div className="erp-list-actions">{actions}</div>}
        </section>
      )}
      {header}
      {children}
      {footer && <div className="card-footer erp-pagination-footer">{footer}</div>}
    </section>
  )
}
