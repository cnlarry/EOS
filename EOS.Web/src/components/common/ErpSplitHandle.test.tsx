import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { firePointer, stubRect } from '../../test/pointer'
import { useVerticalSplit } from '../../hooks/useVerticalSplit'
import { ErpSplitHandle } from './ErpSplitHandle'

const KEY = 'test-split'

/** 最小可用装配：页面根 = 分割区，分割条的下一个兄弟 = 明细区 */
function Harness() {
  const split = useVerticalSplit(KEY)
  return (
    <div className="split-page">
      <div className="split-top" />
      <ErpSplitHandle split={split} />
      <section className="split-bottom" style={split.bottomStyle} />
    </div>
  )
}

/**
 * jsdom 里所有矩形都是 0：给分割区造 600px 高、给明细区造 180px 高，
 * 于是"默认比例"= 180/600 = 30%（与 app.css 里明细卡的默认档位同量级）。
 */
function renderSplit() {
  const view = render(<Harness />)
  const page = view.container.querySelector('.split-page') as HTMLElement
  const bottom = view.container.querySelector('.split-bottom') as HTMLElement
  const handle = screen.getByRole('separator')
  stubRect(page, 0, 900, 0, 600)
  stubRect(bottom, 0, 900, 0, 180)
  return { ...view, page, bottom, handle }
}

describe('ErpSplitHandle', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => localStorage.clear())

  it('没拖过时不写内联样式（沿用样式里的默认分配）', () => {
    const { bottom, handle } = renderSplit()
    expect(bottom.style.cssText).toBe('')
    expect(handle).toHaveAttribute('aria-orientation', 'horizontal')
    expect(handle).toHaveAccessibleName('调整主表与明细高度')
  })

  it('向上拖：明细变高、主表变矮，松手后落库', () => {
    const { bottom, handle } = renderSplit()
    firePointer('pointerdown', handle, { clientX: 300, clientY: 430, button: 0 })
    expect(document.body).toHaveClass('erp-row-resizing')
    firePointer('pointermove', handle, { clientX: 300, clientY: 370 })
    expect(bottom.style.flexBasis).toBe('40%')
    firePointer('pointerup', handle, { clientX: 300, clientY: 370 })
    expect(document.body).not.toHaveClass('erp-row-resizing')
    expect(localStorage.getItem(KEY)).toBe('0.4')
  })

  it('向下拖：主表变高、明细变矮，最低收到"表头 + 一行"', () => {
    const { bottom, handle } = renderSplit()
    firePointer('pointerdown', handle, { clientX: 300, clientY: 430, button: 0 })
    firePointer('pointermove', handle, { clientX: 300, clientY: 490 })
    firePointer('pointerup', handle, { clientX: 300, clientY: 490 })
    // 30% - 10% = 20%，仍在明细最小 92px/600px ≈ 15.33% 之上
    expect(bottom.style.flexBasis).toBe('20%')
    expect(bottom.style.minHeight).toBe('92px')
  })

  it('拖过头时按最小高度夹住（主表 140px、明细 92px）', () => {
    const { bottom, handle } = renderSplit()
    firePointer('pointerdown', handle, { clientX: 300, clientY: 430, button: 0 })
    firePointer('pointermove', handle, { clientX: 300, clientY: -500 })
    expect(bottom.style.flexBasis).toBe('76.67%')
    firePointer('pointermove', handle, { clientX: 300, clientY: 2000 })
    expect(bottom.style.flexBasis).toBe('15.33%')
  })

  it('方向键调整高度，Shift 走大步', () => {
    localStorage.setItem(KEY, '0.5')
    const { bottom, handle } = renderSplit()
    fireEvent.keyDown(handle, { key: 'ArrowUp' })
    expect(bottom.style.flexBasis).toBe('52.67%')
    fireEvent.keyDown(handle, { key: 'ArrowDown', shiftKey: true })
    expect(bottom.style.flexBasis).toBe('42%')
  })

  it('已保存的比例在重新挂载后生效，双击复位清掉记录', () => {
    localStorage.setItem(KEY, '0.42')
    const { bottom, handle } = renderSplit()
    expect(bottom.style.flexBasis).toBe('42%')
    expect(bottom.style.maxHeight).toBe('none')
    expect(bottom.style.minHeight).toBe('92px')
    fireEvent.doubleClick(handle)
    expect(bottom.style.cssText).toBe('')
    expect(localStorage.getItem(KEY)).toBeNull()
  })

  it('忽略非法记录，退回默认分配', () => {
    localStorage.setItem(KEY, 'not-a-number')
    const { bottom } = renderSplit()
    expect(bottom.style.cssText).toBe('')
  })
})
