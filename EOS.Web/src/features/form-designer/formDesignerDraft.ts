/**
 * 设计态草稿的纯操作：所有编辑都是"产生一份新草稿"，组件只负责调用与渲染。
 *
 * 把操作放在纯函数里有两个实际好处：撤销重做只需快照整份草稿；
 * 每步编辑能不能做（不可移除字段、复合格配对、页签归属）都能被单测穷举。
 */

import { DEFAULT_FORM_COLUMNS, resolveFormColumns, resolveTabColumns } from '../document-workbench/formLayout'
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
    moduleId: state.moduleId,
    title: state.title,
    openMode: state.openMode,
    dialogWidth: state.dialogWidth,
    dialogHeight: state.dialogHeight,
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
export function normalizeTabs(tabs: readonly DesignTabInputLike[]): DesignTabLike[] {
  const byNo = new Map<number, { title: string; columns: number }>()
  for (const tab of tabs) {
    if (!Number.isFinite(tab.no) || tab.no <= 0) continue
    if (!byNo.has(tab.no)) {
      byNo.set(tab.no, { title: tab.title ?? '', columns: normalizeTabColumns(tab.columns) })
    }
  }
  if (!byNo.has(RESIDENT_TAB_NO)) {
    byNo.set(RESIDENT_TAB_NO, { title: '', columns: DEFAULT_FORM_COLUMNS })
  }
  return [...byNo.entries()]
    .sort((left, right) => left[0] - right[0])
    .map(([no, rest]) => ({ no, ...rest }))
}

interface DesignTabInputLike {
  no: number
  title: string
  columns?: number | null
}

interface DesignTabLike {
  no: number
  title: string
  columns: number
}

/**
 * 页签列数的归一：越界/缺省一律落兜底 4 列（库内该列 NOT NULL DEFAULT 4，写入侧不落 NULL），
 * 不放行非法值到服务端。
 */
function normalizeTabColumns(columns: number | null | undefined): number {
  return resolveFormColumns(columns)
}

/**
 * 某页签的布局列数（与运行态同一处解析 `resolveTabColumns`）。
 * 设计器的画板、跨度夹取与本地校验都走它——三处与运行态同源，才不会"设计态排 2 列、运行态按 4 列渲染"。
 */
