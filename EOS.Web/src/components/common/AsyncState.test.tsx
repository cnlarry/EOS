import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { EmptyState, ErrorState, LoadingState } from './AsyncState'

describe('AsyncState', () => {
  it('LoadingState 渲染默认与自定义文案', () => {
    const { rerender } = render(<LoadingState />)
    expect(screen.getByRole('status')).toHaveTextContent('正在加载数据…')
    rerender(<LoadingState label="自定义" />)
    expect(screen.getByRole('status')).toHaveTextContent('自定义')
  })

  it('EmptyState 渲染默认与自定义文案', () => {
    const { rerender } = render(<EmptyState />)
    expect(screen.getByText('暂无数据')).toBeInTheDocument()
    rerender(<EmptyState title="空" description="描述" />)
    expect(screen.getByText('空')).toBeInTheDocument()
    expect(screen.getByText('描述')).toBeInTheDocument()
  })

  it('ErrorState 渲染消息并触发重试', () => {
    const onRetry = vi.fn()
    render(<ErrorState message="出错了" onRetry={onRetry} />)
    expect(screen.getByRole('alert')).toHaveTextContent('出错了')
    fireEvent.click(screen.getByRole('button', { name: '重新加载' }))
    expect(onRetry).toHaveBeenCalled()
  })

  it('ErrorState 无 onRetry 时不渲染按钮', () => {
    render(<ErrorState message="出错了" />)
    expect(screen.queryByRole('button', { name: '重新加载' })).not.toBeInTheDocument()
  })
})
