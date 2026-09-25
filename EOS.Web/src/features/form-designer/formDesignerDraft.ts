/**
 * 设计态草稿的纯操作：所有编辑都是"产生一份新草稿"，组件只负责调用与渲染。
 *
 * 把操作放在纯函数里有两个实际好处：撤销重做只需快照整份草稿；
 * 每步编辑能不能做（不可移除字段、复合格配对、页签归属）都能被单测穷举。
 */

import type {
  DesignDraft,
  DesignRow,
  DesignState,
  DesignTable,
  PoolField,
  RowInput,
  SavePayload,
  TabInput,
} from './types'

export const RESIDENT_TAB_NO = 1
export const RESIDENT_TAB_TITLE = '默认'
export const MAX_ROW_SPAN = 3
export const MAX_SECTION_LENGTH = 50
export const MAX_GROUP_LENGTH = 50

export function toDraft(state: DesignState): DesignDraft {
  const master = [...state.master.layout].sort(byPlacement)
  const detail = [...state.detail.layout].sort(byPlacement)
  return {
    columns: state.columns,
    masterTable: state.masterTable,
    detailTable: state.detailTable,
    tabs: normalizeTabs(state.tabs),
    master,
    masterPool: [...state.master.pool],
    detail,
    detailPool: [...state.detail.pool],
    baseline: {
      tabs: normalizeTabs(state.tabs),
      master: cloneRows(master),
      detail: cloneRows(detail),
    },
  }
}

/** 页签必须含常驻的 1 号页签（其余可删；删掉的页签里的字段回落到它）。 */
export function normalizeTabs(tabs: readonly { no: number; title: string }[]): DesignTabLike[] {
  const byNo = new Map<number, string>()
  for (const tab of tabs) {
    if (!Number.isFinite(tab.no) || tab.no <= 0) continue
    if (!byNo.has(tab.no)) byNo.set(tab.no, tab.title ?? '')
  }
  if (!byNo.has(RESIDENT_TAB_NO)) byNo.set(RESIDENT_TAB_NO, '')
  return [...byNo.entries()]
    .sort((left, right) => left[0] - right[0])
    .map(([no, title]) => ({ no, title }))
}

interface DesignTabLike {
  no: number
  title: string
}

function cloneRows(rows: readonly DesignRow[]): DesignRow[] {
  return rows.map((row) => ({ ...row }))
}

function byPlacement(left: DesignRow, right: DesignRow): number {
  return left.tabNo - right.tabNo || left.orderNo - right.orderNo
}

function rowsOf(draft: DesignDraft, table: DesignTable): DesignRow[] {
  return table === 'master' ? draft.master : draft.detail
}

function withRows(draft: DesignDraft, table: DesignTable, rows: DesignRow[]): DesignDraft {
  return table === 'master' ? { ...draft, master: rows } : { ...draft, detail: rows }
}

/** 同一页签（明细无页签，视为同一组）内的顺序序号按当前排列重排，避免碎片序号。 */
function renumber(rows: readonly DesignRow[], table: DesignTable): DesignRow[] {
  if (table === 'detail') {
    return rows.map((row, index) => ({ ...row, orderNo: index + 1, tabNo: RESIDENT_TAB_NO }))
  }
  const counters = new Map<number, number>()
  return rows.map((row) => {
    const next = (counters.get(row.tabNo) ?? 0) + 1
    counters.set(row.tabNo, next)
    return { ...row, orderNo: next }
  })
}

export function tabTitle(tab: DesignTabLike): string {
  return tab.title.trim().length > 0 ? tab.title.trim() : RESIDENT_TAB_TITLE
}

/**
 * 主表行归一：按页签分块且块内保持当前数组顺序，再重排序号。
 *
 * 序号是"物化"的（存库），所以任何一次编辑都必须自己重排序号；
 * 反过来，**排序不能再依赖旧的 ORDER_NO**——交换后的行还带着旧序号，按它排会把交换抵消掉。
 */
function normalizeMaster(rows: readonly DesignRow[]): DesignRow[] {
  return renumber([...rows].sort((left, right) => left.tabNo - right.tabNo), 'master')
}

