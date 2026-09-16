import type { FormFieldDefinition } from './formDefinition'
import { isFullWidthField } from './formFieldKind'

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

/** 单元格在尾部块中的次序；非生命周期列返回 -1。复合格任一项命中即整格归入尾部。 */
function lifecycleRank(cell: FormFieldDefinition[]): number {
  for (const item of cell) {
    const rank = LIFECYCLE_TAIL_RANK.get(item.key.toUpperCase())
    if (rank != null) return rank
  }
  return -1
}

/**
 * 浏览态把生命周期列挪到其它内容之后：先从各节摘出，再按固定次序追加为末尾一节
 * （无标题，只有 10px 节间距），无论元数据顺序与分节如何都排在表单尾部。
 */
export function moveLifecycleToTail(sections: FormSection[]): FormSection[] {
  const tail: { rank: number; cell: FormFieldDefinition[] }[] = []
  const kept = sections
    .map(section => {
      const cells: FormFieldDefinition[][] = []
      for (const cell of section.cells) {
        const rank = lifecycleRank(cell)
        if (rank >= 0) tail.push({ rank, cell })
        else cells.push(cell)
      }
      return { ...section, cells }
    })
    .filter(section => section.cells.length > 0)
  if (tail.length === 0) return kept
  const ordered = tail.sort((left, right) => left.rank - right.rank).map(item => item.cell)
  return [...kept, { title: null, cells: ordered }]
}

export function buildFormCells(fields: FormFieldDefinition[]): FormFieldDefinition[][] {
  const companions = new Map<string, FormFieldDefinition[]>()
  for (const field of fields) {
    if (field.cellRole === 2 && field.cellGroup) {
      const list = companions.get(field.cellGroup) ?? []
      list.push(field)
      companions.set(field.cellGroup, list)
    }
  }
  const cells: FormFieldDefinition[][] = []
  for (const field of fields) {
    if (field.cellRole === 2) continue
    if (field.cellRole === 1 && field.cellGroup && companions.has(field.cellGroup)) {
      cells.push([field, ...(companions.get(field.cellGroup) ?? [])])
    } else {
      cells.push([field])
    }
  }
  return cells
}

export function buildFormRows(cells: FormFieldDefinition[][], columns: number): FormFieldDefinition[][][] {
  const perRow = Math.max(1, columns)
  const rows: FormFieldDefinition[][][] = []
  let current: FormFieldDefinition[][] = []
  const flush = () => {
    if (current.length > 0) {
      rows.push(current)
      current = []
    }
  }
  for (const cell of cells) {
    const main = cell[0]
    const fullWidth = main.span === 2 || isFullWidthField(main)
    if ((main.newLine || fullWidth) && current.length > 0) flush()
    current.push(cell)
    if (fullWidth || current.length >= perRow) flush()
  }
  flush()
  return rows
}

/** 表单分节：标题 + 该节的单元格序列 */
export interface FormSection {
  title: string | null
  cells: FormFieldDefinition[][]
}

/**
 * 页签内分节：
 * - 复用 FORM_CELL_GROUP 归组：同一组名下有 ≥2 个主字段单元格时视为业务分节，
 *   节标题取组名原文（元数据方可配置中文名）；仅含单个复合三件套（ID+选择+名称）的
 *   组名不立节——那是复合单元格语义，不是分区语义。
 * - 无分组字段与未成节的单格组一律归入默认节（无标题、排在最前），保持现有版式不回退。
 */
export function buildFormSections(cells: FormFieldDefinition[][]): FormSection[] {
  const countsByGroup = new Map<string, number>()
  for (const cell of cells) {
    const group = cell[0].cellGroup?.trim()
    if (group) countsByGroup.set(group, (countsByGroup.get(group) ?? 0) + 1)
  }
  const defaultCells: FormFieldDefinition[][] = []
  const sections: FormSection[] = []
  const indexByGroup = new Map<string, number>()
  for (const cell of cells) {
    const group = cell[0].cellGroup?.trim()
    if (group && (countsByGroup.get(group) ?? 0) >= 2) {
      let sectionIndex = indexByGroup.get(group)
      if (sectionIndex == null) {
        sectionIndex = sections.length
        indexByGroup.set(group, sectionIndex)
        sections.push({ title: group, cells: [] })
      }
      sections[sectionIndex].cells.push(cell)
      continue
    }
    defaultCells.push(cell)
  }
  if (defaultCells.length > 0) sections.unshift({ title: null, cells: defaultCells })
  return sections
}
