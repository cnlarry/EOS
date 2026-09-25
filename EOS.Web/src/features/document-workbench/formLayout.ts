import type { FormFieldDefinition } from './formDefinition'

/**
 * 版式行：字段在表单上的位置与占位。与服务端 FormLayoutRow 同形（模块级版式的编译产物）。
 * 明细是网格不是格版式，只消费 key/orderNo/hidden 三项（见 FormDetailLayoutRow）。
 */
export interface FormLayoutRow {
  key: string
  tabNo: number
  orderNo: number
  span: number
  rowSpan: number
  newLine: boolean
  sectionId: string | null
  cellGroup: string | null
  cellRole: number
  hidden: boolean
}

/** 明细版式行：列顺序与列显隐。 */
export interface FormDetailLayoutRow {
  key: string
  orderNo: number
  hidden: boolean
}

export interface FormTabRow {
  no: number
  title: string
}

/** 版式文档（推导结果 / 设计态草稿的形状）。 */
export interface FormLayoutDoc {
  columns: number
  /** 页签集合；空列表表示"未配置页签"，渲染侧兜底为单页签「默认」（不写库）。 */
  tabs: FormTabRow[]
  rows: FormLayoutRow[]
}

/** 行跨度上限：三行约 78px，更高的诉求改用多行文本控件高度。 */
export const FORM_ROW_SPAN_MAX = 3

/**
 * 未声明列数时的栅格列数。
 *
 * 取 4（不是 2）是因为**既有渲染就是这么排的**：统一表单历史上固定按 4 列排布，
 * 而实测 279 个工作台模块里有 140 个 `MODULES.FORM_COLUMNS` 为空——若默认成 2，
 * 这 140 个模块的表单会从每行 4 个字段变成每行 2 个，是纯粹的观感回退。
 */
export const DEFAULT_FORM_COLUMNS = 4

/** 备注类/长文本字段：整行独占且占两行高（与服务端同一判据）。 */
export function isWideTextField(field: Pick<FormFieldDefinition, 'key' | 'dataType'>): boolean {
  const type = (field.dataType ?? '').toLowerCase()
  if (type === 'text' || type === 'ntext') return true
  return /REMARK$/i.test(field.key)
}

/**
 * 零配置模块的默认版式推导（纯函数，与服务端同一套规则；前端画布预览、后端种子生成共用）。
 *
 * - 页签不推导（空列表 = 未配置，渲染侧兜底单页签「默认」）；
 * - 顺序取字段级排序位，缺省按传入次序；
 * - 列跨度取字段级配置，备注类整行；行跨度备注类 2、其余 1；
 * - 分节不推导——分节是独立于复合格格的显式配置（复合格靠 cellGroup/cellRole）；
 * - 隐藏只针对"本来就进不了表单"的字段：不可见、非必填、且不是复合格从字段。
 */
export function deriveDefaultLayout(fields: FormFieldDefinition[], columns: number): FormLayoutDoc {
  const cols = columns > 0 ? columns : DEFAULT_FORM_COLUMNS
  const rows = fields.map((field, index): FormLayoutRow => {
    const wide = isWideTextField(field)
    const group = field.cellGroup?.trim() ? field.cellGroup.trim() : null
    const cellRole = field.cellRole ?? 0
    const tabNo = (field.tabNo ?? 0) > 0 ? field.tabNo : 1
    return {
      key: field.key,
      tabNo,
      orderNo: field.formOrder ?? index + 1,
      // 字段级旧语义映射到子列：FORM_SPAN=1（半行）→ 2 子列、=2（整行独占）→ 4 子列
      span: wide ? cols : Math.min(Math.max(field.span ?? 1, 1), 2) * Math.floor(cols / 2),
      rowSpan: wide ? 2 : 1,
      newLine: field.newLine === true,
      sectionId: null,
      cellGroup: group,
      cellRole,
      hidden: !field.isVisible && !field.isRequired && !(cellRole === 2 && group != null),
    }
  })
  return { columns: cols, tabs: [], rows }
}

