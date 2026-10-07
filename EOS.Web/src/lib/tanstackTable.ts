/**
 * TanStack Table **9 桥接层**（2026-10-07 随依赖 8 → 9 升级引入）。
 *
 * 为什么有这一层：v9 把 API 换成了「特性组合式」——`ColumnDef` / `Table` / `Row` 这些类型都多了
 * 一个 `TFeatures extends TableFeatures` 参数，行选择等方法要显式启用特性才有；全仓 40 个文件、
 * 399 处调用点按新 API 重写是一次独立迁移，不该和依赖升级捆在一起。v9 官方为此提供了
 * `@tanstack/react-table/legacy` 入口（v8 式 API 跑在 v9 内核上，类型加 `Legacy` 前缀）。
 *
 * 所以这里把两个入口的名字合流、并按仓库既有写法还原成 v8 的名字，各业务文件只要把
 * `from '@tanstack/react-table'` 换成 `from '<相对路径>/lib/tanstackTable'` 即可，调用点零改动。
 *
 * 退出路径：等真正做「特性组合式」迁移时，逐个文件把来源换回 `@tanstack/react-table`
 * 并按 v9 新签名调整（`useTable` + `features`、`Row<TFeatures, TData>`、`row.original` 的约束等），
 * 全部改完后删除本文件。别在这里长期堆积新名字——它只该做翻译，不做扩展。
 */

import type { CellContext as CoreCellContext, RowData as CoreRowData } from '@tanstack/react-table'
import type { LegacyColumnDef } from '@tanstack/react-table/legacy'

/**
 * 列定义：v9 的 `LegacyColumnDef<TData extends RowData>` 把行数据约束到 `Record<string, unknown>`，
 * 而本仓的行数据大量是 `interface`（接口没有隐式索引签名 ⇒ 过不了约束校验）。这里用交叉类型
 * `TData & CoreRowData` 满足约束，同时保留 `TData` 的可读性（`row.original.xxx` 仍然有类型）。
 */
export type ColumnDef<TData = unknown, TValue = unknown> = LegacyColumnDef<TData & CoreRowData, TValue>

// v8 式 API：v9 里改了名或搬到 legacy 入口的部分
export {
  useLegacyTable as useReactTable,
  legacyCreateColumnHelper as createColumnHelper,
  getCoreRowModel,
  getSortedRowModel,
  getFilteredRowModel,
  getPaginationRowModel,
  getExpandedRowModel,
  getGroupedRowModel,
  getFacetedRowModel,
  getFacetedUniqueValues,
  getFacetedMinMaxValues,
  type LegacyTable as Table,
  type LegacyRow as Row,
  type LegacyColumn as Column,
  type LegacyCell as Cell,
  type LegacyHeader as Header,
  type LegacyHeaderGroup as HeaderGroup,
  type LegacyTableOptions as TableOptions,
} from '@tanstack/react-table/legacy'

// 两个入口都有的公共类型 / 渲染器：v9 主入口里仍在，只是个别改了名（VisibilityState）
export {
  flexRender,
  type SortingState,
  type Updater,
  type ColumnMeta,
  type TableFeatures,
  type CellData,
  type OnChangeFn,
  type PaginationState,
  type ColumnFiltersState,
  type ExpandedState,
  type GroupingState,
  type ColumnHelper,
  type FilterFn,
  type TableState,
  type ColumnVisibilityState as VisibilityState,
} from '@tanstack/react-table'

/**
 * 单元格上下文：v9 是 `CellContext<TFeatures, TData, TValue>`（features 在前），
 * 本仓按 v8 的 `<TData, TValue>` 用（2 处）。这里把 features 绑成 `any` 还原旧签名。
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type CellContext<TData extends RowData, TValue = unknown> = CoreCellContext<any, TData & CoreRowData, TValue>

/**
 * 行数据约束：v9 把它收紧成 `Record<string, unknown>`，而本仓大量用 `interface` 描述行数据
 * （接口不满足索引签名，会成片报“is not assignable”）。v8 的原定义是 `unknown | object | any`，
 * 实际几乎不约束——这里按 v8 语义还原，避免为一次依赖升级去改几十个数据类型的写法。
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type RowData = any

/**
 * 行选择状态：v9 只记“被选中的行”（`Record<string, true>`，取消选择靠删键），
 * 而本仓各处都按 v8 的 `Record<string, boolean>` 读写（把行置为 false 表示未选中）。
 * 仍按 v8 口径对外；传进表格时 ErpTable 里做一次窄化（见该处注释）。
 */
export type RowSelectionState = Record<string, boolean>
