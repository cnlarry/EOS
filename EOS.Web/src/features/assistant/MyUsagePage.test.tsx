import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MyUsagePage } from './MyUsagePage'

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** 服务端返回的用量快照（形状与 3102 的用量看板完全一致，只多了"只算本人"这一层过滤）。 */
function usageBody(overrides: { models?: unknown[]; trend?: unknown[] } = {}) {
  return {
    days: 30,
    since: '2026-09-01T00:00:00Z',
    today: { requests: 3, promptTokens: 1000, completionTokens: 2000, estimatedCostYuan: 0.005 },
    models: overrides.models ?? [
      {
        modelName: 'deepseek-flash',
        requests: 3,
        promptTokens: 1000,
        completionTokens: 2000,
        estimatedCostYuan: 0.005,
        lastUsedAt: '2026-09-30T10:00:00Z',
      },
    ],
    trend: overrides.trend ?? [
      { day: '2026-09-29T00:00:00', requests: 1, promptTokens: 300, completionTokens: 700, estimatedCostYuan: 0.0017 },
      { day: '2026-09-30T00:00:00', requests: 2, promptTokens: 700, completionTokens: 1300, estimatedCostYuan: 0.0033 },
    ],
    caps: { userDailyCapYuan: 5, globalDailyCapYuan: 50 },
  }
}

function installFetchMock(body: unknown = usageBody()) {
  const calls: string[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === 'string' ? input : input.toString()
    calls.push(url)
    if (url.includes('/assistant/usage')) return jsonResponse(body)
    return jsonResponse({})
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, calls }
}

function renderPage() {
  // 失败立刻暴露，不走进避重试（重试会让"接口没被调用"之类的断言等到超时）
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MyUsagePage />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('MyUsagePage', () => {
  it('渲染当日总览与按模型 / 按天两张表', async () => {
    installFetchMock()
    renderPage()

    expect(await screen.findByText('我的用量')).toBeInTheDocument()
    // 按模型：模型名 + 四位小数的成本
    expect(await screen.findByText('deepseek-flash')).toBeInTheDocument()
    expect(await screen.findByText('¥0.0050')).toBeInTheDocument()
    // 按天：日期只显示到天（不按本地时区把跨零点的值挪到前一天）
    expect(await screen.findByText('2026-09-30')).toBeInTheDocument()
    expect(await screen.findByText('2026-09-29')).toBeInTheDocument()
  })

  it('切换统计区间会带新的 days 重新取数', async () => {
    const { calls } = installFetchMock()
    renderPage()

    await screen.findByText('deepseek-flash')
    fireEvent.change(screen.getByLabelText('统计区间'), { target: { value: '7' } })

    await waitFor(() => {
      expect(calls.some(url => url.includes('/assistant/usage') && url.includes('days=7'))).toBe(true)
    })
  })

  it('没有调用记录时给空态而不是空表', async () => {
    installFetchMock(usageBody({ models: [], trend: [] }))
    renderPage()

    expect(await screen.findByText('这段时间还没有提问记录')).toBeInTheDocument()
    expect(await screen.findByText('这段时间还没有调用记录')).toBeInTheDocument()
  })
})