/** 版式行 → 明细版式行（明细只保留列顺序与列显隐）。 */
export function toDetailRows(rows: FormLayoutRow[]): FormDetailLayoutRow[] {
  return rows.map(row => ({ key: row.key, orderNo: row.orderNo, hidden: row.hidden }))
}

/** 装箱输入：一个格（复合格算一个格）的标识与占位。 */
export interface FormGridCell {
  key: string
  span?: number
  rowSpan?: number
  newLine?: boolean
}

/** 装箱结果：格在栅格中的显式位置（1 起算）。 */
export interface FormGridPlacement {
  key: string
  col: number
  row: number
  span: number
  rowSpan: number
}

/**
 * 显式装箱（纯函数）：整节一张栅格，每格的 grid-column / grid-row 由本函数算出来。
 *
 * **不使用 CSS 的隐式流，也不用 `grid-auto-flow: dense`**：dense 由浏览器决定回填顺序，
 * 视觉顺序与 DOM 顺序会脱节，键盘用户在表单上会乱跳。这里把顺序算成结果的一部分。
 *
 * 规则：
 * - `newLine` 强制另起一行，且此前的行不再接受回填（强制换行的语义不能被紧凑排列取消）；
 * - 本行放不下时另起一行；
 * - `fillHoles`（默认开）：把后续能塞进空洞的格提前填入，它们之间的相对顺序保持不变；
 * - 行跨度按占位处理：跨行格覆盖的行不再放别的格（显式定位下重叠会真叠在界面上）；
 * - 返回值按**视觉顺序**（先按行、再按列）排列，渲染时照此顺序输出 DOM，Tab 顺序与视觉一致；
 * - **节内独立装箱**：每节各自调用，row 从 1 起算（层级：页签 > 分节 > 行 > 格）；
 * - 窄屏（单列）自然退化为每格独占一行。
 */
export function packFormGrid(cells: FormGridCell[], columns: number, fillHoles = true): FormGridPlacement[] {
  const cols = Math.max(1, columns)
  const placed: FormGridPlacement[] = []

  const conflicts = (row: number, col: number, span: number, rowSpan: number) =>
    placed.some(item =>
      item.row < row + rowSpan && row < item.row + item.rowSpan
      && item.col < col + span && col < item.col + item.span)

  // 该行上最靠左的、放得下的空位（找不到返回 null）
  const firstFreeCol = (row: number, span: number, rowSpan: number): number | null => {
    for (let col = 1; col + span - 1 <= cols; col++) {
      if (!conflicts(row, col, span, rowSpan)) return col
    }
    return null
  }

  // 顺序放置用：该行已占用的右边界（含跨行格的延伸），下一格紧接其后
  const endOfRow = (row: number) => placed
    .filter(item => item.row <= row && row <= item.row + item.rowSpan - 1)
    .reduce((end, item) => Math.max(end, item.col + item.span - 1), 0)

  let floor = 1
  for (const cell of cells) {
    const span = Math.min(Math.max(cell.span ?? 1, 1), cols)
    const rowSpan = Math.min(Math.max(cell.rowSpan ?? 1, 1), FORM_ROW_SPAN_MAX)
    const lastRow = placed.reduce((max, item) => Math.max(max, item.row), 0)
    // 强制换行：只在当前行已有内容时另起一行（本就在行首时不必空一行）
    if (cell.newLine && lastRow > 0) floor = lastRow + 1

    let target: { row: number; col: number } | null = null
    if (fillHoles) {
      // 从本段起点往下找第一个放得下的空位（含此前留下的空洞）
      for (let row = floor; row <= lastRow && !target; row++) {
        const col = firstFreeCol(row, span, rowSpan)
        if (col != null) target = { row, col }
      }
    } else if (lastRow > 0) {
      // 顺序放置：只接在当前行末尾，接不下就另起一行（空洞保留）
      const col = endOfRow(lastRow) + 1
      if (col + span - 1 <= cols && !conflicts(lastRow, col, span, rowSpan)) {
        target = { row: lastRow, col }
      }
    }

    if (!target) {
      let row = Math.max(lastRow + 1, floor)
      let col = firstFreeCol(row, span, rowSpan)
      while (col == null) {
        // 跨行格挡住整行时才可能发生：继续往下找
        row += 1
        col = firstFreeCol(row, span, rowSpan)
      }
      target = { row, col }
    }

    placed.push({ key: cell.key, col: target.col, row: target.row, span, rowSpan })
  }

  return placed.sort((left, right) => left.row - right.row || left.col - right.col)
}

