import { IconX } from '@tabler/icons-react'
import type { KeyboardEvent, MouseEvent } from 'react'
import type { WorkspaceTab } from './workspaceTabs'

interface WorkspaceTabBarProps {
  tabs: WorkspaceTab[]
  activeId: string
  /** 存在未保存改动的标签 */
  dirtyIds: ReadonlySet<string>
  /** 撞顶等一次性提示 */
  hint?: string | null
  onActivate: (id: string) => void
  onClose: (id: string) => void
  onCloseOthers: () => void
  onCloseAll: () => void
}

/**
 * 工作区标签栏。键盘可达：Tab 进入、左右方向键切换、Delete/中键关闭；
 * 关闭按钮单独可聚焦（不与标签本体抢焦点）。仅剩一个标签时不提供关闭入口。
 */
export function WorkspaceTabBar({ tabs, activeId, dirtyIds, hint, onActivate, onClose, onCloseOthers, onCloseAll }: WorkspaceTabBarProps) {
  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>, index: number) => {
    const tab = tabs[index]
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onActivate(tab.id)
    } else if (event.key === 'Delete') {
      event.preventDefault()
      onClose(tab.id)
    } else if (event.key === 'ArrowLeft' && index > 0) {
      event.preventDefault()
      onActivate(tabs[index - 1].id)
    } else if (event.key === 'ArrowRight' && index < tabs.length - 1) {
      event.preventDefault()
      onActivate(tabs[index + 1].id)
    }
  }
  const handleAuxClick = (event: MouseEvent<HTMLDivElement>, id: string) => {
    if (event.button === 1) {
      event.preventDefault()
      onClose(id)
    }
  }

  return (
    <div className="erp-tabbar" role="tablist" aria-label="工作区标签">
      {tabs.map((tab, index) => {
        const active = tab.id === activeId
        return (
          <div
            key={tab.id}
            className={`erp-tab${active ? ' is-active' : ''}`}
            role="tab"
            aria-selected={active}
            tabIndex={active ? 0 : -1}
            title={tab.label}
            onClick={() => onActivate(tab.id)}
            onAuxClick={(event) => handleAuxClick(event, tab.id)}
            onKeyDown={(event) => handleKeyDown(event, index)}
          >
            <span className="erp-tab-label">{tab.label}</span>
            {dirtyIds.has(tab.id) && <span className="erp-tab-dirty" role="img" aria-label="有未保存的改动" />}
            {tabs.length > 1 && (
              <button
                type="button"
                className="erp-tab-close"
                aria-label={`关闭标签 ${tab.label}`}
                onClick={(event) => {
                  event.stopPropagation()
                  onClose(tab.id)
                }}
              >
                <IconX size={13} />
              </button>
            )}
          </div>
        )
      })}
      <div className="erp-tabbar-actions">
        {hint && <span className="erp-tab-hint" role="status">{hint}</span>}
        {tabs.length > 1 && (
          <>
            <button type="button" className="erp-tabbar-action" onClick={onCloseOthers}>关闭其他</button>
            <button type="button" className="erp-tabbar-action" onClick={onCloseAll}>关闭全部</button>
          </>
        )}
      </div>
    </div>
  )
}
