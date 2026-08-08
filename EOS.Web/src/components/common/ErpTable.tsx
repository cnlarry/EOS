import { IconChevronDown, IconChevronUp } from '@tabler/icons-react'
import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnDef,
  type RowSelectionState,
  type SortingState,
  type VisibilityState,
} from '@tanstack/react-table'
import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { ErpDataTable } from './ErpDataTable'
import { ErpColumnFilter } from './ErpColumnFilter'
import { emptyQueryCondition, type QueryCondition } from './ErpQueryBuilder'
import { rowsToTsv, writeClipboard } from './tableClipboard'

const formatTotal = (value: number) => new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 }).format(value)

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
  /** 紧凑行高（erp-table-compact） */
  dense?: boolean
  /** 开启复制：Ctrl+C 复制选中/当前行，行右键菜单可复制本行/选中行 */
  copyable?: boolean
  /** 开启键盘导航：方向键/Home/End/PageUp/PageDown 移动活动行，Enter 触发行点击 */
  keyboardNavigation?: boolean
  /** 列头筛选值（列键 → 条件），配合 meta.filterable 与 onColumnFilterChange */
  columnFilterValue?: Record<string, QueryCondition>
  onColumnFilterChange?: (columnId: string, condition: QueryCondition | null) => void
  /** 当前筛选结果的数值列合计（列键 → 值），非空时渲染合计行 */
  totals?: Record<string, number | null>
  /** 表头拖拽重排完成回调（新列顺序，含 select/冻结列）；不传则不启用拖拽重排 */
  onColumnsReorder?: (columnIds: string[]) => void
}