export function tabColumns(draft: DesignDraft, tabNo: number): number {
  return resolveTabColumns(draft.tabs, tabNo)
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

export function tabTitle(tab: { no: number; title: string }): string {
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

/** 移出表单 = 置 IS_HIDDEN 位（保留该字段的排版属性，放回时原位回填），不物理删行。 */
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
  const target = draft.master.find((row) => row.key === key)
  // 跨度上限 = **该行所属页签**的列数（页签 1 两列、页签 2 一列时两行各有各的上限）
  const columns = target ? tabColumns(draft, target.tabNo) : 1
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

/**
 * 明细列管理落地：按给定列键的**有序清单**重建明细版式。
 *
 * 清单里没有的列不是物理删除，而是置为「已移出表单」（保留其排版属性，放回时仍在原表头上）；
 * 锁定列（主键/系统列/必填）不允许移出，仍留在表单里（与 setHidden 同一口径），
 * 排在清单之后——否则字段会在用户没要求的情况下从表单上消失，或被静默移出。
 */
export function applyDetailColumns(draft: DesignDraft, keys: readonly string[]): DesignDraft {
  let next = draft
  for (const key of keys) {
    if (!next.detail.some((row) => row.key.toUpperCase() === key.toUpperCase())) {
      next = addFromPool(next, 'detail', key)
    }
  }
  const remaining = new Map(next.detail.map((row) => [row.key.toUpperCase(), row]))
  const ordered: DesignRow[] = []
  for (const key of keys) {
    const upper = key.toUpperCase()
    const row = remaining.get(upper)
    if (!row) continue
    remaining.delete(upper)
    ordered.push({ ...row, hidden: false })
  }
  for (const row of remaining.values()) {
    ordered.push(row.locked ? { ...row, hidden: false } : { ...row, hidden: true })
  }
  return { ...next, detail: renumber(ordered, 'detail') }
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

/**
 * 新增页签：列数默认沿用**当前页签**的列数（不传则落兜底 4 列）。
 * 沿用当前页签而不是硬编码一个值，是为了"接着刚才那张单子继续排"——
 * 在一个两列的表单里加页签，得到的应该是两列页签。
 */
export function addTab(draft: DesignDraft, title: string, columns: number | null = null): DesignDraft {
  const no = Math.max(RESIDENT_TAB_NO, ...draft.tabs.map((tab) => tab.no)) + 1
  return { ...draft, tabs: normalizeTabs([...draft.tabs, { no, title, columns }]) }
}

/**
 * 页签的布局列数：改声明，并把**该页签内**超出新列数的跨度**夹到新列数**（其它页签不受影响）。
 *
 * 用户 2026-10-06 拍板："当调整布局的时候，自动把跨度超过布局列数的字段调整为布局列数，
 * 这样就合规了"——所以这里与 `setPlacement` 一样是**夹取**语义，不留一堆待用户逐行手改的越界行。
 * 服务端写入侧同样按页签列数夹取（`FormLayoutSubmission.Normalize`），两端一致。
 */
export function setTabColumns(draft: DesignDraft, tabNo: number, columns: number): DesignDraft {
  const next = resolveFormColumns(columns)
  return {
    ...draft,
    tabs: draft.tabs.map((tab) => (tab.no === tabNo ? { ...tab, columns: next } : tab)),
    master: draft.master.map((row) =>
      row.tabNo === tabNo ? { ...row, span: Math.min(row.span, next) } : row,
    ),
  }
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
 * 恢复该字段默认排版：位置与占位都回到加载时的样子。
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
  const tabs: TabInput[] = normalizeTabs(draft.tabs).map((tab) => ({
    no: tab.no,
    title: tab.title,
    // 页签自带布局列数（服务端写入 MODULE_FORM_TAB.LAYOUT_COLUMNS，NOT NULL DEFAULT 4）：
    // 一律落具体值，不提交 null
    columns: normalizeTabColumns(tab.columns),
  }))
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
  return {
    baseUpdatedAt,
    idempotencyKey,
    tabs,
    master,
    detail,
    // 呈现配置整段随同一笔提交：服务端据此写 MODULES 三列并重发布（保存即生效）。
    // 栅格列数不在这里——它是页签级事实，随 tabs 各自提交
    openMode: draft.openMode,
    dialogWidth: draft.dialogWidth,
    dialogHeight: draft.dialogHeight,
  }
}

/**
 * 打开方式：切到本页签 / 新页签时**清掉窗体宽高**——那两种方式不消费尺寸，
 * 留着会变成"看着配了、其实不生效"的值（与运行态读回口径一致）。
 */
export function setOpenMode(draft: DesignDraft, openMode: string): DesignDraft {
  if (openMode !== 'DIALOG') {
    return { ...draft, openMode, dialogWidth: null, dialogHeight: null }
  }
  return { ...draft, openMode }
}

/** 弹窗宽高：留空（null）即按默认 720×560 开窗，由运行态与画板同一处解析。 */
export function setDialogSize(
  draft: DesignDraft,
  size: { width?: number | null; height?: number | null },
): DesignDraft {
  return {
    ...draft,
    dialogWidth: size.width === undefined ? draft.dialogWidth : size.width,
    dialogHeight: size.height === undefined ? draft.dialogHeight : size.height,
  }
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
      issues.push(`${row.label} ${row.lockReason ?? '不允许移出表单'}。`)
    }
    // 跨度上限是该行所属页签的列数（页签级事实）
    const rowColumns = tabColumns(draft, row.tabNo)
    if (row.span < 1 || row.span > rowColumns) {
      issues.push(`${row.label} 的列跨度超出 1..${rowColumns}。`)
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
    if (companions.length > 0 && mains.length === 0) {
      issues.push(`复合格 ${group} 的从字段没有配主字段。`)
    }
  }
  return issues
}

/** 草稿里出现的分节（按首次出现顺序；一格的从字段个数不设上限，与服务端同口径）。 */
export function sectionIds(draft: DesignDraft): string[] {
  const ids: string[] = []
  for (const row of draft.master) {
    const id = row.sectionId?.trim()
    if (id && !ids.includes(id)) ids.push(id)
  }
  return ids
}

/** 分节改名：整节一起改（分节是靠同名聚合的，只改一行会把它拆成两节）。 */
export function renameSection(draft: DesignDraft, from: string, to: string): DesignDraft {
  const next = to.trim().slice(0, MAX_SECTION_LENGTH)
  if (!next) return removeSection(draft, from)
  return {
    ...draft,
    master: draft.master.map((row) =>
      row.sectionId === from ? { ...row, sectionId: next } : row,
    ),
  }
}

/** 删除分节：该节字段回到无分节（不是删字段）。 */
export function removeSection(draft: DesignDraft, sectionId: string): DesignDraft {
  return {
    ...draft,
    master: draft.master.map((row) =>
      row.sectionId === sectionId ? { ...row, sectionId: null } : row,
    ),
  }
}

export interface DraftFile {
  kind: 'eos-form-layout'
  version: 1
  moduleId: number
  title: string
  /** 一行几列随各页签（`tabs[].columns`），文件里不再单列一个模块级列数。 */
  tabs: { no: number; title: string; columns?: number | null }[]
  master: RowInput[]
  detail: { key: string; hidden: boolean }[]
}

/** 导出当前草稿为可存档/可交接的文件（只含版式，不含单据数据）。 */
export function exportDraftFile(draft: DesignDraft): string {
  const file: DraftFile = {
    kind: 'eos-form-layout',
    version: 1,
    moduleId: draft.moduleId,
    title: draft.title,
    tabs: normalizeTabs(draft.tabs).map((tab) => ({ no: tab.no, title: tab.title, columns: tab.columns })),
    master: [...draft.master].sort(byPlacement).map((row) => ({
      key: row.key,
      tabNo: row.tabNo,
      span: row.span,
      rowSpan: row.rowSpan,
      newLine: row.newLine,
      sectionId: row.sectionId,
      cellGroup: row.cellGroup,
      cellRole: row.cellRole,
      hidden: row.hidden,
    })),
    detail: [...draft.detail].sort(byPlacement).map((row) => ({ key: row.key, hidden: row.hidden })),
  }
  return JSON.stringify(file, null, 2)
}

/**
 * 导入版式文件：**只认本模块的字段**——文件里当前模块没有的字段直接丢弃（不报错，
 * 因为模块间字段集本来就会不同），当前模块有而文件里没有的字段回到字段池（不凭空消失）。
 * 结构不对或不是本模块的文件则整体拒绝并说明原因。
 */
export function importDraftFile(draft: DesignDraft, text: string): { draft: DesignDraft } | { error: string } {
  let parsed: DraftFile
  try {
    parsed = JSON.parse(text) as DraftFile
  } catch {
    return { error: '不是有效的 JSON 文件。' }
  }
  if (parsed?.kind !== 'eos-form-layout' || parsed.version !== 1) {
    return { error: '不是表单版式文件（kind/version 不符）。' }
  }
  if (parsed.moduleId !== draft.moduleId) {
    return { error: `该文件属于模块 ${parsed.moduleId}（${parsed.title ?? ''}），不能套用到当前模块。` }
  }
  if (!Array.isArray(parsed.master)) {
    return { error: '文件缺少 master 段。' }
  }
  return { draft: applyRows(draft, parsed) }
}

/** 用来源的行集合重建草稿（导入与"套用来源"共用这一处裁剪逻辑）。 */
function applyRows(draft: DesignDraft, source: Pick<DraftFile, 'tabs' | 'master' | 'detail'>): DesignDraft {
  const knownMaster = new Map(
    [...draft.master, ...draft.masterPool].map((row) => [row.key.toUpperCase(), materialize(row)]),
  )
  const knownDetail = new Map(
    [...draft.detail, ...draft.detailPool].map((row) => [row.key.toUpperCase(), materialize(row)]),
  )
  // 页签连同各自的列数一起套用：来源的"页签 1 两列、页签 2 一列"在目标模块同样成立
  const tabs = normalizeTabs(source.tabs?.length ? source.tabs : [{ no: RESIDENT_TAB_NO, title: '' }])
  const rows: DesignRow[] = []
  for (const input of source.master) {
    const mine = knownMaster.get(String(input.key ?? '').toUpperCase())
    if (!mine) continue
    const tabNo = input.tabNo && input.tabNo > 0 ? input.tabNo : RESIDENT_TAB_NO
    rows.push({
      ...mine,
      tabNo,
      // 来源可能是另一个模块（同主表套用）或旧文件：跨度必须夹到**目标页签**的列数，
      // 否则套用后会出现本页签排不出来的跨度，保存被服务端拒
      span: clamp(input.span ?? mine.span, 1, resolveTabColumns(tabs, tabNo)),
      rowSpan: clamp(input.rowSpan ?? mine.rowSpan, 1, MAX_ROW_SPAN),
      newLine: input.newLine === true,
      sectionId: (input.sectionId ?? '').trim().slice(0, MAX_SECTION_LENGTH) || null,
      cellGroup: input.cellGroup ? String(input.cellGroup).slice(0, MAX_GROUP_LENGTH) : null,
      cellRole: input.cellRole === 1 || input.cellRole === 2 ? input.cellRole : mine.cellRole === 1 ? 1 : 0,
      hidden: input.hidden === true && !mine.locked,
      orderNo: rows.length + 1,
    })
  }
  const placedMaster = new Set(rows.map((row) => row.key.toUpperCase()))
  const details: DesignRow[] = []
  for (const input of source.detail ?? []) {
    const mine = knownDetail.get(String(input.key ?? '').toUpperCase())
    if (!mine) continue
    details.push({ ...mine, hidden: input.hidden === true && !mine.locked, orderNo: details.length + 1 })
  }
  const placedDetail = new Set(details.map((row) => row.key.toUpperCase()))
  return {
    ...draft,
    tabs,
    master: rows,
    detail: details,
    // 当前模块有、来源没有的字段**必须回到字段池**（不能就地消失——那等于替换版式时静默删字段）
    masterPool: poolAfter(draft.masterPool, draft.master, placedMaster),
    detailPool: poolAfter(draft.detailPool, draft.detail, placedDetail),
  }
}

function poolAfter(pool: PoolField[], current: DesignRow[], placed: Set<string>): PoolField[] {
  const kept = pool.filter((row) => !placed.has(row.key.toUpperCase()))
  const known = new Set(kept.map((row) => row.key.toUpperCase()))
  for (const row of current) {
    const key = row.key.toUpperCase()
    if (placed.has(key) || known.has(key)) continue
    kept.push({
      key: row.key,
      label: row.label,
      dataType: row.dataType,
      userVisible: row.userVisible,
      required: row.required,
      isPrimaryKey: row.isPrimaryKey,
      hasChooser: row.hasChooser,
      isVirtual: row.isVirtual,
      locked: row.locked,
      lockReason: row.lockReason,
    })
    known.add(key)
  }
  return kept
}

/** 字段池条目还没有排布属性：按缺省值补成可排布的草稿行（半行、无分节、无复合格）。 */
function materialize(entry: DesignRow | PoolField): DesignRow {
  if ('tabNo' in entry) return entry
  return {
    key: entry.key,
    label: entry.label,
    dataType: entry.dataType,
    tabNo: RESIDENT_TAB_NO,
    orderNo: 0,
    span: 2,
    rowSpan: 1,
    newLine: false,
    sectionId: null,
    cellGroup: null,
    cellRole: 0,
    hidden: false,
    locked: entry.locked,
    lockReason: entry.lockReason,
    userVisible: entry.userVisible,
    required: entry.required,
    isPrimaryKey: entry.isPrimaryKey,
    hasChooser: entry.hasChooser,
    isVirtual: entry.isVirtual,
  }
}

/** 同主表套用来源：把来源模块的版式搬到当前模块（字段按当前模块裁剪，见 applyRows）。 */
export function applyTemplateDraft(draft: DesignDraft, source: DesignState): DesignDraft {
  return applyRows(draft, {
    tabs: source.tabs,
    master: source.master.layout.map((row) => ({
      key: row.key,
      tabNo: row.tabNo,
      span: row.span,
      rowSpan: row.rowSpan,
      newLine: row.newLine,
      sectionId: row.sectionId,
      cellGroup: row.cellGroup,
      cellRole: row.cellRole,
      hidden: row.hidden,
    })),
    detail: source.detail.layout.map((row) => ({ key: row.key, hidden: row.hidden })),
  })
}

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, Math.round(value)))
}
