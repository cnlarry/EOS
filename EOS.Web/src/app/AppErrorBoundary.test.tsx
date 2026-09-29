import { describe, expect, it, vi, afterEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { AppErrorBoundary } from './AppErrorBoundary'

/**
 * 根级错误边界的行为契约（ADR-028）：
 * 渲染期抛错时给可重试的错误页 + 可复制的诊断信息，并**强制上报**——
 * 只显示不上报等于把崩溃静默掉；上报里的报障编号必须与页面上可复制的那个**是同一个值**，
 * 否则用户给的号在服务端日志里搜不到（这正是 ADR-025 要消灭的「两个号」）。
 */
function Boom(): never {
  throw new Error('渲染期错误边界的单测触发')
}

describe('AppErrorBoundary', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('渲染期抛错时显示错误页、诊断信息与三个操作按钮', () => {
    // 错误边界会往 console.error 打日志：这里静音，避免测试输出被 React 的堆栈刷屏
    vi.spyOn(console, 'error').mockImplementation(() => {})

    render(
      <AppErrorBoundary>
        <Boom />
      </AppErrorBoundary>,
    )

    expect(screen.getByText('页面发生异常，已记录')).toBeInTheDocument()
    // 诊断块用 <pre> 渲染，内部文本被拆成多个文本节点，用正则匹配整体
    expect(screen.getByText(/【EOS 诊断信息】/)).toBeInTheDocument()
    expect(screen.getByText(/渲染期错误边界的单测触发/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /重试/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /返回首页/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /复制诊断信息/ })).toBeInTheDocument()
    // 页面文本里必须能读到报障编号（用户要能念给维护者）
    expect(screen.getByText(/报障编号：/)).toBeInTheDocument()
  })

  it('上报载荷带上与页面同一个报障编号与产品版本', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const sendBeacon = vi.fn(() => true)
    vi.stubGlobal('navigator', { ...navigator, sendBeacon })

    render(
      <AppErrorBoundary>
        <Boom />
      </AppErrorBoundary>,
    )

    expect(sendBeacon).toHaveBeenCalledOnce()
    const [url, blob] = sendBeacon.mock.calls[0] as unknown as [string, Blob]
    expect(url).toBe('/api/v1/client-errors')
    const payload = JSON.parse(await blob.text()) as Record<string, unknown>

    expect(payload.kind).toBe('render')
    expect(String(payload.message)).toContain('渲染期错误边界的单测触发')
    // 版本号：构建注入，测试环境为 unknown，但必须是字符串且非空
    expect(typeof payload.appVersion).toBe('string')

    // 页面文本里的编号必须与上报的 correlationId 一致
    const shown = document.body.textContent ?? ''
    const match = shown.match(/报障编号：([0-9a-zA-Z-]+)/)
    expect(match).not.toBeNull()
    expect(payload.correlationId).toBe(match![1])
  })
})
