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
    case 'barcode':
      return <BarcodePreview type={element.barcodeType} />
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

/** 条码画布预览：按类型切换形状（2D 矩阵 / 1D 条线 / PDF417 堆叠）。 */
function BarcodePreview({ type }: { type?: string }) {
  const kind = type ?? 'qrcode'
  if (kind === 'pdf417') {
    return (
      <div className="w-100 h-100 d-flex flex-column align-items-center justify-content-center text-secondary overflow-hidden">
        <svg viewBox="0 0 100 60" className="w-75" aria-hidden="true">
          {[10, 22, 34, 46].map((y) => (
            <g key={y}>
              {[8, 18, 30, 44, 56, 68, 80, 90].map((x, i) => (
                <rect key={x} x={x} y={y} width={i % 3 === 0 ? 6 : 3} height={8} fill="currentColor" />
              ))}
            </g>
          ))}
        </svg>
        <span className="small" style={{ fontSize: 8 }}>PDF417</span>
      </div>
    )
  }
  if (['qrcode', 'datamatrix', 'aztec'].includes(kind)) {
    const isAztec = kind === 'aztec'
    const isDataMatrix = kind === 'datamatrix'
    return (
      <div className="w-100 h-100 d-flex flex-column align-items-center justify-content-center text-secondary overflow-hidden">
        <svg viewBox="0 0 80 80" className="w-75 h-75" aria-hidden="true">
          {isAztec ? (
            <>
              <rect x="14" y="14" width="52" height="52" fill="none" stroke="currentColor" strokeWidth="5" />
              <rect x="32" y="32" width="16" height="16" fill="currentColor" />
            </>
          ) : isDataMatrix ? (
            <>
              <rect x="8" y="8" width="14" height="64" fill="currentColor" />
              <rect x="22" y="8" width="50" height="14" fill="currentColor" />
              <rect x="50" y="30" width="8" height="8" fill="currentColor" />
              <rect x="30" y="50" width="8" height="8" fill="currentColor" />
              <rect x="60" y="50" width="8" height="8" fill="currentColor" />
            </>
          ) : (
            <>
              <rect x="6" y="6" width="68" height="68" fill="none" stroke="currentColor" strokeWidth="3" />
              <rect x="14" y="20" width="8" height="40" fill="currentColor" />
              <rect x="26" y="20" width="4" height="40" fill="currentColor" />
              <rect x="34" y="20" width="8" height="40" fill="currentColor" />
              <rect x="46" y="20" width="4" height="40" fill="currentColor" />
              <rect x="54" y="20" width="12" height="40" fill="currentColor" />
            </>
          )}
        </svg>
        <span className="small" style={{ fontSize: 8 }}>{kind.toUpperCase()}</span>
      </div>
    )
  }
  // 1D 条码：竖线条 + 底部数字
  return (
    <div className="w-100 h-100 d-flex flex-column align-items-center justify-content-center text-secondary overflow-hidden">
      <svg viewBox="0 0 100 40" className="w-100 h-75" preserveAspectRatio="none" aria-hidden="true">
        {[4, 8, 14, 18, 24, 28, 34, 38, 44, 48, 54, 58, 64, 68, 74, 78, 84, 88, 94, 98].map((x, i) => (
          <rect key={x} x={x - (i % 3 === 0 ? 1.5 : 0.75)} y={2} width={i % 3 === 0 ? 3 : 1.5} height={30} fill="currentColor" />
        ))}
      </svg>
      <span className="small" style={{ fontSize: 7, letterSpacing: 1 }}>0123456789</span>
    </div>
  )
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
      className="canvas-element position-absolute overflow-hidden bg-white"
      data-selected={selected}
      data-element-id={element.id}
      style={{
        left: left + (transform?.x ?? 0),
        top: top + (transform?.y ?? 0),
        width, height,
        cursor: canEdit ? 'move' : 'default',
        zIndex: selected ? 20 : 10,
      }}
      onClick={(e) => { e.stopPropagation(); onSelect(e) }}
      {...(canEdit ? { ...attributes, ...listeners } : {})}
    >
      <span
        className="position-absolute badge rounded text-white"
        style={{
          top: -13, left: 0, fontSize: 8, fontWeight: 400, zIndex: 25,
          background: 'rgba(15, 23, 42, 0.55)', opacity: selected ? 1 : 0.7,
          maxWidth: 140, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}
        title={element.id}
      >
        {element.id}
      </span>
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
