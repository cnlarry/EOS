/**
 * 明细录入网格的单元格光标移动（Excel 口径）。
 *
 * 只做"下一格是谁"的判定，不碰 DOM：调用方按行号/列号去取控件并聚焦。
 * 之所以不按固定列数算，是因为一行里的可录入控件数目并不齐（只读列没有输入框、
 * 复合字段多一个选择按钮旁边的输入框），按目标行实际的控件数取模才不会跳格。
 */

export type GridMove = 'next' | 'prev' | 'down' | 'up'

export interface GridCursor {
  row: number
  col: number
}

export interface GridMoveResult {
  /** 落点；null 表示无处可去（首格再 Shift+Tab、末行再 ArrowDown 等） */
  cursor: GridCursor | null
  /** 需要新增一行（Tab/Enter 越过末行）：落点指向新增行 */
  appendRow: boolean
}

/**
 * @param rowCount 当前行数
 * @param controlsInRow 取某行的可录入控件数
 * @param allowAppend 越过末行时是否允许"新增一行"（Enter / Tab 允许，方向键不允许）
 */
export function moveCursor(
  cursor: GridCursor,
  move: GridMove,
  rowCount: number,
  controlsInRow: (row: number) => number,
  allowAppend: boolean,
): GridMoveResult {
  const own = controlsInRow(cursor.row)
  switch (move) {
    case 'next': {
      if (cursor.col + 1 < own) return { cursor: { row: cursor.row, col: cursor.col + 1 }, appendRow: false }
      if (cursor.row + 1 < rowCount) return { cursor: { row: cursor.row + 1, col: 0 }, appendRow: false }
      return allowAppend
        ? { cursor: { row: rowCount, col: 0 }, appendRow: true }
        : { cursor: null, appendRow: false }
    }
    case 'prev': {
      if (cursor.col > 0) return { cursor: { row: cursor.row, col: cursor.col - 1 }, appendRow: false }
      if (cursor.row > 0) {
        const previous = controlsInRow(cursor.row - 1)
        return { cursor: { row: cursor.row - 1, col: Math.max(0, previous - 1) }, appendRow: false }
      }
      return { cursor: null, appendRow: false }
    }
    case 'down': {
      if (cursor.row + 1 < rowCount) {
        const next = controlsInRow(cursor.row + 1)
        return { cursor: { row: cursor.row + 1, col: Math.min(cursor.col, Math.max(0, next - 1)) }, appendRow: false }
      }
      return allowAppend
        ? { cursor: { row: rowCount, col: cursor.col }, appendRow: true }
        : { cursor: null, appendRow: false }
    }
    case 'up': {
      if (cursor.row > 0) {
        const previous = controlsInRow(cursor.row - 1)
        return { cursor: { row: cursor.row - 1, col: Math.min(cursor.col, Math.max(0, previous - 1)) }, appendRow: false }
      }
      return { cursor: null, appendRow: false }
    }
    default:
      return { cursor: null, appendRow: false }
  }
}