/**
 * 统一 ERP 列表表格：TanStack Table 状态模型 + `erp-data-table` 样式外壳。
 *
 * 合并了原 DataTable（排序/选择/列显示）、ErpDataTable（样式）与
 * ResizableTable（列宽拖拽）的能力，并内置现代化网格交互：
 * - 复制：Ctrl+C / 行右键复制（TSV，可直接粘贴进 Excel）；
 * - 键盘导航：方向键/Home/End/PageUp/PageDown 移动活动行，Enter 触发；
 * - 列头快速筛选（meta.filterable + columnFilterValue / onColumnFilterChange）；
 * - 冻结列（meta.frozenLeft / meta.frozenRight，sticky）。
 *
 * 列级行为通过 ColumnMeta 扩展：
 * - `className`：td/th 追加类（对齐、选择列等）；
 * - `cellClassName`：仅作用于数据单元格（优先级高于 className）；
 * - `minWidth`：表头最小列宽（工作台 DISPLAY_LENGTH）；
 * - `filterable` / `frozenLeft` / `frozenRight`：列头筛选 / 冻结。
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
  dense = false,
  copyable = true,
  keyboardNavigation = true,
  columnFilterValue,
  onColumnFilterChange,
  totals,
  onColumnsReorder,
}: ErpTableProps<TData>) {
  const shellRef = useRef<HTMLDivElement>(null)
  const [focusIndex, setFocusIndex] = useState<number | null>(null)
  const [cellMenu, setCellMenu] = useState<{ x: number; y: number; rowId: string; columnId: string; text: string } | null>(null)
  const [openFilter, setOpenFilter] = useState<string | null>(null)
  const [openMenu, setOpenMenu] = useState<string | null>(null)
  const [menuPos, setMenuPos] = useState<{ left: number; top: number } | null>(null)
  const [dragColId, setDragColId] = useState<string | null>(null)
  const [draftFilter, setDraftFilter] = useState<QueryCondition>(emptyQueryCondition())

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

  const rowsModel = table.getRowModel().rows

  // 数据变化时钳制键盘焦点行号
  useEffect(() => {
    setFocusIndex((current) => (current === null ? null : Math.min(current, Math.max(rowsModel.length - 1, 0))))
  }, [rowsModel.length])

  // 键盘焦点行滚动到可视区
  useEffect(() => {
    if (focusIndex == null || !shellRef.current) return
    shellRef.current
      .querySelector(`tr[data-kb-index="${focusIndex}"]`)
      ?.scrollIntoView?.({ block: 'nearest' })
  }, [focusIndex])

  // 行右键菜单：点击别处关闭
  useEffect(() => {
    if (!cellMenu) return
    const close = () => setCellMenu(null)
    window.addEventListener('pointerdown', close)
    window.addEventListener('scroll', close, true)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('scroll', close, true)
    }
  }, [cellMenu])

  // 列头菜单：点击菜单外关闭（触发按钮除外）
  useEffect(() => {
    if (!openMenu) return
    const close = (event: PointerEvent) => {
      const target = event.target as Node
      if (!shellRef.current?.contains(target)) return
      if ((target as HTMLElement).closest?.('.erp-header-menu-trigger')) return
      if (shellRef.current.querySelector('.erp-header-menu')?.contains(target)) return
      setOpenMenu(null)
    }
    window.addEventListener('pointerdown', close)
    const closeOnScroll = () => setOpenMenu(null)
    window.addEventListener('scroll', closeOnScroll, true)
    return () => {
      window.removeEventListener('pointerdown', close)
      window.removeEventListener('scroll', closeOnScroll, true)
    }
  }, [openMenu])

  // 列头筛选弹层：点击表格其它位置关闭
  useEffect(() => {
    if (!openFilter) return
    const close = (event: PointerEvent) => {
      const target = event.target as Node
      if (!shellRef.current?.contains(target)) return
      if (shellRef.current.querySelector('.erp-column-filter-popover')?.contains(target)) return
      setOpenFilter(null)
    }
    window.addEventListener('pointerdown', close)
    return () => window.removeEventListener('pointerdown', close)
  }, [openFilter])

  const copyRows = (ids: string[]) => {
    const tableEl = shellRef.current?.querySelector('table')
    if (!ids.length || !tableEl) return
    const headers = Array.from(tableEl.querySelectorAll('thead th')).map((th) => (th.textContent ?? '').trim())
    const rows: string[][] = []
    tableEl.querySelectorAll('tbody tr').forEach((tr) => {
      if (!ids.includes(tr.getAttribute('data-order-id') ?? '')) return
      rows.push(Array.from(tr.querySelectorAll('td')).map((td) => (td.textContent ?? '').trim()))
    })
    if (rows.length === 0) return
    writeClipboard(rowsToTsv(headers, rows))
  }

  const copySelection = (): string[] => {
    const selected = Object.keys(rowSelection).filter((id) => rowSelection[id])
    if (selected.length > 0) return selected
    if (activeRowId) return [activeRowId]
    if (focusIndex != null && rowsModel[focusIndex]) return [rowsModel[focusIndex].id]
    return []
  }

  const moveFocus = (target: number) => {
    if (rowsModel.length === 0) return
    const clamped = Math.max(0, Math.min(target, rowsModel.length - 1))
    setFocusIndex(clamped)
    onRowClick?.(rowsModel[clamped].original)
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (keyboardNavigation && rowsModel.length > 0) {
      switch (event.key) {
        case 'ArrowDown': moveFocus((focusIndex ?? -1) + 1); event.preventDefault(); return
        case 'ArrowUp': moveFocus((focusIndex ?? rowsModel.length) - 1); event.preventDefault(); return
        case 'Home': moveFocus(0); event.preventDefault(); return
        case 'End': moveFocus(rowsModel.length - 1); event.preventDefault(); return
        case 'PageDown': moveFocus((focusIndex ?? 0) + 10); event.preventDefault(); return
        case 'PageUp': moveFocus((focusIndex ?? 0) - 10); event.preventDefault(); return
        case 'Enter':
          if (focusIndex != null && rowsModel[focusIndex]) {
            onRowClick?.(rowsModel[focusIndex].original)
            event.preventDefault()
          }
          return
      }
    }
    if (copyable && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c') {
      copyRows(copySelection())
      event.preventDefault()
    }
  }

  const openColumnFilter = (columnId: string) => {
    setDraftFilter(columnFilterValue?.[columnId] ?? emptyQueryCondition())
    setOpenFilter(columnId)
  }
  const applyColumnFilter = () => {
    if (openFilter) onColumnFilterChange?.(openFilter, { ...draftFilter, field: openFilter })
    setOpenFilter(null)
  }
  const clearColumnFilter = () => {
    if (openFilter) onColumnFilterChange?.(openFilter, null)
    setOpenFilter(null)
  }

  const cellColumn = cellMenu ? table.getColumn(cellMenu.columnId) : null
  const cellMeta = cellColumn?.columnDef.meta
  const isBit = cellMeta?.dataType === 'bit'
  const cellRowValue = cellMenu ? table.getRow(cellMenu.rowId)?.getValue(cellMenu.columnId) : undefined
  const filterValue = isBit ? (cellRowValue ? '1' : '0') : (cellMenu?.text ?? '')
  const showFilterMenu = Boolean(cellMenu && cellMeta?.filterable && onColumnFilterChange && (cellMenu.text || isBit))

  const openHeaderMenu = (columnId: string, trigger: HTMLElement) => {
    const rect = trigger.getBoundingClientRect()
    const menuWidth = 96
    let left = rect.left
    if (left + menuWidth > window.innerWidth - 8) left = Math.max(8, window.innerWidth - menuWidth - 8)
    setMenuPos({ left, top: rect.bottom + 4 })
    setOpenMenu(columnId)
  }

  const reorderColumns = (dragId: string, targetId: string) => {
    if (!onColumnsReorder) return
    const visibleIds = table.getVisibleLeafColumns().map((column) => column.id)
    const draggableIds = visibleIds.filter((id) => {
      const meta = table.getColumn(id)?.columnDef.meta
      return id !== 'select' && !meta?.frozenLeft && !meta?.frozenRight
    })
    const from = draggableIds.indexOf(dragId)
    const to = draggableIds.indexOf(targetId)
    if (from < 0 || to < 0) return
    const next = [...draggableIds]
    const [moved] = next.splice(from, 1)
    next.splice(to, 0, moved)
    let index = 0
    const order = visibleIds.map((id) => {
      const meta = table.getColumn(id)?.columnDef.meta
      if (id === 'select' || meta?.frozenLeft || meta?.frozenRight) return id
      return next[index++] ?? id
    })
    setDragColId(null)
    onColumnsReorder(order)
  }

  if (data.length === 0 && empty != null) return <>{empty}</>

  return (
    <div ref={shellRef} className="erp-table-shell" tabIndex={0} onKeyDown={handleKeyDown}>
      <ErpDataTable resizable={resizable} storageKey={storageKey} className={`${className} ${dense ? 'erp-table-compact' : ''}`.trim()} persistResize={persistResize} onColumnResize={onColumnResize}>
        <thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => {
                const meta = header.column.columnDef.meta
                const sorted = header.column.getIsSorted()
                const frozen = meta?.frozenLeft ? 'erp-frozen-left' : meta?.frozenRight ? 'erp-frozen-right' : ''
                const thStyle: CSSProperties = {}
                if (meta?.minWidth) thStyle.minWidth = meta.minWidth
                if (meta?.frozenLeft) thStyle.left = 0
                if (meta?.frozenRight) thStyle.right = 0
                const headerLabel = typeof header.column.columnDef.header === 'string'
                  ? header.column.columnDef.header
                  : header.column.id
                const filterActive = Boolean(columnFilterValue?.[header.column.id])
                const menuItems: { key: string; label: string; onClick: (event: MouseEvent<HTMLButtonElement>) => void }[] = []
                if (header.column.getCanSort() && onSortingChange) {
                  const applySort = (event: MouseEvent<HTMLButtonElement>, desc: boolean) => {
                    const id = header.column.id
                    const current = sorting
                    const existingIndex = current.findIndex((item) => item.id === id)
                    if (event.ctrlKey || event.metaKey) {
                      if (existingIndex >= 0) {
                        onSortingChange(current.map((item) => (item.id === id ? { id, desc } : item)))
                      } else {
                        onSortingChange([...current.slice(-4), { id, desc }])
                      }
                    } else {
                      onSortingChange([{ id, desc }])
                    }
                  }
                  menuItems.push(
                    { key: 'none', label: '默认', onClick: () => onSortingChange([]) },
                    { key: 'asc', label: '升序', onClick: (event) => applySort(event, false) },
                    { key: 'desc', label: '降序', onClick: (event) => applySort(event, true) },
                  )
                }
                if (meta?.filterable && onColumnFilterChange) {
                  menuItems.push({ key: 'filter', label: '筛选', onClick: () => openColumnFilter(header.column.id) })
                }
                for (const item of meta?.headerMenu ?? []) menuItems.push({ key: item.label, label: item.label, onClick: () => item.onClick() })
                const sortIndex = sorting.findIndex((item) => item.id === header.column.id)
                const draggable = Boolean(onColumnsReorder) && header.column.id !== 'select' && !meta?.frozenLeft && !meta?.frozenRight
                return (
                  <th
                    key={header.id}
                    data-col-key={header.column.id}
                    data-col-min-width={meta?.minWidth ?? undefined}
                    className={[meta?.className, meta?.headerClassName, frozen, openMenu === header.column.id ? 'erp-header-menu-open' : ''].filter(Boolean).join(' ') || undefined}
                    style={thStyle}
                  >
                    {header.isPlaceholder ? null : menuItems.length > 0 || draggable ? (
                      <div
                        className="erp-header-inner"
                        onDragOver={(event) => { if (draggable) event.preventDefault() }}
                        onDrop={(event) => {
                          if (draggable && dragColId && dragColId !== header.column.id) {
                            event.preventDefault()
                            reorderColumns(dragColId, header.column.id)
                          }
                        }}
                      >
                        <span
                          className="erp-header-label"
                          draggable={draggable}
                          onDragStart={(event) => {
                            if (!draggable) return
                            if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move'
                            setDragColId(header.column.id)
                          }}
                          onDragEnd={() => setDragColId(null)}
                        >
                          {flexRender(header.column.columnDef.header, header.getContext())}
                        </span>
                        <button
                          type="button"
                          className={`erp-header-menu-trigger ${sorted ? 'is-active' : ''} ${filterActive ? 'is-filtered' : ''}`}
                          aria-label={`表头操作${headerLabel}`}
                          title="排序 / 筛选"
                          onClick={(event) => (openMenu === header.column.id ? setOpenMenu(null) : openHeaderMenu(header.column.id, event.currentTarget))}
                        >
                          {sorted === 'asc' ? <IconChevronUp size={13} /> : <IconChevronDown size={13} />}
                          {sorting.length > 1 && sortIndex >= 0 && (
                            <span className="erp-sort-priority">{sortIndex + 1}</span>
                          )}
                        </button>
                      </div>
                    ) : (
                      flexRender(header.column.columnDef.header, header.getContext())
                    )}
                    {openMenu === header.column.id && (
                      <div
                        className="erp-header-menu dropdown-menu show"
                        style={{ position: 'fixed', left: menuPos?.left ?? 0, top: menuPos?.top ?? 0 }}
                        onClick={(event) => event.stopPropagation()}
                      >
                        {menuItems.map((item) => (
                          <button
                            key={item.key}
                            type="button"
                            className="dropdown-item"
                            onClick={(event) => {
                              item.onClick(event)
                              setOpenMenu(null)
                            }}
                          >
                            {item.label}
                          </button>
                        ))}
                      </div>
                    )}
                    {openFilter === header.column.id && (
                      <div className="erp-column-filter-popover" onClick={(event) => event.stopPropagation()}>
                        <ErpColumnFilter
                          condition={draftFilter}
                          onChange={setDraftFilter}
                          onApply={applyColumnFilter}
                          onClear={clearColumnFilter}
                        />
                      </div>
                    )}
                  </th>
                )
              })}
            </tr>
          ))}
        </thead>
        <tbody>
          {rowsModel.map((row, index) => {
            return (
              <tr
                key={row.id}
                data-order-id={row.id}
                data-kb-index={index}
                className={`${row.getIsSelected() ? 'table-active ' : ''}${activeRowId === row.id ? 'erp-row-active ' : ''}${focusIndex === index ? 'erp-row-focus' : ''}`.trim() || undefined}
                onClick={() => { setFocusIndex(null); onRowClick?.(row.original) }}
              >
                {row.getVisibleCells().map((cell) => {
                  const cellMeta = cell.column.columnDef.meta
                  const cellFrozen = cellMeta?.frozenLeft ? 'erp-frozen-left' : cellMeta?.frozenRight ? 'erp-frozen-right' : ''
                  return (
                    <td
                      key={cell.id}
                      className={[cellMeta?.cellClassName ?? cellMeta?.className, cellFrozen].filter(Boolean).join(' ') || undefined}
                      style={cellMeta?.frozenLeft ? { left: 0 } : cellMeta?.frozenRight ? { right: 0 } : undefined}
                      onContextMenu={copyable ? (event) => {
                        event.preventDefault()
                        setCellMenu({
                          x: event.clientX,
                          y: event.clientY,
                          rowId: row.id,
                          columnId: cell.column.id,
                          text: (event.currentTarget.textContent ?? '').trim(),
                        })
                      } : undefined}
                    >
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  )
                })}
              </tr>
            )
          })}
        </tbody>
        {totals && Object.values(totals).some((value) => value != null) && (
          <tfoot>
            <tr>
              {table.getVisibleLeafColumns().map((column, index) => {
                const meta = column.columnDef.meta
                const value = totals[column.id]
                return (
                  <td key={column.id} className={meta?.cellClassName ?? meta?.className}>
                    {index === 0 ? '合计' : value != null ? formatTotal(value) : ''}
                  </td>
                )
              })}
            </tr>
          </tfoot>
        )}
      </ErpDataTable>
      {cellMenu && copyable && (
        <div
          className="dropdown-menu show erp-table-context-menu"
          style={{ position: 'fixed', left: cellMenu.x, top: cellMenu.y, zIndex: 1100 }}
          onPointerDown={(event) => event.stopPropagation()}
        >
          <button className="dropdown-item" onClick={() => { writeClipboard(cellMenu.text); setCellMenu(null) }}>
            复制单元格
          </button>
          <button className="dropdown-item" onClick={() => { copyRows([cellMenu.rowId]); setCellMenu(null) }}>
            复制本行
          </button>
          <button
            className="dropdown-item"
            onClick={() => {
              const selected = Object.keys(rowSelection).filter((id) => rowSelection[id])
              copyRows(selected.length > 0 ? selected : [cellMenu.rowId])
              setCellMenu(null)
            }}
          >
            复制选中行
          </button>
          {showFilterMenu && (
            <>
              <div className="dropdown-divider" />
              {isBit ? (
                <button className="dropdown-item" onClick={() => {
                  onColumnFilterChange!(cellMenu.columnId, { field: cellMenu.columnId, operator: 'eq', value: filterValue, valueTo: '', logic: 'and' })
                  setCellMenu(null)
                }}>
                  筛选：等于（{cellRowValue ? '选中' : '未选中'}）
                </button>
              ) : (
                <>
                  <button className="dropdown-item" onClick={() => {
                    onColumnFilterChange!(cellMenu.columnId, { field: cellMenu.columnId, operator: 'eq', value: filterValue, valueTo: '', logic: 'and' })
                    setCellMenu(null)
                  }}>
                    筛选：等于“{filterValue.slice(0, 12)}{filterValue.length > 12 ? '…' : ''}”
                  </button>
                  <button className="dropdown-item" onClick={() => {
                    onColumnFilterChange!(cellMenu.columnId, { field: cellMenu.columnId, operator: 'contains', value: filterValue, valueTo: '', logic: 'and' })
                    setCellMenu(null)
                  }}>
                    筛选：包含“{filterValue.slice(0, 12)}{filterValue.length > 12 ? '…' : ''}”
                  </button>
                </>
              )}
            </>
          )}
        </div>
      )}
    </div>
  )
}

declare module '@tanstack/react-table' {
  interface ColumnMeta<TData, TValue> {
    className?: string
    headerClassName?: string
    /** 仅作用于数据单元格（优先级高于 className） */
    cellClassName?: string
    minWidth?: number
    /** 该列支持列头快速筛选（需配合 ErpTable 的 onColumnFilterChange） */
    filterable?: boolean
    /** 字段数据类型（如 bit），用于单元格右键筛选等特殊处理 */
    dataType?: string
    /** 列头菜单附加项（如工作台「字段设置」） */
    headerMenu?: { label: string; onClick: () => void }[]
    /** 冻结在左侧（sticky left，建议仅首列） */
    frozenLeft?: boolean
    /** 冻结在右侧（sticky right，建议仅末列） */
    frozenRight?: boolean
  }
}