/**
 * 统一表单布局纯函数：把字段列表组装成"单元格 → 行"的结构。
 *
 * 单元格 = 主字段 + 同组从字段（FORM_CELL_GROUP/FORM_CELL_ROLE），
 * 从字段跟随主字段渲染进同一格（对应 [ID][选择][名称] 三件套）。
 * 行 = 按 FORM_COLUMNS 每行对数 + FORM_NEW_LINE 强制换行 + 整行独占（span=2 / REMARK 类）切分。
 */
/**
 * 浏览态尾部字段：单据生命周期系统列（建立/修改/审核/结案的人·日期·状态）。
 * 服务端独占写入并在新增/编辑态隐藏，浏览态只读；这里只决定它们的排布次序。
 */
const LIFECYCLE_TAIL_KEYS = [
  'CREATE_PERSON', 'CREATE_DATE',
  'LAST_UPDATE_BY', 'LAST_UPDATE_DATE',
  'CONFIRM_PERSON', 'CONFIRM_DATE', 'CONFIRM_TAG',
  'FINISHED_PERSON', 'FINISHED_DATE', 'FINISHED_TAG',
] as const

const LIFECYCLE_TAIL_RANK = new Map<string, number>(LIFECYCLE_TAIL_KEYS.map((key, index) => [key, index]))

/** 字段在尾部块中的次序；非生命周期列返回 -1。 */
function lifecycleRank(key: string): number {
  return LIFECYCLE_TAIL_RANK.get(key.toUpperCase()) ?? -1
}

/** 浏览态需要排到表单尾部的列（建立/修改/审核/结案的人·日期·状态）。 */
export function isLifecycleTailField(field: Pick<FormFieldDefinition, 'key'>): boolean {
  return lifecycleRank(field.key) >= 0
}

/** 装箱输入的最小形状：运行态字段与设计态版式行都能映射到它（可空项按缺省处理）。 */
export interface PackableCell {
  key: string
  span?: number | null
  rowSpan?: number | null
  newLine?: boolean | null
  sectionId?: string | null
  cellGroup?: string | null
  cellRole?: number | null
  tabNo?: number | null
}

export interface PackedFormCell<T> {
  /** 复合格算一个格：第一个是主字段，其余是从字段（同格联动显示）。 */
  cell: T[]
  placement: FormGridPlacement
}

export interface PackedFormSection<T> {
  title: string | null
  cells: PackedFormCell<T>[]
}

/** 复合格归拢：主字段（CELL_ROLE=1）带走同组的从字段；孤立的从字段不成格。 */
export function buildPackedCells<T extends PackableCell>(items: T[]): T[][] {
  const companions = new Map<string, T[]>()
  for (const item of items) {
    if (item.cellRole === 2 && item.cellGroup) {
      const list = companions.get(item.cellGroup) ?? []
      list.push(item)
      companions.set(item.cellGroup, list)
    }
  }
  const cells: T[][] = []
  for (const item of items) {
    if (item.cellRole === 2) continue
    const group = item.cellGroup?.trim()
    if (item.cellRole === 1 && group && companions.has(item.cellGroup as string)) {
      cells.push([item, ...(companions.get(item.cellGroup as string) ?? [])])
    } else {
      cells.push([item])
    }
  }
  return cells
}

