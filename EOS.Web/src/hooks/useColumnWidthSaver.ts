import { useCallback, useRef } from 'react'
import { apiClient } from '../services/api'

/**
 * 个人列宽写回（工作台 / 查询中心共用）。
 *
 * 列宽是个人层，落在 `SYS_USER_COLUMN.F_WIDTH`（`PUT /document-workbench/{moduleId}/column-widths`）。
 * 两个页面共用同一个端点与同一个口径，所以这里把"串行队列 + 钳制 + 失败提示 + 全部写完后刷新一次"
 * 收敛成一处——各页手写一遍时最容易漏的是队列：并发写会把另半张表的 `{}` 覆盖掉。
 *
 * 钳制 [40,300] 与后端 `WorkbenchFieldMetaMapper.FilterColumnWidths` 同源，改需同步。
 */
export function useColumnWidthSaver(moduleId: number | string | null, onSaved?: () => void) {
  const queue = useRef<{ detail: boolean; fieldKey: string; width: number }[]>([])
  const running = useRef(false)
  const onSavedRef = useRef(onSaved)
  onSavedRef.current = onSaved

  const flush = useCallback(async (currentModuleId: number | string) => {
    running.current = true
    try {
      while (queue.current.length > 0) {
        const target = queue.current.shift()!
        const normalized = Math.min(300, Math.max(40, Math.round(target.width)))
        try {
          await apiClient.put<void>(`/document-workbench/${currentModuleId}/column-widths`, {
            master: target.detail ? {} : { [target.fieldKey]: normalized },
            detail: target.detail ? { [target.fieldKey]: normalized } : {},
          })
        } catch (error) {
          window.alert(error instanceof Error ? `保存列宽失败：${error.message}` : '保存列宽失败。')
        }
      }
      // 全部写回完成后再刷新一次定义：逐列刷新会把还没保存完的列重置回旧宽度
      try {
        onSavedRef.current?.()
      } catch {
        // 刷新失败不影响已写回的结果
      }
    } finally {
      running.current = false
    }
  }, [])

  const saveColumnWidth = useCallback((detail: boolean, fieldKey: string, width: number) => {
    if (moduleId == null || moduleId === '') return
    queue.current.push({ detail, fieldKey, width })
    if (!running.current) void flush(moduleId)
  }, [flush, moduleId])

  return { saveColumnWidth }
}
