import type { FormFieldDefinition } from './formDefinition'
import { isFullWidthField } from './formFieldKind'

/**
 * 统一表单布局纯函数：把字段列表组装成"单元格 → 行"的结构。
 *
 * 单元格 = 主字段 + 同组从字段（FORM_CELL_GROUP/FORM_CELL_ROLE），
 * 从字段跟随主字段渲染进同一格（对应旧系统 [ID][选择][名称] 三件套）。
 * 行 = 按 FORM_COLUMNS 每行对数 + FORM_NEW_LINE 强制换行 + 整行独占（span=2 / REMARK 类）切分。
 */
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

/** 表单分节：标题 + 该节的单元格序列（ADR-006 决策 1 分区表单） */
export interface FormSection {
  title: string | null
  cells: FormFieldDefinition[][]
}

/**
 * 页签内分节（ADR-006 决策 1「分区表单」）：
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
