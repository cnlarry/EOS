import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { AssistantDock } from './AssistantDock'
import { resetSituationSource } from './situationSource'

/** 路由探针：闭环判据 P3 是"全程不离开助手"，路径变了这里就看得见。 */
function LocationProbe() {
  const location = useLocation()
  return <div data-testid="path">{location.pathname}</div>
}

function renderDock(path = '/dashboard') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <LocationProbe />
      <AssistantDock />
    </MemoryRouter>,
  )
}

function sseResponse(chunks: string[]): Response {
  const encoder = new TextEncoder()
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(encoder.encode(chunk))
      controller.close()
    },
  })
  return new Response(stream, { status: 200 })
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

interface FetchCall {
  url: string
  method: string
  body: string | null
}

interface FetchHarness {
  fetchMock: ReturnType<typeof vi.fn>
  calls: FetchCall[]
  chatCalls: () => FetchCall[]
}

const IDENTITY = {
  userId: 'u1',
  employeeName: '张三',
  employeeId: 'E001',
  departmentId: 'CG',
  departmentName: '采购部',
  companyId: 'C1',
  defaultGroupId: 'G1',
  directLeader: null,
  departmentLeader: null,
  workGroups: [],
}

function situationSnapshot(overrides: Record<string, unknown> = {}) {
  return {
    identity: IDENTITY,
    where: { moduleId: null, moduleTitle: null, pageType: null, docNo: null, dropped: [] },
    pending: { myApproval: 0, startedInFlight: 0 },
    recent: [],
    digest: { items: [], sources: [], caveats: [] },
    budget: { residentTokens: 12, residentTokenLimit: 300 },
    ...overrides,
  }
}

const OVERDUE_SITUATION = situationSnapshot({
  where: { moduleId: 1606, moduleTitle: '客户订单', pageType: 'list', docNo: null, dropped: [] },
  pending: { myApproval: 0, startedInFlight: 0 },
  digest: {
    items: [
      {
        kind: 'overdue',
        moduleId: 1606,
        moduleTitle: '客户订单',
        key: 'DD2608001',
        reason: '已录入 20 天仍未批核（共 3 条同类滞留）',
        occurredAt: '2026-08-01T00:00:00',
        ageDays: 20,
      },
    ],
    sources: ['overdue:扫描 1 个模块'],
    caveats: ['待我审批与本人在途流程当前计数为 0（审批流未在运行），摘要来源以滞留与被拒为主。'],
  },
})

/** 操作卡会拉一次表单定义：这里给一份最小可用的，字段渲染由 ActionCard 自己的用例细究。 */
function minimalFormDefinition() {
  return {
    moduleId: 1403,
    title: '客户询价单',
    masterTable: 'COP_CHAFFER_M',
    detailTable: null,
    hasAdd: true,
    hasEdit: true,
    mode: 'new',
    ifCopy: true,
    searchMaster: false,
    searchDetail: false,
    masterFields: [
      {
        key: 'REMARK', label: '备注', dataType: 'nvarchar', displayLength: 100, displayFormat: null,
        isRequired: false, verifyIndex: null, regex: null, defaultValue: '', isReadonly: false, isVisible: true,
        onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
        isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
        serverFilled: false, maxLength: null, tabNo: 1, formOrder: null, span: 1, newLine: false,
        cellGroup: null, cellRole: 0, options: [], displayOnly: false, canCopy: true,
      },
    ],
    detailFields: [],
    masterPkOrder: ['CHAFFER_TYPE', 'CHAFFER_NO'],
    detailNoFields: '',
    detailDfVerify: '',
    tabs: [],
    columns: 4,
    buttons: null,
    hasWorkflow: false,
    hasStatelessApprove: false,
    defaultValues: {},
    canDelete: true,
    canApprove: false,
    canDeapprove: false,
    canEndCase: false,
    canUnEndCase: false,
    canAddNew: true,
    canEdit: true,
    canFileView: false,
    canFileUpda: false,
    canFileEdit: false,
    canFileDele: false,
    canSetup: false,
    canFormDesign: false,
  }
}