/**
 * 整节单栅格装箱（运行态与设计态**共用同一份规则**）：分节 → 节内显式装箱。
 *
 * 分节优先按 `sectionId`（版式表的显式分节）；**若整批字段都没有 sectionId**，回落到既有的
 * "同组名下有 ≥2 个主字段即视为分节"启发式——该启发式是 `FIELDS.FORM_CELL_GROUP` 一列两义
 * 的历史产物，退役前保持观感不变。
 *
 * `tailCells` 给出"必须排到表单尾部"的格（浏览态的生命周期列），它们自成末尾一节。
 */
export function packFormSections<T extends PackableCell>(
  items: T[],
  columns: number,
  options: { fillHoles?: boolean; tailCells?: (main: T) => boolean } = {},
): PackedFormSection<T>[] {
  const cols = Math.max(1, columns)
  const fillHoles = options.fillHoles ?? true
  const cells = buildPackedCells(items)

  const hasSections = cells.some(cell => (cell[0].sectionId ?? '').trim().length > 0)
  const tails: T[][] = []
  const grouped: { title: string | null; cells: T[][] }[] = []
  if (hasSections) {
    const byTitle = new Map<string, T[][]>()
    for (const cell of cells) {
      if (options.tailCells?.(cell[0])) {
        tails.push(cell)
        continue
      }
      const title = (cell[0].sectionId ?? '').trim()
      byTitle.set(title, [...(byTitle.get(title) ?? []), cell])
    }
    // 无题节排最前，其余按首次出现顺序
    const titles = [...byTitle.keys()]
    for (const title of titles.sort((left, right) => (left === '' ? -1 : right === '' ? 1 : 0))) {
      grouped.push({ title: title.length > 0 ? title : null, cells: byTitle.get(title) ?? [] })
    }
  } else {
    const countsByGroup = new Map<string, number>()
    for (const cell of cells) {
      const group = cell[0].cellGroup?.trim()
      if (group) countsByGroup.set(group, (countsByGroup.get(group) ?? 0) + 1)
    }
    const defaultCells: T[][] = []
    const indexByGroup = new Map<string, number>()
    for (const cell of cells) {
      if (options.tailCells?.(cell[0])) {
        tails.push(cell)
        continue
      }
      const group = cell[0].cellGroup?.trim()
      if (group && (countsByGroup.get(group) ?? 0) >= 2) {
        const existing = indexByGroup.get(group)
        if (existing === undefined) {
          indexByGroup.set(group, grouped.length)
          grouped.push({ title: group, cells: [cell] })
        } else {
          grouped[existing].cells.push(cell)
        }
      } else {
        defaultCells.push(cell)
      }
    }
    if (defaultCells.length > 0) {
      grouped.unshift({ title: null, cells: defaultCells })
    }
  }

  if (tails.length > 0) {
    tails.sort((left, right) => lifecycleRank(left[0].key) - lifecycleRank(right[0].key))
    grouped.push({ title: null, cells: tails })
  }

  return grouped
    .filter(section => section.cells.length > 0)
    .map(section => ({
      title: section.title,
      cells: packCellsInSection(section.cells, cols, fillHoles),
    }))
}

function packCellsInSection<T extends PackableCell>(cells: T[][], columns: number, fillHoles: boolean): PackedFormCell<T>[] {
  const placements = packFormGrid(
    cells.map(cell => ({
      key: cell[0].key,
      span: cell[0].span ?? 1,
      rowSpan: cell[0].rowSpan ?? 1,
      newLine: cell[0].newLine ?? false,
    })),
    columns,
    fillHoles,
  )
  const byKey = new Map(cells.map(cell => [cell[0].key, cell]))
  return placements
    .map(placement => {
      const cell = byKey.get(placement.key)
      return cell ? { cell, placement } : null
    })
    .filter((item): item is PackedFormCell<T> => item !== null)
}



export function buildFormCells(fields: FormFieldDefinition[]): FormFieldDefinition[][] {
  return buildPackedCells(fields)
}
