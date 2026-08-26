import { describe, expect, it } from 'vitest'
import { childPad, dotLeft, groupPad, lineSidebar, lineTree } from './menuDepth'

describe('menuDepth 多级菜单深度几何', () => {
  it('分组内边距：2 层 40、3 层 59，之后每层 +19', () => {
    expect(groupPad(2)).toBe(40)
    expect(groupPad(3)).toBe(59)
    expect(groupPad(4)).toBe(78)
    expect(groupPad(5)).toBe(97)
    expect(groupPad(6)).toBe(116)
  })

  it('子菜单内边距：验收值 59/78/96.5，之后每层 +19', () => {
    expect(childPad(2)).toBe(59)
    expect(childPad(3)).toBe(78)
    expect(childPad(4)).toBe(96.5)
    expect(childPad(5)).toBe(115.5)
    expect(childPad(6)).toBe(134.5)
  })

  it('小圆点 left：验收值 46.5/65.5/84，之后每层 +19', () => {
    expect(dotLeft(2)).toBe(46.5)
    expect(dotLeft(3)).toBe(65.5)
    expect(dotLeft(4)).toBe(84)
    expect(dotLeft(5)).toBe(103)
    expect(dotLeft(6)).toBe(122)
  })

  it('虚线 left（侧栏）：验收值 28/53/72.5，之后每层 +19', () => {
    expect(lineSidebar(2)).toBe(28)
    expect(lineSidebar(3)).toBe(53)
    expect(lineSidebar(4)).toBe(72.5)
    expect(lineSidebar(5)).toBe(91.5)
    expect(lineSidebar(6)).toBe(110.5)
  })

  it('虚线 left（菜单管理）：验收值 20/46.5/65.5，之后每层 +19', () => {
    expect(lineTree(2)).toBe(20)
    expect(lineTree(3)).toBe(46.5)
    expect(lineTree(4)).toBe(65.5)
    expect(lineTree(5)).toBe(84.5)
    expect(lineTree(6)).toBe(103.5)
  })
})