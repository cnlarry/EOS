import { IconArrowsSort, IconChevronDown, IconChevronUp } from '@tabler/icons-react'
import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnDef,
  type RowSelectionState,
  type SortingState,
  type VisibilityState,
} from '@tanstack/react-table'
import type { MouseEvent, ReactNode } from 'react'
import { ErpDataTable } from './ErpDataTable'

interface ErpTableProps<TData> {
  columns: ColumnDef<TData, unknown>[]
  data: TData[]
  getRowId?: (row: TData) => string
  /** 空数据时渲染的内容；不传或传 null 则渲染空表格 */
  empty?: ReactNode
  sorting?: SortingState
  onSortingChange?: (sorting: SortingState) => void
  rowSelection?: RowSelectionState
  onRowSelectionChange?: (selection: RowSelectionState) => void
  columnVisibility?: VisibilityState
  onColumnVisibilityChange?: (visibility: VisibilityState) => void
  onRowClick?: (row: TData) => void
  activeRowId?: string
  /** 开启列宽拖拽（配合 storageKey 按用户与列表持久化到 localStorage） */
  resizable?: boolean
  storageKey?: string
  /** 是否把拖拽结果持久化到 localStorage；false 时宽度以服务端字段为唯一来源 */
  persistResize?: boolean
  /** 拖拽结束/双击自适应时回调（列键 + 新宽度），用于写回服务端字段元数据 */
  onColumnResize?: (columnKey: string, width: number) => void
  /** 追加到表格的类名（如 table-sm 明细表） */
  className?: string
}

/**
 * 统一 ERP 列表表格：TanStack Table 状态模型 + `erp-data-table` 样式外壳。
 *
 * 合并了原 DataTable（排序/选择/列显示）、ErpDataTable（样式）与
 * ResizableTable（列宽拖拽）的能力，供 DocumentWorkbench 主/子表、
 * 领域列表与维护列表统一使用。
 *
 * 列级行为通过 ColumnMeta 扩展：
 * - `className`：td/th 追加类（对齐、选择列等）；
 * - `minWidth`：表头最小列宽（工作台 DISPLAY_LENGTH）；
 * - `onHeaderContextMenu`：表头右键（工作台字段设置）；
 * - `headerClassName`：仅作用于表头。
 */
export function ErpTable<TData>({
  columns,
  data,
  getRowId,
  empty,
  sorting = [],
  onSortingChange,
  rowSelection = {},
  onRowSelectionChange,
  columnVisibility = {},
  onColumnVisibilityChange,
  onRowClick,
  activeRowId,
  resizable = false,
  storageKey = '',
  persistResize = true,
  onColumnResize,
  className = '',
}: ErpTableProps<TData>) {
  const table = useReactTable({
    data,
    columns,
    state: { sorting, rowSelection, columnVisibility },
    manualSorting: true,
    enableRowSelection: true,
    getCoreRowModel: getCoreRowModel(),
    getRowId,
    onSortingChange: (updater) => onSortingChange?.(typeof updater === 'function' ? updater(sorting) : updater),
    onRowSelectionChange: (updater) =>
      onRowSelectionChange?.(typeof updater === 'function' ? updater(rowSelection) : updater),
    onColumnVisibilityChange: (updater) =>
      onColumnVisibilityChange?.(typeof updater === 'function' ? updater(columnVisibility) : updater),
  })

  if (data.length === 0 && empty != null) return <>{empty}</>

  return (
    <ErpDataTable resizable={resizable} storageKey={storageKey} className={className} persistResize={persistResize} onColumnResize={onColumnResize}>
      <thead>
        {table.getHeaderGroups().map((headerGroup) => (
          <tr key={headerGroup.id}>
            {headerGroup.headers.map((header) => {
              const meta = header.column.columnDef.meta
              const sorted = header.column.getIsSorted()
              return (
                <th
                  key={header.id}
                  data-col-key={header.column.id}
                  data-col-min-width={meta?.minWidth ?? undefined}
                  className={[meta?.className, meta?.headerClassName].filter(Boolean).join(' ') || undefined}
                  style={meta?.minWidth ? { minWidth: meta.minWidth } : undefined}
                  onContextMenu={meta?.onHeaderContextMenu}
                >
                  {header.isPlaceholder ? null : header.column.getCanSort() ? (
                    <button className="erp-sort-button" type="button" onClick={header.column.getToggleSortingHandler()}>
                      {flexRender(header.column.columnDef.header, header.getContext())}
                      {sorted === 'asc' ? (
                        <IconChevronUp size={14} />
                      ) : sorted === 'desc' ? (
                        <IconChevronDown size={14} />
                      ) : (
                        <IconArrowsSort size={14} />
                      )}
                    </button>
                  ) : (
                    flexRender(header.column.columnDef.header, header.getContext())
                  )}
                </th>
              )
            })}
          </tr>
        ))}
      </thead>
      <tbody>
        {table.getRowModel().rows.map((row) => (
          <tr
            key={row.id}
            className={`${row.getIsSelected() ? 'table-active ' : ''}${activeRowId === row.id ? 'erp-row-active' : ''}`.trim() || undefined}
            onClick={() => onRowClick?.(row.original)}
            data-order-id={row.id}
          >
            {row.getVisibleCells().map((cell) => (
              <td key={cell.id} className={cell.column.columnDef.meta?.cellClassName ?? cell.column.columnDef.meta?.className}>
                {flexRender(cell.column.columnDef.cell, cell.getContext())}
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </ErpDataTable>
  )
}

declare module '@tanstack/react-table' {
  interface ColumnMeta<TData, TValue> {
    className?: string
    headerClassName?: string
    /** 仅作用于数据单元格（优先级高于 className） */
    cellClassName?: string
    minWidth?: number
    onHeaderContextMenu?: (event: MouseEvent<HTMLTableCellElement>) => void
  }
}
