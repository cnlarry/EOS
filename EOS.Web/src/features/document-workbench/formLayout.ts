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
