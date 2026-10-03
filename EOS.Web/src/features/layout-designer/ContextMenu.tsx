import { useEffect } from 'react'
import { useMenuPlacement } from '../../components/common/useMenuPlacement'

export interface ContextMenuItem {
  label?: string
  onClick?: () => void
  danger?: boolean
  disabled?: boolean
  separator?: boolean
}

interface ContextMenuProps {
  x: number
  y: number
  items: ContextMenuItem[]
  onClose: () => void
}

export function ContextMenu({ x, y, items, onClose }: ContextMenuProps) {
  // 视口定位：贴近屏幕右/下缘时向内收，下方放不下则翻到落点上方
  const placement = useMenuPlacement(x, y, true)

  useEffect(() => {
    const close = () => onClose()
    const keyClose = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    window.addEventListener('pointerdown', close)
    window.addEventListener('contextmenu', close)
    window.addEventListener('keydown', keyClose)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('contextmenu', close)
      window.removeEventListener('keydown', keyClose)
    }
  }, [onClose])

  return (
    <div
      ref={placement.ref}
      className="position-fixed bg-white shadow border rounded"
      style={{ left: placement.left, top: placement.top, zIndex: 1200, minWidth: 160 }}
      onPointerDown={(e) => e.stopPropagation()}
      onContextMenu={(e) => e.preventDefault()}
    >
      {items.map((item, index) => (
        item.separator
          ? <div key={`sep-${index}`} className="dropdown-divider my-1" />
          : (
            <button
              key={item.label ?? `item-${index}`}
              type="button"
              className={`d-block w-100 text-start px-3 py-1 small border-0 bg-transparent ${item.danger ? 'text-danger' : ''} ${item.disabled ? 'text-secondary' : ''}`}
              disabled={item.disabled}
              style={{ cursor: item.disabled ? 'default' : 'pointer' }}
              onClick={() => { item.onClick?.(); onClose() }}
            >
              {item.label}
            </button>
          )
      ))}
    </div>
  )
}
