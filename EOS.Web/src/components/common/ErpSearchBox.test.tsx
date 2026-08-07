import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ErpSearchBox } from './ErpSearchBox'

afterEach(() => {
  vi.useRealTimers()
})

describe('ErpSearchBox', () => {
  it('输入后按防抖时间提交 trim 后的值', () => {
    vi.useFakeTimers()
    const onChange = vi.fn()
    render(<ErpSearchBox value="" onChange={onChange} debounceMs={400} />)
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: ' 备料 ' } })
    act(() => {
      vi.advanceTimersByTime(399)
    })
    expect(onChange).not.toHaveBeenCalled()
    act(() => {
      vi.advanceTimersByTime(1)
    })
    expect(onChange).toHaveBeenCalledWith('备料')
  })

  it('回车立即提交', () => {
    const onChange = vi.fn()
    render(<ErpSearchBox value="" onChange={onChange} debounceMs={400} />)
    const input = screen.getByRole('searchbox')
    fireEvent.change(input, { target: { value: 'abc' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(onChange).toHaveBeenCalledWith('abc')
  })

  it('清除按钮提交空字符串', () => {
    const onChange = vi.fn()
    render(<ErpSearchBox value="abc" onChange={onChange} />)
    fireEvent.click(screen.getByRole('button', { name: '清除搜索' }))
    expect(onChange).toHaveBeenCalledWith('')
  })

  it('Ctrl+K 聚焦搜索框', () => {
    render(<ErpSearchBox value="" onChange={vi.fn()} />)
    const input = screen.getByRole('searchbox')
    fireEvent.keyDown(window, { key: 'k', ctrlKey: true })
    expect(input).toHaveFocus()
  })
})
