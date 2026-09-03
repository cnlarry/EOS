import { fireEvent, render } from '@testing-library/react'
import { useRef } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useColumnResize } from './useColumnResize'

// jsdom 可能未实现 PointerEvent；退回 MouseEvent 以保证 clientX 可传递
if (!('PointerEvent' in window)) {
  ;(window as unknown as { PointerEvent: typeof MouseEvent }).PointerEvent = MouseEvent
}

interface HarnessProps {
  storageKey: string
  persist?: boolean
  onColumnResize?: (columnKey: string, width: number) => void
  minWidths?: Record<string, number>
  fitRef?: { current: (() => Record<string, number>) | null }
  /** 追加一列固定列（data-col-resizable=false），模拟主表选择列 */
  fixedColumn?: boolean
  /** 首列启用编辑态列宽下限（data-col-min-floor=true），模拟表单明细录入列 */
  floorColumn?: boolean
}

function Harness({ storageKey, persist, onColumnResize, minWidths, fitRef, fixedColumn, floorColumn }: HarnessProps) {
  const ref = useRef<HTMLTableElement>(null)
  useColumnResize(ref, storageKey, { persist: persist ?? true, onColumnResize, fitRef })
  return (
    <table ref={ref}>
      <thead>
        <tr>
          <th data-col-key="a" data-col-min-width={minWidths?.a} data-col-min-floor={floorColumn ? 'true' : undefined}>{'A'}</th>
          <th data-col-key="b" data-col-min-width={minWidths?.b}>{'B'}</th>
          {fixedColumn && <th data-col-key="select" data-col-min-width={40} data-col-resizable="false">{'选择'}</th>}
        </tr>
      </thead>
      <tbody>
        <tr><td>{'aaa'}</td><td>{'bb'}</td>{fixedColumn && <td>{'☐'}</td>}</tr>
      </tbody>
    </table>
  )
}

function handleOf(table: HTMLElement, index: number): HTMLElement {
  const handles = table.querySelectorAll('th .erp-col-resizer')
  return handles[index] as HTMLElement
}

