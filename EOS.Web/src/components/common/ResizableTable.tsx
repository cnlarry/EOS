import { useRef, type ReactNode } from 'react'
import { useColumnResize } from '../../hooks/useColumnResize'

/**
 * 任意 `<table>` 的列宽拖拽调整外壳（类名完全由调用方指定，不改变表格外观）。
 *
 * 适用于 DocumentWorkbench 主表/子表等无法直接用 ErpDataTable（类名固定）的场景。
 * 需要列宽持久化时传入 `storageKey`（按用户与表格隔离，存 localStorage，不写数据库）。
 *
 * ```tsx
 * <ResizableTable className="table table-sm table-vcenter card-table mb-0" storageKey="wb-1204-detail">
 *   <thead>...</thead>
 *   <tbody>...</tbody>
 * </ResizableTable>
 * ```
 */
export function ResizableTable({
  className,
  storageKey,
  children,
  persistResize = true,
  onColumnResize,
  fitRef,
}: {
  className: string
  storageKey: string
  children: ReactNode
  /** 是否持久化到 localStorage；false 时列宽以服务端字段（data-col-min-width）为唯一来源 */
  persistResize?: boolean
  /** 拖拽结束/双击自适应时回调（列键 + 新宽度） */
  onColumnResize?: (columnKey: string, width: number) => void
  /** 外部触发句柄：暴露“自适应全部列宽”函数（返回各列新宽度，工具栏按钮批量写回） */
  fitRef?: { current: (() => Record<string, number>) | null }
}) {
  const tableRef = useRef<HTMLTableElement>(null)
  useColumnResize(tableRef, storageKey, { persist: persistResize, onColumnResize, fitRef })
  return (
    <table ref={tableRef} className={className}>
      {children}
    </table>
  )
}
