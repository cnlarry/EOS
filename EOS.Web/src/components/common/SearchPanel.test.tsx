import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { SearchPanel } from './SearchPanel'

describe('SearchPanel', () => {
  it('输入关键词并提交', () => {
    const onKeywordChange = vi.fn()
    const onSubmit = vi.fn()
    render(<SearchPanel keyword="" onKeywordChange={onKeywordChange} onSubmit={onSubmit}><div>额外条件</div></SearchPanel>)
    fireEvent.change(screen.getByRole('textbox', { name: '搜索订单号或供应商' }), { target: { value: 'ABC' } })
    expect(onKeywordChange).toHaveBeenCalledWith('ABC')
    fireEvent.click(screen.getByRole('button', { name: '查询' }))
    expect(onSubmit).toHaveBeenCalled()
    expect(screen.getByText('额外条件')).toBeInTheDocument()
  })
})
