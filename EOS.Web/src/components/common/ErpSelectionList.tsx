import type { ReactNode } from 'react'
import { LoadingState } from './AsyncState'

/**
 * 通用「选择清单」：左栏 / 树侧边用来切换"当前对象"的清单（模块、字段、报表……）。
 *
 * 为什么封装：这套外观由三部分组成——圆角底色的**选中项**（含左缘 2px 色条、随主题切换）、
 * 悬停与滚动容器、空/加载/错误三态。各页各写一遍时，选中态最容易分叉（浅色能看、深色发白），
 * 空态措辞与滚动归属也会各不相同。这里定义一次，页面只决定宽度与分隔线。
 *
 * 口径：
 * - 选中项是可聚焦的 `<button>`，不是加了 tabIndex 的 `<li>`：键盘 Enter/Space 直接可用；
 * - `label` 是主行（名称），`description` 是次行（编号 / 说明），`meta` 是右侧附加信息（计数 / 徽标）；
 * - 滚动由本组件承担（`overflow-y: auto`），页面不要再套一层滚动容器。
 */
export type ErpSelectionItem = {
  id: string
  /** 主行：对象名称。 */
  label: ReactNode
  /** 次行：编号、说明等（可选）。 */
  description?: ReactNode
  /** 右侧附加信息：计数、徽标、未保存点等（可选）。 */
  meta?: ReactNode
  /** 悬停提示（原生 title）。 */
  tooltip?: string
  /** 主行是标识符（字段名 / 表名）：等宽字体。 */
  monospace?: boolean
  disabled?: boolean
}

export function ErpSelectionList({
  ariaLabel,
  header,
  items,
  activeId = null,
  onSelect,
  loading = false,
  loadingLabel = '正在加载…',
  error = null,
  empty = null,
  footer,
  className,
}: {
  ariaLabel: string
  /** 清单上方的工具行（筛选下拉、计数 + 新增等）。 */
  header?: ReactNode
  items: ErpSelectionItem[]
  activeId?: string | null
  onSelect: (id: string) => void
  loading?: boolean
  loadingLabel?: string
  /** 加载失败的内容（一般是 `<ErrorState onRetry=…/>`）；有它时不再渲染清单。 */
  error?: ReactNode
  /** 清单为空时的内容。 */
  empty?: ReactNode
  /** 清单下方的固定内容（可选）。 */
  footer?: ReactNode
  /** 页面侧只加宽度 / 分隔线（如 `erp-field-nav`），不要覆盖选中态与内边距。 */
  className?: string
}) {
  return (
    <aside className={`erp-selection-list ${className ?? ''}`.trim()} aria-label={ariaLabel}>
      {header ? <div className="erp-selection-list-header">{header}</div> : null}
      {loading ? (
        <LoadingState label={loadingLabel} />
      ) : error != null ? (
        error
      ) : items.length === 0 ? (
        empty
      ) : (
        <ul className="erp-selection-list-items">
          {items.map((item) => {
            const active = item.id === activeId
            return (
              <li key={item.id}>
                <button
                  type="button"
                  className={`erp-selection-item${active ? ' is-active' : ''}`}
                  aria-current={active ? 'true' : undefined}
                  title={item.tooltip}
                  disabled={item.disabled}
                  onClick={() => onSelect(item.id)}
                >
                  <span className="erp-selection-item-main">
                    <span className={`erp-selection-item-label${item.monospace ? ' is-monospace' : ''}`}>
                      {item.label}
                    </span>
                    {item.description != null ? (
                      <span className="erp-selection-item-description">{item.description}</span>
                    ) : null}
                  </span>
                  {item.meta != null ? <span className="erp-selection-item-meta">{item.meta}</span> : null}
                </button>
              </li>
            )
          })}
        </ul>
      )}
      {footer}
    </aside>
  )
}
