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

/** 服务端的失败形状：`{ code, message }`（客户端按 code 归类、按 message 显示原因）。 */
function errorResponse(status: number, code: string, message: string): Response {
  return new Response(JSON.stringify({ code, message }), {
    status,
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

/** 质量页签的默认聚合：带一段「回答质量」结论，四个数字都能算出来。 */
const defaultQualityReport = {
  days: 7,
  since: '2026-10-25T00:00:00Z',
  overview: {
    sessions: 3, userMessages: 5, assistantMessages: 5, toolCalls: 4, succeededCalls: 3,
    failedCalls: 1, unknownOutcomeCalls: 0, rejectedCalls: 0, unknownToolCalls: 0,
    promptTokens: 900, completionTokens: 400, averageElapsedMs: 1200, positiveFeedback: 1,
    negativeFeedback: 0,
  },
  tools: [{
    name: 'resolve_metric', calls: 4, succeeded: 3, failed: 1, unknownOutcome: 0,
    rejected: 0, unknown: 0,
  }],
  daily: [{ day: '2026-10-31', messages: 10, toolCalls: 4, failed: 1, promptTokens: 900, completionTokens: 400 }],
  callBuckets: [{ callsPerReply: 1, replies: 4 }],
  spentYuan: 0.006,
  requests: 12,
  alerts: [
    {
      code: 'JUDGE_OVERREACH', severity: 'critical', title: '越界承诺', critical: true,
      message: '有 1 条回答声称做了不可代签的动作（批核/解批/结案/权限授予）。目标恒为 0：先停用相关能力面并复盘。',
    },
    {
      code: 'JUDGE_DAILY_CAP', severity: 'warning', title: '今日判定已达上限', critical: false,
      message: '今日判定次数或成本已达上限：此后不再产出新结论——这是「没判」，不是「没问题」。',
    },
  ],
  answerQuality: {
    judged: 2,
    clauses: [
      { clause: 'A', title: '口径标注', pass: 5, fail: 1, na: 0, unsure: 0, evaluated: 6, passRate: 5 / 6 },
      { clause: 'B', title: '范围一致', pass: 6, fail: 0, na: 0, unsure: 0, evaluated: 6, passRate: 1 },
      { clause: 'C', title: '诚实缺口', pass: 3, fail: 0, na: 3, unsure: 0, evaluated: 3, passRate: 1 },
      { clause: 'D', title: '不越界承诺', pass: 5, fail: 0, na: 1, unsure: 0, evaluated: 5, passRate: 1 },
      { clause: 'E', title: '不编造事实', pass: 4, fail: 0, na: 0, unsure: 2, evaluated: 4, passRate: 1 },
      { clause: 'F', title: '权限明说', pass: 0, fail: 0, na: 4, unsure: 2, evaluated: 0, passRate: null },
    ],
    citationRate: 5 / 6,
    scopeRate: 1,
    overreachCount: 0,
    unsureRate: 0.15,
    rubricVersion: 'v1',
    judgeModels: ['deepseek/deepseek-flash'],
    spentYuan: 0.0123,
    calls: 2,
    dailyCalls: 2,
    dailyMaxCalls: 200,
    dailySpentYuan: 0.0123,
    dailyCostCapYuan: 5,
    capped: false,
  },
}

/** 判定结论的默认分页：一条 FAIL（带逐字证据）一条 PASS。 */
const defaultVerdictPage = {
  items: [
    {
      verdictId: 2, messageId: 34092, sessionId: 33523, userId: 'admin', clause: 'A', verdict: 'FAIL',
      evidence: '合计 139,022,210.42', note: '未点名口径', rubricVersion: 'v1',
      judgeModel: 'deepseek/deepseek-flash', fallback: false, judgedAt: '2026-10-31T13:12:00Z',
      currentRubric: true,
    },
    {
      verdictId: 1, messageId: 34090, sessionId: 33522, userId: 'admin', clause: 'B', verdict: 'PASS',
      evidence: '仅统计 9 月', note: null, rubricVersion: 'v1',
      judgeModel: 'deepseek/deepseek-flash', fallback: true, judgedAt: '2026-10-30T02:00:00Z',
      currentRubric: true,
    },
  ],
  total: 2,
  page: 1,
  pageSize: 50,
}

/** 判官校准读数：一条漏报（人判不通过、判官判通过），用来验红色警示与两边引文都摆出来。 */
const defaultCalibrationReport = {
  samples: 3,
  reviewed: 2,
  compared: 1,
  uncompared: 1,
  clauses: [
    { clause: 'A', title: '口径标注', compared: 1, agreed: 0, mismatched: 1, rate: 0 },
    { clause: 'B', title: '范围一致', compared: 1, agreed: 1, mismatched: 0, rate: 1 },
  ],
  agreementRate: 0.5,
  dangerousCount: 1,
  mismatches: [{
    caseId: 'JG-01', messageId: 34092, clause: 'A',
    humanVerdict: 'FAIL', humanEvidence: '人：全部时间也算进来了',
    judgeVerdict: 'PASS', judgeEvidence: '回答里提到了口径',
    judgeModel: 'deepseek/deepseek-flash', dangerous: true,
  }],
  rubricVersion: 'v1',
  judgeModels: ['deepseek/deepseek-flash'],
  notes: ['JG-02：判官还没有对回答 999999 给出当前条款版本的结论，无法比对。'],
}

function installFetchMock(options: {
  providers?: unknown[]
  current?: Record<string, unknown> | null
  currentEmbedding?: Record<string, unknown> | null
  presets?: unknown[]
  presetEmbedding?: string
  /** 厂商清单这次拉回来的内容（默认含一条目录里**没有**的型号，用来验"要人选用途"那条路）。 */
  discovered?: unknown[]
  /** 让拉取失败，用来验"降级到目录、并说明原因"。 */
  discoverFails?: boolean
  /** 质量页签的聚合（默认带一段「回答质量」结论，用来验四个数字与筛选）。 */
  quality?: Record<string, unknown>
  /** 判定结论分页（默认两条：一条 FAIL、一条 PASS）。 */
  verdicts?: Record<string, unknown>
  /** 判官校准读数（默认一条漏报）。 */
  calibration?: Record<string, unknown>
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
      if (options.discoverFails) {
        return errorResponse(503, 'MODEL_DISCOVERY_FAILED',
          '连不上 DeepSeek 开放平台（https://api.deepseek.com）：连接超时。')
      }
      return jsonResponse({
        providerId: 1,
        models: options.discovered ?? [
          { modelCode: 'deepseek-chat', displayName: 'DeepSeek Chat', contextWindow: 65536, maxOutputTokens: 8192 },
          { modelCode: 'deepseek-reasoner', displayName: 'DeepSeek Reasoner', contextWindow: 65536, maxOutputTokens: 8192 },
          // 目录里没有它 → 必须由人选用途才能加
          { modelCode: 'deepseek-v4-pro', displayName: 'DeepSeek V4 Pro', contextWindow: 131072, maxOutputTokens: 16384 },
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
            {
              // 目录里的嵌入型号**带着维度**：选中即落库，不需要用户填
              kind: 'EMBEDDING', dimension: 1024,
              modelCode: 'text-embedding-v4', displayName: '通义 text-embedding-v4', contextWindow: null,
              maxOutputTokens: null, supportsTools: false, inputPerMillionYuan: 0.5,
              outputPerMillionYuan: null, defaultTemperature: null, remark: null,
            },
          ],
        },
      ])
    }
    if (url.includes('/admin/assistant/quality/calibration')) {
      return jsonResponse(options.calibration ?? defaultCalibrationReport)
    }
    if (url.includes('/admin/assistant/quality/verdicts')) {
      return jsonResponse(options.verdicts ?? defaultVerdictPage)
    }
    if (url.includes('/admin/assistant/quality/judge') && method === 'POST') {
      return jsonResponse({
        candidates: 3, judged: 1, skippedBySampling: 2, skippedByCap: 0, failed: 0,
        stopReason: null, costYuan: 0.0012,
      })
    }
    if (url.includes('/admin/assistant/quality')) {
      return jsonResponse(options.quality ?? defaultQualityReport)
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
    fireEvent.click(within(row).getByRole('button', { name: '参数' }))

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
    fireEvent.click(within(row).getByRole('button', { name: '参数' }))

    expect(await screen.findByLabelText('上下文窗口', {}, { timeout: 10_000 })).not.toHaveAttribute('readonly')
    expect(screen.getByLabelText('支持工具调用')).not.toBeDisabled()
  })

  it('标题行不重复用途状态：当前模型只由表里的「当前」徽标标出', async () => {
    installFetchMock({
      providers: [providerRow({ apiKeyConfigured: false, apiKeyMaskedTail: null })],
      current: null,
    })

    renderPage()

    await screen.findByText('EOS_ASSISTANT_KEY_DEEPSEEK')
    // 标题行只有标题：配没配由表里看得出来（有「当前」徽标就是配了），
    // 同一件事在两个地方各说一遍，读的人还得核对两处结论是否一致
    expect(screen.queryByText(/对话：/)).toBeNull()
    expect(screen.queryByText(/嵌入：/)).toBeNull()
    expect(screen.queryByText('尚未配置')).toBeNull()
    // 「密钥未配置」是另一件事、也仍然显眼：它决定这家接入点能不能用
    expect(screen.getByText('未配置')).toBeInTheDocument()
    // 横幅同样没有：提醒夹在页签与表格之间会把两者割开
    expect(screen.queryAllByRole('alert')).toHaveLength(0)
  })

  it('当前模型在表里标「当前」：对话有、嵌入没有时两者各看得出来', async () => {
    installFetchMock()

    renderPage()

    // 默认 mock：对话已配、嵌入没配。配好的那条在模型表里带「当前」徽标
    const chatRow = await waitFor(() => rowOf('deepseek-chat'), { timeout: 10_000 })
    expect(within(chatRow).getByText('当前')).toBeInTheDocument()
    // 供应商行另一枚「当前对话」标出是哪一家
    expect(screen.getByText('当前对话')).toBeInTheDocument()
  })

  it('完全没有供应商时给出下一步，而不是一个空表格', async () => {
    installFetchMock({ providers: [], current: null })

    renderPage()

    expect(await screen.findByText('还没有供应商')).toBeInTheDocument()
    // 空态要指出去哪儿点：这个入口在标题行右侧，与标题同一行
    expect(screen.getByRole('button', { name: '添加供应商' })).toBeInTheDocument()
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
    // 目录里这家的型号默认全勾上（含嵌入那条，它带着维度）
    expect(screen.getByLabelText('deepseek-chat')).toBeChecked()
    expect(screen.getByLabelText('deepseek-reasoner')).toBeChecked()
    expect(screen.getByLabelText('text-embedding-v4')).toBeChecked()

    fireEvent.click(screen.getByRole('button', { name: '添加' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'POST' && call.url.includes('/admin/assistant/providers')),
    ).toBe(true))
    const posted = JSON.parse(harness.calls.find(call => call.method === 'POST')!.body!)
    expect(posted.code).toBe('deepseek')
    expect(posted.models).toHaveLength(3)
    // 嵌入型号的维度也是目录带来的：不带它，落库的行等于"尺寸待定"
    expect(posted.models.find((item: { modelCode: string }) => item.modelCode === 'text-embedding-v4').dimension)
      .toBe(1024)
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

  it('按天表的日期显示 yyyy-MM-dd（它是日期标签，不是一个时刻）', async () => {
    installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByRole('tab', { name: '用量' }))

    // mock 给的是服务端那种 ISO 串（2026-10-01T00:00:00）：直接摆出来会被列宽截成
    // "2026-10-01T00:0…"，而这一列本来就是个日期
    expect(await screen.findByText('2026-10-01')).toBeInTheDocument()
    expect(screen.queryByText('2026-10-01T00:00:00')).toBeNull()
  })

  it('添加模型：列出目录里这家支持的型号（排除已在库的），选中后连同参考参数一起落库', async () => {
    const harness = installFetchMock()
    const postedModels = () => harness.calls
      .filter(call => call.method === 'POST' && call.url.includes('/admin/assistant/models'))
      .map(call => JSON.parse(call.body!))

    renderPage()
    // 「添加模型」在没选中供应商时是**禁用**的（详情区还没有上下文）；
    // 点一个禁用按钮不会报错，只会什么都不发生——所以要等它变成可用再点
    await waitFor(() => expect(screen.getByRole('button', { name: '添加模型' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '添加模型' }))

    // 目录里这家的型号都会列出：deepseek-chat 已经作为**对话**落库，所以"对话"那种标「已加」、
    // 复选框不可勾选；但同一型号仍能再加一条判官（身份含用途，迁移 469）。其余两条照常可选
    expect(await screen.findByLabelText('deepseek-reasoner', {}, { timeout: 10_000 })).toBeInTheDocument()
    const chatCheckbox = screen.getByLabelText('deepseek-chat')
    expect(chatCheckbox).toBeDisabled()
    const chatKind = screen.getByLabelText('deepseek-chat 的用途') as HTMLSelectElement
    expect(chatKind).toHaveValue('CHAT')
    expect(within(chatKind).getByRole('option', { name: /对话/ })).toBeDisabled()
    expect(within(chatKind).getByRole('option', { name: /判官/ })).not.toBeDisabled()
    // 打开就**自动拉了一次**厂商清单（不需要用户先点一个"拉取"）
    expect(harness.calls.filter(call => call.url.includes('/models/discover'))).toHaveLength(1)

    // 用途与维度**由目录带出来、不必人选也不必填**——这一步本来就只是"选"。
    // 断言落在整行的文本上：这行由多段拼成，逐段查容易随文案调整误报
    const embedRow = screen.getByLabelText('text-embedding-v4').closest('label')!
    expect(embedRow.textContent).toContain('（1024 维）')
    expect(embedRow.textContent).toContain('参考单价 入 ¥0.5')

    fireEvent.click(screen.getByLabelText('deepseek-reasoner'))
    fireEvent.click(screen.getByLabelText('text-embedding-v4'))
    fireEvent.click(screen.getByRole('button', { name: '添加（2 个）' }))

    await waitFor(() => expect(postedModels()).toHaveLength(2))
    const chat = postedModels().find(item => item.modelCode === 'deepseek-reasoner')!
    const embed = postedModels().find(item => item.modelCode === 'text-embedding-v4')!

    // 落库的是**目录里的参考值**，不是留空等用户回来补
    expect(chat.kind).toBe('CHAT')
    expect(chat.contextWindow).toBe(65536)
    expect(chat.maxOutputTokens).toBe(8192)
    expect(chat.supportsTools).toBe(true)
    expect(embed.kind).toBe('EMBEDDING')
    expect(embed.dimension).toBe(1024)
    expect(embed.inputPerMillionYuan).toBe(0.5)
    // 嵌入模型不带窗口/输出与工具调用：它用不上这些值，带过去就是没人会读的垃圾数据
    expect(embed.contextWindow).toBeNull()
    expect(embed.maxOutputTokens).toBeNull()
    expect(embed.supportsTools).toBe(false)
    expect(postedModels().every(item => item.providerId === 1)).toBe(true)
  })

  it('同一型号可以再加一条判官：用途切到判官即可勾选，落库时收敛掉工具能力', async () => {
    const harness = installFetchMock()
    const postedModels = () => harness.calls
      .filter(call => call.method === 'POST' && call.url.includes('/admin/assistant/models'))
      .map(call => JSON.parse(call.body!))

    renderPage()
    await waitFor(() => expect(screen.getByRole('button', { name: '添加模型' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '添加模型' }))

    // deepseek-chat 已经作为**对话**在库：默认那一种不可勾选，切到判官才解锁
    const checkbox = await screen.findByLabelText('deepseek-chat', {}, { timeout: 10_000 })
    expect(checkbox).toBeDisabled()
    fireEvent.change(screen.getByLabelText('deepseek-chat 的用途'), { target: { value: 'JUDGE' } })
    expect(screen.getByLabelText('deepseek-chat')).not.toBeDisabled()

    fireEvent.click(screen.getByLabelText('deepseek-chat'))
    fireEvent.click(screen.getByRole('button', { name: '添加（1 个）' }))

    await waitFor(() => expect(postedModels()).toHaveLength(1))
    const posted = postedModels()[0]
    expect(posted.modelCode).toBe('deepseek-chat')
    expect(posted.kind).toBe('JUDGE')
    // 判官不调工具：目录那条写着"支持工具"，落成判官时必须收敛成 false
    expect(posted.supportsTools).toBe(false)
    // 窗口与单价对判定仍然有用：照目录带上
    expect(posted.contextWindow).toBe(65536)
    expect(posted.dimension).toBeNull()
  })

  it('清单里没有的型号：从「添加模型」里的次要出口转手工填，仍以新增提交', async () => {
    const harness = installFetchMock()

    renderPage()
    // 同上：等选中供应商（按钮从禁用变可用）再点，否则弹窗根本不会打开
    await waitFor(() => expect(screen.getByRole('button', { name: '添加模型' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '添加模型' }))
    fireEvent.click(await screen.findByRole('button', { name: '清单里没有，手工填' }, { timeout: 10_000 }))

    // 手填表单（自建端点 / 清单里都没有的型号走这条）：默认用途是对话
    expect(await screen.findByLabelText('用途')).toBeInTheDocument()
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

  it('厂商清单里目录没有的型号：要人选用途才给加（信息客观上不存在，不猜）', async () => {
    const harness = installFetchMock()
    const postedModels = () => harness.calls
      .filter(call => call.method === 'POST' && call.url.includes('/admin/assistant/models'))
      .map(call => JSON.parse(call.body!))

    renderPage()
    await waitFor(() => expect(screen.getByRole('button', { name: '添加模型' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '添加模型' }))

    // deepseek-v4-pro 只出现在厂商清单里（目录还没有它）：它也在列表里，但只有型号名
    const row = await screen.findByLabelText('deepseek-v4-pro', {}, { timeout: 10_000 })
    fireEvent.click(row)

    // 勾上了但没选用途 → 不能提交（不猜用途：猜错会得到一个永远打不通的行）
    expect(screen.getByRole('button', { name: /添加（1 个）/ })).toBeDisabled()
    expect(screen.getByText(/还没有用途/)).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('deepseek-v4-pro 的用途'), { target: { value: 'CHAT' } })
    expect(screen.getByRole('button', { name: /添加（1 个）/ })).not.toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: /添加（1 个）/ }))

    await waitFor(() => expect(postedModels()).toHaveLength(1))
    const posted = postedModels()[0]
    expect(posted.modelCode).toBe('deepseek-v4-pro')
    expect(posted.kind).toBe('CHAT')
    // 厂商顺手给的窗口与最大输出带上（这条是厂商的数据，不猜）
    expect(posted.contextWindow).toBe(131072)
    expect(posted.maxOutputTokens).toBe(16384)
    // 目录里没有参考单价 → 留空（= 用全局兜底价），不按 0 元算
    expect(posted.inputPerMillionYuan).toBeNull()
  })

  it('厂商清单没拉到时：仍能加目录里的型号，并说明原因且可重试', async () => {
    const harness = installFetchMock({ discoverFails: true })

    renderPage()
    await waitFor(() => expect(screen.getByRole('button', { name: '添加模型' })).not.toBeDisabled(), { timeout: 10_000 })
    fireEvent.click(screen.getByRole('button', { name: '添加模型' }))

    // 拉取失败是**降级**而不是阻断：目录里的型号照样可选、可加
    expect(await screen.findByLabelText('deepseek-reasoner', {}, { timeout: 10_000 })).toBeInTheDocument()
    expect(screen.getByText(/厂商清单这次没拉到/)).toBeInTheDocument()
    expect(screen.getByText(/连接超时/)).toBeInTheDocument()

    // 「重试」真的会再拉一次（失败之后还能自救）。
    // 放在"添加"之前点：加成功会关掉对话框，之后再点就点了个已经卸掉的按钮
    fireEvent.click(screen.getByRole('button', { name: '重试' }))
    await waitFor(() => expect(harness.calls.filter(call => call.url.includes('/models/discover')).length)
      .toBeGreaterThan(1))

    // 降级状态下仍能把目录里的型号加进来
    fireEvent.click(screen.getByLabelText('deepseek-reasoner'))
    fireEvent.click(screen.getByRole('button', { name: /添加（1 个）/ }))
    await waitFor(() => expect(harness.calls.some(call =>
      call.method === 'POST' && call.url.includes('/admin/assistant/models'))).toBe(true))
  })

  it('嵌入当前在表里独立标出，取消时带上用途（不会连带清掉对话那条）', async () => {
    const harness = installFetchMock({
      providers: [providerRow({ models: [modelRow(), embeddingRow({ isActive: true })] })],
      currentEmbedding: {
        modelId: 21, displayName: '通义 text-embedding-v4', modelCode: 'text-embedding-v4',
        providerId: 1, providerCode: 'deepseek', providerDisplayName: 'DeepSeek 开放平台',
        dimension: 1024, apiKeyConfigured: true,
      },
    })

    renderPage()

    // 供应商行那枚「当前嵌入」与「当前对话」并列：两个用途各自只有一个当前
    expect(await screen.findByText('当前嵌入')).toBeInTheDocument()
    // 切到嵌入用途后，那条模型在自己行里也带「当前」
    fireEvent.click(await screen.findByRole('button', { name: '嵌入（1）' }))
    const embedRow = await waitFor(() => rowOf('text-embedding-v4'), { timeout: 10_000 })
    expect(within(embedRow).getByText('当前')).toBeInTheDocument()

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

  it('配置面动作与标题同处一行：图标 + 文字（.erp-command-btn），不再另起一条只有「刷新」的命令栏', async () => {
    const { container } = renderPage()

    // `.erp-command-btn` 是"图标 + 文字"的共享尺寸档：类名丢了不会有任何报错，
    // 只会让按钮变成一排看不懂的图标
    const add = await screen.findByRole('button', { name: '添加供应商' })
    expect(add).toHaveClass('erp-command-btn')
    expect(add.querySelector('svg')).not.toBeNull()
    expect(add.textContent).toBe('添加供应商')

    // 这张表没有列表自身工具（无搜索、无分页）：单为「刷新」起一条命令栏等于给表格白加一行
    expect(container.querySelector('.erp-list-command-bar')).toBeNull()
    expect(screen.queryByRole('button', { name: '刷新' })).toBeNull()
  })

  it('行内动作按钮同样是图标 + 文字（纯文字按钮在读的人眼里是一排等宽的字）', async () => {
    installFetchMock()

    renderPage()

    const row = await waitFor(() => rowOf('deepseek-chat'), { timeout: 10_000 })
    for (const name of ['设为当前', '参数', '删除']) {
      const button = within(row).getByRole('button', { name })
      expect(button.querySelector('svg')).not.toBeNull()
      // 文字还在（图标是补充而不是替代）
      expect(button.textContent).toBe(name)
    }
  })

  it('用量页签两块等分；模型页签不等分（那边是主表按内容、子表占满）', async () => {
    installFetchMock()

    const { container } = renderPage()

    expect(await screen.findByText('模型管理')).toBeInTheDocument()
    const modelsPane = container.querySelector('.erp-tabbed-panel-body > .erp-workbench-page')!
    expect(modelsPane.className).toContain('erp-model-admin-page')
    // 等分是给"两张表地位相同"的场景（按模型 / 按天）；模型页签不是这种场景
    expect(modelsPane.className).not.toContain('erp-workbench-even')

    fireEvent.click(screen.getByRole('tab', { name: '用量' }))

    await waitFor(() => expect(container.querySelector('.erp-tabbed-panel-body > .erp-workbench-page'))
      .toHaveClass('erp-workbench-even'))
  })

  it('质量页签：回答质量给出四个数字，判定结论带逐字证据并可按条款筛选', async () => {
    const { calls } = installFetchMock()
    renderPage()

    fireEvent.click(await screen.findByRole('tab', { name: '质量' }))

    // 四个数字是 ADR-044 §4.5 写死的那四个，界面上必须逐条点名（不能只给一个"质量分"）
    expect(await screen.findByText('口径标注率')).toBeInTheDocument()
    // 条款版本只显示一个 v（rubricVersion 本身已含 v，再拼就成 "vv1"）
    expect(await screen.findByText(/只判「有没有如实回答」/)).toBeInTheDocument()
    await waitFor(() => expect(document.body.textContent ?? '').toContain('条款 v1 ·'))
    expect(document.body.textContent ?? '').not.toContain('vv1')
    expect(screen.getByText('范围一致率')).toBeInTheDocument()
    expect(screen.getByText('越界承诺数')).toBeInTheDocument()
    expect(screen.getByText('无法判定率')).toBeInTheDocument()
    // 告警（J3）：红线与"停判"都必须看得见——停判不是"没问题"
    expect(await screen.findByText(/先停用相关能力面/)).toBeInTheDocument()
    expect(screen.getByText(/今日判定次数或成本已达上限/)).toBeInTheDocument()
    // 5/6 与 100% 同时出现说明比率不是"全通过/全失败"的假值（数据到达前显示的是 —）
    await waitFor(() => expect(screen.getAllByText('83.3%').length).toBeGreaterThan(0))
    expect(screen.getAllByText('100.0%').length).toBeGreaterThan(0)

    // 结论行：条款 / 结论 / 逐字证据 / 判官模型（含回落与旧版本标记）
    const failRow = (await screen.findByText('合计 139,022,210.42')).closest('tr')!
    expect(within(failRow).getByText('不通过')).toBeInTheDocument()
    expect(within(failRow).getByText('A')).toBeInTheDocument()
    expect(within(failRow).getByText(/deepseek\/deepseek-flash/)).toBeInTheDocument()
    // 回落是要标出来的：判官没配时用的是对话模型，不能假装用的是判官模型
    const passRow = screen.getByText('仅统计 9 月').closest('tr')!
    expect(within(passRow).getByText(/· 回落/)).toBeInTheDocument()

    // 按条款筛选走服务端：请求里带 clause=A（前端不自己过滤，否则分页总数会与它不一致）
    fireEvent.change(screen.getByLabelText('按条款筛选'), { target: { value: 'A' } })
    await waitFor(() => expect(calls.some((call) => call.url.includes('clause=A'))).toBe(true))
  })

  it('判官校准：给出样本/一致率，漏报单独红标并把两边的引文都摆出来', async () => {
    installFetchMock()
    renderPage()

    fireEvent.click(await screen.findByRole('tab', { name: '质量' }))

    expect(await screen.findByText('判官校准')).toBeInTheDocument()
    // 一致率与样本计数：只算已审阅（2 条里 1 条能对上）
    await waitFor(() => expect(screen.getByText(/一致率 50\.0%/)).toBeInTheDocument())
    expect(screen.getByText(/样本 3 \/ 已审阅 2 \/ 可比对 1 \/ 未比对 1/)).toBeInTheDocument()
    // 漏报（人判不通过、判官判通过）必须单独红标——它会把错误答案留在库里
    expect(screen.getByText(/有 1 条漏报/)).toBeInTheDocument()
    expect(screen.getByText(/不要调采样率/)).toBeInTheDocument()
    // 不一致行：两边的话都要看得见，否则改条款时无从下笔
    expect(await screen.findByText('人：全部时间也算进来了')).toBeInTheDocument()
    expect(screen.getByText('回答里提到了口径')).toBeInTheDocument()
    // 比不了的要说明原因，不能静默消失
    expect(screen.getByText(/判官还没有对回答 999999/)).toBeInTheDocument()
  })

  it('表格按电子表口径：表头可排序（真的重排）、操作列冻结、列宽可拖、右键可复制', async () => {
    installFetchMock({ providers: [providerRow(), secondProviderRow()] })

    renderPage()

    const table = (await screen.findByText('DeepSeek 开放平台')).closest('table')!
    const firstColumn = () => [...table.querySelectorAll('tbody tr')]
      .map(row => row.querySelector('td')?.textContent?.trim() ?? '')
    const before = firstColumn()

    // 排序：表头那个小菜单（默认/升序/降序）。**不传 clientSideSorting 时它根本不渲染**——
    // 也就是说"表头能排序"这件事在页面上只有一个开关，丢了不会有任何报错
    fireEvent.click(within(table).getByLabelText('表头操作供应商'))
    fireEvent.click(screen.getByRole('button', { name: '降序' }))

    await waitFor(() => expect(firstColumn()[0]).not.toBe(before[0]))
    // 只是换了序，没丢行
    expect([...firstColumn()].sort()).toEqual([...before].sort())

    // 操作列冻结在右侧：横向滚动时不必来回拉才能点到"这一行的操作"
    expect(within(table).getByRole('columnheader', { name: /操作/ })).toHaveClass('erp-frozen-right')
    // 列宽可拖（拖拽把手由 resizable 带出）
    expect(table.querySelectorAll('.erp-col-resizer').length).toBeGreaterThan(0)
    // 复制：单元格右键菜单（ErpTable 默认能力，页面不必额外开）
    fireEvent.contextMenu(within(table).getAllByRole('cell')[0])
    expect(await screen.findByRole('button', { name: '复制单元格' })).toBeInTheDocument()
  })
})
