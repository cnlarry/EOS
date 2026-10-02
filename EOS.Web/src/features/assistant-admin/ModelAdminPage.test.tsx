import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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

/** 第二家供应商：用来验"点上面那行、下面那张表跟着换"。 */
function secondProviderRow(overrides: Partial<Record<string, unknown>> = {}) {
  return providerRow({
    providerId: 2,
    code: 'dashscope',
    displayName: '阿里云百炼（通义千问）',
    baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    apiKeyEnvVar: 'EOS_ASSISTANT_KEY_DASHSCOPE',
    apiKeyMaskedTail: '****wxyz',
    models: [modelRow({
      modelId: 31, providerId: 2, modelCode: 'qwen-max', displayName: '通义千问 Max', isActive: false,
    })],
    ...overrides,
  })
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

/** 按单元格文本找到它所在的行——表里的操作按钮都按行定位，否则"编辑/删除"会撞在一起。 */
const rowOf = (text: string) => screen.getByText(text).closest('tr')!

describe('ModelAdminPage', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('整页用页签分成"模型 / 用量"，模型页签是"上供应商表 / 下模型表"的主子表', async () => {
    installFetchMock()
    const { container } = renderPage()

    // 页签：模型是默认页签
    expect(await screen.findByRole('tab', { name: '模型' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: '用量' })).toHaveAttribute('aria-selected', 'false')
    // 页面级页签的容器要能把高度接着传下去（否则工作台的 flex 链断在这里，内层表格不出滚动条）
    expect(container.querySelector('.erp-page-tabs')).not.toBeNull()
    // 主子表：上（供应商表）与下（模型表）各是一个独立的滚动区
    expect(container.querySelector('.erp-master-table-region')).not.toBeNull()
    expect(container.querySelector('.erp-detail-card')).not.toBeNull()
    // 页签**紧挨着**内容面板：工作台是面板的直接子元素，中间不夹提示条之类的东西
    // （夹一层横幅会把页签与内容在视觉上割开）
    expect(container.querySelector('.erp-tabbed-panel-body > .erp-workbench-page')).not.toBeNull()

    fireEvent.click(screen.getByRole('tab', { name: '用量' }))

    expect(await screen.findByText('按模型（近 30 天）')).toBeInTheDocument()
    // 页签是**条件渲染**而不是藏起来：切走之后供应商表不在文档里（藏起来会留一堆隐藏的可访问名）
    expect(screen.queryByText('EOS_ASSISTANT_KEY_DEEPSEEK')).toBeNull()
  })

  it('供应商表（主表）：端点 / 密钥状态 / 默认超时都摆出来', async () => {
    installFetchMock()
    renderPage()

    const row = (await screen.findByText('DeepSeek 开放平台')).closest('tr')!

    // 端点在**供应商**级：加第二个模型不必重复填它
    expect(within(row).getByText('https://api.deepseek.com')).toBeInTheDocument()
    expect(within(row).getByText('EOS_ASSISTANT_KEY_DEEPSEEK')).toBeInTheDocument()
    expect(within(row).getByText(/已配置 \*\*\*\*abcd/)).toBeInTheDocument()
    expect(within(row).getByText('300 秒')).toBeInTheDocument()
    // "当前对话"是供应商级的徽标（真正"当前"的是它名下那条模型）
    expect(within(row).getByText('当前对话')).toBeInTheDocument()
  })

  it('点供应商行切换下面的模型表（主子联动）', async () => {
    installFetchMock({ providers: [providerRow(), secondProviderRow()] })
    renderPage()

    // 默认看第一家
    expect(await screen.findByText('deepseek-chat')).toBeInTheDocument()
    expect(screen.queryByText('qwen-max')).toBeNull()

    fireEvent.click(rowOf('阿里云百炼（通义千问）'))

    expect(await screen.findByText('qwen-max')).toBeInTheDocument()
    expect(screen.queryByText('deepseek-chat')).toBeNull()
    // 详情区标题跟着选中行走：看不到"现在这张表是谁的"是最容易搞错的地方
    expect(screen.getByText('阿里云百炼（通义千问） 的模型')).toBeInTheDocument()
  })

  it('用途切换器：对话看窗口/输出，嵌入看维度（两套列不混用）', async () => {
    installFetchMock({ providers: [providerRow({ models: [modelRow(), embeddingRow()] })] })
    renderPage()

    // 默认对话：窗口/输出要显示出来（它会被真的用来裁剪历史，不是装饰）
    expect(await screen.findByText('65,536 / 8,192')).toBeInTheDocument()
    expect(screen.queryByText('1024 维')).toBeNull()
    // 段上带条数：一眼看出这家有几个对话、几个嵌入
    expect(screen.getByRole('button', { name: '对话（1）' })).toHaveClass('btn-secondary')

    fireEvent.click(screen.getByRole('button', { name: '嵌入（1）' }))

    // 嵌入：维度必须显示（决定向量能不能存进集合），窗口/输出那列不摆出来
    expect(await screen.findByText('1024 维')).toBeInTheDocument()
    expect(screen.queryByText('65,536 / 8,192')).toBeNull()
  })

  it('缺维度的嵌入模型要点出来，而不是显示成"维度空着"', async () => {
    installFetchMock({
      providers: [providerRow({ models: [modelRow(), embeddingRow({ dimension: null })] })],
    })

    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: /^嵌入（/ }))

    expect(await screen.findByText('缺维度')).toBeInTheDocument()
  })

  it('预设里有的型号：窗口与最大输出来自厂商公开值，不给改', async () => {
    installFetchMock()
    renderPage()

    // 超时给足：满量跑（上千条用例并行）时这一页的数据要一两秒才到位，默认 1s 会偶发失败；
    // 这条用例要断言的是"锁定"这件事，不是加载有多快
    const row = await waitFor(() => rowOf('deepseek-chat'), { timeout: 10_000 })
    fireEvent.click(within(row).getByRole('button', { name: '编辑' }))

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

    const row = await waitFor(() => rowOf('my-own-model'), { timeout: 10_000 })
    fireEvent.click(within(row).getByRole('button', { name: '编辑' }))

    expect(await screen.findByLabelText('上下文窗口', {}, { timeout: 10_000 })).not.toHaveAttribute('readonly')
    expect(screen.getByLabelText('支持工具调用')).not.toBeDisabled()
  })

  it('两个用途各说一句，缺哪个说哪个——并进卡片头，不再用横幅', async () => {
    installFetchMock({
      providers: [providerRow({ apiKeyConfigured: false, apiKeyMaskedTail: null })],
      current: null,
    })

    renderPage()

    await screen.findByText('EOS_ASSISTANT_KEY_DEEPSEEK')
    // 两个用途各有自己的一条"尚未配置"（对话与嵌入坏掉的表现完全不同：一个是"助手不回话"，
    // 一个是"问制度类问题没答案"），合成一句会让人去查错方向。
    // 断言落在徽标上：它被包成一个 span 之后"对话：尚未配置"已经跨元素，getByText 匹配不到
    expect(screen.getAllByText('尚未配置')).toHaveLength(2)
    // 横幅已移除：它是"提醒"而不是内容，夹在页签与表格之间会把两者割开；
    // 而这两条状态本来就在同一行上，且贴着各自的"当前"值
    expect(screen.queryAllByRole('alert')).toHaveLength(0)
    expect(screen.getByText('未配置')).toBeInTheDocument()
  })

  it('对话配好了也说清嵌入还没配（两种状态各报各的）', async () => {
    installFetchMock()

    renderPage()

    // 默认 mock：对话已配、嵌入没配。嵌入那条必须出现，否则"知识库为什么不好用"
    // 在界面上没有落点——用户只会看到助手本身一切正常
    expect(await screen.findByText(/对话：DeepSeek 开放平台 \/ deepseek-chat/)).toBeInTheDocument()
    expect(screen.getByText('尚未配置')).toBeInTheDocument()
  })

  it('完全没有供应商时给出下一步，而不是一个空表格', async () => {
    installFetchMock({ providers: [], current: null })

    renderPage()

    expect(await screen.findByText('还没有供应商')).toBeInTheDocument()
    // 两个用途都还没有配置：两条徽标都在（此时右上角还有「添加供应商」可点）
    expect(await screen.findAllByText('尚未配置')).toHaveLength(2)
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
    // 先等数据到位：rowOf 是**同步**查找（不能等着用它的返回值当 find 的参数），
    // 在数据回来之前直接调用会当场抛"找不到 DeepSeek 开放平台"
    await screen.findByText('DeepSeek 开放平台')
    fireEvent.click(within(rowOf('DeepSeek 开放平台')).getByRole('button', { name: '密钥' }))
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
    await screen.findByText('deepseek-chat')

    // 用 title 定位而不是可访问名："删除"两个字在模型行与供应商行都有，可访问名会撞
    expect(screen.getByTitle('删除（当前模型不可删）')).toBeDisabled()
    // 级联会一次带走整家供应商的配置，所以先删模型再删供应商
    expect(screen.getByTitle('名下还有模型，需先删除它们')).toBeDisabled()
  })

  it('用量页签：按模型与按天各占一块（上主下子），并给出当日上限口径', async () => {
    installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('tab', { name: '用量' }))

    expect(await screen.findByText('按模型（近 30 天）')).toBeInTheDocument()
    expect(screen.getByText('按天（近 30 天）')).toBeInTheDocument()
    expect(screen.getByText(/上限：每人 ¥5\/天、全局 ¥50\/天/)).toBeInTheDocument()
  })

  it('拉取型号：厂商给的清单里排除已在库的，勾选后按所选用途与维度落库', async () => {
    const harness = installFetchMock()

    renderPage()
    // 「拉取型号 / 新增模型」在没选中供应商时是**禁用**的（详情区还没有上下文）；
    // 点一个禁用按钮不会报错，只会什么都不发生——所以要等它变成可用再点
    await waitFor(() => expect(screen.getByRole('button', { name: '拉取型号' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '拉取型号' }))

    // 厂商给了两条，库里已有 deepseek-chat → 只列另一条
    // （勾了也会被唯一约束拒，而拒在逐条提交的中途会留下半截结果）
    expect(await screen.findByLabelText('qwen-embedding', {}, { timeout: 10_000 })).toBeInTheDocument()
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
    // 同上：等选中供应商（按钮从禁用变可用）再点，否则弹窗根本不会打开
    await waitFor(() => expect(screen.getByRole('button', { name: '新增模型' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '新增模型' }))

    // 对话模型的字段先摆出来（默认用途是对话）
    expect(await screen.findByLabelText('用途', {}, { timeout: 10_000 })).toBeInTheDocument()
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
    expect(await screen.findByText('无需凭据')).toBeInTheDocument()
    expect(screen.queryByText('未配置')).toBeNull()
  })
})
