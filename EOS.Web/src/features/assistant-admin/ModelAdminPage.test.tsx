import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { ModelAdminPage } from './ModelAdminPage'

interface FetchCall {
  url: string
  method: string
  body: string | null
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

function modelRow(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    modelId: 1,
    displayName: 'DeepSeek 生产',
    provider: 'deepseek',
    modelName: 'deepseek-chat',
    baseUrl: 'https://api.deepseek.com',
    apiKeyEnvVar: 'EOS_ASSISTANT_API_KEY',
    apiKeyConfigured: true,
    apiKeyMaskedTail: '****abcd',
    timeoutSeconds: 300,
    temperature: 0.3,
    maxTokens: 4096,
    isActive: true,
    enabled: true,
    sortIdx: 0,
    remark: null,
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
    ...overrides,
  }
}

function installFetchMock(options: { models?: unknown[]; current?: Record<string, unknown> } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })

    if (url.includes('/admin/assistant/models') && method === 'PUT' && url.includes('/key')) {
      return jsonResponse({ envVar: 'EOS_ASSISTANT_API_KEY', configured: true, maskedTail: '****wxyz', processUpdated: true, persisted: true })
    }
    if (url.includes('/admin/assistant/models') && method === 'POST') {
      return jsonResponse({ modelId: 2 })
    }
    if (url.includes('/admin/assistant/models')) {
      if (method === 'GET') {
        return jsonResponse({
          items: options.models ?? [modelRow()],
          current: options.current ?? {
            source: 'database', modelId: 1, displayName: 'DeepSeek 生产', model: 'deepseek-chat',
            baseUrl: 'https://api.deepseek.com', timeoutSeconds: 300, temperature: 0.3,
            maxTokens: 4096, apiKeyConfigured: true,
          },
        })
      }
      return new Response(null, { status: 204 })
    }
    if (url.includes('/admin/assistant/usage')) {
      return jsonResponse({
        days: 30,
        since: '2026-09-02T00:00:00Z',
        today: { requests: 12, promptTokens: 3000, completionTokens: 1500, estimatedCostYuan: 0.006 },
        models: [{ modelName: 'deepseek-chat', requests: 12, promptTokens: 3000, completionTokens: 1500, estimatedCostYuan: 0.006, lastUsedAt: '2026-10-01T09:00:00Z' }],
        trend: [{ day: '2026-10-01T00:00:00', requests: 12, promptTokens: 3000, completionTokens: 1500, estimatedCostYuan: 0.006 }],
        caps: { userDailyCapYuan: 5, globalDailyCapYuan: 50 },
      })
    }
    return jsonResponse({})
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, calls }
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/admin/assistant/models']}>
        <ToastProvider>
          <ModelAdminPage />
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('ModelAdminPage', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列表只显示密钥的环境变量名与掩码，页面上不出现任何"像密钥的东西"', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('DeepSeek 生产')).toBeInTheDocument()
    // 显示的是变量名 + 掩码：库里本来就没有密钥可显示
    expect(screen.getByText('EOS_ASSISTANT_API_KEY')).toBeInTheDocument()
    expect(screen.getByText('已配置 ****abcd')).toBeInTheDocument()
    // 当前模型的来源与身份要说清楚，否则改完看不出有没有生效
    expect(screen.getByText(/来源：本页配置/)).toBeInTheDocument()
    // 没有记录时助手用配置文件那套——这句提示必须在，否则空表看起来像坏了
    expect(screen.getByText(/这里没有记录时，助手用配置文件/)).toBeInTheDocument()
  })

  it('密钥未配置的模型不能设为当前（否则助手会立刻不可用）', async () => {
    installFetchMock({
      models: [modelRow({ isActive: false, apiKeyConfigured: false, apiKeyMaskedTail: null })],
      current: {
        source: 'appsettings', model: 'deepseek-chat', baseUrl: 'https://api.deepseek.com',
        timeoutSeconds: 300, temperature: null, maxTokens: null, apiKeyConfigured: true,
      },
    })

    renderPage()
    await screen.findByText('DeepSeek 生产')

    expect(screen.getByText('未配置')).toBeInTheDocument()
    // 服务端也会拒（409 MODEL_KEY_NOT_CONFIGURED），界面先把这一步挡住
    expect(screen.getByRole('button', { name: '设为当前' })).toBeDisabled()
  })

  it('新增模型的请求体里没有密钥字段——密钥只能走单独那条路', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: /新增模型/ }))
    expect(await screen.findByLabelText('显示名')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('显示名'), { target: { value: '备用模型' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'POST' && call.url.includes('/admin/assistant/models')),
    ).toBe(true))
    const posted = JSON.parse(harness.calls.find(call => call.method === 'POST')!.body!)
    expect(posted.displayName).toBe('备用模型')
    // 把密钥混进"新增模型"的 body，早晚会有人顺手把它存下来——这里钉死它没有
    expect(Object.keys(posted)).not.toContain('apiKey')
    expect(Object.keys(posted)).not.toContain('apiKeyValue')
  })

  it('设置密钥走单独端点，写入后弹窗关闭、页面上不再出现密钥明文', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '密钥' }))
    expect(await screen.findByLabelText('密钥')).toBeInTheDocument()
    // 只写不读：弹窗里要说清它不会再显示出来
    expect(screen.getByText(/密钥不入库、也不会再显示出来/)).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('密钥'), { target: { value: 'sk-super-secret-value' } })
    fireEvent.click(screen.getByRole('button', { name: '写入' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/models/1/key')),
    ).toBe(true))
    const put = harness.calls.find(call => call.method === 'PUT' && call.url.includes('/key'))!
    expect(put.body).toContain('sk-super-secret-value')

    // 提交之后 DOM 里不该再留着它
    await waitFor(() => expect(screen.queryByLabelText('密钥')).toBeNull())
    expect(document.body.textContent).not.toContain('sk-super-secret-value')
  })

  it('取消当前模型：只在当前来自本页配置时提供，点了会调取消失端点', async () => {
    const harness = installFetchMock()

    renderPage()
    const cancel = await screen.findByRole('button', { name: '取消当前' })
    fireEvent.click(cancel)

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'POST' && call.url.includes('/models/active/clear')),
    ).toBe(true))
  })

  it('用量区按模型聚合，并给出当日的上限口径', async () => {
    installFetchMock()

    renderPage()

    // 按模型的聚合此前根本不存在（MODEL_NAME 只写不聚合）
    expect(await screen.findByText('按模型（近 30 天）')).toBeInTheDocument()
    // 模型名在模型表与用量表各出现一次：用"至少两处"来证明用量表也渲染了它
    expect(screen.getAllByText('deepseek-chat').length).toBeGreaterThanOrEqual(2)
    // 千分位在"按模型"与"按天"两张表里各出现一次：两张都渲染成功
    expect(screen.getAllByText('3,000').length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText('按天（近 30 天）')).toBeInTheDocument()
    expect(screen.getByText(/上限：每人 ¥5\/天、全局 ¥50\/天/)).toBeInTheDocument()
  })
})
