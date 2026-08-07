import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { Button } from './Button'

describe('Button', () => {
  it('默认渲染为次要按钮并带 children', () => {
    render(<Button>确定</Button>)
    const button = screen.getByRole('button', { name: '确定' })
    expect(button).toHaveClass('btn', 'btn-outline-secondary')
    expect(button).toBeEnabled()
  })

  it('应用 primary 变体与 sm 尺寸', () => {
    render(<Button variant="primary" size="sm">保存</Button>)
    expect(screen.getByRole('button', { name: '保存' })).toHaveClass('btn-primary', 'btn-sm')
  })

  it('loading 时禁用并显示 spinner，不显示 icon', () => {
    render(
      <Button loading icon={<span data-testid="icon">i</span>}>提交</Button>,
    )
    const button = screen.getByRole('button', { name: '提交' })
    expect(button).toBeDisabled()
    expect(button.querySelector('.spinner-border')).toBeInTheDocument()
    expect(screen.queryByTestId('icon')).not.toBeInTheDocument()
  })

  it('合并传入的 className', () => {
    render(<Button className="w-100">导出</Button>)
    expect(screen.getByRole('button', { name: '导出' })).toHaveClass('w-100')
  })
})
