import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from './Toast'
import { useToast } from './toastContext'

function Trigger({ duration }: { duration?: number }) {
  const { notify } = useToast()
  return (
    <button type="button" onClick={() => notify({ message: '保存成功', variant: 'success', duration })}>
      成功
    </button>
  )
}

function WarningTrigger() {
  const { notify } = useToast()
  return (
    <button type="button" onClick={() => notify({ message: '标签已达上限', variant: 'warning' })}>
      警告
    </button>
  )
}

describe('ToastProvider', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('按语义渲染，到时先淡出再摘除', () => {
    render(<ToastProvider><Trigger /></ToastProvider>)
    expect(screen.queryByText('保存成功')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '成功' }))
    const toast = screen.getByRole('status')
    expect(toast).toHaveTextContent('保存成功')
    expect(toast).toHaveClass('alert-success')

    // 到点先进入淡出态（节点还在，动画看得见），动画结束才摘掉
    act(() => { vi.advanceTimersByTime(4000) })
    expect(screen.getByRole('status')).toHaveClass('erp-toast-leaving')
    act(() => { vi.advanceTimersByTime(220) })
    expect(screen.queryByText('保存成功')).not.toBeInTheDocument()
  })

  it('警告类用 alert 播报，手动关闭同样走淡出', () => {
    render(<ToastProvider><WarningTrigger /></ToastProvider>)
    fireEvent.click(screen.getByRole('button', { name: '警告' }))
    expect(screen.getByRole('alert')).toHaveTextContent('标签已达上限')

    fireEvent.click(screen.getByRole('button', { name: '关闭提示' }))
    expect(screen.getByRole('alert')).toHaveClass('erp-toast-leaving')
    act(() => { vi.advanceTimersByTime(220) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('同一条提示重复触发只保留一条，并重新计时', () => {
    render(<ToastProvider><WarningTrigger /></ToastProvider>)
    fireEvent.click(screen.getByRole('button', { name: '警告' }))
    act(() => { vi.advanceTimersByTime(3000) })
    fireEvent.click(screen.getByRole('button', { name: '警告' }))
    expect(screen.getAllByRole('alert')).toHaveLength(1)

    // 倒计时被重启：再过 3 秒仍在，满 4 秒才开始淡出
    act(() => { vi.advanceTimersByTime(3000) })
    expect(screen.getAllByRole('alert')).toHaveLength(1)
    act(() => { vi.advanceTimersByTime(1000) })
    expect(screen.getByRole('alert')).toHaveClass('erp-toast-leaving')
    act(() => { vi.advanceTimersByTime(220) })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('正在淡出的提示不参与去重，重新触发会开一条新的', () => {
    render(<ToastProvider><WarningTrigger /></ToastProvider>)
    fireEvent.click(screen.getByRole('button', { name: '警告' }))
    act(() => { vi.advanceTimersByTime(4000) })
    fireEvent.click(screen.getByRole('button', { name: '警告' }))

    // 一条在淡出、一条新进来
    expect(screen.getAllByRole('alert')).toHaveLength(2)
    expect(screen.getAllByRole('alert')[0]).toHaveClass('erp-toast-leaving')
    expect(screen.getAllByRole('alert')[1]).not.toHaveClass('erp-toast-leaving')
  })

  it('duration 为 0 时只能手动关闭', () => {
    render(<ToastProvider><Trigger duration={0} /></ToastProvider>)
    fireEvent.click(screen.getByRole('button', { name: '成功' }))
    act(() => { vi.advanceTimersByTime(60_000) })
    expect(screen.getByRole('status')).toHaveTextContent('保存成功')
  })

  it('不在 ToastProvider 内使用直接报错', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {})
    expect(() => render(<Trigger />)).toThrow('useToast must be used within ToastProvider')
    spy.mockRestore()
  })
})