describe('useColumnResize', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('无 storageKey 时不注入结构', () => {
    const { container } = render(<Harness storageKey="" />)
    const table = container.querySelector('table')!
    expect(table.querySelector('colgroup')).toBeNull()
    expect(table.querySelectorAll('.erp-col-resizer').length).toBe(0)
  })

  it('按 data-col-min-width 初始化 colgroup 与拖拽手柄', () => {
    const { container } = render(<Harness storageKey="t1" minWidths={{ a: 120, b: 80 }} />)
    const table = container.querySelector('table')!
    const cols = table.querySelectorAll('colgroup col')
    expect(cols.length).toBe(3)
    expect((cols[0] as HTMLTableColElement).style.width).toBe('120px')
    expect((cols[1] as HTMLTableColElement).style.width).toBe('80px')
    expect(table.style.tableLayout).toBe('fixed')
    expect(table.querySelectorAll('.erp-col-resizer').length).toBe(2)
  })

  it('拖拽手柄持久化宽度并触发 onColumnResize', () => {
    const onColumnResize = vi.fn()
    const { container } = render(<Harness storageKey="t1" minWidths={{ a: 100, b: 60 }} onColumnResize={onColumnResize} />)
    const table = container.querySelector('table')!
    const handle = handleOf(table, 0)
    fireEvent.pointerDown(handle, { clientX: 10 })
    fireEvent.pointerMove(handle, { clientX: 40 })
    fireEvent.pointerUp(handle, { clientX: 40 })
    const cols = table.querySelectorAll('colgroup col')
    expect((cols[0] as HTMLTableColElement).style.width).toBe('130px')
    expect(onColumnResize).toHaveBeenCalledWith('a', 130)
    expect(JSON.parse(localStorage.getItem('erp-table-cols:t1')!)).toEqual({ a: 130, b: 60 })
    expect(table.classList.contains('erp-col-resizing')).toBe(false)
  })

  it('persist=false 时不写 localStorage，仍回调', () => {
    const onColumnResize = vi.fn()
    const { container } = render(<Harness storageKey="t1" persist={false} minWidths={{ a: 100, b: 60 }} onColumnResize={onColumnResize} />)
    const table = container.querySelector('table')!
    const handle = handleOf(table, 0)
    fireEvent.pointerDown(handle, { clientX: 10 })
    fireEvent.pointerMove(handle, { clientX: 30 })
    fireEvent.pointerUp(handle, { clientX: 30 })
    expect(localStorage.getItem('erp-table-cols:t1')).toBeNull()
    expect(onColumnResize).toHaveBeenCalledWith('a', 120)
  })

  it('无位移的快速两次点击触发双击自适应', () => {
    const onColumnResize = vi.fn()
    const { container } = render(<Harness storageKey="t1" minWidths={{ a: 100, b: 60 }} onColumnResize={onColumnResize} />)
    const table = container.querySelector('table')!
    const handle = handleOf(table, 1)
    fireEvent.pointerDown(handle, { clientX: 10 })
    fireEvent.pointerUp(handle, { clientX: 10 })
    fireEvent.pointerDown(handle, { clientX: 10 })
    fireEvent.pointerUp(handle, { clientX: 10 })
    expect(onColumnResize).toHaveBeenCalledWith('b', 48)
  })

  it('fitRef 自适应返回全部可调列宽并跳过不可调整列（如选择列）', () => {
    const onColumnResize = vi.fn()
    const fitRef: { current: (() => Record<string, number>) | null } = { current: null }
    render(<Harness storageKey="t1" minWidths={{ a: 100, b: 60 }} onColumnResize={onColumnResize} fitRef={fitRef} fixedColumn />)
    expect(fitRef.current).not.toBeNull()
    expect(fitRef.current!()).toEqual({ a: 48, b: 48 })
    expect(onColumnResize).not.toHaveBeenCalled()
  })

  it('minWidthFloor 列的历史/拖拽宽度不会低于列宽下限', () => {
    localStorage.setItem('erp-table-cols:t1', JSON.stringify({ a: 60, b: 80 }))
    const { container } = render(<Harness storageKey="t1" minWidths={{ a: 120, b: 80 }} floorColumn />)
    const table = container.querySelector('table')!
    const cols = table.querySelectorAll('colgroup col')
    expect((cols[0] as HTMLTableColElement).style.width).toBe('120px')
    expect((cols[1] as HTMLTableColElement).style.width).toBe('80px')
    const handle = handleOf(table, 0)
    fireEvent.pointerDown(handle, { clientX: 10 })
    fireEvent.pointerMove(handle, { clientX: 0 })
    fireEvent.pointerUp(handle, { clientX: 0 })
    expect((cols[0] as HTMLTableColElement).style.width).toBe('120px')
  })

  it('已保存宽度优先于默认宽度', () => {
    localStorage.setItem('erp-table-cols:t1', JSON.stringify({ a: 200, b: 50 }))
    const { container } = render(<Harness storageKey="t1" minWidths={{ a: 100, b: 60 }} />)
    const cols = container.querySelectorAll('colgroup col')
    expect((cols[0] as HTMLTableColElement).style.width).toBe('200px')
    expect((cols[1] as HTMLTableColElement).style.width).toBe('50px')
  })

  it('早期数组格式（按列索引）自动迁移', () => {
    localStorage.setItem('erp-table-cols:t1', JSON.stringify([150, 90]))
    const { container } = render(<Harness storageKey="t1" />)
    const cols = container.querySelectorAll('colgroup col')
    expect((cols[0] as HTMLTableColElement).style.width).toBe('150px')
    expect((cols[1] as HTMLTableColElement).style.width).toBe('90px')
  })

  it('损坏的 localStorage 内容被忽略', () => {
    localStorage.setItem('erp-table-cols:t1', '{oops')
    const { container } = render(<Harness storageKey="t1" minWidths={{ a: 100, b: 60 }} />)
    const cols = container.querySelectorAll('colgroup col')
    expect((cols[0] as HTMLTableColElement).style.width).toBe('100px')
  })

  it('data-col-min-width 变化时跟随服务端数值', async () => {
    const { container, rerender } = render(<Harness storageKey="t1" minWidths={{ a: 100, b: 60 }} />)
    const table = container.querySelector('table')!
    const th = table.querySelector('th[data-col-key="a"]')!
    th.setAttribute('data-col-min-width', '160')
    await new Promise((resolve) => setTimeout(resolve, 0))
    const col = table.querySelectorAll('colgroup col')[0] as HTMLTableColElement
    expect(col.style.width).toBe('160px')
    rerender(<Harness storageKey="t1" minWidths={{ a: 160, b: 60 }} />)
  })
})
