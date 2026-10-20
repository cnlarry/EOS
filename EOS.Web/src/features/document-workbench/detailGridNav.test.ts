import { describe, expect, it } from 'vitest'
import { moveCursor } from './detailGridNav'

// 三行，控件数各不相同（只读列没有输入框）
const counts = [3, 2, 3]
const controlsInRow = (row: number) => counts[row] ?? 0

describe('moveCursor', () => {
  it('Tab 在同一行向右，行尾折到下一行首格', () => {
    expect(moveCursor({ row: 0, col: 1 }, 'next', 3, controlsInRow, true)).toEqual({ cursor: { row: 0, col: 2 }, appendRow: false })
    expect(moveCursor({ row: 0, col: 2 }, 'next', 3, controlsInRow, true)).toEqual({ cursor: { row: 1, col: 0 }, appendRow: false })
  })

  it('末行末格再 Tab：新增一行，光标落到新行首格', () => {
    expect(moveCursor({ row: 2, col: 2 }, 'next', 3, controlsInRow, true)).toEqual({ cursor: { row: 3, col: 0 }, appendRow: true })
  })

  it('不允许新增时（方向键）末行不越界', () => {
    expect(moveCursor({ row: 2, col: 2 }, 'next', 3, controlsInRow, false)).toEqual({ cursor: null, appendRow: false })
  })

  it('Shift+Tab 回到上一行最后一格（按上一行实际控件数）', () => {
    expect(moveCursor({ row: 1, col: 0 }, 'prev', 3, controlsInRow, true)).toEqual({ cursor: { row: 0, col: 2 }, appendRow: false })
  })

  it('首格再 Shift+Tab 无处可去', () => {
    expect(moveCursor({ row: 0, col: 0 }, 'prev', 3, controlsInRow, true)).toEqual({ cursor: null, appendRow: false })
  })

  it('Enter 向下同列；目标行列少时钳到最后一格', () => {
    expect(moveCursor({ row: 0, col: 0 }, 'down', 3, controlsInRow, true)).toEqual({ cursor: { row: 1, col: 0 }, appendRow: false })
    expect(moveCursor({ row: 1, col: 1 }, 'down', 3, controlsInRow, true)).toEqual({ cursor: { row: 2, col: 1 }, appendRow: false })
    expect(moveCursor({ row: 0, col: 2 }, 'down', 3, counts2, true)).toEqual({ cursor: { row: 1, col: 1 }, appendRow: false })
  })

  it('末行 Enter 新增一行，光标留在同一列', () => {
    expect(moveCursor({ row: 2, col: 1 }, 'down', 3, controlsInRow, true)).toEqual({ cursor: { row: 3, col: 1 }, appendRow: true })
  })

  it('Shift+Enter 向上，首行无处可去', () => {
    expect(moveCursor({ row: 2, col: 0 }, 'up', 3, controlsInRow, true)).toEqual({ cursor: { row: 1, col: 0 }, appendRow: false })
    expect(moveCursor({ row: 0, col: 2 }, 'up', 3, controlsInRow, true)).toEqual({ cursor: null, appendRow: false })
  })
})

function counts2(row: number) {
  return row === 0 ? 3 : 2
}
