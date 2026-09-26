import { useDraggable, useDroppable } from '@dnd-kit/core'
import { IconPlus } from '@tabler/icons-react'
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
  /** 末尾「+ 添加列」：打开统一选择器 */
  onAddField: () => void
  onRowContextMenu: (key: string, x: number, y: number) => void
}

/**
 * 明细列版式：明细是**网格**不是格版式，故不进画布，单独一块。
 *
 * 这里用**真实表格的表头**横着铺开（对齐运行态明细网格的观感），而不是竖排列表：
 * 设计者看到的列顺序、列名宽度关系与运行态一致。只渲染 thead——明细没有格版式，
 * 行内容、占位、复合格、页签、分节在这里都不存在。
 * 拖动调整顺序，右键精修（隐藏列 / 恢复该列默认）。
 */
export default function DetailColumnPanel({
  table,
  rows,
  selectedKey,
  preview,
  draggingKey,
  dropTarget,
  onSelect,
  onAddField,
  onRowContextMenu,
}: DetailColumnPanelProps) {
  return (
    <section className="card erp-detail-card erp-designer-detail">
      <div className="card-header erp-detail-toolbar">
        <div className="d-flex gap-2 w-100 align-items-center">
          <span className="fw-semibold small">明细列（{table}）</span>
          <span className="erp-designer-muted ms-auto">{rows.length} 列 · 拖动调整顺序，右键精修</span>
        </div>
      </div>
      <div className="table-responsive">
        <table className="table table-vcenter card-table erp-data-table erp-designer-detail-table">
          <thead>
            <tr>
              {rows.map(row => (
                <DetailHeadCell
                  key={row.key}
                  row={row}
                  selected={selectedKey === row.key}
                  dragging={draggingKey === row.key}
                  preview={preview}
                  dropClass={
                    dropTarget?.kind === 'insert' && dropTarget.key === row.key
                      ? dropTarget.before ? 'is-drop-before' : 'is-drop-after'
                      : ''
                  }
                  onSelect={onSelect}
                  onContextMenu={preview ? undefined : (x, y) => onRowContextMenu(row.key, x, y)}
                />
              ))}
              {!preview ? (
                <th className="erp-designer-add-th">
                  <button type="button" title="添加明细列（打开统一选择器）" onClick={onAddField}>
                    <IconPlus size={14} />
                    添加列
                  </button>
                </th>
              ) : null}
            </tr>
          </thead>
        </table>
      </div>
    </section>
  )
}

function DetailHeadCell({
  row,
  selected,
  dragging,
  preview,
  dropClass,
  onSelect,
  onContextMenu,
}: {
  row: DesignRow
  selected: boolean
  dragging: boolean
  preview: boolean
  dropClass: string
  onSelect: (key: string) => void
  onContextMenu?: (x: number, y: number) => void
}) {
  const { setNodeRef: setDragRef, attributes, listeners } = useDraggable({
    id: dragId({ from: 'canvas', table: 'detail', key: row.key }),
    data: { kind: 'field', key: row.key },
    disabled: preview,
  })
  const { setNodeRef: setDropRef } = useDroppable({
    id: `cell-drop:detail:${row.key}`,
    data: { kind: 'cell', key: row.key },
  })
  const classes = ['erp-designer-detail-head']
  if (selected) classes.push('is-selected')
  if (dragging) classes.push('is-dragging')
  if (row.hidden) classes.push('is-hidden')
  if (dropClass) classes.push(dropClass)
  return (
    <th
      ref={node => {
        setDragRef(node)
        setDropRef(node)
      }}
      className={classes.join(' ')}
      data-designer-cell={row.key}
      onClick={() => {
        if (preview) return
        onSelect(row.key)
      }}
      onContextMenu={event => {
        if (!onContextMenu) return
        event.preventDefault()
        onSelect(row.key)
        onContextMenu(event.clientX, event.clientY)
      }}
      title={preview ? undefined : `${row.label}（拖动调整顺序，右键精修）`}
      {...attributes}
      {...listeners}
    >
      <span className="erp-designer-detail-label">{row.label}</span>
      <span className="erp-designer-badges">
        {row.required ? <em>必填</em> : null}
        {row.isPrimaryKey ? <em>主键</em> : null}
        {row.locked ? <em title={row.lockReason ?? ''}>🔒</em> : null}
        {!row.userVisible ? <em title="当前用户不可见">不可见</em> : null}
        {row.hidden ? <em className="is-hidden">已隐藏</em> : null}
      </span>
    </th>
  )
}
