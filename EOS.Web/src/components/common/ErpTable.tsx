import { IconChevronDown, IconChevronUp } from '@tabler/icons-react'
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type CellData,
  type ColumnDef,
  type RowData,
  type RowSelectionState,
  type SortingState,
  type TableFeatures,
  type VisibilityState,
} from '../../lib/tanstackTable'
// 仅此处需要 v9 原始的行数据约束（模块增强的泛型必须与 v9 声明逐字一致），桥接层对外给的是放宽口径
import type { RowData as CoreRowData } from '@tanstack/react-table'
import { useCallback, useEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { ErpDataTable } from './ErpDataTable'
import { ErpColumnFilter } from './ErpColumnFilter'
import { emptyQueryCondition, type QueryCondition } from './queryCondition'
import { rowsToTsv, writeClipboard } from './tableClipboard'
import { useMenuPlacement } from './useMenuPlacement'

/** 行窗口化阈值：少于该行数的数据直接全量渲染（避免小表/布局测量开销） */
const VIRTUAL_MIN_ROWS = 80
/** 可视区上/下行数缓冲，保证滚动过程中新入视口的行已被渲染 */
const VIRTUAL_OVERSCAN = 12

interface ErpTableProps<TData extends RowData> {
  columns: ColumnDef<TData, unknown>[]
  data: TData[]
  getRowId?: (row: TData, index: number) => string
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
  /** 点击行内任意处选中该行（单选语义：清除其它行选中；已单独选中时再次点击取消选中）；
   *  交互控件（输入框/按钮/链接/选择器等）除外；首列复选框仍可多选 */
  rowClickSingleSelect?: boolean
  /** 开启列宽拖拽（配合 storageKey 按用户与列表持久化到 localStorage） */
  resizable?: boolean
  storageKey?: string
  /** 是否把拖拽结果持久化到 localStorage；false 时宽度以服务端字段为唯一来源 */
  persistResize?: boolean
  /** 拖拽结束/双击自适应时回调（列键 + 新宽度），用于写回服务端字段元数据 */
  onColumnResize?: (columnKey: string, width: number) => void
  /** 外部触发句柄：暴露“自适应全部列宽”函数（返回各列新宽度，工具栏按钮批量写回） */
  fitRef?: { current: (() => Record<string, number>) | null }
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
  /** 表头拖拽重排完成回调（新列顺序，含 select/冻结列）；不传则不启用拖拽重排 */
  onColumnsReorder?: (columnIds: string[]) => void
  /** 客户端本地排序（TanStack 内置排序；服务端排序场景保持默认 false，由外部排序后传 data） */
  clientSideSorting?: boolean
  /** 行自定义类名（如占位行 erp-detail-filler） */
  rowClassName?: (row: TData) => string | undefined
  /** 行双击回调（如选择器双击确认） */
  onRowDoubleClick?: (row: TData) => void
  /** 是否外包 .table-responsive（默认 true；选择器等需要自定义滚动容器时传 false） */
  responsive?: boolean
  /** 滚动到底自动加载更多：滚动容器（shell 最近的 overflow 祖先）接近底部时触发；配合 hasMore/loadingMore 使用 */
  onEndReached?: () => void
  /** 是否还有下一页可加载（false 时停止监听并显示“已加载全部”） */
  hasMore?: boolean
  /** 加载更多进行中（防重复触发，可显示“正在加载更多…”） */
  loadingMore?: boolean
  /** 大数据行窗口化：行数超过阈值且滚动容器可滚时只渲染可视区行（+overscan）。
   *  启用后屏幕外行不在 DOM，复制等取文本操作优先使用列 meta.copyText。 */
  virtualize?: boolean
}

/**
 * 统一 ERP 列表表格：TanStack Table 状态模型 + `erp-data-table` 样式外壳。
 *
 * 合并了原 DataTable（排序/选择/列显示）、ErpDataTable（样式）与
 * ResizableTable（列宽拖拽）的能力，并内置现代化网格交互：
 * - 复制：Ctrl+C / 行右键复制（TSV，可直接粘贴进 Excel）；
 * - 键盘导航：方向键/Home/End/PageUp/PageDown 移动活动行，Enter 触发；
 * - 列头快速筛选（meta.filterable + columnFilterValue / onColumnFilterChange）；
 * - 冻结列（meta.frozenLeft / meta.frozenRight，sticky）；
 * - 客户端本地排序（clientSideSorting，适合本地数据网格）；
 * - 行自定义类名（rowClassName，如占位行）与行双击回调（onRowDoubleClick）。
 *
 * 列级行为通过 ColumnMeta 扩展：
 * - `className`：td/th 追加类；
 * - `cellClassName`：仅作用于数据单元格（优先级高于 className）；
 * - `minWidth`：表头最小列宽（工作台 DISPLAY_LENGTH）；
 * - `filterable` / `frozenLeft` / `frozenRight`：列头筛选 / 冻结。
 */
export function ErpTable<TData>({
  columns,
  data,
  getRowId,
  empty,
  sorting,
  onSortingChange,
  rowSelection = {},
  onRowSelectionChange,
  columnVisibility = {},
  onColumnVisibilityChange,
  onRowClick,
  activeRowId,
  rowClickSingleSelect = false,
  resizable = false,
  storageKey = '',
  persistResize = true,
  onColumnResize,
  fitRef,
  className = '',
  dense = false,
  copyable = true,
  keyboardNavigation = true,
  columnFilterValue,
  onColumnFilterChange,
  onColumnsReorder,
  clientSideSorting = false,
  rowClassName,
  onRowDoubleClick,
  responsive = true,
  onEndReached,
  hasMore = false,
  loadingMore = false,
  virtualize = false,
}: ErpTableProps<TData>) {
  const shellRef = useRef<HTMLDivElement>(null)
  const sentinelRef = useRef<HTMLDivElement | null>(null)
  const [focusIndex, setFocusIndex] = useState<number | null>(null)
  const [cellMenu, setCellMenu] = useState<{ x: number; y: number; rowId: string; columnId: string; text: string } | null>(null)
  // 单元格右键菜单视口定位：贴近屏幕右/下缘时向内收，下方放不下则翻到落点上方
  const cellMenuPlacement = useMenuPlacement(cellMenu?.x ?? 0, cellMenu?.y ?? 0, Boolean(cellMenu && copyable))
  const [openFilter, setOpenFilter] = useState<string | null>(null)
  const [openMenu, setOpenMenu] = useState<string | null>(null)
  const [menuPos, setMenuPos] = useState<{ left: number; top: number } | null>(null)
  const [dragColId, setDragColId] = useState<string | null>(null)
  const [draftFilter, setDraftFilter] = useState<QueryCondition>(emptyQueryCondition())
  // 未受控排序：外部未传 sorting/onSortingChange 时用内部稳定状态，
  // 避免默认空数组每次渲染都是新引用导致 TanStack 状态循环（卡死路由过渡）
  const [internalSorting, setInternalSorting] = useState<SortingState>([])
  const resolvedSorting = sorting ?? internalSorting
  const updateSorting = (updater: SortingState | ((current: SortingState) => SortingState)) => {
    const next = typeof updater === 'function' ? updater(resolvedSorting) : updater
    if (onSortingChange) onSortingChange(next)
    else setInternalSorting(next)
  }
  const onEndReachedRef = useRef(onEndReached)
  onEndReachedRef.current = onEndReached
  const loadingMoreRef = useRef(loadingMore)
  loadingMoreRef.current = loadingMore
  const hasMoreRef = useRef(hasMore)
  hasMoreRef.current = hasMore
  const [showAllLoaded, setShowAllLoaded] = useState(false)
  const allLoadedTimerRef = useRef<number | null>(null)
  const seenBottomRef = useRef(false)
  const wasLoadingMoreRef = useRef(false)
  // 行窗口化（virtualize）：滚动容器、实测行高与当前可视窗口
  const scrollContainerRef = useRef<HTMLElement | null>(null)
  const rowHeightRef = useRef(27)
  const [virtualActive, setVirtualActive] = useState(false)
  const [virtualWindow, setVirtualWindow] = useState({ start: 0, end: 0 })

  /** 短暂提示“已加载全部”（滚动触发加载完成或再次尝试滚动到底时闪现后消失） */
  const flashAllLoaded = () => {
    setShowAllLoaded(true)
    if (allLoadedTimerRef.current !== null) window.clearTimeout(allLoadedTimerRef.current)
    allLoadedTimerRef.current = window.setTimeout(() => setShowAllLoaded(false), 1500)
  }

  // 滚动触发的加载完成后（最后一页）短暂提示；再次滚动尝试到底时由下方观察器再提示
  useEffect(() => {
    if (wasLoadingMoreRef.current && !loadingMore && !hasMore) flashAllLoaded()
    wasLoadingMoreRef.current = loadingMore
    return () => {
      if (allLoadedTimerRef.current !== null) window.clearTimeout(allLoadedTimerRef.current)
    }
  }, [loadingMore, hasMore])

  // 滚动加载更多：以 shell 最近的 overflow 祖先为 root，底部哨兵进入视口（含 120px 提前量）时触发
  useEffect(() => {
    if (!onEndReached) return
    const sentinel = sentinelRef.current
    if (!sentinel) return
    let root: Element | Document | null = null
    let current: HTMLElement | null = sentinel
    while (current) {
      const style = getComputedStyle(current)
      if (/(auto|scroll|overlay)/.test(style.overflowY) || /(auto|scroll|overlay)/.test(style.overflow)) {
        root = current
        break
      }
      current = current.parentElement
    }
    const observer = new IntersectionObserver((entries) => {
      if (loadingMoreRef.current) return
      if (!entries.some(entry => entry.isIntersecting)) return
      if (hasMoreRef.current) {
        onEndReachedRef.current?.()
      } else if (seenBottomRef.current) {
        // 全部加载后用户再次尝试滚动到底：短暂提示一次
        flashAllLoaded()
      }
      seenBottomRef.current = true
    }, { root, rootMargin: '120px 0px' })
    observer.observe(sentinel)
    return () => observer.disconnect()
  }, [onEndReached])

  const table = useReactTable({
    // 桥接层的列定义把行类型映射成 `TData & RowData`（为满足 v9 的索引签名约束），
    // 表格实例推出的行类型因此比入参窄一层：入参与选择状态各窄化一次。运行期无差别。
    data: data as (TData & CoreRowData)[],
    columns,
    // v9 的行选择状态只记被选中的行（Record<string, true>），本仓按 v8 记 true/false；
    // 多出来的 false 项在 v9 里是假值、不参与判定，故此处直接窄化即可。
    state: { sorting: resolvedSorting, rowSelection: rowSelection as Record<string, true>, columnVisibility },
    manualSorting: !clientSideSorting,
    enableRowSelection: true,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: clientSideSorting ? getSortedRowModel() : undefined,
    getRowId,
    onSortingChange: updateSorting,
    onRowSelectionChange: (updater) =>
      onRowSelectionChange?.(typeof updater === 'function' ? updater(rowSelection as Record<string, true>) : updater),
    onColumnVisibilityChange: (updater) =>
      onColumnVisibilityChange?.(typeof updater === 'function' ? updater(columnVisibility) : updater),
  })

  const rowsModel = table.getRowModel().rows

  // 可见列与其冻结偏移：冻结组宽度按列键累计（选择列固定 33px，其余用 DISPLAY_LENGTH），
  // 使 select + 若干业务列同处左侧/右侧时 sticky 偏移互不重叠
  const visibleColumns = table.getVisibleLeafColumns()
  const cellCount = visibleColumns.length
  const frozenOffsets = (() => {
    const leftByKey = new Map<string, number>()
    const rightByKey = new Map<string, number>()
    let left = 0
    let right = 0
    for (const column of visibleColumns) {
      const meta = column.columnDef.meta
      if (meta?.frozenLeft) {
        leftByKey.set(column.id, left)
        left += column.id === 'select' ? 33 : meta.minWidth ?? 100
      }
    }
    for (let i = visibleColumns.length - 1; i >= 0; i--) {
      const column = visibleColumns[i]!
      const meta = column.columnDef.meta
      if (meta?.frozenRight) {
        rightByKey.set(column.id, right)
        right += meta.minWidth ?? 100
      }
    }
    return { leftByKey, rightByKey }
  })()

  // 行窗口化当前渲染区间（非虚拟时即全量）
  const virtualStart = virtualActive ? Math.min(virtualWindow.start, rowsModel.length) : 0
  const virtualEnd = virtualActive ? Math.min(virtualWindow.end, rowsModel.length) : rowsModel.length
  const renderedRows = virtualActive ? rowsModel.slice(virtualStart, virtualEnd) : rowsModel

  // 数据变化时钳制键盘焦点行号
  useEffect(() => {
    setFocusIndex((current) => (current === null ? null : Math.min(current, Math.max(rowsModel.length - 1, 0))))
  }, [rowsModel.length])

  // 键盘焦点行滚动到可视区（虚拟化下按行高换算容器滚动位置）
  useEffect(() => {
    if (focusIndex == null) return
    if (virtualActive) {
      const container = scrollContainerRef.current
      const height = rowHeightRef.current
      if (!container || height <= 0) return
      const top = focusIndex * height
      const bottom = top + height
      const viewTop = container.scrollTop
      const viewBottom = viewTop + container.clientHeight
      if (top < viewTop) container.scrollTop = top
      else if (bottom > viewBottom) container.scrollTop = bottom - container.clientHeight
      return
    }
    shellRef.current
      ?.querySelector(`tr[data-kb-index="${focusIndex}"]`)
      ?.scrollIntoView?.({ block: 'nearest' })
  }, [focusIndex, virtualActive])

  // 行窗口化：定位滚动容器（shell 最近的 overflow 祖先，与滚动加载共用同一容器）
  useEffect(() => {
    if (!virtualize) {
      scrollContainerRef.current = null
      return
    }
    let node: HTMLElement | null = shellRef.current
    while (node) {
      const style = getComputedStyle(node)
      if (/(auto|scroll|overlay)/.test(style.overflowY) || /(auto|scroll|overlay)/.test(style.overflow)) {
        scrollContainerRef.current = node
        break
      }
      node = node.parentElement
    }
  }, [virtualize])

  // 按当前滚动位置计算并更新可视窗口（起始行 + 可视行数 + 上下 overscan）
  const applyScrollWindow = useCallback((container: HTMLElement, total: number) => {
    const height = rowHeightRef.current
    if (height <= 0 || total <= 0) return
    const viewport = container.clientHeight
    if (viewport <= 0) {
      setVirtualWindow({ start: 0, end: Math.min(total, VIRTUAL_OVERSCAN * 2 + 10) })
      return
    }
    const start = Math.max(0, Math.floor(container.scrollTop / height) - VIRTUAL_OVERSCAN)
    const end = Math.min(total, start + Math.ceil(viewport / height) + VIRTUAL_OVERSCAN * 2)
    setVirtualWindow((current) => (current.start === start && current.end === end ? current : { start, end }))
  }, [])

  // 行数变化后重估窗口化：行数不足 / 容器不可滚时回退全量渲染
  useEffect(() => {
    if (!virtualize) return
    const container = scrollContainerRef.current
    const total = rowsModel.length
    if (!container || total === 0) return
    if (total < VIRTUAL_MIN_ROWS) {
      setVirtualActive(false)
      return
    }
    const sample = container.querySelector('tbody tr:not(.erp-virtual-spacer)') as HTMLElement | null
    const measured = sample ? sample.offsetHeight : 0
    if (measured > 0) rowHeightRef.current = measured
    // 容器暂无布局高度（所在标签被隐藏）时不能据此判定“无需窗口化”，否则恢复可见后不会自恢复
    if (container.clientHeight === 0) return
    if (container.scrollHeight <= container.clientHeight + 4) {
      setVirtualActive(false)
      return
    }
    if (!virtualActive) setVirtualActive(true)
    applyScrollWindow(container, total)
  }, [virtualize, rowsModel.length, virtualActive, applyScrollWindow])

  // 滚动 / 容器尺寸变化时更新可视窗口（行数变化时重新绑定以使用最新总行数）
  useEffect(() => {
    if (!virtualActive) return
    const container = scrollContainerRef.current
    if (!container) return
    const onScroll = () => applyScrollWindow(container, rowsModel.length)
    const onResize = () => applyScrollWindow(container, rowsModel.length)
    container.addEventListener('scroll', onScroll, { passive: true })
    window.addEventListener('resize', onResize)
    return () => {
      container.removeEventListener('scroll', onScroll)
      window.removeEventListener('resize', onResize)
    }
  }, [virtualActive, rowsModel.length, applyScrollWindow])

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
    if (!ids.length) return
    const tableEl = shellRef.current?.querySelector('table')
    if (!tableEl) return
    const headers = Array.from(tableEl.querySelectorAll('thead th')).map((th) => (th.textContent ?? '').trim())
    // 已渲染行走 DOM（与显示一致）；虚拟化下屏幕外行不在 DOM，按列 meta.copyText / 原始值生成
    const domRows = new Map<string, HTMLTableRowElement>()
    for (const tr of Array.from(tableEl.querySelectorAll('tbody tr[data-order-id]'))) {
      const orderId = tr.getAttribute('data-order-id')
      if (orderId) domRows.set(orderId, tr as HTMLTableRowElement)
    }
    const rows: string[][] = []
    for (const id of ids) {
      const row = rowsModel.find((candidate) => candidate.id === id)
      if (!row) continue
      const tr = domRows.get(id)
      const domCells = tr ? Array.from(tr.querySelectorAll(':scope > td')) : []
      const line = row.getVisibleCells().map((cell, cellIndex) => {
        const copyText = cell.column.columnDef.meta?.copyText
        if (copyText) {
          const text = copyText({ value: cell.getValue(), row: row.original })
          if (text !== undefined) return text
        }
        const domText = domCells[cellIndex]?.textContent?.trim()
        if (domText != null && domText !== '') return domText
        const raw = cell.getValue()
        return typeof raw === 'string' || typeof raw === 'number' ? String(raw) : ''
      })
      rows.push(line)
    }
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
            if (rowClickSingleSelect) {
              const focusRow = rowsModel[focusIndex]
              const isSelected = focusRow.getIsSelected()
              const onlySelected = isSelected && Object.keys(rowSelection).length === 1
              table.setRowSelection(onlySelected ? {} : { [focusRow.id]: true })
            }
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
      <ErpDataTable responsive={responsive} resizable={resizable} storageKey={storageKey} className={`${className} ${dense ? 'erp-table-compact' : ''}`.trim()} persistResize={persistResize} onColumnResize={onColumnResize} fitRef={fitRef}>
        <thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => {
                const meta = header.column.columnDef.meta
                const sorted = header.column.getIsSorted()
                const frozen = meta?.frozenLeft ? 'erp-frozen-left' : meta?.frozenRight ? 'erp-frozen-right' : ''
                const thStyle: CSSProperties = {}
                if (meta?.minWidth) thStyle.minWidth = meta.minWidth
                if (meta?.maxWidth) thStyle.maxWidth = meta.maxWidth
                if (meta?.frozenLeft) thStyle.left = frozenOffsets.leftByKey.get(header.column.id) ?? 0
                if (meta?.frozenRight) thStyle.right = frozenOffsets.rightByKey.get(header.column.id) ?? 0
                const headerLabel = typeof header.column.columnDef.header === 'string'
                  ? header.column.columnDef.header
                  : header.column.id
                const filterActive = Boolean(columnFilterValue?.[header.column.id])
                const menuItems: { key: string; label: string; onClick: (event: MouseEvent<HTMLButtonElement>) => void }[] = []
                if (header.column.getCanSort() && (onSortingChange || clientSideSorting)) {
                  const applySort = (event: MouseEvent<HTMLButtonElement>, desc: boolean) => {
                    const id = header.column.id
                    const current = resolvedSorting
                    const existingIndex = current.findIndex((item) => item.id === id)
                    if (event.ctrlKey || event.metaKey) {
                      if (existingIndex >= 0) {
                        updateSorting(current.map((item) => (item.id === id ? { id, desc } : item)))
                      } else {
                        updateSorting([...current.slice(-4), { id, desc }])
                      }
                    } else {
                      updateSorting([{ id, desc }])
                    }
                  }
                  menuItems.push(
                    { key: 'none', label: '默认', onClick: () => updateSorting([]) },
                    { key: 'asc', label: '升序', onClick: (event) => applySort(event, false) },
                    { key: 'desc', label: '降序', onClick: (event) => applySort(event, true) },
                  )
                }
                if (meta?.filterable && onColumnFilterChange) {
                  menuItems.push({ key: 'filter', label: '筛选', onClick: () => openColumnFilter(header.column.id) })
                }
                for (const item of meta?.headerMenu ?? []) menuItems.push({ key: item.label, label: item.label, onClick: () => item.onClick() })
                const sortIndex = resolvedSorting.findIndex((item) => item.id === header.column.id)
                const draggable = Boolean(onColumnsReorder) && header.column.id !== 'select' && !meta?.frozenLeft && !meta?.frozenRight
                return (
                  <th
                    key={header.id}
                    data-col-key={header.column.id}
                    data-col-min-width={meta?.minWidth ?? undefined}
                    data-col-max-width={meta?.maxWidth ?? undefined}
                    data-col-min-floor={meta?.minWidthFloor ? 'true' : undefined}
                    data-col-resizable={meta?.resizable === false ? 'false' : 'true'}
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
                          {resolvedSorting.length > 1 && sortIndex >= 0 && (
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
          {virtualActive && virtualStart > 0 && (
            <tr className="erp-virtual-spacer" aria-hidden="true">
              <td colSpan={cellCount} style={{ height: virtualStart * rowHeightRef.current }} />
            </tr>
          )}
          {renderedRows.map((row, offset) => {
            const index = virtualStart + offset
            const customClass = rowClassName?.(row.original)
            return (
              <tr
                key={row.id}
                data-order-id={row.id}
                data-kb-index={index}
                className={`${row.getIsSelected() ? 'table-active ' : ''}${activeRowId === row.id ? 'erp-row-active ' : ''}${focusIndex === index ? 'erp-row-focus' : ''}${customClass ? ` ${customClass}` : ''}`.trim() || undefined}
                onClick={(event) => {
                  setFocusIndex(null)
                  if (rowClickSingleSelect) {
                    const target = event.target as HTMLElement
                    if (!target.closest('input, button, a, select, textarea, label, [contenteditable="true"], .erp-col-resizer')) {
                      const isSelected = row.getIsSelected()
                      const onlySelected = isSelected && Object.keys(rowSelection).length === 1
                      table.setRowSelection(onlySelected ? {} : { [row.id]: true })
                    }
                  }
                  onRowClick?.(row.original)
                }}
                onDoubleClick={onRowDoubleClick ? () => onRowDoubleClick(row.original) : undefined}
              >
                {row.getVisibleCells().map((cell) => {
                  const cellMeta = cell.column.columnDef.meta
                  const cellFrozen = cellMeta?.frozenLeft ? 'erp-frozen-left' : cellMeta?.frozenRight ? 'erp-frozen-right' : ''
                  const cellStyle: CSSProperties = {}
                  if (cellMeta?.frozenLeft) cellStyle.left = frozenOffsets.leftByKey.get(cell.column.id) ?? 0
                  if (cellMeta?.frozenRight) cellStyle.right = frozenOffsets.rightByKey.get(cell.column.id) ?? 0
                  if (cellMeta?.maxWidth) cellStyle.maxWidth = cellMeta.maxWidth
                  const truncate = cellMeta?.truncate !== false
                  return (
                    <td
                      key={cell.id}
                      className={[cellMeta?.cellClassName ?? cellMeta?.className, cellFrozen].filter(Boolean).join(' ') || undefined}
                      style={Object.keys(cellStyle).length > 0 ? cellStyle : undefined}
                      onMouseEnter={(event) => {
                        // title 延迟到悬停才计算，避免大列表渲染热路径对每个单元格做格式化
                        if (cellMeta?.truncate === false) return
                        const raw = cell.getValue()
                        const next = cellMeta?.title != null
                          ? (typeof cellMeta.title === 'function'
                            ? cellMeta.title({ value: raw, row: row.original })
                            : cellMeta.title)
                          : (typeof raw === 'string' || typeof raw === 'number' ? String(raw) : undefined)
                        const current = event.currentTarget.getAttribute('title')
                        if (next == null || next === '') {
                          if (current) event.currentTarget.removeAttribute('title')
                        } else if (current !== next) {
                          event.currentTarget.setAttribute('title', next)
                        }
                      }}
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
                      {truncate
                        ? <span className="erp-cell-ellipsis">{flexRender(cell.column.columnDef.cell, cell.getContext())}</span>
                        : flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  )
                })}
              </tr>
            )
          })}
          {virtualActive && virtualEnd < rowsModel.length && (
            <tr className="erp-virtual-spacer" aria-hidden="true">
              <td colSpan={cellCount} style={{ height: (rowsModel.length - virtualEnd) * rowHeightRef.current }} />
            </tr>
          )}
        </tbody>
      </ErpDataTable>
      {onEndReached ? (
        <div ref={sentinelRef} className="erp-table-load-more">
          {loadingMore
            ? <span className="erp-table-load-more-hint">正在加载更多…</span>
            : showAllLoaded ? <span className="erp-table-load-more-hint">已加载全部</span> : null}
        </div>
      ) : null}
      {cellMenu && copyable && (
        <div
          ref={cellMenuPlacement.ref}
          className="dropdown-menu show erp-table-context-menu"
          style={{ position: 'fixed', left: cellMenuPlacement.left, top: cellMenuPlacement.top, zIndex: 1100 }}
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
  // 泛型参数必须与 v9 的声明一致（否则 TS2428）：v9 是
  // <TFeatures extends TableFeatures, TData extends RowData, TValue extends CellData>
  // 泛型参数必须与 v9 的声明逐字一致（含 in/out 修饰符），否则 TS2428
  interface ColumnMeta<in out TFeatures extends TableFeatures, in out TData extends CoreRowData, TValue extends CellData = CellData> {
    className?: string
    headerClassName?: string
    /** 仅作用于数据单元格（优先级高于 className） */
    cellClassName?: string
    minWidth?: number
    /** 该列最大宽度（自动列宽上限，超宽内容省略截断，避免撑爆表格） */
    maxWidth?: number
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
    /** 该列不允许拖拽调整列宽（如固定宽度的选择列/操作列） */
    resizable?: boolean
    /** 编辑态列宽下限：data-col-min-width 同时作为硬下限，历史/拖拽宽度不得低于它（如表单明细录入列） */
    minWidthFloor?: boolean
    /** 单元格单行省略（默认 true）：不换行 + 超宽省略号 + title 悬停全文；复选框/按钮/输入框等交互列设为 false */
    truncate?: boolean
    /** 悬停 title 全文：默认取单元格原始值；显示文本与原始值不同（如格式化）时用函数返回展示文本 */
    title?: string | ((info: { value: TValue; row: TData }) => string | undefined)
    /** 复制到剪贴板的文本（单元格/行/选中复制）。虚拟化下屏幕外行不在 DOM，
     *  提供后优先使用；未提供时回退已渲染单元格文本，再回退原始值。返回 undefined 继续回退。 */
    copyText?: (info: { value: TValue; row: TData }) => string | undefined
  }
}
