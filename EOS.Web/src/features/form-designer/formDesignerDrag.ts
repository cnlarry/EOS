/**
 * 设计态拖拽的落点判定与施加（纯函数）。
 *
 * 拖拽的语义全部落在这里，组件只负责把"鼠标在哪个格的哪一段"翻译成 DropTarget——
 * 这样"拖到左边缘=插到前面、中心=合并为复合格、拖出画布=移除"这套规则可以被单测穷举，
 * 也不必让每个格自己判断。落点不合法时**给理由**而不是静默无视（用户要知道为什么没反应）。
 */

import {
  addFromPool,
  mergeCompanion,
  moveRow,
  moveRowToTab,
  setHidden,
  setSection,
} from './formDesignerDraft'
import type { DesignDraft, DesignTable } from './types'

/** 拖拽源：画布上的字段（可带所在表）或字段池里的字段。 */
export interface DragSource {
  from: 'canvas' | 'pool'
  table: DesignTable
  key: string
}

export type DropTarget =
  | { kind: 'insert'; key: string; before: boolean }
  | { kind: 'merge'; key: string }
  | { kind: 'tab'; tabNo: number }
  | { kind: 'section'; sectionId: string | null }
  | { kind: 'remove' }
  | null

export type DropResult = { draft: DesignDraft } | { rejected: string }

export function parseDragId(id: string): DragSource | null {
  const parts = id.split(':')
  if (parts.length !== 3) return null
  const [from, table, key] = parts
  if ((from !== 'canvas' && from !== 'pool') || (table !== 'master' && table !== 'detail') || key.length === 0) {
    return null
  }
  return { from, table, key }
}

export function dragId(source: DragSource): string {
  return `${source.from}:${source.table}:${source.key}`
}

/** 格内三段分区：左边缘插入到前、中心合并、右边缘插入到后（中心区约 40% 面积）。 */
export function zoneOf(offsetX: number, width: number): 'before' | 'merge' | 'after' {
  if (width <= 0) return 'merge'
  const ratio = offsetX / width
  if (ratio < 0.3) return 'before'
  if (ratio > 0.7) return 'after'
  return 'merge'
}

/** 按落点移动一个字段（同表内前/后插入；跨表不支持——明细没有格版式语义）。 */
function insertRelative(draft: DesignDraft, table: DesignTable, key: string, anchor: string, before: boolean): DropResult {
  const rows = table === 'master' ? draft.master : draft.detail
  const from = rows.findIndex(row => row.key === key)
  const to = rows.findIndex(row => row.key === anchor)
  if (from < 0 || to < 0) return { rejected: '找不到要移动的字段。' }
  if (from === to) return { draft }
  const target = before ? to : to + 1
  const delta = target > from ? target - from - 1 : target - from
  if (delta === 0) return { draft }
  return { draft: moveRow(draft, table, key, delta) }
}

/**
 * 施加一次拖拽。非法组合返回 <c>rejected</c> 供界面提示（例如"必填字段不能移出表单"）。
 */
export function applyDrop(draft: DesignDraft, source: DragSource, target: DropTarget): DropResult {
  if (!target) return { rejected: '没有落在可放置的位置上。' }

  // 从字段池拖入：只支持"插入到某字段前后"与"拖到页签/分节"
  if (source.from === 'pool') {
    if (poolHas(draft, source.table, source.key)) return { rejected: '该字段已在表单中。' }
    const added = addFromPool(draft, source.table, source.key)
    switch (target.kind) {
      case 'insert':
        return insertRelative(added, source.table, source.key, target.key, target.before)
      case 'tab':
        return source.table === 'master'
          ? { draft: moveRowToTab(added, source.key, target.tabNo) }
          : { rejected: '明细没有页签。' }
      case 'section':
        return source.table === 'master'
          ? { draft: setSection(added, source.key, target.sectionId) }
          : { rejected: '明细没有分节。' }
      case 'remove':
        return { rejected: '字段还没在表单里，无需移除。' }
      default:
        return { draft: added }
    }
  }

  switch (target.kind) {
    case 'remove':
      return removeField(draft, source, target)
    case 'tab':
      return source.table === 'master'
        ? { draft: moveRowToTab(draft, source.key, target.tabNo) }
        : { rejected: '明细没有页签。' }
    case 'section':
      return source.table === 'master'
        ? { draft: setSection(draft, source.key, target.sectionId) }
        : { rejected: '明细没有分节。' }
    case 'merge': {
      if (source.table !== 'master') return { rejected: '明细没有复合格。' }
      const result = mergeCompanion(draft, target.key, source.key)
      return result.rejected ? { rejected: result.rejected } : { draft: result.draft }
    }
    default:
      return insertRelative(draft, source.table, source.key, target.key, target.before)
  }
}

/** 移出表单 = 置 IS_HIDDEN 位（保留排版属性，放回时原位回填）；不可移出字段直接拒绝并说明原因。 */
function removeField(
  draft: DesignDraft,
  source: DragSource,
  _target: Extract<DropTarget, { kind: 'remove' }>,
): DropResult {
  const rows = source.table === 'master' ? draft.master : draft.detail
  const row = rows.find(item => item.key === source.key)
  if (!row) return { rejected: '找不到要移除的字段。' }
  if (row.locked) return { rejected: `${row.label} ${row.lockReason ?? '不允许移出表单'}。` }
  return { draft: setHidden(draft, source.table, source.key, true) }
}

function poolHas(draft: DesignDraft, table: DesignTable, key: string): boolean {
  const pool = table === 'master' ? draft.masterPool : draft.detailPool
  return !pool.some(field => field.key === key)
}
