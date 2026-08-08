import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { emptyQueryCondition } from './queryCondition'
import { ErpColumnFilter } from './ErpColumnFilter'

describe('ErpColumnFilter', () => {
  it('运算符变更触发 onChange', () => {
    const onChange = vi.fn()
    render(<ErpColumnFilter condition={emptyQueryCondition()} onChange={onChange} onApply={vi.fn()} onClear={vi.fn()} />)
    fireEvent.change(screen.getByLabelText('筛选运算符'), { target: { value: 'contains' } })
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ operator: 'contains' }))
  })

  it('between 运算符显示值上限，empty 禁用值输入', () => {
    const onChange = vi.fn()
    const { rerender } = render(
      <ErpColumnFilter condition={emptyQueryCondition()} onChange={onChange} onApply={vi.fn()} onClear={vi.fn()} />,
    )
    expect(screen.queryByLabelText('筛选值上限')).not.toBeInTheDocument()
    rerender(
      <ErpColumnFilter
        condition={{ ...emptyQueryCondition(), operator: 'between' }}
        onChange={onChange}
        onApply={vi.fn()}
        onClear={vi.fn()}
      />,
    )
    expect(screen.getByLabelText('筛选值上限')).toBeInTheDocument()
    rerender(
      <ErpColumnFilter
        condition={{ ...emptyQueryCondition(), operator: 'empty' }}
        onChange={onChange}
        onApply={vi.fn()}
        onClear={vi.fn()}
      />,
    )
    expect(screen.getByLabelText('筛选值')).toBeDisabled()
  })

  it('应用与清除触发对应回调', () => {
    const onApply = vi.fn()
    const onClear = vi.fn()
    render(<ErpColumnFilter condition={emptyQueryCondition()} onChange={vi.fn()} onApply={onApply} onClear={onClear} />)
    fireEvent.click(screen.getByRole('button', { name: '应用' }))
    fireEvent.click(screen.getByRole('button', { name: '清除' }))
    expect(onApply).toHaveBeenCalledTimes(1)
    expect(onClear).toHaveBeenCalledTimes(1)
  })
})
