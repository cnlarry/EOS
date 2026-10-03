import { useLayoutEffect, useRef, useState } from 'react'
import type { ChooserAnchor } from './FormFieldRenderer'
import type { FormChooserSource } from './formDefinition'

const VIEWPORT_MARGIN = 8

export interface ChooserSourceMenuProps {
  /** 选择器按钮的视口矩形：菜单左缘与按钮对齐，纵向贴住按钮的对应边 */
  anchor: ChooserAnchor
  /** down = 从按钮下方弹出；up = 字段贴近屏幕下缘，改从按钮上方弹出 */
  direction: 'down' | 'up'
  sources: FormChooserSource[]
  onPick: (source: FormChooserSource) => void
  onClose: () => void
}

/**
 * 多来源选择器菜单。
 *
 * 与选择器按钮**融为一体**：菜单左缘与按钮左缘对齐、纵向紧贴按钮的下（或上）边，
 * 由 FormFieldRenderer 同时给按钮让出该侧的边框与圆角（`.is-menu-open` / `.is-menu-open-up`），
 * 视觉上按钮与菜单是一块。
 *
 * 位置按真实尺寸量测后夹进视口（左右越界向内收、纵向越界贴边），首帧即定稿，不闪错位。
 */
export function ChooserSourceMenu({ anchor, direction, sources, onPick, onClose }: ChooserSourceMenuProps) {
  const ref = useRef<HTMLDivElement | null>(null)
  const [placed, setPlaced] = useState<{ left: number; top: number } | null>(null)

  useLayoutEffect(() => {
    const node = ref.current
    if (!node) return
    const width = node.offsetWidth
    const height = node.offsetHeight
    const left = Math.min(Math.max(VIEWPORT_MARGIN, anchor.left), Math.max(VIEWPORT_MARGIN, window.innerWidth - VIEWPORT_MARGIN - width))
    const top = direction === 'up'
      ? Math.max(VIEWPORT_MARGIN, anchor.top - height)
      : Math.min(Math.max(VIEWPORT_MARGIN, anchor.bottom), Math.max(VIEWPORT_MARGIN, window.innerHeight - VIEWPORT_MARGIN - height))
    setPlaced(current => current && current.left === left && current.top === top ? current : { left, top })
  }, [anchor, direction])

  return (
    <>
      <div
        className="erp-source-menu-backdrop"
        onClick={onClose}
        onContextMenu={event => { event.preventDefault(); onClose() }}
      />
      <div
        ref={ref}
        className={`erp-source-menu ${direction === 'up' ? 'is-up' : 'is-down'}`}
        role="menu"
        aria-label="选择数据来源"
        style={placed
          ? { left: placed.left, top: placed.top }
          : { left: anchor.left, top: direction === 'up' ? anchor.top : anchor.bottom }}
      >
        {sources.map(source => (
          <button
            key={source.serialNo ?? source.table ?? ''}
            type="button"
            role="menuitem"
            className="erp-source-menu-item"
            onClick={() => onPick(source)}
          >
            {source.description || source.table}
          </button>
        ))}
      </div>
    </>
  )
}
