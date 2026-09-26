import { useDraggable, useDroppable } from '@dnd-kit/core'
import { IconPlus } from '@tabler/icons-react'
import { dragId, type DropTarget } from './formDesignerDrag'
import type { DesignRow } from './types'

interface DetailColumnPanelProps {
  table: string
  rows: DesignRow[]
  selectedKey: string | null
  draggingKey: string | null
  dropTarget: DropTarget
  onSelect: (key: string) => void
  /** 标题栏右侧「+ 添加列」：打开统一选择器 */
  onAddField: () => void
  onRowContextMenu: (key: string, x: number, y: number) => void
}

/**
 * 明细列版式：明细是**网格**不是格版式，故不进画布，单独一块。
 *
 * 这里用**真实表格**横着铺开（对齐运行态明细网格的观感），而不是竖排列表：
 * 一行表头（列名 = 运行态列名）+ 一行占位体（字段代号 = 主表画布同一套占位口径）。
 * 明细没有格版式：行内容、占位跨度、复合格、页签、分节在这里都不存在。
 * 拖动调整顺序，右键精修（移出表单 / 恢复该列默认排版）。
 *
 * 已移出表单的列**不在表头上显示**（移出后运行态也没有这一列）：它们退在字段池里，
 * 用标题栏右侧的「添加列」再选回来即原位放回。
 */
export default function DetailColumnPanel({
  table,
  rows,
  selectedKey,
  draggingKey,
  dropTarget,
  onSelect,
  onAddField,
  onRowContextMenu,
}: DetailColumnPanelProps) {
  const columns = rows.filter(row => !row.hidden)
  return (
    <section className="card erp-detail-card erp-designer-detail">
      <div className="card-header erp-detail-toolbar">
        <div className="d-flex gap-2 w-100 align-items-center">
          <span className="fw-semibold small">明细列（{table}）</span>
          <button
            type="button"
            className="erp-designer-add-col ms-auto"
            title="添加明细列（打开统一选择器）"
            onClick={onAddField}
          >
            <IconPlus size={14} />
            添加列
          </button>
        </div>
      </div>
      <div className="table-responsive">
        <table className="table table-vcenter card-table erp-data-table erp-designer-detail-table">
          <thead>
            <tr>
              {columns.map(row => (
                <DetailHeadCell
                  key={row.key}
                  row={row}
                  table={table}
                  selected={selectedKey === row.key}
                  dragging={draggingKey === row.key}
                  dropClass={
                    dropTarget?.kind === 'insert' && dropTarget.key === row.key
                      ? dropTarget.before ? 'is-drop-before' : 'is-drop-after'
                      : ''
                  }
                  onSelect={onSelect}
                  onContextMenu={(x, y) => onRowContextMenu(row.key, x, y)}
                />
              ))}
            </tr>
          </thead>
          {/* 一行占位体：列名在表头、字段代号在格里（与主表画布同一口径）。
              这一行也是实体列与虚拟列的样式落点——占位框本身就是区分标记，表头不必再挂角标 */}
          <tbody>
            <tr>
              {columns.map(row => (
                <DetailPlaceholderCell
                  key={row.key}
                  row={row}
                  table={table}
                  selected={selectedKey === row.key}
                  onSelect={onSelect}
                  onContextMenu={(x, y) => onRowContextMenu(row.key, x, y)}
                />
              ))}
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  )
}

/**
 * 明细的占位单元格：显示字段代号，与所在列的表头同属**一列**——点选、右键精修一起生效
 * （选中态由表头与占位格共同呈现，一列整体可辨）。
 * 拖动仍然只在表头上：表头是列的把手，占位格只是这一列的显示面。
 */
function DetailPlaceholderCell({
  row,
  table,
  selected,
  onSelect,
  onContextMenu,
}: {
  row: DesignRow
  table: string
  selected: boolean
  onSelect: (key: string) => void
  onContextMenu?: (x: number, y: number) => void
}) {
  const classes = ['erp-designer-detail-cell']
  if (selected) classes.push('is-selected')
  const fieldClasses = ['erp-designer-field']
  if (!row.userVisible) fieldClasses.push('is-denied')
  if (row.isVirtual) fieldClasses.push('is-virtual')
  const name = `${table}.${row.key}${row.isVirtual ? '（虚拟列）' : ''}`
  return (
    <td
      className={classes.join(' ')}
      onClick={() => onSelect(row.key)}
      onContextMenu={event => {
        if (!onContextMenu) return
        event.preventDefault()
        onSelect(row.key)
        onContextMenu(event.clientX, event.clientY)
      }}
    >
      <div className={fieldClasses.join(' ')} title={row.userVisible ? name : `${name}（当前用户不可见）`}>
        <span className="erp-designer-value">{row.key}</span>
      </div>
    </td>
  )
}

function DetailHeadCell({
  row,
  table,
  selected,
  dragging,
  dropClass,
  onSelect,
  onContextMenu,
}: {
  row: DesignRow
  table: string
  selected: boolean
  dragging: boolean
  dropClass: string
  onSelect: (key: string) => void
  onContextMenu?: (x: number, y: number) => void
}) {
  const { setNodeRef: setDragRef, attributes, listeners } = useDraggable({
    id: dragId({ from: 'canvas', table: 'detail', key: row.key }),
    data: { kind: 'field', key: row.key },
  })
  const { setNodeRef: setDropRef } = useDroppable({
    id: `cell-drop:detail:${row.key}`,
    data: { kind: 'cell', key: row.key },
  })
  const classes = ['erp-designer-detail-head']
  if (selected) classes.push('is-selected')
  if (dragging) classes.push('is-dragging')
  if (dropClass) classes.push(dropClass)
  return (
    <th
      ref={node => {
        setDragRef(node)
        setDropRef(node)
      }}
      className={classes.join(' ')}
      data-designer-cell={row.key}
      onClick={() => onSelect(row.key)}
      onContextMenu={event => {
        if (!onContextMenu) return
        event.preventDefault()
        onSelect(row.key)
        onContextMenu(event.clientX, event.clientY)
      }}
      title={`${table}.${row.key}（拖动调整顺序，右键精修）`}
      {...attributes}
      {...listeners}
    >
      <span className="erp-designer-detail-label">{row.label}</span>
      <span className="erp-designer-badges">
        {row.required ? <em>必填</em> : null}
        {row.isPrimaryKey ? <em>主键</em> : null}
        {row.locked ? <em title={row.lockReason ?? ''}>🔒</em> : null}
        {!row.userVisible ? <em title="当前用户不可见">不可见</em> : null}
      </span>
    </th>
  )
}
