import { useEffect } from 'react'

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
      className="position-fixed bg-white shadow border rounded"
      style={{ left: Math.min(x, window.innerWidth - 180), top: Math.min(y, window.innerHeight - 260), zIndex: 1200, minWidth: 160 }}
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
