import { IconChevronDown, IconChevronUp } from '@tabler/icons-react'
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type CellData,
  type ColumnDef,
  type Row,
  type RowData,
  type RowSelectionState,
  type SortingState,
  type TableFeatures,
  type VisibilityState,
} from '../../lib/tanstackTable'
// 仅此处需要 v9 原始的行数据约束（模块增强的泛型必须与 v9 声明逐字一致），桥接层对外给的是放宽口径
import type { RowData as CoreRowData } from '@tanstack/react-table'
import { Fragment, memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ClipboardEvent, type KeyboardEvent, type MouseEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { ErpDataTable } from './ErpDataTable'
import { ErpColumnFilter } from './ErpColumnFilter'
import { emptyQueryCondition, type QueryCondition } from './queryCondition'
import { gridToTsv, parseClipboardGrid, rowsToTsv, writeClipboard } from './tableClipboard'
import { moveRange, normalizeRange, type CellPoint, type CellRange } from './tableRange'
import { useMenuPlacement } from './useMenuPlacement'

/** 行窗口化阈值：少于该行数的数据直接全量渲染（避免小表/布局测量开销） */
const VIRTUAL_MIN_ROWS = 80
/** 可视区上/下行数缓冲，保证滚动过程中新入视口的行已被渲染 */
const VIRTUAL_OVERSCAN = 12

/**
 * 虚拟窗口的行区间（start 含、end 不含）。
 *
 * 滚到底时必须把 end 直接拉到 total：`ceil(viewport / 行高)` 是按首行实测高度估算的，
 * 而粘性表头占位、行高与整表平均值不完全一致，都会让估算少算几行；一旦少算，
 * 底部虚拟占位行就留下来，表现为"数据已经到底，下面还有一大块空白"。
 * 到底后占位行消失会把滚动位置钳到新的底部，下一次计算仍然判定为"到底"，
 * 因此不会在两种高度之间来回抖动。
 */
export function computeVirtualWindow(scrollTop: number, viewport: number, scrollHeight: number, total: number, height: number) {
  if (height <= 0 || total <= 0) return { start: 0, end: 0 }
  const atBottom = scrollHeight > viewport && scrollTop >= scrollHeight - viewport - 2
  const start = Math.max(0, Math.floor(scrollTop / height) - VIRTUAL_OVERSCAN)
  const end = atBottom
    ? total
    : Math.min(total, start + Math.ceil(viewport / height) + VIRTUAL_OVERSCAN * 2)
  return { start, end }
}

/**
 * 量值类字段：没有显式对齐时默认右对齐，让金额/数量成列可扫。
 * 整型不在内——编号、项次、年月一类标识也常是整型，右对齐反而不像编号；
 * 与 fieldFormat 的默认口径一致（整型不分组即"标识"）。
 */
const MEASURE_DATA_TYPES = new Set(['decimal', 'numeric', 'money', 'smallmoney', 'float', 'real', 'double'])

function defaultNumericAlign(dataType: string | undefined): string | undefined {
  return dataType && MEASURE_DATA_TYPES.has(dataType.toLowerCase()) ? 'text-end' : undefined
}

/** 列头拖到可视区边缘多宽内开始自动横向滚动，以及每帧最多滚多少像素 */
const AUTOSCROLL_EDGE = 56
const AUTOSCROLL_MAX_STEP = 14
/** 按住边缘越久滚得越快：每帧再加这么多，封顶为额外 8px/帧 */
const AUTOSCROLL_BOOST_STEP = 0.35
const AUTOSCROLL_BOOST_MAX = 8

/** 浮起预览的最大宽度：宽列整块跟手会盖住插入线，也容易顶到视口外 */
const DRAG_GHOST_MAX_WIDTH = 200
/** 拖动启动阈值：鼠标 4px / 触屏 6px，避免点表头时被当成拖拽 */
const DRAG_START_MOUSE = 4
const DRAG_START_TOUCH = 6
/** 指针纵向离开表格可视区多少像素内仍算有效落点 */
const DRAG_OUTSIDE_TOLERANCE = 16

/** 拖动期间缓存的表头几何（视口坐标） */
interface ColumnDragCell {
  id: string
  left: number
  right: number
  top: number
  bottom: number
}

interface ColumnDragLayout {
  cells: ColumnDragCell[]
  reorderable: Set<string>
  view: { left: number; top: number; right: number; bottom: number }
  /** 表格底边；量不到时用落点列自己的底边兜底（jsdom / 尚未布局） */
  tableBottom: number | null
  /** 可画插入线的横向范围（让开冻结列） */
  leftBound: number
  rightBound: number
}

/**
 * 拖动列头时要自动横向滚动的容器：从表头往上找第一个"横向确实能滚"的祖先。
 * 滚动容器不一定是 `.erp-table-shell`（有的页面是 `.table-responsive`，也有页面级滚动区），
 * 所以按实际溢出判断，不写死类名。
 */
function findHorizontalScrollHost(element: HTMLElement | null): HTMLElement | null {
  for (let node = element?.parentElement ?? null; node; node = node.parentElement) {
    const style = window.getComputedStyle(node)
    if (/(auto|scroll)/.test(style.overflowX) && node.scrollWidth > node.clientWidth + 1) return node
  }
  return null
}

/** 表格自己处理按键前先排除这些"正在编辑的东西" */
const EDITABLE_TARGET_SELECTOR = 'input, textarea, select, [contenteditable="true"]'

/**
 * 事件是否发生在可编辑控件里。
 *
 * 表格的键盘快捷键（方向键移行、Home/End 跳首末、Ctrl+C 复制整行）在输入框里必须让位：
 * 否则在明细格里连"用方向键挪光标""选几个字复制"都做不到，Ctrl+C 还会把整行覆盖到剪贴板。
 */
function isEditableTarget(target: EventTarget | null): boolean {
  return target instanceof HTMLElement && Boolean(target.closest(EDITABLE_TARGET_SELECTOR))
}

interface FrozenOffsets {
  leftByKey: Map<string, number>
  rightByKey: Map<string, number>
}

const EMPTY_FROZEN: FrozenOffsets = { leftByKey: new Map(), rightByKey: new Map() }
/** 默认值用同一份常量：解构默认写 `{}` 的话每次渲染都是新身份，
 *  会让表格内部"可见列 / 全选判定"的 memo 每帧全部作废（行数越多越明显） */
const EMPTY_SELECTION: RowSelectionState = {}
const EMPTY_VISIBILITY: VisibilityState = {}

/** 区域粘贴：从 `startRowIndex` 起逐行落位，`columns` 是起点列及其右侧的全部可见列键 */
export interface RangePasteInfo {
  startRowIndex: number
  columns: string[]
  rows: string[][]
}

/** 区域动作（清空 / 向下填充）：给出行下标与列键，页面据此改自己的数据 */
export interface RangeActionInfo {
  rowIndices: number[]
  columns: string[]
}

export interface ErpTableProps<TData extends RowData> {
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
  /**
   * 单元格右键菜单的追加项（页面自己的动作，如"在查询中心查找此值"）。
   * 返回空数组则不渲染；固定项（复制/筛选）不受影响。
   */
  cellMenuExtra?: (context: { columnId: string; row: TData; value: unknown }) => { key: string; label: string; onClick: () => void }[]
  /**
   * 列顺序重排回调（拖拽落位或表头菜单「左移/右移一列」；新列顺序含 select/冻结列）。
   * 不传则不启用列重排。
   */
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
  /**
   * 后台刷新中（配合 React Query 的 `isFetching` 与 `keepPreviousData`）：
   * 保留旧行、整体降透明度并挡掉点击，右上角转小圈。
   * 不要用 `isPending` 驱动它——首载没有旧数据，应当由页面渲染整块状态。
   */
  loading?: boolean
  /**
   * 单元格区域选择：鼠标拖选成一整块（Shift+点击扩展），Ctrl+C 复制该块为 TSV（不带表头）、
   * 方向键在块内移动、Esc 清除。默认关闭——列表页不开也不会改变既有的"方向键移行"。
   */
  cellSelection?: boolean
  /** 区域粘贴（Ctrl+V）。只在 `cellSelection` 开启且已选出区域时触发，粘贴起点为区域左上格 */
  onPasteRange?: (info: RangePasteInfo) => void
  /** 区域清空（Delete 键） */
  onClearRange?: (info: RangeActionInfo) => void
  /** 区域向下填充（Ctrl+D）：来源为区域首行 */
  onFillDown?: (info: RangeActionInfo) => void
  /**
   * 这个值一变，把列表滚回顶部。
   *
   * 排序/筛选/换关键词是**整批换数据**：留在原来的滚动偏移上，用户看到的是"另一批数据的中段"，
   * 会以为筛选没生效（或以为丢了行）。翻页/加载更多不属于换批，不要放进这个键。
   */
  scrollResetKey?: unknown
  /**
   * 选区数值摘要（Excel 状态栏的等价物）：选了一块之后回调"多少格、其中几个数、合计/平均/最值"。
   * 选区清空时回调 null。是否展示、放在哪由页面决定——表格不自己占版面。
   */
  onSelectionSummaryChange?: (summary: RangeSummary | null) => void
}

/** 选区里可解析为数字的格子统计（金额、数量这类列选中后可以直接看合计） */
export interface RangeSummary {
  /** 选区覆盖的格子数 */
  cells: number
  /** 其中可解析为数字的格子数 */
  numbers: number
  sum: number
  average: number
  min: number
  max: number
}

/**
 * 把单元格值解析成数字：数字类型直接取；字符串要**去掉千分位/空白/前导货币符号后仍是纯数字**才算。
 * 日期（`2026-10-20`）、编码（`A-01`）、布尔一律不算——把它们加进合计是没有意义的数。
 */
export function parseNumericCell(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null
  if (typeof value !== 'string') return null
  let text = value.trim().replace(/[\s,]/g, '')
  text = text.replace(/^[¥$€£]/, '')
  const percent = text.endsWith('%')
  if (percent) text = text.slice(0, -1)
  if (!/^-?\d+(\.\d+)?$/.test(text)) return null
  const parsed = Number(text)
  return Number.isFinite(parsed) ? parsed : null
}

/** 最近的纵向滚动祖先；找不到时回落到自身（表格外壳常常就是滚动容器） */
function scrollableAncestor(node: HTMLElement | null): HTMLElement | null {
  let current: HTMLElement | null = node
  while (current) {
    const style = getComputedStyle(current)
    if (/(auto|scroll|overlay)/.test(style.overflowY) || /(auto|scroll|overlay)/.test(style.overflow)) return current
    current = current.parentElement
  }
  return node
}

/** 单行渲染：整表重渲染（开菜单、改选择）不牵动未变化的行 */
interface BodyRowProps<TData extends CoreRowData> {
  row: Row<TData>
  selected: boolean
  active: boolean
  focused: boolean
  rowClass?: string
  /** 本行落在选区内时给出区域，否则 null（null 恒定，未受影响的行走 memo 快路径） */
  range: CellRange | null
  /** 本行是起选行时给出起选列（画锚点描边） */
  anchorCol: number | null
  cellSelection: boolean
  frozenOffsets: FrozenOffsets
  copyable: boolean
  onActivate: (row: Row<TData>, event: MouseEvent<HTMLTableRowElement>) => void
  onDoubleActivate?: (row: Row<TData>) => void
  onCellPointerDown: (rowId: string, col: number, extend: boolean, event: MouseEvent<HTMLTableCellElement>) => void
  onCellPointerEnter: (rowId: string, col: number) => void
  onCellContextMenu: (event: MouseEvent<HTMLTableCellElement>, rowId: string, columnId: string) => void
}

function BodyRowInner<TData extends CoreRowData>({
  row,
  selected,
  active,
  focused,
  rowClass,
  range,
  anchorCol,
  cellSelection,
  frozenOffsets,
  copyable,
  onActivate,
  onDoubleActivate,
  onCellPointerDown,
  onCellPointerEnter,
  onCellContextMenu,
}: BodyRowProps<TData>) {
  const rangeBounds = range ? normalizeRange(range) : null
  const className = [
    selected ? 'table-active' : '',
    active ? 'erp-row-active' : '',
    focused ? 'erp-row-focus' : '',
    rowClass ?? '',
  ].filter(Boolean).join(' ')
  return (
    <tr
      data-order-id={row.id}
      data-kb-index={row.index}
      className={className || undefined}
      onClick={(event) => onActivate(row, event)}
      onDoubleClick={onDoubleActivate ? () => onDoubleActivate(row) : undefined}
    >
      {row.getVisibleCells().map((cell, colIndex) => {
        const cellMeta = cell.column.columnDef.meta
        const cellFrozen = cellMeta?.frozenLeft ? 'erp-frozen-left' : cellMeta?.frozenRight ? 'erp-frozen-right' : ''
        const cellStyle: CSSProperties = {}
        if (cellMeta?.frozenLeft) cellStyle.left = frozenOffsets.leftByKey.get(cell.column.id) ?? 0
        if (cellMeta?.frozenRight) cellStyle.right = frozenOffsets.rightByKey.get(cell.column.id) ?? 0
        if (cellMeta?.maxWidth) cellStyle.maxWidth = cellMeta.maxWidth
        const truncate = cellMeta?.truncate !== false
        // 行号/勾选/操作列默认不参与区域选择（它们的 id 以 __ 开头或就是 select），
        // 其余列可用 meta.cellSelectable 单独排除
        const selectable = cellSelection
          && cellMeta?.cellSelectable !== false
          && cell.column.id !== 'select'
          && !cell.column.id.startsWith('__')
        const inRange = rangeBounds != null && colIndex >= rangeBounds.left && colIndex <= rangeBounds.right
        const cellClassName = [
          cellMeta?.cellClassName ?? cellMeta?.className ?? defaultNumericAlign(cellMeta?.dataType),
          cellFrozen,
          inRange ? 'erp-cell-range' : '',
          anchorCol === colIndex && inRange ? 'erp-cell-range-anchor' : '',
        ].filter(Boolean).join(' ')
        return (
          <td
            key={cell.id}
            className={cellClassName || undefined}
            style={Object.keys(cellStyle).length > 0 ? cellStyle : undefined}
            data-col-id={cell.column.id}
            onMouseDown={selectable
              ? (event) => onCellPointerDown(row.id, colIndex, event.shiftKey, event)
              : undefined}
            onMouseEnter={(event) => {
              // 区域拖选：鼠标跨格移动时扩展选区
              if (selectable) onCellPointerEnter(row.id, colIndex)
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
            onContextMenu={copyable
              ? (event) => onCellContextMenu(event, row.id, cell.column.id)
              : undefined}
          >
            {truncate
              ? <span className="erp-cell-ellipsis">{flexRender(cell.column.columnDef.cell, cell.getContext())}</span>
              : flexRender(cell.column.columnDef.cell, cell.getContext())}
          </td>
        )
      })}
    </tr>
  )
}

const BodyRow = memo(BodyRowInner) as typeof BodyRowInner

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
 * - 行自定义类名（rowClassName，如占位行）与行双击回调（onRowDoubleClick）；
 * - 单元格区域选择（cellSelection，拖选成块后 Ctrl+C 复制纯块，可接粘贴/清空/填充）。
 *
 * 列级行为通过 ColumnMeta 扩展：
 * - `className`：td/th 追加类；
 * - `cellClassName`：仅作用于数据单元格（优先级高于 className）；
 * - `minWidth`：表头最小列宽（工作台 DISPLAY_LENGTH）；
 * - `autoWidth`：初始列宽按内容自适应（操作列），之后与普通列一样可拖拽；
 * - `filterable` / `frozenLeft` / `frozenRight`：列头筛选 / 冻结；
 * - `cellSelectable`：该列是否参与区域选择（缺省参与；行号/勾选/操作列应设 false）。
 */
export function ErpTable<TData>({
  columns,
  data,
  getRowId,
  empty,
  sorting,
  onSortingChange,
  rowSelection = EMPTY_SELECTION,
  onRowSelectionChange,
  columnVisibility = EMPTY_VISIBILITY,
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
  cellMenuExtra,
  onColumnsReorder,
  clientSideSorting = false,
  rowClassName,
  onRowDoubleClick,
  responsive = true,
  onEndReached,
  hasMore = false,
  loadingMore = false,
  virtualize = false,
  loading = false,
  cellSelection = false,
  onPasteRange,
  onClearRange,
  onFillDown,
  scrollResetKey,
  onSelectionSummaryChange,
}: ErpTableProps<TData>) {
  const shellRef = useRef<HTMLDivElement>(null)
  const sentinelRef = useRef<HTMLDivElement | null>(null)
  /** 键盘活动行按行键记（行键在窗口化滚动时不变，按行号记会让每次滚动都判定为"焦点变了"） */
  const [focusRowId, setFocusRowId] = useState<string | null>(null)
  const [cellRange, setCellRange] = useState<CellRange | null>(null)
  const draggingRangeRef = useRef(false)
  /** 拖选的起点：鼠标按下那格。与选区一起用，避免把"当前 focus"当成起点 */
  const dragAnchorRef = useRef<{ row: number; col: number } | null>(null)
  /** 有区域动作（粘贴/清空/填充）才把单击当成"落一个活动格"；纯浏览列表的单击仍是选行 */
  const rangeActionsEnabled = Boolean(onPasteRange || onClearRange || onFillDown)
  const [cellMenu, setCellMenu] = useState<{ x: number; y: number; rowId: string; columnId: string; text: string } | null>(null)
  // 单元格右键菜单视口定位：贴近屏幕右/下缘时向内收，下方放不下则翻到落点上方
  const cellMenuPlacement = useMenuPlacement(cellMenu?.x ?? 0, cellMenu?.y ?? 0, Boolean(cellMenu && copyable))
  const [openFilter, setOpenFilter] = useState<string | null>(null)
  const [openMenu, setOpenMenu] = useState<string | null>(null)
  const [menuPos, setMenuPos] = useState<{ left: number; top: number } | null>(null)
  /** 正在被拖起的列（浮起预览只显示一个紧凑的列头卡片，不再复制整列） */
  const [draggingColumn, setDraggingColumn] = useState<{ id: string; label: string; width: number; height: number } | null>(null)
  /**
   * 落点：一条 2px 插入线（与电子表格的列插入指示同义）。
   * 不用一整列色块——插槽与被拖列、整列高亮叠在一起会遮住数据，也说不清"插在哪边"。
   */
  const [columnDropLine, setColumnDropLine] = useState<{ id: string; after: boolean; left: number; top: number; height: number } | null>(null)
  const columnDropLineRef = useRef<typeof columnDropLine>(null)
  const columnDragRef = useRef<{
    id: string; label: string; startX: number; startY: number; x: number; y: number
    offsetX: number; offsetY: number; width: number; height: number
    active: boolean; target: { id: string; after: boolean } | null
  } | null>(null)
  const columnGhostRef = useRef<HTMLDivElement>(null)
  /** 拖动期间缓存表头几何：pointermove 里量所有表头会强制同步布局，列多时拖动明显掉帧 */
  const dragLayoutRef = useRef<ColumnDragLayout | null>(null)
  const dragLayoutDirtyRef = useRef(false)
  const ghostFadeTimerRef = useRef<number | null>(null)
  /** 本次 pointerdown 时该列表头菜单是否已经打开：pointerdown 会先关闭菜单，pointerup 不能再把它弹回来 */
  const headerMenuWasOpenRef = useRef(false)
  /** 本次拖拽要自动滚动的横向容器（拖动开始时按源列位置找一次） */
  const scrollHostRef = useRef<HTMLElement | null>(null)
  const autoScrollRef = useRef<{ raf: number | null; velocity: number; boost: number }>({ raf: null, velocity: 0, boost: 0 })
  const dragPointerRef = useRef({ x: 0, y: 0 })
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
  // 行窗口化（virtualize）：滚动容器、实测行高与当前可视窗口。
  // 行高初值只是激活前的首帧估值，激活时按首行 offsetHeight 覆盖；取偏小值，
  // 估小只会多渲染几行，估大才会在滚动中露出空白。
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

  // 滚动加载更多：以 shell 最近的 overflow 祖先为 root，底部哨兵进入视口（含 120px 提前量）时触发。
  // 回调经 ref 取用：页面普遍传内联箭头函数，若把它放进依赖，每次渲染都会重建观察器
  // （并重跑一遍向上找滚动容器的 getComputedStyle 链）。
  const watchEndReached = Boolean(onEndReached)
  useEffect(() => {
    if (!watchEndReached) return
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
  }, [watchEndReached])

  /**
   * 行键兜底去重：`getRowId` 的结果会被当作 React 键用，重复键会让 React 复用/丢弃行节点
   * （窗口化下表现为表格中间大面积空白）。调用方给的键未必唯一——日志的字段组合、
   * 被 1:N 关联放大的主表行都会撞键——所以这里按数据顺序给重复项补 `#2`/`#3` 后缀，
   * 保证同一份 data 里键恒唯一。唯一键保持原值，不影响页面自己的 rowSelection / activeRowId。
   *
   * 页面普遍传内联箭头函数，映射每次渲染都会重算一遍；行数据是快照、条数有限，
   * 这点开销远小于"键撞了再回头查"的代价。
   */
  const rowIdentity = useMemo(() => {
    if (!getRowId) return null
    const seen = new Map<string, number>()
    const duplicates: string[] = []
    const ids = data.map((row, index) => {
      const id = String(getRowId(row, index))
      const repeat = seen.get(id) ?? 0
      seen.set(id, repeat + 1)
      if (repeat === 0) return id
      duplicates.push(id)
      return `${id}#${repeat + 1}`
    })
    return { ids, duplicates }
  }, [data, getRowId])
  const rowIds = rowIdentity?.ids
  const warnedDuplicatesRef = useRef<string | null>(null)
  useEffect(() => {
    if (!import.meta.env.DEV || !rowIdentity?.duplicates.length) return
    // 键撞了说明调用方给的 getRowId 不足以标识一行：表格仍会正常渲染（上面已补后缀），
    // 但页面自己的 rowSelection / activeRowId 只能匹配到第一行，需要调用方修正键定义。
    // 同一批撞键只提醒一次——内联 getRowId 会让映射每次渲染都重算。
    const signature = [...new Set(rowIdentity.duplicates)].slice(0, 5).join('|')
    if (signature === warnedDuplicatesRef.current) return
    warnedDuplicatesRef.current = signature
    console.warn('[ErpTable] getRowId 出现重复键，已按出现顺序补后缀：', signature.split('|'))
  }, [rowIdentity])

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
    getRowId: rowIds ? (_row: TData & CoreRowData, index: number) => rowIds[index] ?? String(index) : undefined,
    onSortingChange: updateSorting,
    onRowSelectionChange: (updater) =>
      onRowSelectionChange?.(typeof updater === 'function' ? updater(rowSelection as Record<string, true>) : updater),
    onColumnVisibilityChange: (updater) =>
      onColumnVisibilityChange?.(typeof updater === 'function' ? updater(columnVisibility) : updater),
  })

  const rowsModel = table.getRowModel().rows
  // 行键 → 行号：区域选择、键盘焦点、复制都按行号取行，避免每次 find 扫全表
  const rowIndexById = useMemo(() => {
    const map = new Map<string, number>()
    rowsModel.forEach((row, index) => map.set(row.id, index))
    return map
  }, [rowsModel])
  const rowIndexRef = useRef(rowIndexById)
  rowIndexRef.current = rowIndexById
  // 选区经 ref 取用：拖选回调因此保持稳定，未受影响的行走 memo 快路径
  const cellRangeRef = useRef<CellRange | null>(null)
  cellRangeRef.current = cellRange

  // 可见列与其冻结偏移：冻结组宽度按列键累计（选择列固定 33px，其余用 DISPLAY_LENGTH），
  // 使 select + 若干业务列同处左侧/右侧时 sticky 偏移互不重叠
  const visibleColumns = table.getVisibleLeafColumns()
  const cellCount = visibleColumns.length
  // 区域键盘的横向边界：跳过勾选/行号/操作列，Home/End 落到"第一个/最后一个可选列"
  const selectableColIndexes = useMemo(() => visibleColumns
    .map((column, index) => ({ column, index }))
    .filter(({ column }) => column.columnDef.meta?.cellSelectable !== false
      && column.id !== 'select' && !column.id.startsWith('__'))
    .map(({ index }) => index), [visibleColumns])
  const firstSelectableCol = selectableColIndexes[0] ?? 0
  const lastSelectableCol = selectableColIndexes.length
    ? selectableColIndexes[selectableColIndexes.length - 1]!
    : Math.max(0, cellCount - 1)
  const frozenOffsets = useMemo<FrozenOffsets>(() => {
    const hasFrozen = visibleColumns.some((column) => column.columnDef.meta?.frozenLeft || column.columnDef.meta?.frozenRight)
    if (!hasFrozen) return EMPTY_FROZEN
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
  }, [visibleColumns])

  // 选区数值摘要：只在选区变化时算一遍（用户动作驱动的，不在滚动热路径上）
  const rangeSummary = useMemo<RangeSummary | null>(() => {
    if (!cellSelection || !cellRange) return null
    const { top, bottom, left, right } = normalizeRange(cellRange)
    let cells = 0
    let numbers = 0
    let sum = 0
    let min = Infinity
    let max = -Infinity
    for (let rowIndex = top; rowIndex <= bottom; rowIndex += 1) {
      const row = rowsModel[rowIndex]
      if (!row) continue
      for (let colIndex = left; colIndex <= right; colIndex += 1) {
        const column = visibleColumns[colIndex]
        if (!column) continue
        cells += 1
        const parsed = parseNumericCell(row.getValue(column.id))
        if (parsed === null) continue
        numbers += 1
        sum += parsed
        if (parsed < min) min = parsed
        if (parsed > max) max = parsed
      }
    }
    if (cells === 0) return null
    return { cells, numbers, sum, average: numbers ? sum / numbers : 0, min: numbers ? min : 0, max: numbers ? max : 0 }
  }, [cellSelection, cellRange, rowsModel, visibleColumns])

  const summaryCallbackRef = useRef(onSelectionSummaryChange)
  summaryCallbackRef.current = onSelectionSummaryChange
  useEffect(() => {
    summaryCallbackRef.current?.(rangeSummary)
  }, [rangeSummary])

  // 行窗口化当前渲染区间（非虚拟时即全量）
  const virtualStart = virtualActive ? Math.min(virtualWindow.start, rowsModel.length) : 0
  const virtualEnd = virtualActive ? Math.min(virtualWindow.end, rowsModel.length) : rowsModel.length
  const renderedRows = virtualActive ? rowsModel.slice(virtualStart, virtualEnd) : rowsModel

  // 数据变化后清掉已不存在的行焦点 / 越界的选区
  useEffect(() => {
    setFocusRowId((current) => (current && rowIndexById.has(current) ? current : null))
    setCellRange((current) => (current && rowIndexById.size > 0
      && current.anchor.row < rowIndexById.size && current.focus.row < rowIndexById.size
      ? current : null))
  }, [rowIndexById, rowsModel.length])

  // 键盘焦点行滚动到可视区（虚拟化下按行高换算容器滚动位置）
  useEffect(() => {
    if (!focusRowId) return
    const index = rowIndexById.get(focusRowId)
    if (index === undefined) return
    if (virtualActive) {
      const container = scrollContainerRef.current
      const height = rowHeightRef.current
      if (!container || height <= 0) return
      const top = index * height
      const bottom = top + height
      const viewTop = container.scrollTop
      const viewBottom = viewTop + container.clientHeight
      if (top < viewTop) container.scrollTop = top
      else if (bottom > viewBottom) container.scrollTop = bottom - container.clientHeight
      return
    }
    const rowEl = Array.from(shellRef.current?.querySelectorAll<HTMLTableRowElement>('tr[data-order-id]') ?? [])
      .find((el) => el.dataset.orderId === focusRowId)
    rowEl?.scrollIntoView?.({ block: 'nearest' })
  }, [focusRowId, virtualActive, rowIndexById])

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

  /**
   * 按当前滚动位置计算并更新可视窗口。
   *
   * 滚动热路径里不读 `scrollHeight`：那是强制布局的属性，读到就是一次同步 layout；
   * 总高按「行数 × 实测行高」估，到底判定用同一个估值（略小于真实值 ⇒ 提前一点判定到底，
   * 只多渲染末尾几行，不影响正确性）。
   */
  const applyScrollWindow = useCallback((container: HTMLElement, total: number) => {
    const height = rowHeightRef.current
    if (height <= 0 || total <= 0) return
    const viewport = container.clientHeight
    if (viewport <= 0) {
      setVirtualWindow({ start: 0, end: Math.min(total, VIRTUAL_OVERSCAN * 2 + 10) })
      return
    }
    const estimatedHeight = total * height
    const next = computeVirtualWindow(container.scrollTop, viewport, estimatedHeight, total, height)
    setVirtualWindow((current) => (current.start === next.start && current.end === next.end ? current : next))
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

  // 滚动 / 容器尺寸变化时更新可视窗口（行数变化时重新绑定以使用最新总行数）。
  // 滚动事件按帧合并：一次滚动会连发几十个事件，逐个 setState 等于把整表重渲染几十遍。
  const rowsTotalRef = useRef(rowsModel.length)
  rowsTotalRef.current = rowsModel.length
  useEffect(() => {
    if (!virtualActive) return
    const container = scrollContainerRef.current
    if (!container) return
    let frame = 0
    const onScroll = () => {
      if (frame !== 0) return
      frame = window.requestAnimationFrame(() => {
        frame = 0
        applyScrollWindow(container, rowsTotalRef.current)
      })
    }
    container.addEventListener('scroll', onScroll, { passive: true })
    window.addEventListener('resize', onScroll)
    return () => {
      if (frame !== 0) window.cancelAnimationFrame(frame)
      container.removeEventListener('scroll', onScroll)
      window.removeEventListener('resize', onScroll)
    }
  }, [virtualActive, applyScrollWindow])

  // 整批换数据（排序/筛选/换关键词）后滚回顶部：留在旧偏移上会看到"另一批数据的中段"
  const resetKeyRef = useRef(scrollResetKey)
  useEffect(() => {
    if (resetKeyRef.current === scrollResetKey) return
    resetKeyRef.current = scrollResetKey
    const container = scrollContainerRef.current ?? scrollableAncestor(shellRef.current)
    if (container) container.scrollTop = 0
  }, [scrollResetKey])

  // 区域拖选的松手收尾（鼠标移出表格后才松开也要停）
  useEffect(() => {
    if (!cellSelection) return
    const stop = () => {
      draggingRangeRef.current = false
      dragAnchorRef.current = null
    }
    window.addEventListener('mouseup', stop)
    window.addEventListener('blur', stop)
    return () => {
      window.removeEventListener('mouseup', stop)
      window.removeEventListener('blur', stop)
    }
  }, [cellSelection])

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

  // 列头菜单：点击菜单外关闭（触发按钮除外）。点击页面其它区域也要关，
  // 否则菜单会跟着表格一起"挂着"，把别处的点击吃掉。
  useEffect(() => {
    if (!openMenu) return
    const close = (event: PointerEvent) => {
      const target = event.target as Node
      if ((target as HTMLElement).closest?.('.erp-header-menu-trigger')) return
      if (shellRef.current?.querySelector('.erp-header-menu')?.contains(target)) return
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

  // 列头筛选弹层：点击弹层外（含页面其它区域）关闭
  useEffect(() => {
    if (!openFilter) return
    const close = (event: PointerEvent) => {
      const target = event.target as Node
      if (shellRef.current?.querySelector('.erp-column-filter-popover')?.contains(target)) return
      setOpenFilter(null)
    }
    window.addEventListener('pointerdown', close)
    return () => window.removeEventListener('pointerdown', close)
  }, [openFilter])

  /** 已渲染行的单元格文本（随显示走；窗口化下屏幕外行取不到 DOM，由 data 兜底） */
  const domCellsOf = (tableEl: Element): Map<string, ChildNode[]> => {
    const map = new Map<string, ChildNode[]>()
    for (const tr of Array.from(tableEl.querySelectorAll('tbody tr[data-order-id]'))) {
      const orderId = tr.getAttribute('data-order-id')
      if (orderId) map.set(orderId, Array.from(tr.querySelectorAll(':scope > td')))
    }
    return map
  }

  /**
   * 单元格取文本：优先列 meta.copyText（虚拟化下屏幕外行不在 DOM，只有它能给出显示值），
   * 其次已渲染 DOM 的文本，最后回退原始值。
   */
  const cellTextOf = (cell: { getValue: () => unknown; column: { columnDef: { meta?: { copyText?: unknown } } } }, row: { original: unknown }, domCell?: ChildNode): string => {
    const copyText = cell.column.columnDef.meta?.copyText as CopyTextFn | undefined
    if (copyText) {
      const text = copyText({ value: cell.getValue(), row: row.original })
      if (text !== undefined) return text
    }
    const domText = domCell?.textContent?.trim()
    if (domText != null && domText !== '') return domText
    const raw = cell.getValue()
    return typeof raw === 'string' || typeof raw === 'number' ? String(raw) : ''
  }

  const copyRows = (ids: string[]) => {
    if (!ids.length) return
    const tableEl = shellRef.current?.querySelector('table')
    if (!tableEl) return
    const headers = Array.from(tableEl.querySelectorAll('thead th')).map((th) => (th.textContent ?? '').trim())
    // 已渲染行走 DOM（与显示一致）；虚拟化下屏幕外行不在 DOM，按列 meta.copyText / 原始值生成
    const domRows = domCellsOf(tableEl)
    const rows: string[][] = []
    for (const id of ids) {
      const index = rowIndexById.get(id)
      const row = index === undefined ? undefined : rowsModel[index]
      if (!row) continue
      const domCells = domRows.get(id) ?? []
      rows.push(row.getVisibleCells().map((cell, cellIndex) => cellTextOf(cell, row, domCells[cellIndex])))
    }
    if (rows.length === 0) return
    writeClipboard(rowsToTsv(headers, rows))
  }

  /** 区域复制：不带表头，粘回 Excel 就是一块数据 */
  const copyRange = (range: CellRange) => {
    const tableEl = shellRef.current?.querySelector('table')
    if (!tableEl) return
    const { top, bottom, left, right } = normalizeRange(range)
    const domRows = domCellsOf(tableEl)
    const lines: string[][] = []
    for (let rowIndex = top; rowIndex <= bottom; rowIndex += 1) {
      const row = rowsModel[rowIndex]
      if (!row) continue
      const cells = row.getVisibleCells()
      const domCells = domRows.get(row.id) ?? []
      const line: string[] = []
      for (let colIndex = left; colIndex <= right; colIndex += 1) {
        const cell = cells[colIndex]
        line.push(cell ? cellTextOf(cell, row, domCells[colIndex]) : '')
      }
      lines.push(line)
    }
    if (lines.length === 0) return
    writeClipboard(gridToTsv(lines))
  }

  const copySelection = (): string[] => {
    const selected = Object.keys(rowSelection).filter((id) => rowSelection[id])
    if (selected.length > 0) return selected
    if (activeRowId) return [activeRowId]
    if (focusRowId) return [focusRowId]
    return []
  }

  const moveFocus = (target: number) => {
    if (rowsModel.length === 0) return
    const clamped = Math.max(0, Math.min(target, rowsModel.length - 1))
    const row = rowsModel[clamped]
    setFocusRowId(row.id)
    onRowClick?.(row.original)
  }

  /** 区域动作要的列键：从区域左边界到右边界 */
  const columnIdsOf = (left: number, right: number) => visibleColumns.slice(left, right + 1).map((column) => column.id)

  const handleRangeKey = (event: KeyboardEvent<HTMLDivElement>, range: CellRange): boolean => {
    const { top, bottom, left, right } = normalizeRange(range)
    const total = rowsModel.length
    /** 整块/扩选地把焦点放到指定格（横向钳到可选列，纵向钳到表内） */
    const moveTo = (row: number, col: number, extend: boolean) => {
      const nextRow = Math.max(0, Math.min(row, total - 1))
      const nextCol = Math.max(firstSelectableCol, Math.min(col, lastSelectableCol))
      setCellRange(extend ? { anchor: range.anchor, focus: { row: nextRow, col: nextCol } } : { anchor: { row: nextRow, col: nextCol }, focus: { row: nextRow, col: nextCol } })
    }
    /** 方向键移动：先按整体移动算，再把落点钳回可选列（否则会挪到勾选/操作列上） */
    const moveBy = (dRow: number, dCol: number) => {
      const next = moveRange(range, dRow, dCol, total, cellCount, event.shiftKey)
      const clampCol = (point: CellPoint) => ({ ...point, col: Math.max(firstSelectableCol, Math.min(point.col, lastSelectableCol)) })
      setCellRange({ anchor: clampCol(next.anchor), focus: clampCol(next.focus) })
    }
    switch (event.key) {
      case 'ArrowDown':
        // Ctrl+方向键：到该方向的边界（Ctrl+Shift 则扩选到边界），与 Excel 一致
        if (event.ctrlKey || event.metaKey) moveTo(total - 1, range.focus.col, event.shiftKey)
        else moveBy(1, 0)
        event.preventDefault()
        return true
      case 'ArrowUp':
        if (event.ctrlKey || event.metaKey) moveTo(0, range.focus.col, event.shiftKey)
        else moveBy(-1, 0)
        event.preventDefault()
        return true
      case 'ArrowRight':
        if (event.ctrlKey || event.metaKey) moveTo(range.focus.row, lastSelectableCol, event.shiftKey)
        else moveBy(0, 1)
        event.preventDefault()
        return true
      case 'ArrowLeft':
        if (event.ctrlKey || event.metaKey) moveTo(range.focus.row, firstSelectableCol, event.shiftKey)
        else moveBy(0, -1)
        event.preventDefault()
        return true
      case 'Home':
        moveTo((event.ctrlKey || event.metaKey) ? 0 : range.focus.row, firstSelectableCol, event.shiftKey)
        event.preventDefault()
        return true
      case 'End':
        moveTo((event.ctrlKey || event.metaKey) ? total - 1 : range.focus.row, lastSelectableCol, event.shiftKey)
        event.preventDefault()
        return true
      case 'Escape':
        setCellRange(null)
        event.preventDefault()
        return true
      case 'Delete':
        if (onClearRange) {
          const rowIndices: number[] = []
          for (let index = top; index <= bottom; index += 1) rowIndices.push(index)
          onClearRange({ rowIndices, columns: columnIdsOf(left, right) })
          event.preventDefault()
          return true
        }
        return false
      default:
        break
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'd' && onFillDown) {
      const rowIndices: number[] = []
      for (let index = top; index <= bottom; index += 1) rowIndices.push(index)
      onFillDown({ rowIndices, columns: columnIdsOf(left, right) })
      event.preventDefault()
      return true
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a' && onRowSelectionChange) {
      const next: RowSelectionState = {}
      for (const row of rowsModel) next[row.id] = true
      onRowSelectionChange(next)
      event.preventDefault()
      return true
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c') {
      copyRange(range)
      event.preventDefault()
      return true
    }
    return false
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    // 编辑控件内的按键归控件自己：方向键挪光标、Home/End 到行首行尾、Ctrl+C 复制选中的文字。
    // 表格若在这里抢走，用户在格子里连"选几个字复制"都做不到。
    if (isEditableTarget(event.target)) return
    if (cellSelection && cellRange && handleRangeKey(event, cellRange)) return
    // Ctrl+A 全选行（Excel 的"整个区域"）：只对配了选择回调的表格生效
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a' && onRowSelectionChange && rowsModel.length > 0) {
      const next: RowSelectionState = {}
      for (const row of rowsModel) next[row.id] = true
      onRowSelectionChange(next)
      event.preventDefault()
      return
    }
    if (keyboardNavigation && rowsModel.length > 0) {
      switch (event.key) {
        case 'ArrowDown': moveFocus((focusRowId ? rowIndexById.get(focusRowId) ?? -1 : -1) + 1); event.preventDefault(); return
        case 'ArrowUp': moveFocus((focusRowId ? rowIndexById.get(focusRowId) ?? rowsModel.length : rowsModel.length) - 1); event.preventDefault(); return
        case 'Home': moveFocus(0); event.preventDefault(); return
        case 'End': moveFocus(rowsModel.length - 1); event.preventDefault(); return
        case 'PageDown': moveFocus((focusRowId ? rowIndexById.get(focusRowId) ?? 0 : 0) + 10); event.preventDefault(); return
        case 'PageUp': moveFocus((focusRowId ? rowIndexById.get(focusRowId) ?? 0 : 0) - 10); event.preventDefault(); return
        case 'Enter':
          if (focusRowId) {
            const index = rowIndexById.get(focusRowId)
            const focusRow = index === undefined ? undefined : rowsModel[index]
            if (focusRow) {
              if (rowClickSingleSelect) {
                const isSelected = focusRow.getIsSelected()
                const onlySelected = isSelected && Object.keys(rowSelection).length === 1
                table.setRowSelection(onlySelected ? {} : { [focusRow.id]: true })
              }
              onRowClick?.(focusRow.original)
              event.preventDefault()
            }
          }
          return
      }
    }
    if (copyable && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c') {
      copyRows(copySelection())
      event.preventDefault()
    }
  }

  const handlePaste = (event: ClipboardEvent<HTMLDivElement>) => {
    if (!cellSelection || !onPasteRange || !cellRange) return
    if (isEditableTarget(event.target)) return
    const text = event.clipboardData?.getData('text/plain') ?? ''
    if (!text) return
    const grid = parseClipboardGrid(text)
    if (grid.length === 0) return
    const { top, left } = normalizeRange(cellRange)
    event.preventDefault()
    onPasteRange({
      startRowIndex: top,
      columns: visibleColumns.slice(left).map((column) => column.id),
      rows: grid,
    })
  }

  // 行点击 / 区域拖选：回调经 ref 取用，保持行组件的 props 恒定（否则每渲染都是新身份，memo 形同虚设）
  const rowCallbacksRef = useRef({ rowClickSingleSelect, rowSelection, onRowClick, table })
  rowCallbacksRef.current = { rowClickSingleSelect, rowSelection, onRowClick, table }
  const handleRowActivate = useCallback((row: Row<TData & CoreRowData>, event: MouseEvent<HTMLTableRowElement>) => {
    const context = rowCallbacksRef.current
    setFocusRowId(null)
    if (context.rowClickSingleSelect) {
      const target = event.target as HTMLElement
      if (!target.closest('input, button, a, select, textarea, label, [contenteditable="true"], .erp-col-resizer')) {
        const isSelected = row.getIsSelected()
        const onlySelected = isSelected && Object.keys(context.rowSelection).length === 1
        context.table.setRowSelection(onlySelected ? {} : { [row.id]: true })
      }
    }
    context.onRowClick?.(row.original)
  }, [])
  const onRowDoubleClickRef = useRef(onRowDoubleClick)
  onRowDoubleClickRef.current = onRowDoubleClick
  const handleRowDoubleActivate = useCallback((row: Row<TData & CoreRowData>) => {
    onRowDoubleClickRef.current?.(row.original)
  }, [])
  const handleCellPointerDown = useCallback((rowId: string, col: number, extend: boolean, event: MouseEvent<HTMLTableCellElement>) => {
    if (event.button !== 0) return
    const target = event.target as HTMLElement
    // 交互控件自己要用鼠标：在这里抢走会让复选框/选择器按钮点不动
    if (target.closest('input, button, a, select, textarea, label, [contenteditable="true"], .erp-col-resizer')) return
    const index = rowIndexRef.current.get(rowId)
    if (index === undefined) return
    event.preventDefault()
    // 阻止默认后浏览器不会把焦点给这个 div，而粘贴事件只发给获得焦点的元素——这里补上
    shellRef.current?.focus({ preventScroll: true })
    setFocusRowId(null)
    draggingRangeRef.current = true
    dragAnchorRef.current = { row: index, col }
    const current = cellRangeRef.current
    if (extend && current) {
      setCellRange({ anchor: current.anchor, focus: { row: index, col } })
      return
    }
    // 只读列表上"单击一格"是选行，不该顺手留下一块选区（否则后续方向键变成移格子）；
    // 带粘贴/清空/填充的可录入表格才把单击当成"落一个活动格"。
    if (rangeActionsEnabled) {
      setCellRange({ anchor: { row: index, col }, focus: { row: index, col } })
    } else {
      setCellRange(null)
    }
  }, [rangeActionsEnabled])
  const handleCellPointerEnter = useCallback((rowId: string, col: number) => {
    if (!draggingRangeRef.current) return
    const anchor = dragAnchorRef.current
    const index = rowIndexRef.current.get(rowId)
    if (!anchor || index === undefined) return
    if (anchor.row === index && anchor.col === col) return
    setCellRange((current) => (current && current.anchor.row === anchor.row && current.anchor.col === anchor.col
      && current.focus.row === index && current.focus.col === col
      ? current
      : { anchor, focus: { row: index, col } }))
  }, [])
  const handleCellContextMenu = useCallback((event: MouseEvent<HTMLTableCellElement>, rowId: string, columnId: string) => {
    if (!copyable) return
    event.preventDefault()
    setCellMenu({
      x: event.clientX,
      y: event.clientY,
      rowId,
      columnId,
      text: (event.currentTarget.textContent ?? '').trim(),
    })
  }, [copyable])

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

  // 列头拖拽顺序自己实现（不用 HTML5 draggable）：原生拖影只有被拖的那个小节点，
  // 抓不住整列、也没有落点反馈。这里用指针事件画一层浮起的列头预览跟手，目标列亮一条插入线。
  const visibleColumnIds = table.getVisibleLeafColumns().map((column) => column.id)
  const reorderableColumnIds = onColumnsReorder
    ? visibleColumnIds.filter((id) => {
        const meta = table.getColumn(id)?.columnDef.meta
        return id !== 'select' && !meta?.frozenLeft && !meta?.frozenRight
      })
    : []
  // 拖动事件挂在 window 上（渲染后触发），读的是这两份快照：每次渲染后同步，拿到的就是当前列集合
  const visibleColumnIdsRef = useRef<string[]>([])
  const reorderableColumnIdsRef = useRef<string[]>([])
  useEffect(() => {
    visibleColumnIdsRef.current = visibleColumnIds
    reorderableColumnIdsRef.current = reorderableColumnIds
  })

  const positionColumnGhost = useCallback((x: number, y: number) => {
    const session = columnDragRef.current
    const ghost = columnGhostRef.current
    if (!session || !ghost) return
    ghost.style.transform = `translate3d(${x - session.offsetX}px, ${y - session.offsetY}px, 0)`
  }, [])

  /** 量一次表头几何（只在拖起、滚动、窗口尺寸变化时调用；不在 pointermove 里做） */
  const captureColumnLayout = useCallback(() => {
    const host = shellRef.current
    if (!host) return null
    const viewEl = scrollHostRef.current ?? host
    const viewRect = viewEl.getBoundingClientRect()
    const cells: ColumnDragCell[] = []
    let leftBound = viewRect.width > 0 ? viewRect.left : Number.NEGATIVE_INFINITY
    let rightBound = viewRect.width > 0 ? viewRect.right : Number.POSITIVE_INFINITY
    for (const cell of host.querySelectorAll<HTMLTableCellElement>('thead th[data-col-key]')) {
      const id = cell.dataset.colKey
      if (!id) continue
      const rect = cell.getBoundingClientRect()
      cells.push({ id, left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom })
      if (cell.classList.contains('erp-frozen-left')) leftBound = Math.max(leftBound, rect.right)
      if (cell.classList.contains('erp-frozen-right')) rightBound = Math.min(rightBound, rect.left)
    }
    // 冻结列把可画插入线的横向范围夹在中间；两端顺序颠倒时退回可视区
    if (leftBound > rightBound) {
      leftBound = viewRect.left
      rightBound = viewRect.right
    }
    const tableRect = host.querySelector('table')?.getBoundingClientRect()
    const layout: ColumnDragLayout = {
      cells,
      reorderable: new Set(reorderableColumnIdsRef.current),
      view: { left: viewRect.left, top: viewRect.top, right: viewRect.right, bottom: viewRect.bottom },
      tableBottom: tableRect && tableRect.height > 0 ? tableRect.bottom : null,
      leftBound,
      rightBound,
    }
    dragLayoutRef.current = layout
    dragLayoutDirtyRef.current = false
    return layout
  }, [])

  /**
   * 指针落在哪一列的左/右半区。指针还在被拖的那一列上时没有落点——
   * 否则"在自己的右半边松手"会被算成"插到下一列后面"（一次没动却换了位置）。
   */
  const columnDropTargetAt = useCallback((x: number) => {
    const session = columnDragRef.current
    const layout = dragLayoutRef.current
    if (!session?.active || !layout) return null
    let first: ColumnDragCell | null = null
    let last: ColumnDragCell | null = null
    for (const cell of layout.cells) {
      if (!layout.reorderable.has(cell.id) || cell.right - cell.left <= 0) continue
      first ??= cell
      last = cell
      if (x >= cell.left && x <= cell.right) {
        if (cell.id === session.id) return null
        const after = x > cell.left + (cell.right - cell.left) / 2
        return { id: cell.id, after, left: after ? cell.right : cell.left, top: cell.top, bottom: cell.bottom }
      }
    }
    if (first && x < first.left) {
      if (first.id === session.id) return null
      return { id: first.id, after: false, left: first.left, top: first.top, bottom: first.bottom }
    }
    if (last && x > last.right) {
      if (last.id === session.id) return null
      return { id: last.id, after: true, left: last.right, top: last.top, bottom: last.bottom }
    }
    return null
  }, [])

  /** 指针是否还在表格的可视范围：拖到工具栏、侧栏、另一张表或页面空白处应当取消 */
  const pointerInsideTable = useCallback((x: number, y: number) => {
    const layout = dragLayoutRef.current
    if (!layout) return false
    const { left, right, top, bottom } = layout.view
    // jsdom / 尚未布局时宽高为 0：不做边界限制，避免把测试与首帧误判成"表格外"
    if (bottom <= top) return true
    if (right > left && (x < left - AUTOSCROLL_EDGE || x > right + AUTOSCROLL_EDGE)) return false
    return y >= top - DRAG_OUTSIDE_TOLERANCE && y <= bottom + DRAG_OUTSIDE_TOLERANCE
  }, [])

  /** 拖动中的一次重定位：浮起预览、插入线都按当前指针重算 */
  const refreshColumnDrag = useCallback((x: number, y: number) => {
    const session = columnDragRef.current
    if (!session?.active) return
    if (dragLayoutDirtyRef.current) captureColumnLayout()
    const layout = dragLayoutRef.current
    if (!layout) return
    dragPointerRef.current = { x, y }
    positionColumnGhost(x, y)
    const inside = pointerInsideTable(x, y)
    document.body.classList.toggle('erp-column-drag-invalid', !inside)
    const hit = inside ? columnDropTargetAt(x) : null
    const target = hit ? { id: hit.id, after: hit.after } : null
    session.target = target
    if (!hit) {
      if (columnDropLineRef.current) {
        columnDropLineRef.current = null
        setColumnDropLine(null)
      }
      return
    }
    const top = Math.max(hit.top, layout.view.top)
    const bottom = layout.tableBottom == null
      ? hit.bottom
      : Math.max(top, Math.min(layout.view.bottom, layout.tableBottom))
    // 插入线落在冻结列后面就画到冻结边界上：否则会盖在冻结列上，看起来像能插进冻结区
    const left = Math.min(Math.max(hit.left, layout.leftBound), layout.rightBound)
    const line = { id: hit.id, after: hit.after, left, top, height: Math.max(0, bottom - top) }
    const previous = columnDropLineRef.current
    if (previous && previous.id === line.id && previous.after === line.after
      && previous.left === line.left && previous.top === line.top && previous.height === line.height) return
    columnDropLineRef.current = line
    setColumnDropLine(line)
  }, [captureColumnLayout, columnDropTargetAt, pointerInsideTable, positionColumnGhost])

  /** 指针压在表格左右边缘时的横向自动滚动：一帧一步，按住越久加速度越大 */
  const autoScrollStep = useCallback(() => {
    const state = autoScrollRef.current
    const host = scrollHostRef.current
    const session = columnDragRef.current
    if (!host || !session?.active || state.velocity === 0) {
      state.raf = null
      state.boost = 0
      return
    }
    const direction = Math.sign(state.velocity)
    const step = direction * Math.min(
      AUTOSCROLL_MAX_STEP + AUTOSCROLL_BOOST_MAX,
      Math.abs(state.velocity) + state.boost,
    )
    const before = host.scrollLeft
    host.scrollLeft = before + step
    // 已经滚到两端：停下，等指针再动（继续贴着边缘移动会重新启动）
    if (host.scrollLeft === before) {
      state.raf = null
      state.boost = 0
      return
    }
    state.boost = Math.min(AUTOSCROLL_BOOST_MAX, state.boost + AUTOSCROLL_BOOST_STEP)
    // 滚动改变了表头在视口里的 left，下一帧重新量一次（不在 pointermove 里量）
    dragLayoutDirtyRef.current = true
    refreshColumnDrag(dragPointerRef.current.x, dragPointerRef.current.y)
    state.raf = requestAnimationFrame(autoScrollStep)
  }, [refreshColumnDrag])

  /** 把"可重排列的新顺序"还原成整行可见列顺序（含 select/冻结列）后交给父组件 */
  const emitColumnOrder = useCallback((next: string[]) => {
    if (!onColumnsReorder) return
    const reorderableSet = new Set(reorderableColumnIdsRef.current)
    let index = 0
    const order = visibleColumnIdsRef.current.map((id) => (reorderableSet.has(id) ? next[index++] : id))
    onColumnsReorder(order)
  }, [onColumnsReorder])

  const commitColumnDrop = useCallback((dragId: string, target: { id: string; after: boolean }) => {
    if (!onColumnsReorder) return
    // 目标就是自己：没有可提交的位移（落点判定之外的最后一道保险）
    if (target.id === dragId) return
    const reorderable = reorderableColumnIdsRef.current
    const from = reorderable.indexOf(dragId)
    const targetIndex = reorderable.indexOf(target.id)
    if (from < 0 || targetIndex < 0) return
    // 先摘掉被拖的列，再按"目标列移动后的索引"落位；插回原位则视为没改
    const next = [...reorderable]
    next.splice(from, 1)
    const insertAt = targetIndex - (from < targetIndex ? 1 : 0) + (target.after ? 1 : 0)
    if (insertAt === from) return
    next.splice(insertAt, 0, dragId)
    // 拖回原处/在相邻列两侧来回放，顺序其实没变：不提交，免得白跑一次保存
    if (next.every((id, index) => id === reorderable[index])) return
    emitColumnOrder(next)
  }, [emitColumnOrder, onColumnsReorder])

  /** 表头菜单里的精确调序（一次一列），给不方便拖拽/需要键盘操作的场景兜底 */
  const moveColumn = useCallback((columnId: string, delta: -1 | 1) => {
    const reorderable = reorderableColumnIdsRef.current
    const from = reorderable.indexOf(columnId)
    const to = from + delta
    if (from < 0 || to < 0 || to >= reorderable.length) return
    const next = [...reorderable]
    const [moved] = next.splice(from, 1)
    next.splice(to, 0, moved)
    emitColumnOrder(next)
  }, [emitColumnOrder])

  /** 按下表头：先只记账，移动超过阈值才真正"抓起"——点表头不该被当成拖拽 */
  const beginColumnDrag = (event: ReactPointerEvent<HTMLElement>, columnId: string, label: string) => {
    headerMenuWasOpenRef.current = false
    if (!onColumnsReorder || event.button !== 0 || event.defaultPrevented) return
    // 表头里的按钮/链接、表头菜单/筛选弹层与列宽手柄各有各的手势，别被列拖拽吃掉
    if ((event.target as HTMLElement).closest('a, button, input, select, textarea, label, [contenteditable="true"], [role="button"], .erp-col-resizer, .erp-header-menu, .erp-column-filter-popover')) return
    const cell = event.currentTarget.closest('th')
    if (!cell) return
    const rect = cell.getBoundingClientRect()
    // 记下"按下时这张表头的菜单是否开着"：window 上的外部点击关闭逻辑会在本轮 pointerdown
    // 先把菜单关掉，pointerup 若还按 openMenu 判断就会把同一次点击误判成"重新打开"。
    headerMenuWasOpenRef.current = openMenu === columnId
    // 上一次落位后 100ms 内又按下表头：先结束上一轮的预览状态。
    // 不能只清计时器——那会在"只是点一下、没有真正拖动"时把旧预览永远留在屏幕上。
    if (ghostFadeTimerRef.current != null) {
      window.clearTimeout(ghostFadeTimerRef.current)
      ghostFadeTimerRef.current = null
      setDraggingColumn(null)
    }
    // 预览宽度封顶：宽列整块跟手会盖住插入线，也容易顶到视口外
    const ghostWidth = Math.max(1, Math.min(rect.width, DRAG_GHOST_MAX_WIDTH))
    columnDragRef.current = {
      id: columnId, label, startX: event.clientX, startY: event.clientY,
      x: event.clientX, y: event.clientY,
      offsetX: Math.min(Math.max(event.clientX - rect.left, 0), Math.max(0, ghostWidth - 24)),
      offsetY: event.clientY - rect.top,
      width: ghostWidth, height: rect.height, active: false, target: null,
    }
    // 自动滚动与表头几何只认这一次拖拽的宿主：拖动过程中页面重渲染也不改判
    scrollHostRef.current = findHorizontalScrollHost(event.currentTarget)
    dragLayoutRef.current = null
    dragLayoutDirtyRef.current = false
    dragPointerRef.current = { x: event.clientX, y: event.clientY }
    event.preventDefault()
  }

  /**
   * 单击表头打开表头菜单：把 13px 小箭头的命中区扩到整块表头。
   * 用 pointerup 而不是 click——pointerdown 的 preventDefault 会吞掉兼容 click，pointerup 仍会到达；
   * 拖拽超过阈值后 active=true，这里直接让位给落位逻辑，不会在拖完时误弹菜单。
   */
  const openHeaderMenuFromHeader = (event: ReactPointerEvent<HTMLElement>, columnId: string) => {
    // 只认"从这块表头按下"的手势：从单元格/菜单里拖出来落在表头上不算单击
    if (!columnDragRef.current) return
    const wasOpen = headerMenuWasOpenRef.current
    headerMenuWasOpenRef.current = false
    if (event.button > 0 || columnDragRef.current.active) return
    // 表头菜单/筛选弹层/列宽手柄/自定义表头控件各有自己的点击语义
    if ((event.target as HTMLElement).closest('button, a, input, select, textarea, label, [contenteditable="true"], [role="button"], .erp-col-resizer, .erp-header-menu, .erp-column-filter-popover')) return
    const cell = event.currentTarget.closest('th')
    if (!cell) return
    if (wasOpen || openMenu === columnId) {
      setOpenMenu(null)
      return
    }
    openHeaderMenu(columnId, cell)
  }

  // 落位要读最新的提交函数，但监听器不该因为它换身份而重建（否则拖动中一次外部重渲染
  // 会拆掉监听器、丢掉 grabbing 光标）。经 ref 取用，effect 只依赖稳定回调。
  const commitColumnDropRef = useRef(commitColumnDrop)
  useLayoutEffect(() => { commitColumnDropRef.current = commitColumnDrop }, [commitColumnDrop])
  // 只关心"能不能拖"这个布尔值：onColumnsReorder 的函数身份会随父组件列数组变化，
  // 若进 effect 依赖，落位后的父组件重渲染会在淡出计时器还没到点时把它清掉，预览就卡在屏幕上。
  const reorderEnabled = Boolean(onColumnsReorder)

  useEffect(() => {
    if (!reorderEnabled) return
    const finish = (commit: boolean) => {
      const session = columnDragRef.current
      // 同一页可能有多张 ErpTable（主表 + 子表）：没有拖拽的实例也会收到 window 的 pointerup，
      // 不能让它去清 body 上的全局拖拽状态、把另一张表正在进行的拖拽打断。
      if (!session) return
      columnDragRef.current = null
      if (autoScrollRef.current.raf != null) cancelAnimationFrame(autoScrollRef.current.raf)
      autoScrollRef.current = { raf: null, velocity: 0, boost: 0 }
      scrollHostRef.current = null
      dragLayoutRef.current = null
      dragLayoutDirtyRef.current = false
      document.body.classList.remove('erp-column-drag-active', 'erp-column-drag-invalid')
      const ghost = columnGhostRef.current
      columnDropLineRef.current = null
      setColumnDropLine(null)
      if (session?.active) {
        // 松手只让预览快速淡出：列顺序是立刻换的，不再做"滑进插槽"的 200ms 拖尾
        if (ghost) {
          ghost.style.transition = 'opacity 100ms ease-out'
          ghost.style.opacity = '0'
        }
        if (ghostFadeTimerRef.current != null) window.clearTimeout(ghostFadeTimerRef.current)
        ghostFadeTimerRef.current = window.setTimeout(() => {
          ghostFadeTimerRef.current = null
          setDraggingColumn(null)
        }, 100)
      } else {
        setDraggingColumn(null)
      }
      if (commit && session?.active && session.target) commitColumnDropRef.current(session.id, session.target)
    }
    const onMove = (event: PointerEvent) => {
      const session = columnDragRef.current
      if (!session) return
      session.x = event.clientX
      session.y = event.clientY
      if (!session.active) {
        // 阈值内算点击（表头也可以排序/开菜单，手指或触控板抖动不该启动拖拽）
        const threshold = event.pointerType === 'touch' ? DRAG_START_TOUCH : DRAG_START_MOUSE
        if (Math.hypot(event.clientX - session.startX, event.clientY - session.startY) < threshold) return
        session.active = true
        document.body.classList.add('erp-column-drag-active')
        captureColumnLayout()
        setDraggingColumn({ id: session.id, label: session.label, width: session.width, height: session.height })
      }
      refreshColumnDrag(event.clientX, event.clientY)
      // 贴住表格左右边缘：越靠边滚得越快（二次曲线），指针移开边缘即停。
      // 指针纵向离开表格时不再滚动——那时松手是取消，不该继续把视口推向别处。
      const layout = dragLayoutRef.current
      const host = scrollHostRef.current
      let velocity = 0
      if (host && layout && pointerInsideTable(event.clientX, event.clientY)) {
        const leftEdge = layout.view.left + AUTOSCROLL_EDGE
        const rightEdge = layout.view.right - AUTOSCROLL_EDGE
        if (event.clientX < leftEdge) {
          velocity = -Math.round(2 + Math.min(1, (leftEdge - event.clientX) / AUTOSCROLL_EDGE) ** 2 * (AUTOSCROLL_MAX_STEP - 2))
        } else if (event.clientX > rightEdge) {
          velocity = Math.round(2 + Math.min(1, (event.clientX - rightEdge) / AUTOSCROLL_EDGE) ** 2 * (AUTOSCROLL_MAX_STEP - 2))
        }
      }
      // 方向变了或离开边缘就重新起速；沿同一边继续按住则保留已累积的加速度
      const previousDirection = Math.sign(autoScrollRef.current.velocity)
      if (velocity === 0 || Math.sign(velocity) !== previousDirection) autoScrollRef.current.boost = 0
      autoScrollRef.current.velocity = velocity
      if (velocity !== 0 && autoScrollRef.current.raf == null) {
        autoScrollRef.current.raf = requestAnimationFrame(autoScrollStep)
      }
    }
    const onUp = () => finish(true)
    const onCancel = () => finish(false)
    const onKeyDown = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') finish(false)
    }
    // 拖动中任何容器滚动（自动滚动、用户滚轮、触控板）都要重算插入线位置
    const onScroll = () => {
      if (!columnDragRef.current?.active) return
      dragLayoutDirtyRef.current = true
      refreshColumnDrag(dragPointerRef.current.x, dragPointerRef.current.y)
    }
    const onResize = () => {
      if (!columnDragRef.current?.active) return
      dragLayoutDirtyRef.current = true
      refreshColumnDrag(dragPointerRef.current.x, dragPointerRef.current.y)
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', onCancel)
    window.addEventListener('blur', onCancel)
    window.addEventListener('keydown', onKeyDown)
    window.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', onResize)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', onCancel)
      window.removeEventListener('blur', onCancel)
      window.removeEventListener('keydown', onKeyDown)
      window.removeEventListener('scroll', onScroll, true)
      window.removeEventListener('resize', onResize)
      document.body.classList.remove('erp-column-drag-active', 'erp-column-drag-invalid')
      if (ghostFadeTimerRef.current != null) {
        window.clearTimeout(ghostFadeTimerRef.current)
        ghostFadeTimerRef.current = null
      }
    }
  }, [reorderEnabled, refreshColumnDrag, autoScrollStep, captureColumnLayout, pointerInsideTable])

  // 预览刚挂上时先摆到位，免得第一帧从视口左上角"飞"过来（paint 前定位）
  useLayoutEffect(() => {
    const session = columnDragRef.current
    if (!draggingColumn || !session) return
    const ghost = columnGhostRef.current
    // 连续两次拖动可能复用同一个 portal 节点：把上一轮淡出留下的内联样式清掉
    if (ghost) {
      ghost.style.transition = ''
      ghost.style.opacity = ''
    }
    positionColumnGhost(session.x, session.y)
  }, [draggingColumn, positionColumnGhost])

  // 列顺序不做整表 FLIP：父组件现在是乐观更新，落手即换序；再给所有 <td> 逐个写
  // transform 会把整张表拖进 250ms 的重绘，列多/行多时反而显得"慢半拍"。
  // 电子表格换列也是立即到位——插入线消失、列就位，不再补一段位移动画。

  if (data.length === 0 && empty != null) return <>{empty}</>

  const rangeBounds = cellRange ? normalizeRange(cellRange) : null
  return (
    <div
      ref={shellRef}
      className={`erp-table-shell${loading ? ' is-loading' : ''}`}
      tabIndex={0}
      aria-busy={loading || undefined}
      onKeyDown={handleKeyDown}
      onPaste={handlePaste}
    >
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
                const draggable = Boolean(onColumnsReorder) && header.column.id !== 'select' && !meta?.frozenLeft && !meta?.frozenRight
                const menuItems: { key: string; label: string; disabled?: boolean; onClick: (event: MouseEvent<HTMLButtonElement>) => void }[] = []
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
                if (draggable) {
                  // 与拖拽写同一份列顺序：菜单调序是精确/键盘可达的兜底入口
                  const position = reorderableColumnIds.indexOf(header.column.id)
                  menuItems.push(
                    { key: 'move-left', label: '左移一列', disabled: position <= 0, onClick: () => moveColumn(header.column.id, -1) },
                    { key: 'move-right', label: '右移一列', disabled: position < 0 || position >= reorderableColumnIds.length - 1, onClick: () => moveColumn(header.column.id, 1) },
                  )
                }
                const sortIndex = resolvedSorting.findIndex((item) => item.id === header.column.id)
                return (
                  <th
                    key={header.id}
                    data-col-key={header.column.id}
                    data-col-min-width={meta?.minWidth ?? undefined}
                    data-col-max-width={meta?.maxWidth ?? undefined}
                    data-col-min-floor={meta?.minWidthFloor ? 'true' : undefined}
                    data-col-resizable={meta?.resizable === false ? 'false' : 'true'}
                    data-col-autofit={meta?.autoWidth ? 'true' : undefined}
                    className={[meta?.className ?? defaultNumericAlign(meta?.dataType), meta?.headerClassName, frozen, openMenu === header.column.id ? 'erp-header-menu-open' : '', draggable ? 'is-column-draggable' : '', draggingColumn?.id === header.column.id ? 'is-column-dragging' : ''].filter(Boolean).join(' ') || undefined}
                    style={thStyle}
                    onPointerDown={draggable ? (event) => beginColumnDrag(event, header.column.id, headerLabel) : undefined}
                    onPointerUp={draggable ? (event) => openHeaderMenuFromHeader(event, header.column.id) : undefined}
                  >
                    {header.isPlaceholder ? null : menuItems.length > 0 || draggable ? (
                      <div className="erp-header-inner">
                        <span className="erp-header-label">
                          {flexRender(header.column.columnDef.header, header.getContext())}
                        </span>
                        <button
                          type="button"
                          className={`erp-header-menu-trigger ${sorted ? 'is-active' : ''} ${filterActive ? 'is-filtered' : ''}`}
                          aria-label={`表头操作${headerLabel}`}
                          aria-expanded={openMenu === header.column.id}
                          title="表头操作：排序 / 筛选 / 列调序"
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
                          <Fragment key={item.key}>
                            {item.key === 'move-left' && <div className="dropdown-divider" />}
                            <button
                              type="button"
                              className="dropdown-item"
                              disabled={item.disabled}
                              onClick={(event) => {
                                // 菜单项点完就把焦点还给表头按钮：键盘用户调完一列还能接着按方向/回车
                                const trigger = event.currentTarget.closest('th')?.querySelector<HTMLButtonElement>('.erp-header-menu-trigger')
                                item.onClick(event)
                                setOpenMenu(null)
                                trigger?.focus()
                              }}
                            >
                              {item.label}
                            </button>
                          </Fragment>
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
            const inRange = rangeBounds != null && index >= rangeBounds.top && index <= rangeBounds.bottom
            return (
              <BodyRow
                key={row.id}
                row={row as unknown as Row<TData & CoreRowData>}
                selected={row.getIsSelected()}
                active={activeRowId === row.id}
                focused={focusRowId === row.id}
                rowClass={rowClassName?.(row.original)}
                range={inRange ? cellRange : null}
                anchorCol={cellRange && cellRange.anchor.row === index && inRange ? cellRange.anchor.col : null}
                cellSelection={cellSelection}
                frozenOffsets={frozenOffsets}
                copyable={copyable}
                onActivate={handleRowActivate}
                onDoubleActivate={onRowDoubleClick ? handleRowDoubleActivate : undefined}
                onCellPointerDown={handleCellPointerDown}
                onCellPointerEnter={handleCellPointerEnter}
                onCellContextMenu={handleCellContextMenu}
              />
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
          {cellRange && (
            <button className="dropdown-item" onClick={() => { copyRange(cellRange); setCellMenu(null) }}>
              复制所选区域
            </button>
          )}
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
          {cellMenuExtra && cellMenu ? (() => {
            const row = table.getRow(cellMenu.rowId)?.original
            if (!row) return null
            const items = cellMenuExtra({ columnId: cellMenu.columnId, row, value: cellRowValue })
            if (items.length === 0) return null
            return (
              <>
                <div className="dropdown-divider" />
                {items.map(item => (
                  <button key={item.key} className="dropdown-item" onClick={() => { item.onClick(); setCellMenu(null) }}>
                    {item.label}
                  </button>
                ))}
              </>
            )
          })() : null}
        </div>
      )}
      {draggingColumn && createPortal(
        <div
          ref={columnGhostRef}
          className="erp-column-drag-ghost"
          style={{ width: draggingColumn.width, height: draggingColumn.height }}
          aria-hidden="true"
        >
          <div className="erp-column-drag-ghost-inner">{draggingColumn.label}</div>
        </div>,
        document.body,
      )}
      {columnDropLine && createPortal(
        <div
          className="erp-column-drop-line"
          style={{ left: columnDropLine.left, top: columnDropLine.top, height: columnDropLine.height }}
          aria-hidden="true"
        />,
        document.body,
      )}
    </div>
  )
}

/** 只用到 meta.copyText 的窄口径，供纯文本提取函数标注参数 */
type CopyTextFn = (info: { value: unknown; row: unknown }) => string | undefined

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
    /** 该列不允许拖拽调整列宽（如固定宽度的选择列） */
    resizable?: boolean
    /**
     * 初始列宽按内容自适应：首次布局时按"表头 + 本页该列最宽单元格"实测宽度落列宽，
     * 之后与普通列一样可拖拽（拖拽值优先于它）。操作列用它——按钮组的宽度由按钮个数决定，
     * 写死一个够宽的固定值在按钮少的页面里就白白占着横向空间。
     */
    autoWidth?: boolean
    /** 编辑态列宽下限：data-col-min-width 同时作为硬下限，历史/拖拽宽度不得低于它（如表单明细录入列） */
    minWidthFloor?: boolean
    /** 单元格单行省略（默认 true）：不换行 + 超宽省略号 + title 悬停全文；复选框/按钮/输入框等交互列设为 false */
    truncate?: boolean
    /** 悬停 title 全文：默认取单元格原始值；显示文本与原始值不同（如格式化）时用函数返回展示文本 */
    title?: string | ((info: { value: TValue; row: TData }) => string | undefined)
    /** 复制到剪贴板的文本（单元格/行/选中复制）。虚拟化下屏幕外行不在 DOM，
     *  提供后优先使用；未提供时回退已渲染单元格文本，再回退原始值。返回 undefined 继续回退。 */
    copyText?: (info: { value: TValue; row: TData }) => string | undefined
    /** 该列是否参与单元格区域选择（默认参与；行号/勾选/操作列设为 false，避免选区落在不可编辑的格子上） */
    cellSelectable?: boolean
  }
}
