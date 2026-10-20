import { useCallback, useMemo, useState } from 'react'
import type { RowSelectionState } from '../lib/tanstackTable'

interface UseListSelectionOptions<T> {
  /** 当前已加载的行（用于把行键还原成行对象；跨页选择只认已加载的部分） */
  rows: T[] | undefined
  /** 行键：必须与列表的 `getRowId` 同一口径，否则选择态与行对不上 */
  getId: (row: T) => string
}

/**
 * 列表选择态：复选框多选 + 行点击单选 + 活动行。
 *
 * 这四件事原先在 5 个页面各写一份（把 TanStack 的 updater 归一化、把选中键还原成行、
 * 单选时同步活动行、清空时同时清两处），口径稍有差别就会出现"勾了行但活动行没变"。
 *
 * 返回的 `value` / `onChange` 直接喂给 `ErpListGrid` 的 `selection`；
 * `activeRowId` / `onRowClick` 喂给同名列；`selectedIds` / `selectedRows` 给工具栏用。
 */
export function useListSelection<T>({ rows, getId }: UseListSelectionOptions<T>) {
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  /** 多选时保留的"刚才点过的那一行"；只选中一行时活动行就是那一行（不另存） */
  const [lastActiveId, setLastActiveId] = useState<string | null>(null)

  const selectedIds = useMemo(() => Object.keys(rowSelection).filter((key) => rowSelection[key]), [rowSelection])

  const selectedRows = useMemo(() => {
    if (!rows || rows.length === 0 || selectedIds.length === 0) return [] as T[]
    const wanted = new Set(selectedIds)
    return rows.filter((row) => wanted.has(getId(row)))
  }, [rows, selectedIds, getId])

  const activeRowId = selectedIds.length === 1 ? selectedIds[0]! : lastActiveId

  const activeRow = useMemo(() => {
    if (!activeRowId || !rows) return null
    return rows.find((row) => getId(row) === activeRowId) ?? null
  }, [activeRowId, rows, getId])

  /** TanStack 会传 updater 函数，归一化成纯值再落状态（直接把函数塞进 state 会让选择态变成函数） */
  const onChange = useCallback((next: RowSelectionState | ((current: RowSelectionState) => RowSelectionState)) => {
    setRowSelection((current) => (typeof next === 'function' ? next(current) : next))
  }, [])

  /** 行点击：单选语义（清掉其它行，只留这一行） */
  const onRowClick = useCallback((row: T) => {
    const id = getId(row)
    setRowSelection({ [id]: true })
    setLastActiveId(id)
  }, [getId])

  const clear = useCallback(() => {
    setRowSelection({})
    setLastActiveId(null)
  }, [])

  return {
    /** 直接作为 `<ErpListGrid selection={...}>` */
    selection: { value: rowSelection, onChange },
    activeRowId: activeRowId ?? undefined,
    activeRow,
    onRowClick,
    selectedIds,
    selectedRows,
    clear,
  }
}
