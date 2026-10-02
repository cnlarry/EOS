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
    kind: 'CHAT',
    dimension: null,
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

/** 一条嵌入模型：用途、维度与"不摆窗口/输出"都是它区别于对话模型的地方。 */
function embeddingRow(overrides: Partial<Record<string, unknown>> = {}) {
  return modelRow({
    modelId: 21,
    modelCode: 'text-embedding-v4',
    displayName: '通义 text-embedding-v4',
    contextWindow: null,
    maxOutputTokens: null,
    supportsTools: false,
    isActive: false,
    kind: 'EMBEDDING',
    dimension: 1024,
    ...overrides,
  })
}

function installFetchMock(options: {
  providers?: unknown[]
  current?: Record<string, unknown> | null
  currentEmbedding?: Record<string, unknown> | null
  presets?: unknown[]
  discovered?: unknown[]
  presetEmbedding?: string
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
          // 嵌入那条**默认没有**：与真实情况一致（要用户自己配出来）
          currentEmbedding: options.currentEmbedding ?? null,
        })
      }
      return new Response(null, { status: 204 })
    }
    if (url.includes('/admin/assistant/models/discover')) {
      return jsonResponse({
        providerId: 1,
        models: options.discovered ?? [
          { modelCode: 'deepseek-chat', displayName: 'DeepSeek Chat', contextWindow: 65536, maxOutputTokens: 8192 },
          { modelCode: 'qwen-embedding', displayName: '通义嵌入', contextWindow: null, maxOutputTokens: null },
        ],
      })
    }
    if (url.includes('/admin/assistant/models/active/clear')) {
      return new Response(null, { status: 204 })
    }
    if (url.includes('/admin/assistant/models') && method === 'POST') {
      return jsonResponse({ modelId: 99 })
    }
    if (url.includes('/admin/assistant/presets')) {
      return jsonResponse(options.presets ?? [
        {
          code: 'deepseek', displayName: 'DeepSeek 开放平台', baseUrl: 'https://api.deepseek.com',
          suggestedApiKeyEnvVar: 'EOS_ASSISTANT_KEY_DEEPSEEK', timeoutSeconds: 300, remark: null,
          authStyle: 'Bearer', embedding: options.presetEmbedding ?? 'Unsupported', modelListing: 'Supported',
          models: [
            {
              kind: 'CHAT', dimension: null,
              modelCode: 'deepseek-chat', displayName: 'DeepSeek Chat', contextWindow: 65536,
              maxOutputTokens: 8192, supportsTools: true, inputPerMillionYuan: null,
              outputPerMillionYuan: null, defaultTemperature: null, remark: null,
            },
            {
              kind: 'CHAT', dimension: null,
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

  it('预设里有的型号：窗口与最大输出来自厂商公开值，不给改', async () => {
    installFetchMock()
    renderPage()

    // 供应商行与模型行各有一个「编辑」，模型在供应商之下，所以取最后一个。
    // 超时给足：满量跑（上千条用例并行）时这一页的数据要一两秒才到位，默认 1s 会偶发失败；
    // 这条用例要断言的是"锁定"这件事，不是加载有多快
    const edits = await screen.findAllByRole('button', { name: '编辑' }, { timeout: 10_000 })
    fireEvent.click(edits[edits.length - 1])

    // 预设清单是**异步**拉的：字段先渲染出来、锁定状态随后才到，所以要 waitFor
    // （直接断言会读到"还没锁"的那一帧——这正是本仓库记过的"跨渲染帧竞态"）
    expect(await screen.findByLabelText('上下文窗口')).toBeInTheDocument()
    // mock 的预设里正好有这个型号（deepseek-chat）→ 两个字段锁上：
    // 这两个数改错了不报错，只会让助手被厂商拒（算大了）或白丢历史（算小了）
    await waitFor(() => expect(screen.getByLabelText('上下文窗口')).toHaveAttribute('readonly'))
    expect(screen.getByLabelText('最大输出')).toHaveAttribute('readonly')
    expect(screen.getByLabelText('支持工具调用')).toBeDisabled()
  })

  it('预设里没有的型号：这些字段仍可改（自建端点与新型号要自己填）', async () => {
    installFetchMock({
      providers: [providerRow({ models: [modelRow({ modelCode: 'my-own-model' })] })],
    })
    renderPage()

    const edits = await screen.findAllByRole('button', { name: '编辑' }, { timeout: 10_000 })
    fireEvent.click(edits[edits.length - 1])

    expect(await screen.findByLabelText('上下文窗口', {}, { timeout: 10_000 })).not.toHaveAttribute('readonly')
    expect(screen.getByLabelText('支持工具调用')).not.toBeDisabled()
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
    expect(screen.getByText('当前对话')).toBeInTheDocument()
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
    // 直接断言告警整段的文本：里面的文案是分句拼的，逐句查容易因为断句变化而误报。
    // 两个用途各有一条告警，所以按"哪一条在说对话模型"挑出来
    const alerts = await screen.findAllByRole('alert')
    const chatAlert = alerts.find(item => item.textContent?.includes('对话模型'))!
    expect(chatAlert.textContent).toContain('尚未配置对话模型')
    expect(chatAlert.textContent).toContain('工作助手当前不可用')
    expect(chatAlert.textContent).toContain('请先「设置密钥」')
    expect(screen.getByText('未配置')).toBeInTheDocument()
  })

  it('嵌入模型没配时也直说，且说清坏的是知识库而不是助手', async () => {
    installFetchMock()

    renderPage()

    await screen.findByText('EOS_ASSISTANT_KEY_DEEPSEEK')
    // 这条是本轮新增分区的关键：嵌入缺失在界面上必须与"助手不可用"分开说——
    // 它坏掉时用户看到的是"问制度没答案"，不会想到是模型没配
    const alerts = await screen.findAllByRole('alert')
    const embeddingAlert = alerts.find(item => item.textContent?.includes('嵌入模型'))!
    expect(embeddingAlert.textContent).toContain('尚未配置嵌入模型')
    expect(embeddingAlert.textContent).toContain('知识库当前不可用')
    expect(embeddingAlert.textContent).toContain('KB_EMBEDDING_NOT_CONFIGURED')
  })

  it('完全没有供应商时给出下一步，而不是一个空表格', async () => {
    installFetchMock({ providers: [], current: null })

    renderPage()

    expect(await screen.findByText('还没有供应商')).toBeInTheDocument()
    // 告警与空状态都会提到"预设目录"，所以限定在告警里断言
    const alerts = await screen.findAllByRole('alert')
    const chatAlert = alerts.find(item => item.textContent?.includes('对话模型'))!
    expect(chatAlert.textContent).toContain('尚未配置对话模型')
    expect(chatAlert.textContent).toContain('添加供应商')
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

  it('按用途分区：嵌入与对话各成一段，嵌入段显示维度', async () => {
    installFetchMock({ providers: [providerRow({ models: [modelRow(), embeddingRow()] })] })

    renderPage()

    // 两段标题都在：混在一张表里必然有一半的行在某些列上是空的，
    // 读的人分不清"这项没有"与"这项没填"
    expect(await screen.findByText('对话模型')).toBeInTheDocument()
    expect(screen.getByText('嵌入模型')).toBeInTheDocument()
    // 维度必须显示：它决定向量能不能存进集合（与集合登记不一致时入库会被拒）
    expect(screen.getByText('1024 维')).toBeInTheDocument()
    // 嵌入模型不摆窗口/输出那一列，所以对话模型的数字只出现一次
    expect(screen.getAllByText('65,536 / 8,192')).toHaveLength(1)
  })

  it('缺维度的嵌入模型要标出来，而不是显示成"维度空着"', async () => {
    installFetchMock({
      providers: [providerRow({ models: [modelRow(), embeddingRow({ dimension: null })] })],
    })

    renderPage()

    await screen.findByText('嵌入模型')
    // 缺维度是**入库会被拒**的状态，得在列表里就能看见
    expect(screen.getByText('缺维度')).toBeInTheDocument()
  })

  it('拉取型号：厂商给的清单里排除已在库的，勾选后按所选用途与维度落库', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '拉取型号' }))

    // 厂商给了两条，库里已有 deepseek-chat → 只列另一条
    // （勾了也会被唯一约束拒，而拒在逐条提交的中途会留下半截结果）
    expect(await screen.findByLabelText('qwen-embedding')).toBeInTheDocument()
    expect(screen.queryByLabelText('deepseek-chat')).toBeNull()

    fireEvent.click(screen.getByLabelText('qwen-embedding'))
    fireEvent.change(screen.getByLabelText('qwen-embedding 的用途'), { target: { value: 'EMBEDDING' } })
    fireEvent.change(screen.getByLabelText('qwen-embedding 的维度'), { target: { value: '1024' } })
    fireEvent.click(screen.getByRole('button', { name: /落库/ }))

    await waitFor(() => expect(harness.calls.some(
      call => call.method === 'POST' && call.url.includes('/admin/assistant/models'),
    )).toBe(true))
    const posted = JSON.parse(
      harness.calls.find(call => call.method === 'POST' && call.url.includes('/admin/assistant/models'))!.body!)
    expect(posted.kind).toBe('EMBEDDING')
    expect(posted.dimension).toBe(1024)
    // 嵌入模型不带窗口/输出与工具调用：它用不上这些值，带过去就是没人会读的垃圾数据
    expect(posted.contextWindow).toBeNull()
    expect(posted.maxOutputTokens).toBeNull()
    expect(posted.supportsTools).toBe(false)
  })

  it('手工新增模型：用途选嵌入后自动带出维度，并以新增（而不是修改）提交', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '新增模型' }))

    // 对话模型的字段先摆出来（默认用途是对话）
    expect(await screen.findByLabelText('用途')).toBeInTheDocument()
    // 手工新增时**不带** isActive/enabled 之类的默认判断，先都填上
    fireEvent.change(screen.getByLabelText('模型标识'), { target: { value: 'local-bge-m3' } })
    fireEvent.change(screen.getByLabelText('显示名'), { target: { value: '本地嵌入服务' } })
    // 换用途后：维度出现且给了 1024 这个初值（现成集合就是它），窗口那几项消失
    fireEvent.change(screen.getByLabelText('用途'), { target: { value: 'EMBEDDING' } })
    expect(screen.getByLabelText('维度')).toHaveValue('1024')

    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(harness.calls.some(
      call => call.method === 'POST' && call.url.includes('/admin/assistant/models'),
    )).toBe(true))
    const posted = JSON.parse(
      harness.calls.find(call => call.method === 'POST' && call.url.includes('/admin/assistant/models'))!.body!)
    expect(posted.kind).toBe('EMBEDDING')
    expect(posted.dimension).toBe(1024)
    expect(posted.providerId).toBe(1)
    expect(posted.modelCode).toBe('local-bge-m3')
  })

  it('嵌入当前独立显示，且取消时带上用途（不会连带清掉对话那条）', async () => {
    const harness = installFetchMock({
      providers: [providerRow({ models: [modelRow(), embeddingRow({ isActive: true })] })],
      currentEmbedding: {
        modelId: 21, displayName: '通义 text-embedding-v4', modelCode: 'text-embedding-v4',
        providerId: 1, providerCode: 'deepseek', providerDisplayName: 'DeepSeek 开放平台',
        dimension: 1024, apiKeyConfigured: true,
      },
    })

    renderPage()
    expect(await screen.findByText(/嵌入：.*text-embedding-v4/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '取消嵌入当前' }))

    await waitFor(() => expect(harness.calls.some(call =>
      call.method === 'POST'
      && call.url.includes('/models/active/clear')
      && call.url.includes('kind=EMBEDDING'))).toBe(true))
  })

  it('无凭据端点：变量名为空是"无需凭据"，不是"未配置"', async () => {
    installFetchMock({
      providers: [providerRow({ apiKeyEnvVar: null, apiKeyConfigured: false, apiKeyMaskedTail: null })],
    })

    renderPage()

    // 把合法的"无凭据端点"显示成红色"未配置"，会让管理员去找一把根本不存在的密钥
    expect(await screen.findByText('无需凭据（未设密钥变量名）')).toBeInTheDocument()
    expect(screen.queryByText('未配置')).toBeNull()
  })
})