/** 动作预演草稿：模型调 preview_record_action 后随 done 事件下发。 */
const ACTION_PREVIEW_DRAFT = {
  kind: 'record-action-preview',
  moduleId: 1403,
  moduleTitle: '客户询价单',
  action: 'insert',
  blocked: false,
  moduleDenialCode: null,
  moduleDenialMessage: null,
  rows: [
    { keys: [], values: { REMARK: '照抄上一单' }, allowed: true, denialCode: null, denialMessage: null, impacts: null },
    { keys: [], values: { REMARK: '第二行' }, allowed: false, denialCode: 'ADD_CAPABILITY_MISSING', denialMessage: '你没有这个模块的新增权限。', impacts: null },
  ],
  notes: [],
}

function installFetchMock(options: {
  sessions?: unknown[]
  messages?: unknown[]
  situation?: unknown
  chatFrames?: string[]
  formDefinition?: unknown
} = {}): FetchHarness {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = (init?.method ?? 'GET').toUpperCase()
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })
    if (url.includes('/assistant/situation')) return jsonResponse(options.situation ?? situationSnapshot())
    if (url.includes('/chat')) return sseResponse(options.chatFrames ?? ['event: delta\ndata: {"text":"好的"}\n\n'])
    if (url.includes('/form-definition')) return jsonResponse(options.formDefinition ?? minimalFormDefinition())
    if (url.includes('/messages')) return jsonResponse(options.messages ?? [])
    if (url.includes('/assistant/sessions')) {
      return method === 'POST'
        ? jsonResponse({ id: '42', userId: 'u1', title: '新会话', createdAt: '', lastActiveAt: '' })
        : jsonResponse(options.sessions ?? [])
    }
    return jsonResponse({})
  })
  vi.stubGlobal('fetch', fetchMock)
  return {
    fetchMock,
    calls,
    chatCalls: () => calls.filter(call => call.method === 'POST' && call.url.includes('/chat')),
  }
}

