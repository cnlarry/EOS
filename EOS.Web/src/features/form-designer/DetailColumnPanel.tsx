import { useDraggable, useDroppable } from '@dnd-kit/core'
import { dragId, type DropTarget } from './formDesignerDrag'
import type { DesignRow } from './types'

interface DetailColumnPanelProps {
  table: string
  rows: DesignRow[]
  selectedKey: string | null
  preview: boolean
  draggingKey: string | null
  dropTarget: DropTarget
  onSelect: (key: string) => void
  onMove: (key: string, delta: number) => void
  onHidden: (key: string, hidden: boolean) => void
}

/**
 * 明细列编辑：明细是**网格**不是格版式，故不进画布，单独一块列表。
 * 这里只做顺序与显隐——明细没有页签、分节、复合格、占位，故不提供这些入口
 * （提供了也看不到效果，只会让用户以为坏了）。顺序可拖拽，也可用箭头微调。
 */
export default function DetailColumnPanel({
  table,
  rows,
  selectedKey,
  preview,
  draggingKey,
  dropTarget,
  onSelect,
  onMove,
  onHidden,
}: DetailColumnPanelProps) {
  return (
    <section className="erp-designer-detail">
      <div className="erp-designer-detail-title">
        明细列（{table}）<span className="erp-designer-muted">{rows.length} 列</span>
      </div>
      <div className="erp-designer-detail-list">
        {rows.map((row, index) => (
          <DetailRow
            key={row.key}
            row={row}
            index={index}
            total={rows.length}
            selected={selectedKey === row.key}
            dragging={draggingKey === row.key}
            preview={preview}
            dropClass={
              dropTarget?.kind === 'insert' && dropTarget.key === row.key
                ? dropTarget.before ? 'is-drop-before' : 'is-drop-after'
                : ''
            }
            onSelect={onSelect}
            onMove={onMove}
            onHidden={onHidden}
          />
        ))}
      </div>
    </section>
  )
}

function DetailRow({
  row,
  index,
  total,
  selected,
  dragging,
  preview,
  dropClass,
  onSelect,
  onMove,
  onHidden,
}: {
  row: DesignRow
  index: number
  total: number
  selected: boolean
  dragging: boolean
  preview: boolean
  dropClass: string
  onSelect: (key: string) => void
  onMove: (key: string, delta: number) => void
  onHidden: (key: string, hidden: boolean) => void
}) {
  const { setNodeRef: setDragRef, attributes, listeners } = useDraggable({
    id: dragId({ from: 'canvas', table: 'detail', key: row.key }),
    data: { kind: 'field', key: row.key },
    disabled: preview,
  })
  const { setNodeRef: setDropRef } = useDroppable({ id: `cell-drop:detail:${row.key}`, data: { kind: 'cell', key: row.key } })
  const classes = ['erp-designer-detail-row']
  if (selected) classes.push('is-selected')
  if (dragging) classes.push('is-dragging')
  if (dropClass) classes.push(dropClass)
  return (
    <div
      ref={node => {
        setDragRef(node)
        setDropRef(node)
      }}
      className={classes.join(' ')}
      onClick={() => onSelect(row.key)}
      {...attributes}
      {...listeners}
    >
      <span className="erp-designer-detail-index">{index + 1}</span>
      <span className="erp-designer-detail-label">{row.label}</span>
      <span className="erp-designer-muted">{row.key}</span>
      <span className="erp-designer-badges">
        {row.required ? <em>必填</em> : null}
        {row.isPrimaryKey ? <em>主键</em> : null}
        {row.locked ? <em title={row.lockReason ?? ''}>🔒</em> : null}
        {!row.userVisible ? <em title="当前用户不可见">不可见</em> : null}
        {row.hidden ? <em className="is-hidden">已隐藏</em> : null}
      </span>
      {!preview ? (
        <span className="erp-designer-detail-actions">
          <button type="button" title="上移" disabled={index === 0} onClick={() => onMove(row.key, -1)}>
            ↑
          </button>
          <button type="button" title="下移" disabled={index === total - 1} onClick={() => onMove(row.key, 1)}>
            ↓
          </button>
          <button
            type="button"
            title={row.locked ? (row.lockReason ?? '不允许隐藏') : row.hidden ? '显示该列' : '隐藏该列'}
            disabled={row.locked}
            onClick={() => onHidden(row.key, !row.hidden)}
          >
            {row.hidden ? '○' : '●'}
          </button>
        </span>
      ) : null}
    </div>
  )
}
