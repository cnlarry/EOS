import type { ReactNode } from 'react'
import { ResizableTable } from './ResizableTable'

/**
 * 标准 ERP 列表表格外壳。
 *
 * 统一使用 DocumentWorkbench 主表列表表格的样式（`erp-data-table`：
 * 单元格内边距 5px、字号 12px、表头 nowrap、行高与工作台一致），供无法用
 * DocumentWorkbench 承载的列表页（如数据表/字段维护、后续新增列表）复用。
 *
 * 开启列宽调整：
 * ```tsx
 * <ErpDataTable resizable storageKey="my-list">
 *   <thead>...</thead>
 *   <tbody>...</tbody>
 * </ErpDataTable>
 * ```
 */
export function ErpDataTable({
  children,
  className = '',
  responsive = true,
  resizable = false,
  storageKey = '',
  persistResize = true,
  onColumnResize,
}: {
  children: ReactNode
  className?: string
  responsive?: boolean
  resizable?: boolean
  storageKey?: string
  persistResize?: boolean
  onColumnResize?: (columnKey: string, width: number) => void
}) {
  const table = (
    <ResizableTable
      className={`table table-vcenter card-table mb-0 erp-data-table ${className}`.trim()}
      storageKey={resizable ? storageKey : ''}
      persistResize={persistResize}
      onColumnResize={onColumnResize}
    >
      {children}
    </ResizableTable>
  )
  return responsive ? <div className="table-responsive">{table}</div> : table
}
