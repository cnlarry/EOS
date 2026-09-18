import { IconX } from '@tabler/icons-react'
import { useEffect, useState, type KeyboardEvent, type MouseEvent } from 'react'
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
  /** 关闭除指定标签外的其它标签（脏标签不在此列） */
  onCloseOthers: (id: string) => void
  onCloseAll: () => void
}

interface TabMenu {
  x: number
  y: number
  tabId: string
}

const MENU_WIDTH = 160
const MENU_HEIGHT = 116

/**
 * 工作区标签栏。键盘可达：Tab 进入、左右方向键切换、Delete/中键关闭、
 * ContextMenu 键（或 Shift+F10）打开标签操作菜单。
 * 标签操作走右键菜单而非常驻按钮，标签栏横向空间全部留给标签本身。
 */
export function WorkspaceTabBar({ tabs, activeId, dirtyIds, hint, onActivate, onClose, onCloseOthers, onCloseAll }: WorkspaceTabBarProps) {
  const [menu, setMenu] = useState<TabMenu | null>(null)

  // 菜单打开期间：点击别处 / Esc / 滚动 / 尺寸变化都收起
  useEffect(() => {
    if (!menu) return
    const close = () => setMenu(null)
    const onKey = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') setMenu(null)
    }
    window.addEventListener('pointerdown', close)
    window.addEventListener('keydown', onKey)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [menu])

  const openMenuAt = (clientX: number, clientY: number, tabId: string) => {
    // 靠右/靠下时向内翻转，避免菜单出界
    const x = clientX + MENU_WIDTH > window.innerWidth ? Math.max(0, clientX - MENU_WIDTH) : clientX
    const y = clientY + MENU_HEIGHT > window.innerHeight ? Math.max(0, clientY - MENU_HEIGHT) : clientY
    setMenu({ x, y, tabId })
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>, index: number) => {
    const tab = tabs[index]
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      onActivate(tab.id)
    } else if (event.key === 'Delete') {
      event.preventDefault()
      onClose(tab.id)
    } else if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault()
      const rect = event.currentTarget.getBoundingClientRect()
      openMenuAt(rect.left, rect.bottom, tab.id)
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
    <div className="erp-tabbar nav nav-tabs erp-tabbed-panel-tabs erp-workspace-tabs" role="tablist" aria-label="工作区标签">
      {tabs.map((tab, index) => {
        const active = tab.id === activeId
        return (
          <div className="nav-item" role="presentation" key={tab.id}>
            <div
              className={`nav-link erp-workspace-tab${active ? ' active' : ''}`}
              role="tab"
              aria-selected={active}
              tabIndex={active ? 0 : -1}
              title={tab.label}
              onClick={() => onActivate(tab.id)}
              onAuxClick={(event) => handleAuxClick(event, tab.id)}
              onKeyDown={(event) => handleKeyDown(event, index)}
              onContextMenu={(event) => {
                event.preventDefault()
                openMenuAt(event.clientX, event.clientY, tab.id)
              }}
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
          </div>
        )
      })}
      <div className="erp-tabbar-actions">
        {hint && <span className="erp-tab-hint" role="status">{hint}</span>}
      </div>
      {menu && (
        <div
          className="erp-menu-context-menu"
          role="menu"
          aria-label="标签操作"
          style={{ left: menu.x, top: menu.y }}
          onPointerDown={(event) => event.stopPropagation()}
        >
          <button
            type="button"
            role="menuitem"
            disabled={tabs.length <= 1}
            onClick={() => { setMenu(null); onClose(menu.tabId) }}
          >
            关闭当前
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={tabs.length <= 1}
            onClick={() => { setMenu(null); onCloseOthers(menu.tabId) }}
          >
            关闭其它
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={tabs.length <= 1}
            onClick={() => { setMenu(null); onCloseAll() }}
          >
            关闭全部
          </button>
        </div>
      )}
    </div>
  )
}
