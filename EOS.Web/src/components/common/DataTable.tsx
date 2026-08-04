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
import { EmptyState } from './AsyncState'

interface DataTableProps<TData> {
  columns: ColumnDef<TData, unknown>[]
  data: TData[]
  getRowId?: (row: TData) => string
  emptyTitle?: string
  emptyDescription?: string
  sorting?: SortingState
  onSortingChange?: (sorting: SortingState) => void
  rowSelection?: RowSelectionState
  onRowSelectionChange?: (selection: RowSelectionState) => void
  columnVisibility?: VisibilityState
  onColumnVisibilityChange?: (visibility: VisibilityState) => void
  onRowClick?: (row: TData) => void
  activeRowId?: string
}

export function DataTable<TData>({
  columns,
  data,
  getRowId,
  emptyTitle,
  emptyDescription,
  sorting = [],
  onSortingChange,
  rowSelection = {},
  onRowSelectionChange,
  columnVisibility = {},
  onColumnVisibilityChange,
  onRowClick,
  activeRowId,
}: DataTableProps<TData>) {
  const table = useReactTable({
    data,
    columns,
    state: { sorting, rowSelection, columnVisibility },
    manualSorting: true,
    enableRowSelection: true,
    getCoreRowModel: getCoreRowModel(),
    getRowId,
    onSortingChange: (updater) => onSortingChange?.(typeof updater === 'function' ? updater(sorting) : updater),
    onRowSelectionChange: (updater) => onRowSelectionChange?.(typeof updater === 'function' ? updater(rowSelection) : updater),
    onColumnVisibilityChange: (updater) => onColumnVisibilityChange?.(typeof updater === 'function' ? updater(columnVisibility) : updater),
  })

  if (data.length === 0) return <EmptyState title={emptyTitle} description={emptyDescription} />

  return (
    <div className="table-responsive">
      <table className="table table-vcenter card-table erp-data-table">
        <thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => {
                const sorted = header.column.getIsSorted()
                return (
                  <th key={header.id} className={header.column.columnDef.meta?.className}>
                    {header.isPlaceholder ? null : header.column.getCanSort() ? (
                      <button className="erp-sort-button" type="button" onClick={header.column.getToggleSortingHandler()}>
                        {flexRender(header.column.columnDef.header, header.getContext())}
                        {sorted === 'asc' ? <IconChevronUp size={14} /> : sorted === 'desc' ? <IconChevronDown size={14} /> : <IconArrowsSort size={14} />}
                      </button>
                    ) : flexRender(header.column.columnDef.header, header.getContext())}
                  </th>
                )
              })}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map((row) => (
            <tr key={row.id} className={`${row.getIsSelected() ? 'table-active ' : ''}${activeRowId === row.id ? 'erp-row-active' : ''}`} onClick={() => onRowClick?.(row.original)} data-order-id={row.id}>
              {row.getVisibleCells().map((cell) => (
                <td key={cell.id} className={cell.column.columnDef.meta?.className}>
                  {flexRender(cell.column.columnDef.cell, cell.getContext())}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

declare module '@tanstack/react-table' {
  interface ColumnMeta<TData, TValue> {
    className?: string
  }
}