describe('AssistantDock', () => {
  beforeEach(() => {
    localStorage.clear()
    resetSituationSource()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('默认渲染悬浮球，点击打开抽屉并加载会话列表', async () => {
    installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '库存问题', createdAt: '', lastActiveAt: '' }],
    })

    renderDock()
    expect(screen.queryByRole('complementary')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: '打开工作助手' }))
    await waitFor(() => expect(screen.getByRole('complementary')).not.toBeNull())
    await waitFor(() => expect(screen.getByText('库存问题')).toBeInTheDocument())
  })

  it('Ctrl+/ 快捷键切换抽屉开关，状态写入 localStorage', async () => {
    installFetchMock()
    renderDock()

    fireEvent.keyDown(window, { key: '/', ctrlKey: true })
    expect(screen.getByRole('complementary')).not.toBeNull()
    expect(localStorage.getItem('erp-assistant-open')).toBe('true')

    fireEvent.keyDown(window, { key: '/', ctrlKey: true })
    expect(screen.queryByRole('complementary')).toBeNull()
    expect(localStorage.getItem('erp-assistant-open')).toBe('false')
  })

  it('打开即见：零输入就有处境内容，且不发起对话请求', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    const harness = installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }],
      situation: OVERDUE_SITUATION,
    })

    renderDock('/workbench/1606')

    // 你在哪 / 压着什么
    await waitFor(() => expect(screen.getByText(/你在「客户订单」/)).toBeInTheDocument())
    expect(screen.getByText(/有 1 条单据滞留未批核/)).toBeInTheDocument()
    expect(screen.getByText(/已录入 20 天仍未批核/)).toBeInTheDocument()
    // 不是"一片 0"的空摘要：待办为 0 时不出现待办计数句
    expect(screen.queryByText(/待我审批 0 条/)).toBeNull()
    // 打开抽屉不烧模型额度：全程没有对话请求
    expect(harness.chatCalls()).toHaveLength(0)
    expect(harness.calls.some(call => call.url.includes('/assistant/situation'))).toBe(true)
  })

  it('摘要条目可点：按需追问才发起对话请求', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    const harness = installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }],
      situation: OVERDUE_SITUATION,
    })

    renderDock('/workbench/1606')
    const item = await screen.findByRole('button', { name: /已录入 20 天仍未批核/ })
    expect(harness.chatCalls()).toHaveLength(0)

    fireEvent.click(item)
    await waitFor(() => expect(harness.chatCalls()).toHaveLength(1))
    const body = JSON.parse(harness.chatCalls()[0].body ?? '{}') as { content: string }
    expect(body.content).toContain('DD2608001')
  })

  it('发送消息时携带处境（选中行 / 脏字段 / 最近被拒）', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    const harness = installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }],
    })

    renderDock('/workbench/1606')
    await waitFor(() => expect(screen.getByRole('complementary')).not.toBeNull())

    // 模拟界面动作：列表选中 + 表单脏字段 + 服务端拒绝
    const source = await import('./situationSource')
    source.reportSelection(['DD2608001'])
    source.reportDirtyFields([{ field: 'REMARK', old: '', new: '加急' }])
    source.reportServerNotice('VALIDATION_FAILED', '供应商未填')

    fireEvent.change(screen.getByPlaceholderText(/输入问题/), { target: { value: '在吗' } })
    fireEvent.click(screen.getByRole('button', { name: '发送' }))

    await waitFor(() => expect(harness.chatCalls()).toHaveLength(1))
    const body = JSON.parse(harness.chatCalls()[0].body ?? '{}') as { context: Record<string, unknown> }
    expect(body.context.moduleId).toBe(1606)
    expect(body.context.pageType).toBe('list')
    expect(body.context.selection).toEqual(['DD2608001'])
    expect(body.context.formDirty).toEqual([{ field: 'REMARK', old: '', new: '加急' }])
    expect(body.context.lastNotice).toEqual({ code: 'VALIDATION_FAILED', summary: '供应商未填' })
  })

  it('发送消息后流式增量进入助手气泡', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }],
      chatFrames: [
        'event: delta\ndata: {"text":"你"}\n\n',
        'event: delta\ndata: {"text":"好"}\n\nevent: error\ndata: {"code":"X","message":"中断"}\n\n',
      ],
    })

    renderDock()
    await waitFor(() => expect(screen.getByText('会话A')).toBeInTheDocument())

    fireEvent.change(screen.getByPlaceholderText(/输入问题/), { target: { value: '在吗' } })
    fireEvent.click(screen.getByRole('button', { name: '发送' }))

    await waitFor(() => expect(screen.getByText(/你好/)).toBeInTheDocument())
  })

  it('动作预演在助手内就地成卡：逐行可见，且全程不改路由（P3 闭环）', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }],
      chatFrames: [
        `event: done\ndata: ${JSON.stringify({
          message: { id: 'm2', sessionId: '11', role: 2, content: '预演如下', modelName: null, promptTokens: null, completionTokens: null, elapsedMs: null, correlationId: null, createdAt: '' },
          drafts: [ACTION_PREVIEW_DRAFT],
        })}\n\n`,
      ],
    })

    renderDock('/workbench/1403')
    await waitFor(() => expect(screen.getByText('会话A')).toBeInTheDocument())

    fireEvent.change(screen.getByPlaceholderText(/输入问题/), { target: { value: '照抄上一单再下一单' } })
    fireEvent.click(screen.getByRole('button', { name: '发送' }))

    // 卡片在抽屉里就地渲染：逐行的结论与可就地改的字段都在
    await waitFor(() => expect(screen.getByText(/新增：客户询价单（2 行）/)).toBeInTheDocument())
    expect(screen.getByText(/不可执行：你没有这个模块的新增权限。/)).toBeInTheDocument()
    await waitFor(() => expect(screen.getByLabelText('备注（第 1 行）')).toBeInTheDocument())

    // 闭环判据：全程不离开助手，路由没有被跳走
    expect(screen.getByTestId('path').textContent).toBe('/workbench/1403')
    expect(screen.getByRole('complementary')).not.toBeNull()
  })

  it('用户气泡可一键记住', async () => {
    localStorage.setItem('erp-assistant-open', 'true')
    installFetchMock({
      sessions: [{ id: '11', userId: 'u1', title: '会话A', createdAt: '', lastActiveAt: '' }],
      messages: [
        { id: 'm1', sessionId: '11', role: 1, content: '先看送货单', modelName: null, promptTokens: null, completionTokens: null, elapsedMs: null, correlationId: null, createdAt: '' },
      ],
    })

    renderDock()
    const remember = await screen.findByRole('button', { name: /记住这条消息/ })
    fireEvent.click(remember)

    await waitFor(() => expect(screen.getByText(/已记住/)).toBeInTheDocument())
  })
})
