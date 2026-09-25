import { useState } from 'react'
import { MAX_ROW_SPAN } from './formDesignerDraft'

interface PlacementPickerProps {
  columns: number
  span: number
  rowSpan: number
  onPick: (span: number, rowSpan: number) => void
  onClose: () => void
}

/**
 * 占位选择器：不给用户"列跨度/行跨度"数字输入框，而是划过即预览的可视矩阵。
 * 矩阵尺寸固定，但超出模块列数的格子不可选（避免选出越界占位）。
 */
export default function PlacementPicker({ columns, span, rowSpan, onPick, onClose }: PlacementPickerProps) {
  const maxColumns = Math.max(1, columns)
  const [hover, setHover] = useState({ span, rowSpan })

  return (
    <div className="erp-designer-popover" role="dialog" aria-label="占位">
      <div className="erp-designer-popover-title">占位（列 × 行）</div>
      <div className="erp-designer-placement" style={{ gridTemplateColumns: `repeat(${maxColumns}, 1fr)` }}>
        {Array.from({ length: maxColumns * MAX_ROW_SPAN }, (_, index) => {
          const col = (index % maxColumns) + 1
          const row = Math.floor(index / maxColumns) + 1
          const active = col <= hover.span && row <= hover.rowSpan
          return (
            <button
              key={`${col}-${row}`}
              type="button"
              className={active ? 'erp-designer-placement-cell is-active' : 'erp-designer-placement-cell'}
              onMouseEnter={() => setHover({ span: col, rowSpan: row })}
              onClick={() => {
                onPick(col, row)
                onClose()
              }}
              aria-label={`${col} 列 × ${row} 行`}
            />
          )
        })}
      </div>
      <div className="erp-designer-placement-caption">
        {hover.span} 列 × {hover.rowSpan} 行
      </div>
      <div className="erp-designer-popover-actions">
        {[
          { label: '整行', span: maxColumns, rowSpan: 1 },
          { label: '半行', span: 1, rowSpan: 1 },
          { label: '占 2 行', span: 1, rowSpan: 2 },
          { label: '占 3 行', span: 1, rowSpan: 3 },
        ].map((preset) => (
          <button
            key={preset.label}
            type="button"
            className="erp-command-btn"
            onClick={() => {
              onPick(Math.min(preset.span, maxColumns), preset.rowSpan)
              onClose()
            }}
          >
            {preset.label}
          </button>
        ))}
      </div>
    </div>
  )
}
