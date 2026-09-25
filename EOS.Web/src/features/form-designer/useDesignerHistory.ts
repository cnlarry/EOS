import { useCallback, useRef, useState } from 'react'

/**
 * 设计态的撤销重做：每条编辑产生一份新草稿，历史保存快照即可。
 * 上限固定（默认 50 步），与报表版式设计器的历史深度一致。
 */
export interface DesignerHistory<T> {
  value: T
  commit: (next: T) => void
  /** 不记历史地替换（加载完成、保存成功后重设基线）。 */
  rebase: (next: T) => void
  undo: () => void
  redo: () => void
  canUndo: boolean
  canRedo: boolean
  /** 与基线不同即视为有未保存改动。 */
  dirty: boolean
}

export function useDesignerHistory<T>(initial: T, limit = 50): DesignerHistory<T> {
  const [past, setPast] = useState<T[]>([])
  const [present, setPresent] = useState<T>(initial)
  const [future, setFuture] = useState<T[]>([])
  const baseline = useRef<T>(initial)

  const commit = useCallback((next: T) => {
    setPast((history) => [...history.slice(-(limit - 1)), present])
    setPresent(next)
    setFuture([])
  }, [limit, present])

  const rebase = useCallback((next: T) => {
    baseline.current = next
    setPast([])
    setFuture([])
    setPresent(next)
  }, [])

  const undo = useCallback(() => {
    setPast((history) => {
      if (history.length === 0) return history
      const previous = history[history.length - 1]
      setFuture((later) => [present, ...later])
      setPresent(previous)
      return history.slice(0, -1)
    })
  }, [present])

  const redo = useCallback(() => {
    setFuture((later) => {
      if (later.length === 0) return later
      const next = later[0]
      setPast((history) => [...history, present])
      setPresent(next)
      return later.slice(1)
    })
  }, [present])

  return {
    value: present,
    commit,
    rebase,
    undo,
    redo,
    canUndo: past.length > 0,
    canRedo: future.length > 0,
    dirty: present !== baseline.current,
  }
}
