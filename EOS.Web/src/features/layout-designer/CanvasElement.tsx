import { useDraggable } from '@dnd-kit/core'
import { useRef } from 'react'
import type { LayoutElement } from './types'

interface CanvasElementProps {
  element: LayoutElement
  zoom: number
  left: number
  top: number
  selected: boolean
  canEdit: boolean
  onSelect: (event: React.MouseEvent) => void
  onResize: (newWMm: number, newHMm: number, anchor: 'se' | 'e' | 's') => void
}

const RESIZE_HANDLE = 8

/** 元素预览（画布内所见即所得） */
export function ElementPreview({ element, zoom }: { element: LayoutElement; zoom: number }) {
  const fontSize = (element.style?.fontSize ?? 9) * zoom / 3
  const align = element.style?.align ?? 'left'
  const textAlign = align === 'center' ? 'center' : align === 'right' ? 'right' : 'left'
  const lineWidth = (element.style?.lineWidth ?? 0.5) * zoom / 3
  const borderWidth = (element.style?.borderWidth ?? 0.5) * zoom / 3
  switch (element.type) {
    case 'text':
      return (
        <div
          className="w-100 h-100"
          style={{
            fontSize, fontWeight: element.style?.bold ? 700 : undefined,
            fontStyle: element.style?.italic ? 'italic' : undefined,
            color: element.style?.color ?? '#000',
            textAlign, padding: '0 2px', whiteSpace: 'pre-wrap',
            overflow: 'hidden',
          }}
        >
          {element.content || '（空文本）'}
        </div>
      )
    case 'field':
      return (
        <div className="w-100 h-100 text-secondary overflow-hidden" style={{ fontSize, textAlign, padding: '0 2px' }}>
          {element.field ? `[${element.field}]` : '（未绑定字段）'}
        </div>
      )
    case 'line':
      return (
        <div className="w-100 d-flex align-items-center justify-content-center" style={{ height: '100%' }}>
          <div className="w-100" style={{ borderTop: `${lineWidth}px solid ${element.style?.color ?? '#000'}` }} />
        </div>
      )
    case 'rect':
      return (
        <div
          className="w-100 h-100"
          style={{
            border: `${borderWidth}px solid ${element.style?.borderColor ?? '#000'}`,
            background: element.style?.backgroundColor ?? '#fff',
          }}
        />
      )
    case 'image':
      return <div className="w-100 h-100 d-flex align-items-center justify-content-center text-secondary">LOGO</div>
    case 'table':
      return (
        <div className="w-100 h-100 overflow-hidden">
          <div className="d-flex text-secondary border-bottom" style={{ fontSize: 8 }}>
            {(element.columns ?? []).map((c) => (
              <div key={c.field} className="flex-shrink-0 px-1 text-truncate" style={{ width: (c.width ?? 20) * zoom }}>
                {c.label}
              </div>
            ))}
          </div>
          <div className="text-secondary" style={{ fontSize: 8 }}>明细行…</div>
        </div>
      )
    default:
      return <div className="text-secondary">?</div>
  }
}

export function CanvasElement({
  element, zoom, left, top, selected, canEdit, onSelect, onResize,
}: CanvasElementProps) {
  const { attributes, listeners, setNodeRef, transform } = useDraggable({
    id: element.id,
    disabled: !canEdit,
  })
  const resizeRef = useRef<{ startX: number; startY: number; startW: number; startH: number; anchor: 'se' | 'e' | 's' } | null>(null)

  const width = Math.max(element.w * zoom, 8)
  const height = Math.max(element.h * zoom, 4)

  const onResizePointerDown = (event: React.PointerEvent, anchor: 'se' | 'e' | 's') => {
    if (!canEdit) return
    event.preventDefault()
    event.stopPropagation()
    resizeRef.current = { startX: event.clientX, startY: event.clientY, startW: element.w, startH: element.h, anchor }
    const handleMove = (move: PointerEvent) => {
      const ref = resizeRef.current
      if (!ref) return
      const dw = (move.clientX - ref.startX) / zoom
      const dh = (move.clientY - ref.startY) / zoom
      const newW = Math.max(0.5, ref.anchor === 's' ? ref.startW : ref.startW + dw)
      const newH = Math.max(0.5, ref.anchor === 'e' ? ref.startH : ref.startH + dh)
      onResize(newW, newH, ref.anchor)
    }
    const handleUp = () => {
      resizeRef.current = null
      window.removeEventListener('pointermove', handleMove)
      window.removeEventListener('pointerup', handleUp)
    }
    window.addEventListener('pointermove', handleMove)
    window.addEventListener('pointerup', handleUp)
  }

  return (
    <div
      ref={setNodeRef}
      className="position-absolute overflow-hidden bg-white"
      style={{
        left: left + (transform?.x ?? 0),
        top: top + (transform?.y ?? 0),
        width, height,
        outline: selected ? '2px solid var(--tblr-primary)' : '1px dashed rgba(0,0,0,0.25)',
        cursor: canEdit ? 'move' : 'default',
        zIndex: selected ? 20 : 10,
      }}
      onClick={(e) => { e.stopPropagation(); onSelect(e) }}
      {...(canEdit ? { ...attributes, ...listeners } : {})}
    >
      <ElementPreview element={element} zoom={zoom} />
      {selected && canEdit && (
        <>
          <div
            className="position-absolute"
            style={{
              right: -RESIZE_HANDLE / 2, top: '50%', width: RESIZE_HANDLE, height: RESIZE_HANDLE,
              transform: 'translateY(-50%)', cursor: 'ew-resize',
              background: '#fff', border: '1px solid var(--tblr-primary)', borderRadius: 2,
            }}
            onPointerDown={(e) => onResizePointerDown(e, 'e')}
          />
          <div
            className="position-absolute"
            style={{
              bottom: -RESIZE_HANDLE / 2, left: '50%', width: RESIZE_HANDLE, height: RESIZE_HANDLE,
              transform: 'translateX(-50%)', cursor: 'ns-resize',
              background: '#fff', border: '1px solid var(--tblr-primary)', borderRadius: 2,
            }}
            onPointerDown={(e) => onResizePointerDown(e, 's')}
          />
          <div
            className="position-absolute"
            style={{
              right: -RESIZE_HANDLE / 2, bottom: -RESIZE_HANDLE / 2, width: RESIZE_HANDLE, height: RESIZE_HANDLE,
              cursor: 'nwse-resize',
              background: '#fff', border: '1px solid var(--tblr-primary)', borderRadius: 2,
            }}
            onPointerDown={(e) => onResizePointerDown(e, 'se')}
          />
        </>
      )}
    </div>
  )
}