/** 在页签内按视觉顺序整体重排（同页签的前后移动用）。 */
function reorderWithinTab(draft: DesignDraft, key: string, delta: number): DesignDraft {
  const row = draft.master.find((item) => item.key === key)
  if (!row) return draft
  const inTab = draft.master.filter((item) => item.tabNo === row.tabNo)
  const index = inTab.findIndex((item) => item.key === key)
  const target = index + delta
  if (index < 0 || target < 0 || target >= inTab.length) return draft
  const swapped = [...inTab]
  const [moved] = swapped.splice(index, 1)
  swapped.splice(target, 0, moved)
  const before = draft.master.filter((item) => item.tabNo < row.tabNo)
  const after = draft.master.filter((item) => item.tabNo > row.tabNo)
  return { ...draft, master: normalizeMaster([...before, ...swapped, ...after]) }
}

/** 左右移动 = 交换相邻两个字段（占位属性随字段一起走）。 */
export function moveRow(draft: DesignDraft, table: DesignTable, key: string, delta: number): DesignDraft {
  if (delta === 0) return draft
  if (table === 'master') return reorderWithinTab(draft, key, delta)
  const index = draft.detail.findIndex((item) => item.key === key)
  const target = index + delta
  if (index < 0 || target < 0 || target >= draft.detail.length) return draft
  const rows = [...draft.detail]
  const [moved] = rows.splice(index, 1)
  rows.splice(target, 0, moved)
  return { ...draft, detail: renumber(rows, 'detail') }
}

export function moveRowToTab(draft: DesignDraft, key: string, tabNo: number): DesignDraft {
  return {
    ...draft,
    master: normalizeMaster(draft.master.map((row) => (row.key === key ? { ...row, tabNo } : row))),
  }
}

/** 移除字段 = 表单内隐藏（保留该字段的排版属性，恢复时原位回填），不物理删行。 */
export function setHidden(draft: DesignDraft, table: DesignTable, key: string, hidden: boolean): DesignDraft {
  const rows = rowsOf(draft, table)
  const row = rows.find((item) => item.key === key)
  if (!row) return draft
  if (hidden && row.locked) return draft
  return withRows(draft, table, rows.map((item) => (item.key === key ? { ...item, hidden } : item)))
}

export function setPlacement(
  draft: DesignDraft,
  key: string,
  placement: { span?: number; rowSpan?: number; newLine?: boolean },
): DesignDraft {
  const columns = draft.columns > 0 ? draft.columns : 1
  return {
    ...draft,
    master: draft.master.map((row) =>
      row.key === key
        ? {
            ...row,
            span: placement.span === undefined ? row.span : clamp(placement.span, 1, columns),
            rowSpan:
              placement.rowSpan === undefined ? row.rowSpan : clamp(placement.rowSpan, 1, MAX_ROW_SPAN),
            newLine: placement.newLine === undefined ? row.newLine : placement.newLine,
          }
        : row,
    ),
  }
}

export function setSection(draft: DesignDraft, key: string, sectionId: string | null): DesignDraft {
  const trimmed = (sectionId ?? '').trim().slice(0, MAX_SECTION_LENGTH)
  return {
    ...draft,
    master: draft.master.map((row) =>
      row.key === key ? { ...row, sectionId: trimmed.length > 0 ? trimmed : null } : row,
    ),
  }
}

/**
 * 复合格：主字段带启用来源（有启用中的选择器）才能成组；一格最多 1 个从字段，
 * 主从必须在同一张表（这里只在主表内操作）。解组时把全组清空。
 */
export function mergeCompanion(
  draft: DesignDraft,
  mainKey: string,
  companionKey: string | null,
): { draft: DesignDraft; rejected?: string } {
  const main = draft.master.find((row) => row.key === mainKey)
  if (!main) return { draft }
  if (!main.hasChooser) {
    return { draft, rejected: `${main.label} 没有启用中的选择器来源，不能作为主字段。` }
  }
  const group = main.cellGroup?.trim() || main.key
  if (companionKey === null) {
    return {
      draft: {
        ...draft,
        master: draft.master.map((row) =>
          row.cellGroup === group ? { ...row, cellGroup: null, cellRole: 0 } : row,
        ),
      },
    }
  }
  const companion = draft.master.find((row) => row.key === companionKey)
  if (!companion || companion.key === main.key) {
    return { draft, rejected: '从字段无效。' }
  }
  if (companion.required || companion.locked) {
    return { draft, rejected: `${companion.label} 是必填或系统列，不能作为从字段。` }
  }
  return {
    draft: {
      ...draft,
      master: draft.master.map((row) => {
        if (row.cellGroup === group && row.cellRole === 2) {
          return { ...row, cellGroup: null, cellRole: 0 }
        }
        if (row.key === main.key) return { ...row, cellGroup: group.slice(0, MAX_GROUP_LENGTH), cellRole: 1 }
        if (row.key === companion.key) return { ...row, cellGroup: group.slice(0, MAX_GROUP_LENGTH), cellRole: 2 }
        return row
      }),
    },
  }
}

