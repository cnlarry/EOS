import { describe, expect, it } from 'vitest'
import { clampCell, isCellInRange, moveRange, normalizeRange, type CellRange } from './tableRange'

const range = (ar: number, ac: number, fr: number, fc: number): CellRange => ({ anchor: { row: ar, col: ac }, focus: { row: fr, col: fc } })

describe('normalizeRange', () => {
  it('反向拖选也折成左上/右下', () => {
    expect(normalizeRange(range(3, 5, 1, 2))).toEqual({ top: 1, bottom: 3, left: 2, right: 5 })
  })

  it('单格选区四边相同', () => {
    expect(normalizeRange(range(2, 2, 2, 2))).toEqual({ top: 2, bottom: 2, left: 2, right: 2 })
  })
})

describe('isCellInRange', () => {
  it('落在矩形内才算选中', () => {
    const selection = range(1, 1, 3, 2)
    expect(isCellInRange(selection, 1, 1)).toBe(true)
    expect(isCellInRange(selection, 3, 2)).toBe(true)
    expect(isCellInRange(selection, 2, 3)).toBe(false)
    expect(isCellInRange(selection, 4, 1)).toBe(false)
    expect(isCellInRange(null, 0, 0)).toBe(false)
  })
})

describe('clampCell', () => {
  it('钳到表格边界内', () => {
    expect(clampCell(-3, -1, 5, 4)).toEqual({ row: 0, col: 0 })
    expect(clampCell(9, 9, 5, 4)).toEqual({ row: 4, col: 3 })
  })

  it('空表时不回负数', () => {
    expect(clampCell(2, 2, 0, 0)).toEqual({ row: 0, col: 0 })
  })
})

describe('moveRange', () => {
  it('不按 Shift 时整块跟到新格', () => {
    expect(moveRange(range(1, 1, 1, 1), 1, 0, 10, 5, false)).toEqual(range(2, 1, 2, 1))
  })

  it('按 Shift 只移动 focus，anchor 不动', () => {
    expect(moveRange(range(1, 1, 1, 1), 0, 1, 10, 5, true)).toEqual(range(1, 1, 1, 2))
  })

  it('到边界后停住不越界', () => {
    expect(moveRange(range(0, 0, 0, 0), -1, -1, 10, 5, false)).toEqual(range(0, 0, 0, 0))
    expect(moveRange(range(9, 4, 9, 4), 1, 1, 10, 5, false)).toEqual(range(9, 4, 9, 4))
  })
})
