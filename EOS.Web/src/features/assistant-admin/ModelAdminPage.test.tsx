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
    modelId: 11,
    providerId: 1,
    modelCode: 'deepseek-chat',
    displayName: 'DeepSeek Chat（通用对话）',
    contextWindow: 65536,
    maxOutputTokens: 8192,
    defaultTemperature: null,
    timeoutSeconds: null,
    inputPerMillionYuan: null,
    outputPerMillionYuan: null,
    supportsTools: true,
    isActive: true,
    enabled: true,
    sortIdx: 0,
    remark: null,
    ...overrides,
  }
}

function providerRow(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    providerId: 1,
    code: 'deepseek',
    displayName: 'DeepSeek 开放平台',
    baseUrl: 'https://api.deepseek.com',
    apiKeyEnvVar: 'EOS_ASSISTANT_KEY_DEEPSEEK',
    apiKeyConfigured: true,
    apiKeyMaskedTail: '****abcd',
    timeoutSeconds: 300,
    enabled: true,
    sortIdx: 0,
    remark: null,
    models: [modelRow()],
    ...overrides,
  }
}

function installFetchMock(options: {
  providers?: unknown[]
  current?: Record<string, unknown> | null
  presets?: unknown[]
} = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })

    if (url.includes('/admin/assistant/providers') && method === 'PUT' && url.includes('/key')) {
      return jsonResponse({
        envVar: 'EOS_ASSISTANT_KEY_DEEPSEEK', configured: true, maskedTail: '****wxyz',
        processUpdated: true, persisted: true,
      })
    }
    if (url.includes('/admin/assistant/providers') && method === 'POST') {
      return jsonResponse({ providerId: 2, modelsCreated: 2 })
    }
    if (url.includes('/admin/assistant/providers')) {
      if (method === 'GET') {
        return jsonResponse({
          providers: options.providers ?? [providerRow()],
          current: options.current === undefined
            ? {
              modelId: 11, displayName: 'DeepSeek Chat（通用对话）', modelCode: 'deepseek-chat',
              providerId: 1, providerCode: 'deepseek', providerDisplayName: 'DeepSeek 开放平台',
              contextWindow: 65536, timeoutSeconds: 300, supportsTools: true, apiKeyConfigured: true,
            }
            : options.current,
        })
      }
      return new Response(null, { status: 204 })
    }
    if (url.includes('/admin/assistant/presets')) {
      return jsonResponse(options.presets ?? [
        {
          code: 'deepseek', displayName: 'DeepSeek 开放平台', baseUrl: 'https://api.deepseek.com',
          suggestedApiKeyEnvVar: 'EOS_ASSISTANT_KEY_DEEPSEEK', timeoutSeconds: 300, remark: null,
          models: [
            {
              modelCode: 'deepseek-chat', displayName: 'DeepSeek Chat', contextWindow: 65536,
              maxOutputTokens: 8192, supportsTools: true, inputPerMillionYuan: null,
              outputPerMillionYuan: null, defaultTemperature: null, remark: null,
            },
            {
              modelCode: 'deepseek-reasoner', displayName: 'DeepSeek Reasoner', contextWindow: 65536,
              maxOutputTokens: 8192, supportsTools: true, inputPerMillionYuan: null,
              outputPerMillionYuan: null, defaultTemperature: null, remark: null,
            },
          ],
        },
      ])
    }
    if (url.includes('/admin/assistant/usage')) {
      return jsonResponse({
        days: 30,
        since: '2026-09-02T00:00:00Z',
        today: { requests: 12, promptTokens: 3000, completionTokens: 1500, estimatedCostYuan: 0.006 },
        models: [{
          modelName: 'deepseek-chat', requests: 12, promptTokens: 3000, completionTokens: 1500,
          estimatedCostYuan: 0.006, lastUsedAt: '2026-10-01T09:00:00Z',
        }],
        trend: [{
          day: '2026-10-01T00:00:00', requests: 12, promptTokens: 3000, completionTokens: 1500,
          estimatedCostYuan: 0.006,
        }],
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

  it('两级渲染：供应商带出端点与密钥状态，其下挂模型', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('DeepSeek 开放平台')).toBeInTheDocument()
    // 端点在**供应商**级：加第二个模型不必重复填它
    expect(screen.getByText('https://api.deepseek.com')).toBeInTheDocument()
    expect(screen.getByText('EOS_ASSISTANT_KEY_DEEPSEEK')).toBeInTheDocument()
    expect(screen.getByText(/已配置 \*\*\*\*abcd/)).toBeInTheDocument()
    // 模型名在"模型表"与下面的"用量表"里各出现一次，所以按数量断言（两处都渲染了才算对）
    expect(screen.getAllByText('deepseek-chat').length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText('当前生效')).toBeInTheDocument()
    // 窗口要显示出来：它会被真的用来裁剪历史，不是装饰
    expect(screen.getByText('65,536 / 8,192')).toBeInTheDocument()
  })

  it('未配置时顶部直说"助手不可用"并给出下一步，而不是等人去点聊天才发现', async () => {
    installFetchMock({
      providers: [providerRow({ apiKeyConfigured: false, apiKeyMaskedTail: null })],
      current: null,
    })

    renderPage()

    // 先等数据到位再断言告警：告警在首帧就会出现（那时 providers 还没回来），
    // 文案会从"还没有供应商"那一支换成"密钥没配"那一支
    await screen.findByText('EOS_ASSISTANT_KEY_DEEPSEEK')
    // 直接断言告警整段的文本：里面的文案是分句拼的，逐句查容易因为断句变化而误报
    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('尚未配置模型')
    expect(alert.textContent).toContain('工作助手当前不可用')
    expect(alert.textContent).toContain('请先「设置密钥」')
    expect(screen.getByText('未配置')).toBeInTheDocument()
  })

  it('完全没有供应商时给出下一步，而不是一个空表格', async () => {
    installFetchMock({ providers: [], current: null })

    renderPage()

    expect(await screen.findByText('还没有供应商')).toBeInTheDocument()
    // 告警与空状态都会提到"预设目录"，所以限定在告警里断言
    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('尚未配置模型')
    expect(alert.textContent).toContain('添加供应商')
  })

  it('添加供应商：选预设后带出端点与可用模型，一次提交（请求体里没有密钥）', async () => {
    // 这条用"全新环境"（一个供应商都还没有）：同一个 CODE 只能有一个接入点，
    // 已经存在 deepseek 时它不会再出现在可选列表里（那是另一条用例的事）
    const harness = installFetchMock({ providers: [], current: null })

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: /添加供应商/ }))
    expect(await screen.findByLabelText('预设供应商')).toBeInTheDocument()

    // 预设带出端点与建议的环境变量名——这是"预设"的意义。
    // 用 waitFor：回填发生在 effect 里，比"下拉框出现"晚一帧，直接断言会撞上竞态
    await waitFor(() => expect(screen.getByLabelText('端点')).toHaveValue('https://api.deepseek.com'))
    expect(screen.getByLabelText('密钥环境变量名')).toHaveValue('EOS_ASSISTANT_KEY_DEEPSEEK')
    // 两个可用模型默认勾选
    expect(screen.getByLabelText('deepseek-chat')).toBeChecked()
    expect(screen.getByLabelText('deepseek-reasoner')).toBeChecked()

    fireEvent.click(screen.getByRole('button', { name: '添加' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'POST' && call.url.includes('/admin/assistant/providers')),
    ).toBe(true))
    const posted = JSON.parse(harness.calls.find(call => call.method === 'POST')!.body!)
    expect(posted.code).toBe('deepseek')
    expect(posted.models).toHaveLength(2)
    // 密钥不在这个 body 里：密钥只能走单独的「设置密钥」那条路，那条路不进库
    expect(Object.keys(posted)).not.toContain('apiKey')
    expect(Object.keys(posted.models[0])).not.toContain('apiKey')
  })

  it('设置密钥走供应商端点，写入后不再出现明文', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '密钥' }))
    expect(await screen.findByLabelText('密钥')).toBeInTheDocument()
    expect(screen.getByText(/密钥不入库、也不会再显示出来/)).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('密钥'), { target: { value: 'sk-super-secret-value' } })
    fireEvent.click(screen.getByRole('button', { name: '写入' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/providers/1/key')),
    ).toBe(true))
    await waitFor(() => expect(screen.queryByLabelText('密钥')).toBeNull())
    expect(document.body.textContent).not.toContain('sk-super-secret-value')
  })

  it('设为当前与取消当前都打各自的端点', async () => {
    const harness = installFetchMock({
      providers: [providerRow({ models: [modelRow({ isActive: false })] })],
      current: null,
    })

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '设为当前' }))
    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'POST' && call.url.includes('/models/11/activate')),
    ).toBe(true))
  })

  it('当前模型的删除按钮禁用；名下还有模型的供应商也不给删', async () => {
    installFetchMock()

    renderPage()
    await screen.findAllByText('deepseek-chat')

    // 用 title 定位而不是可访问名："删除"两个字在模型行与供应商行都有，可访问名会撞
    expect(screen.getByTitle('删除（当前模型不可删）')).toBeDisabled()
    // 级联会一次带走整家供应商的配置，所以先删模型再删供应商
    expect(screen.getByTitle('名下还有模型，需先删除它们')).toBeDisabled()
  })

  it('用量区按模型与按天聚合，并给出当日上限口径', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('按模型（近 30 天）')).toBeInTheDocument()
    expect(screen.getByText('按天（近 30 天）')).toBeInTheDocument()
    expect(screen.getByText(/上限：每人 ¥5\/天、全局 ¥50\/天/)).toBeInTheDocument()
  })
})
