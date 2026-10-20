import { type ReactNode } from 'react'
import type { ColumnDef, RowData, RowSelectionState, SortingState } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from './AsyncState'
import { ErpListCard } from './ErpListCard'
import { ErpPagination } from './ErpPagination'
import { ErpTable, type ErpTableProps } from './ErpTable'

export interface ErpListPagination {
  page: number
  pageSize: number
  total: number
  onPageChange: (page: number) => void
  onPageSizeChange?: (size: number) => void
  pageSizes?: number[]
}

/**
 * 排序二选一：数据已全在本页用 `client`；分页取数只能 `server`（客户端排序只会排当前页）。
 * `client` 也可以受控（把 sorting 提到页面里，例如要按"分组段"画分块线时）。
 */
export type ErpListSorting =
  | { mode: 'client'; value?: SortingState; onChange?: (next: SortingState) => void }
  | { mode: 'server'; value: SortingState; onChange: (next: SortingState) => void }

export interface ErpListGridProps<TData extends RowData> {
  /**
   * 列表身份，同时是列宽的持久化键（localStorage）。
   * 同一页面里多张表必须各不相同，否则一张表的列宽会盖到另一张上。
   */
  listId: string
  columns: ColumnDef<TData, unknown>[]
  rows: TData[] | undefined
  /** 行键：不传则按数组下标（只适合快照类数据，翻页/排序后会错位） */
  rowKey?: (row: TData, index: number) => string
  /** 首载（还没有旧数据）：渲染整块加载态，避免出现"空表闪一下" */
  loading?: boolean
  /** 后台刷新：保留旧行 + 遮罩，不要整块换成加载态 */
  fetching?: boolean
  loadingLabel?: string
  error?: string | null
  onRetry?: () => void
  /** 空数据时渲染的内容（通常是 `<EmptyState …/>`）；不传则渲染空表格 */
  empty?: ReactNode
  sorting?: ErpListSorting
  /** 选择态：传 `useListSelection` 的 `selection`，或页面自己的受控值 */
  selection?: { value: RowSelectionState; onChange: (next: RowSelectionState) => void }
  activeRowId?: string
  onRowClick?: (row: TData) => void
  onRowDoubleClick?: (row: TData) => void
  /** 分页脚：给了就在卡片底部渲染标准分页（不传则是滚动加载/无分页） */
  pagination?: ErpListPagination
  /** 命令栏左侧槽位（通常是 `<ErpSearchBox/>`） */
  search?: ReactNode
  /** 命令栏右侧槽位（按钮区，用 `ErpCommandBar` 或统一按钮） */
  actions?: ReactNode
  /** 表格上方的说明行（`.erp-list-header`） */
  header?: ReactNode
  /** 表格之前、卡片之内的内容（如"当前分组筛选"提示条） */
  before?: ReactNode
  /**
   * 给"状态 + 表格"套的区域内层类名（如工作台的 `.erp-master-table-region`，它自己就是滚动容器）。
   * 给了它就不再强求表格是卡片的直接子项——那个区域承担同样的滚动契约。
   */
  regionClassName?: string
  /** 表格之后的附加内容（如选中行的详情面板）；卡片内部、与表格同处一个滚动契约 */
  children?: ReactNode
  /** 卡片底部自定义内容；与 `pagination` 同时给时以 `footer` 为准 */
  footer?: ReactNode
  ariaLabel?: string
  /** 嵌在弹窗/面板里：不渲染卡片外壳与命令栏，只出"状态 + 表格" */
  plain?: boolean
  /**
   * 高级开关原样透传给 `ErpTable`（虚拟化、区域选择、列宽写回服务端、列筛选、列重排……）。
   * 组合件只固化"每个列表都一样"的那几件，不再复制这十几个 prop。
   */
  tableProps?: Partial<Omit<ErpTableProps<TData>, 'columns' | 'data' | 'getRowId' | 'empty'>>
}

/**
 * 标准列表页组合件：卡片外壳 + 命令栏 + 三态 + 表格 + 分页。
 *
 * 它收拢的是 40+ 个列表页各自重复的样板：手写 `storageKey`、手写空态、手写
 * loading/error 分支、把 TanStack 的 selection updater 归一化、拼分页参数。
 * 表格能力本身仍由 `ErpTable` 提供，高级开关走 `tableProps` 透传。
 *
 * 结构上它保证"表格是卡片的直接子项"这条滚动契约——中间多套一层容器会把表格顶出卡片、
 * 滚动条消失（这条以前只是注释里的口头约定）。
 */
export function ErpListGrid<TData extends RowData>({
  listId,
  columns,
  rows,
  rowKey,
  loading = false,
  fetching = false,
  loadingLabel = '正在加载数据…',
  error = null,
  onRetry,
  empty,
  sorting,
  selection,
  activeRowId,
  onRowClick,
  onRowDoubleClick,
  pagination,
  search,
  actions,
  header,
  before,
  regionClassName,
  children,
  footer,
  ariaLabel,
  plain = false,
  tableProps,
}: ErpListGridProps<TData>) {
  const sortingProps = sorting?.mode === 'server'
    ? { sorting: sorting.value, onSortingChange: sorting.onChange }
    : sorting?.mode === 'client'
      ? {
        clientSideSorting: true,
        ...(sorting.value ? { sorting: sorting.value } : {}),
        ...(sorting.onChange ? { onSortingChange: sorting.onChange } : {}),
      }
      : {}
  const selectionProps = selection
    ? { rowSelection: selection.value, onRowSelectionChange: selection.onChange }
    : {}

  const body = loading
    ? <LoadingState label={loadingLabel} />
    : error
      ? <ErrorState message={error} onRetry={onRetry} />
      : (
        <>
          <ErpTable<TData>
            resizable
            storageKey={listId}
            {...tableProps}
            columns={columns}
            data={rows ?? []}
            getRowId={rowKey}
            empty={empty}
            {...sortingProps}
            {...selectionProps}
            // 配了选择态就默认"点行即单选"——这是列表页的默认语义；个别页面可经 tableProps 关掉
            rowClickSingleSelect={selection ? (tableProps?.rowClickSingleSelect ?? true) : tableProps?.rowClickSingleSelect}
            activeRowId={activeRowId}
            onRowClick={onRowClick}
            onRowDoubleClick={onRowDoubleClick}
            loading={fetching}
          />
        </>
      )

  const footerContent = footer ?? (pagination ? <ErpPagination {...pagination} /> : undefined)
  // 自定义区域自己就是滚动容器：刷新中的半透明遮罩也由它承载（与工作台原有观感一致）
  const region = regionClassName
    ? <div className={`${regionClassName}${fetching ? ' is-loading' : ''}`}>{body}</div>
    : body

  if (plain) {
    return <>{region}{children}{footerContent}</>
  }

  return (
    <ErpListCard
      ariaLabel={ariaLabel}
      search={search}
      actions={actions}
      header={header}
      footer={footerContent}
    >
      {before}
      {region}
      {children}
    </ErpListCard>
  )
}

/** 列表空态的统一写法：省得每页各写一份 `<EmptyState>` 的属性 */
export function listEmpty(title: string, description?: string) {
  return <EmptyState title={title} description={description} />
}
