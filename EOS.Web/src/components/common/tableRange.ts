/**
 * 电子表格的单元格区域：选中、扩展与判定。
 *
 * 行/列一律按**当前渲染顺序**（行 = 数据数组下标，列 = 可见叶列下标）计数，
 * 与 `ErpTable` 的 `rowsModel` / `getVisibleCells()` 一致；不掺数据主键，
 * 这样翻页、排序后区域仍能落到眼前的格子上。
 */

export interface CellPoint {
  row: number
  col: number
}

/** anchor = 起选格（区域左上角的来源），focus = 当前活动格（Shift 扩展时的另一端） */
export interface CellRange {
  anchor: CellPoint
  focus: CellPoint
}

export interface NormalizedRange {
  top: number
  bottom: number
  left: number
  right: number
}

export function normalizeRange(range: CellRange): NormalizedRange {
  return {
    top: Math.min(range.anchor.row, range.focus.row),
    bottom: Math.max(range.anchor.row, range.focus.row),
    left: Math.min(range.anchor.col, range.focus.col),
    right: Math.max(range.anchor.col, range.focus.col),
  }
}

export function isCellInRange(range: CellRange | null, row: number, col: number): boolean {
  if (!range) return false
  const { top, bottom, left, right } = normalizeRange(range)
  return row >= top && row <= bottom && col >= left && col <= right
}

/** 把坐标钳到 [0, count-1]；count 为 0 时返回 0（调用方自行判定空表） */
export function clampCell(row: number, col: number, rowCount: number, colCount: number): CellPoint {
  return {
    row: Math.max(0, Math.min(row, Math.max(0, rowCount - 1))),
    col: Math.max(0, Math.min(col, Math.max(0, colCount - 1))),
  }
}

/**
 * 方向键移动后的落点。
 *
 * `extend`（Shift）只移动 focus，anchor 不动——与 Excel 的扩展选区一致；
 * 不按 Shift 时整块区域跟到新格（anchor = focus = 落点）。
 */
export function moveRange(range: CellRange, rowDelta: number, colDelta: number, rowCount: number, colCount: number, extend: boolean): CellRange {
  const next = clampCell(range.focus.row + rowDelta, range.focus.col + colDelta, rowCount, colCount)
  return extend ? { anchor: range.anchor, focus: next } : { anchor: next, focus: next }
}