/** 把字段从池里放回表单（追加到默认页签末尾）。 */
export function addFromPool(draft: DesignDraft, table: DesignTable, key: string): DesignDraft {
  const pool = table === 'master' ? draft.masterPool : draft.detailPool
  const field = pool.find((item) => item.key === key)
  if (!field) return draft
  const rows = rowsOf(draft, table)
  if (rows.some((row) => row.key === key)) return draft
  const appended = [...rows, toRow(field)]
  const nextRows = table === 'master' ? normalizeMaster(appended) : renumber(appended, table)
  const nextPool = pool.filter((item) => item.key !== key)
  return table === 'master'
    ? { ...draft, master: nextRows, masterPool: nextPool }
    : { ...draft, detail: nextRows, detailPool: nextPool }
}

function toRow(field: PoolField): DesignRow {
  return {
    key: field.key,
    label: field.label,
    dataType: field.dataType,
    tabNo: RESIDENT_TAB_NO,
    orderNo: Number.MAX_SAFE_INTEGER,
    span: 1,
    rowSpan: 1,
    newLine: false,
    sectionId: null,
    cellGroup: null,
    cellRole: 0,
    hidden: false,
    locked: field.locked,
    lockReason: field.lockReason,
    userVisible: field.userVisible,
    required: field.required,
    isPrimaryKey: field.isPrimaryKey,
    hasChooser: field.hasChooser,
    isVirtual: field.isVirtual,
  }
}

export function addTab(draft: DesignDraft, title: string): DesignDraft {
  const no = Math.max(RESIDENT_TAB_NO, ...draft.tabs.map((tab) => tab.no)) + 1
  return { ...draft, tabs: normalizeTabs([...draft.tabs, { no, title }]) }
}

export function renameTab(draft: DesignDraft, no: number, title: string): DesignDraft {
  return {
    ...draft,
    tabs: draft.tabs.map((tab) =>
      tab.no === no ? { ...tab, title: title.trim().slice(0, 50) } : tab,
    ),
  }
}

/** 删除页签：其中的字段回到常驻页签，不丢字段。 */
export function deleteTab(draft: DesignDraft, no: number): DesignDraft {
  if (no === RESIDENT_TAB_NO) return draft
  return {
    ...draft,
    tabs: draft.tabs.filter((tab) => tab.no !== no),
    master: normalizeMaster(
      draft.master.map((row) => (row.tabNo === no ? { ...row, tabNo: RESIDENT_TAB_NO } : row)),
    ),
  }
}

/**
 * 恢复该字段默认：位置与占位都回到加载时的样子。
 * 位置按"默认版式里它前面的那个字段之后"落位——直接插到末尾会让恢复出来的版式与原默认不同。
 */
export function resetRow(draft: DesignDraft, table: DesignTable, key: string): DesignDraft {
  const rows = rowsOf(draft, table)
  const current = rows.find((row) => row.key === key)
  if (!current) return draft
  const baseline = table === 'master' ? draft.baseline.master : draft.baseline.detail
  const original = baseline.find((row) => row.key === key)
  if (!original) {
    // 默认版式里本就没有该字段：清掉占位与复合格，位置不动
    const cleared: DesignRow = {
      ...current,
      span: 1,
      rowSpan: 1,
      newLine: false,
      sectionId: null,
      cellGroup: null,
      cellRole: 0,
      hidden: false,
    }
    return withRows(draft, table, rows.map((row) => (row.key === key ? cleared : row)))
  }

  const target: DesignRow = { ...original, tabNo: table === 'master' ? original.tabNo : RESIDENT_TAB_NO }
  const remaining = rows.filter((row) => row.key !== key)
  const baselineOrder = baseline.map((row) => row.key)
  const predecessor = baselineOrder
    .slice(0, baselineOrder.indexOf(key))
    .reverse()
    .find((candidate) => remaining.some((row) => row.key === candidate))
  const insertAt = predecessor ? remaining.findIndex((row) => row.key === predecessor) + 1 : 0
  const next = [...remaining.slice(0, insertAt), target, ...remaining.slice(insertAt)]
  return withRows(draft, table, table === 'master' ? normalizeMaster(next) : renumber(next, 'detail'))
}

