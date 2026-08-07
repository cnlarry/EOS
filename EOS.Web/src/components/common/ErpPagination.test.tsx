import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ErpPagination } from './ErpPagination'

describe('ErpPagination', () => {
  it('显示总数与当前页码信息', () => {
    render(<ErpPagination total={50} page={2} pageSize={10} onPageChange={vi.fn()} />)
    expect(screen.getByText('共 50 条，第 2/5 页')).toBeInTheDocument()
  })

  it('翻页按钮触发对应页码回调', () => {
    const onPageChange = vi.fn()
    render(<ErpPagination total={50} page={2} pageSize={10} onPageChange={onPageChange} />)
    fireEvent.click(screen.getByRole('button', { name: '首页' }))
    expect(onPageChange).toHaveBeenCalledWith(1)
    fireEvent.click(screen.getByRole('button', { name: '上一页' }))
    expect(onPageChange).toHaveBeenCalledWith(1)
    fireEvent.click(screen.getByRole('button', { name: '下一页' }))
    expect(onPageChange).toHaveBeenCalledWith(3)
    fireEvent.click(screen.getByRole('button', { name: '尾页' }))
    expect(onPageChange).toHaveBeenCalledWith(5)
  })

  it('首页边界禁用上一页，尾页边界禁用下一页', () => {
    const { rerender } = render(<ErpPagination total={50} page={1} pageSize={10} onPageChange={vi.fn()} />)
    expect(screen.getByRole('button', { name: '首页' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '上一页' })).toBeDisabled()
    rerender(<ErpPagination total={50} page={5} pageSize={10} onPageChange={vi.fn()} />)
    expect(screen.getByRole('button', { name: '下一页' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '尾页' })).toBeDisabled()
  })

  it('页码输入跳转并钳制到有效范围', () => {
    const onPageChange = vi.fn()
    render(<ErpPagination total={50} page={2} pageSize={10} onPageChange={onPageChange} />)
    const jumper = screen.getByLabelText('跳转到页码')
    fireEvent.change(jumper, { target: { value: '4' } })
    fireEvent.keyDown(jumper, { key: 'Enter' })
    expect(onPageChange).toHaveBeenCalledWith(4)
    fireEvent.change(jumper, { target: { value: '99' } })
    fireEvent.keyDown(jumper, { key: 'Enter' })
    expect(onPageChange).toHaveBeenCalledWith(5)
  })

  it('每页条数变更回调', () => {
    const onPageSizeChange = vi.fn()
    render(
      <ErpPagination total={50} page={1} pageSize={10} onPageChange={vi.fn()} pageSizes={[10, 20]} onPageSizeChange={onPageSizeChange} />,
    )
    fireEvent.change(screen.getByLabelText('每页数量'), { target: { value: '20' } })
    expect(onPageSizeChange).toHaveBeenCalledWith(20)
  })
})
