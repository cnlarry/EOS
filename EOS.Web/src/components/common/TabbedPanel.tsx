import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react'

export interface TabbedPanelTab<T extends string = string> {
  key: T
  label: string
}

/**
 * 系统级页签面板（设计语言）：
 * - 标签行 + 带边框圆角内容面板，激活标签与面板融为一体；
 * - 完整 ARIA tabs 模式（tablist/tab/tabpanel、roving tabindex）；
 * - 支持方向键切换（←/→、Home/End），与 Tabler 页签交互一致。
 *
 * 内容面板由调用方根据 activeKey 自行渲染（children 即当前页签内容）。
 */
export function TabbedPanel<T extends string>({
  tabs,
  activeKey,
  onActiveKeyChange,
  children,
  className = '',
  label,
}: {
  tabs: TabbedPanelTab<T>[]
  activeKey: T
  onActiveKeyChange: (key: T) => void
  children?: ReactNode
  /** 附加到根容器的样式类。 */
  className?: string
  /** tablist 的无障碍名称（默认「页签」）。 */
  label?: string
}) {
  const baseId = useId()
  const tabRefs = useRef(new Map<string, HTMLButtonElement>())
  const resolved = tabs.find((tab) => tab.key === activeKey) ?? tabs[0]
  const activeIndex = resolved ? tabs.indexOf(resolved) : -1

  const handleKeyDown = (event: KeyboardEvent<HTMLUListElement>) => {
    if (tabs.length === 0) return
    let next: number
    if (event.key === 'Home') {
      next = 0
    } else if (event.key === 'End') {
      next = tabs.length - 1
    } else if (event.key === 'ArrowRight') {
      next = (activeIndex + 1) % tabs.length
    } else if (event.key === 'ArrowLeft') {
      next = (activeIndex - 1 + tabs.length) % tabs.length
    } else {
      return
    }
    event.preventDefault()
    const tab = tabs[next]
    if (!tab) return
    onActiveKeyChange(tab.key)
    tabRefs.current.get(tab.key)?.focus()
  }

  return (
    <div className={`erp-tabbed-panel ${className}`.trim()}>
      <ul
        className="nav nav-tabs erp-tabbed-panel-tabs"
        role="tablist"
        aria-label={label ?? '页签'}
        onKeyDown={handleKeyDown}
      >
        {tabs.map((tab) => {
          const selected = tab.key === resolved?.key
          return (
            <li className="nav-item" role="presentation" key={tab.key}>
              <button
                type="button"
                role="tab"
                id={`${baseId}-tab-${tab.key}`}
                className={`nav-link${selected ? ' active' : ''}`}
                aria-selected={selected}
                aria-controls={`${baseId}-panel`}
                tabIndex={selected ? 0 : -1}
                ref={(node) => {
                  if (node) tabRefs.current.set(tab.key, node)
                  else tabRefs.current.delete(tab.key)
                }}
                onClick={() => onActiveKeyChange(tab.key)}
              >
                {tab.label}
              </button>
            </li>
          )
        })}
      </ul>
      {resolved && (
        <div
          className="erp-tabbed-panel-body"
          role="tabpanel"
          id={`${baseId}-panel`}
          aria-labelledby={`${baseId}-tab-${resolved.key}`}
        >
          {children}
        </div>
      )}
    </div>
  )
}