/** 重置为默认版式：完全回到推导结果（由后端删行后重新推导）。 */
export function resetAll(draft: DesignDraft): DesignDraft {
  return {
    ...draft,
    tabs: normalizeTabs([{ no: RESIDENT_TAB_NO, title: '' }]),
    master: cloneRows(draft.baseline.master),
    detail: cloneRows(draft.baseline.detail),
    masterPool: draft.masterPool,
    detailPool: draft.detailPool,
  }
}

export function toSavePayload(
  draft: DesignDraft,
  baseUpdatedAt: string | null,
  idempotencyKey: string,
): SavePayload {
  const tabs: TabInput[] = normalizeTabs(draft.tabs).map((tab) => ({ no: tab.no, title: tab.title }))
  const master: RowInput[] = [...draft.master]
    .sort(byPlacement)
    .map((row) => ({
      key: row.key,
      tabNo: row.tabNo,
      span: row.span,
      rowSpan: row.rowSpan,
      newLine: row.newLine,
      sectionId: row.sectionId,
      cellGroup: row.cellGroup,
      cellRole: row.cellRole,
      hidden: row.hidden,
    }))
  const detail = [...draft.detail]
    .sort(byPlacement)
    .map((row) => ({ key: row.key, hidden: row.hidden }))
  return { baseUpdatedAt, idempotencyKey, tabs, master, detail }
}

/**
 * 本地校验：与后端 fail-closed 同口径，先给用户即时反馈。
 * 只作提示——保存时以服务端判定为准（权限、模块字段集、必填口径都只有服务端清楚）。
 */
export function validateDraft(draft: DesignDraft): string[] {
  const issues: string[] = []
  const tabNumbers = new Set(draft.tabs.map((tab) => tab.no))
  for (const row of draft.master) {
    if (!tabNumbers.has(row.tabNo)) {
      issues.push(`${row.label} 所属页签已不存在。`)
    }
    if (row.hidden && row.locked) {
      issues.push(`${row.label} ${row.lockReason ?? '不允许从表单移除'}。`)
    }
    if (row.span < 1 || row.span > draft.columns) {
      issues.push(`${row.label} 的列跨度超出 1..${draft.columns}。`)
    }
    if (row.rowSpan < 1 || row.rowSpan > MAX_ROW_SPAN) {
      issues.push(`${row.label} 的行跨度超出 1..${MAX_ROW_SPAN}。`)
    }
  }
  // 定制过的表：未列出的字段即"未加入表单"，用户可填的必填字段不允许缺位
  for (const field of draft.masterPool) {
    if (field.userVisible && field.required && !field.isVirtual) {
      issues.push(`${field.label} 是必填字段，必须加入表单。`)
    }
  }
  const groups = new Map<string, DesignRow[]>()
  for (const row of draft.master) {
    const group = row.cellGroup?.trim()
    if (!group || row.cellRole === 0) continue
    groups.set(group, [...(groups.get(group) ?? []), row])
  }
  for (const [group, rows] of groups) {
    const mains = rows.filter((row) => row.cellRole === 1)
    const companions = rows.filter((row) => row.cellRole === 2)
    if (mains.length !== 1) {
      issues.push(`复合格 ${group} 必须有且仅有 1 个主字段。`)
    }
    if (companions.length > 1) {
      issues.push(`复合格 ${group} 最多 1 个从字段。`)
    }
    if (companions.length > 0 && mains.length === 0) {
      issues.push(`复合格 ${group} 的从字段没有配主字段。`)
    }
  }
  return issues
}

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, Math.round(value)))
}
