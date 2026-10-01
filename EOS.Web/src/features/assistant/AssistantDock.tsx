import { IconRobot } from '@tabler/icons-react'
import { useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { AssistantPanel } from './AssistantPanel'
import { ASSISTANT_PATH, useAssistant } from './assistantContext'
import { useOpenTab } from '../../components/layout/WorkspaceNavContext'

const FAB_POS_KEY = 'erp-assistant-fab-pos'
const FAB_SIZE = 52
const FAB_MARGIN = 8

interface FabPos {
  x: number
  y: number
}

function readFabPos(): FabPos | null {
  try {
    const raw = localStorage.getItem(FAB_POS_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as FabPos
    if (typeof parsed.x === 'number' && typeof parsed.y === 'number') return parsed
    return null
  } catch {
    return null
  }
}

/**
 * 助手的**半屏形态**：右下角浮球 + 右侧抽屉。
 *
 * <para>
 * 它只是个壳——会话状态与流式请求都在 <see cref="useAssistant"/> 的 Provider 里，
 * 所以"收起抽屉"或"切到全屏"都不会打断正在进行的对话。
 * 全屏形态是独立的工作区标签页（<see cref="AssistantPage"/>），两者共用同一份状态。
 * </para>
 */
export function AssistantDock() {
  const location = useLocation()
  const { open, setOpen } = useAssistant()
  const openTab = useOpenTab()
  // 已经站在全屏助手页上时不再显示浮球：助手占满屏幕，"叫出助手"的入口是多余的，
  // 点它还会造出半屏与全屏并存的两个界面（形态同一时刻只该有一种）。
  const onAssistantPage = location.pathname === ASSISTANT_PATH
  const [fabPos, setFabPos] = useState<FabPos | null>(readFabPos)
  const drawerRef = useRef<HTMLElement>(null)
  const fabDragRef = useRef<{ startX: number; startY: number; originX: number; originY: number; moved: boolean } | null>(null)
  // 拖拽移动标记独立于指针会话：onPointerUp 清空会话，但移动事实保留到 onClick 消费，
  // 避免「拖拽后误触发打开」。
  const fabMovedRef = useRef(false)

  useEffect(() => {
    if (fabPos) localStorage.setItem(FAB_POS_KEY, JSON.stringify(fabPos))
  }, [fabPos])

  // 点抽屉以外收起（**不销毁**：状态在 Provider 里，再点浮球原样回来），Esc 同效。
  // 菜单是抽屉内的浮层，它自己的关闭由 AssistantPanel 负责。
  useEffect(() => {
    if (!open) return
    const onPointerDown = (event: PointerEvent) => {
      if (drawerRef.current && !drawerRef.current.contains(event.target as Node)) setOpen(false)
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
    }
  }, [open, setOpen])

  // 浮球拖拽：pointer 捕获 + 视口内钳制；位置按用户存 localStorage（纯展示偏好，不落库）
  const onFabPointerDown = (event: React.PointerEvent<HTMLButtonElement>) => {
    const current = fabPos ?? {
      x: window.innerWidth - 24 - FAB_SIZE,
      y: window.innerHeight - 24 - FAB_SIZE,
    }
    fabDragRef.current = { startX: event.clientX, startY: event.clientY, originX: current.x, originY: current.y, moved: false }
    fabMovedRef.current = false
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }

  const onFabPointerMove = (event: React.PointerEvent<HTMLButtonElement>) => {
    const state = fabDragRef.current
    if (!state) return
    const dx = event.clientX - state.startX
    const dy = event.clientY - state.startY
    if (!state.moved && Math.hypot(dx, dy) > 4) {
      state.moved = true
      fabMovedRef.current = true
    }
    const nextX = Math.min(Math.max(state.originX + dx, FAB_MARGIN), window.innerWidth - FAB_SIZE - FAB_MARGIN)
    const nextY = Math.min(Math.max(state.originY + dy, FAB_MARGIN), window.innerHeight - FAB_SIZE - FAB_MARGIN)
    setFabPos({ x: nextX, y: nextY })
  }

  const endFabDrag = () => {
    fabDragRef.current = null
  }

  const openFab = () => {
    if (fabMovedRef.current) {
      fabMovedRef.current = false
      return
    }
    setOpen(true)
  }

  /** 切到全屏：开一个工作区标签页并收起抽屉。会话状态不在这里，所以对话不会因此中断。 */
  const expand = () => {
    openTab(ASSISTANT_PATH)
    setOpen(false)
  }

  return (
    <>
      {!open && !onAssistantPage && (
        <button
          className={`erp-assistant-fab${fabPos ? ' erp-assistant-fab-dragged' : ''}`}
          type="button"
          title="工作助手 (Ctrl+/)（可拖拽调整位置）"
          aria-label="打开工作助手"
          style={fabPos ? { left: fabPos.x, top: fabPos.y } : undefined}
          onPointerDown={onFabPointerDown}
          onPointerMove={onFabPointerMove}
          onPointerUp={endFabDrag}
          onPointerCancel={endFabDrag}
          onClick={openFab}
        >
          <IconRobot size={26} />
        </button>
      )}
      {open && !onAssistantPage && (
        <aside ref={drawerRef} className="erp-assistant-drawer" aria-label="工作助手">
          <AssistantPanel variant="drawer" onExpand={expand} />
        </aside>
      )}
    </>
  )
}
