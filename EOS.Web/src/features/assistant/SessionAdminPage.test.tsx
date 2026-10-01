import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { AssistantProvider } from './AssistantProvider'
import { SessionAdminPage } from './SessionAdminPage'

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

/** 列表接口返回的一行：`archivedAt` 为 null 表示在列。 */
function sessionRow(id: string, title: string, archivedAt: string | null) {
  return {
    id, userId: 'u1', title,
    createdAt: '2026-09-30T10:00:00Z', lastActiveAt: '2026-10-01T09:00:00Z',
    archivedAt, messageCount: 4,
  }
}

function installFetchMock(options: { sessions?: unknown[]; total?: number } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })

    if (url.includes('/assistant/sessions')) {
      if (method === 'POST') {
        return jsonResponse({ id: '42', userId: 'u1', title: '新会话', createdAt: '', lastActiveAt: '' })
      }
      if (method === 'PUT' || method === 'DELETE') return new Response(null, { status: 204 })
      const items = options.sessions ?? []
      return jsonResponse({ items, total: options.total ?? items.length })
    }
    // 处境的返回形状由前端自己判别（不合法就当没有），给个空对象即可
    if (url.includes('/assistant/situation')) return jsonResponse({})
    return jsonResponse({})
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, calls }
}

/**
 * 等列表首屏那几轮重渲染落定（表格出现 + 助手侧会话加载完成）。
 *
 * 不等的话，`findByRole` 会在表格刚插入时就把元素交出来，而 React 紧接着还会替换这批行内节点——
 * 拿到旧节点再点，事件不会走到处理器（点了等于没点）。这类"点了没反应"在测试里极难看出来，
 * 所以这里显式等一拍再重新查询。
 */
async function settleTable() {
  await screen.findByRole('columnheader', { name: '操作' })
  await new Promise(resolve => setTimeout(resolve, 0))
}

function renderAdmin() {
  // 列表页走 react-query：测试给一个不重试的 client，失败立刻暴露而不是等退避
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/assistant/sessions']}>
        <ToastProvider>
          <AssistantProvider>
            <SessionAdminPage />
          </AssistantProvider>
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('SessionAdminPage', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出会话：标题、消息数、归档标记', async () => {
    installFetchMock({
      sessions: [sessionRow('11', '十月采购对账', null), sessionRow('12', '八月盘点', '2026-09-20T02:00:00Z')],
    })

    renderAdmin()
    await settleTable()

    expect(screen.getByText('十月采购对账')).toBeInTheDocument()
    expect(screen.getByText('八月盘点')).toBeInTheDocument()
    // 「已归档」既是筛选按钮也是行上的状态徽标，所以出现两次
    expect(screen.getAllByText('已归档')).toHaveLength(2)
    // 消息数：管理页要能一眼看出哪些会话有内容（两行各 4 条）
    expect(screen.getAllByText('4')).toHaveLength(2)
    // 表头要有"消息"这一列，否则消息数只是碰巧出现的数字
    expect(screen.getAllByRole('columnheader').some(node => node.textContent?.includes('消息'))).toBe(true)
  })

  it('删除只对已归档开放：在列的行不给删除按钮', async () => {
    installFetchMock({
      sessions: [sessionRow('11', '十月采购对账', null), sessionRow('12', '八月盘点', '2026-09-20T02:00:00Z')],
    })

    renderAdmin()
    await screen.findByText('十月采购对账')

    // 归档/取消归档两行都有；删除只有已归档那行有——不该出现"点了才被告知不行"的动作
    expect(screen.getAllByRole('button', { name: '归档' })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: '取消归档' })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1)
  })

  it('归档：请求带 archived=true，提示里说明历史完整保留', async () => {
    const harness = installFetchMock({ sessions: [sessionRow('11', '十月采购对账', null)] })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByRole('button', { name: '归档' }))

    fireEvent.click(await screen.findByRole('button', { name: '归档' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/sessions/11/archive')),
    ).toBe(true))
    expect(harness.calls.find(call => call.method === 'PUT')?.body).toContain('"archived":true')
    expect(await screen.findByText(/历史完整保留/)).toBeInTheDocument()
  })

  it('删除要二次确认：确认框说清不可恢复，取消则不请求', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const harness = installFetchMock({ sessions: [sessionRow('12', '八月盘点', '2026-09-20T02:00:00Z')] })

    renderAdmin()
    // 首屏会连着几轮重渲染（本列表 + 助手的会话列表同时在加载）：等它落定、重新查询后再点，
    // 否则会点在即将被替换的节点上——点了等于没点。
    await settleTable()
    fireEvent.click(screen.getByRole('button', { name: '删除' }))

    // 归档可恢复、删除不能——这句话必须出现在确认框里
    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(String(confirmSpy.mock.calls[0][0])).toContain('无法恢复')
    expect(confirmSpy.mock.calls[0][0]).toContain('八月盘点')
    expect(harness.calls.some(call => call.method === 'DELETE')).toBe(false)

    confirmSpy.mockReturnValue(true)
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'DELETE' && call.url.includes('/sessions/12')),
    ).toBe(true))
  })

  it('切到「已归档」视图时走服务端 state 过滤（前端筛会让分页算错）', async () => {
    const harness = installFetchMock({ sessions: [] })

    renderAdmin()
    fireEvent.click(await screen.findByRole('button', { name: '已归档' }))

    await waitFor(() => expect(harness.calls.some(call => call.url.includes('state=archived'))).toBe(true))
  })

  it('搜索走服务端 keyword（标题过滤），不是拿回全量在前端搜', async () => {
    const harness = installFetchMock({ sessions: [sessionRow('11', '十月采购对账', null)] })

    renderAdmin()
    fireEvent.change(await screen.findByLabelText('搜索会话'), { target: { value: '采购' } })

    await waitFor(() => expect(
      harness.calls.some(call => call.url.includes('keyword=') && decodeURIComponent(call.url).includes('采购')),
    ).toBe(true))
  })

  it('重命名：弹窗里改名后发 PUT', async () => {
    const harness = installFetchMock({ sessions: [sessionRow('11', '十月采购对账', null)] })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByRole('button', { name: '重命名' }))

    const input = await screen.findByLabelText('会话名称')
    fireEvent.change(input, { target: { value: '十月采购复核' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/sessions/11/rename')),
    ).toBe(true))
    expect(harness.calls.find(call => call.url.includes('/rename'))?.body).toContain('十月采购复核')
  })

  it('后端还没升级时（列表仍是裸数组）也不白屏：归一化后照常列出', async () => {
    // 前后端滚动更新的窗口期：前端已发、后端未重启，接口还是旧形状
    const calls: FetchCall[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      calls.push({ url, method: init?.method ?? 'GET', body: null })
      if (url.includes('/assistant/sessions')) {
        return jsonResponse([sessionRow('11', '十月采购对账', null)])
      }
      return jsonResponse({})
    }))

    renderAdmin()
    await settleTable()

    expect(screen.getByText('十月采购对账')).toBeInTheDocument()
    expect(screen.getByText('共 1 个在列会话')).toBeInTheDocument()
  })

  it('分页：点第 2 页时按 offset 取页（服务端分页，不是前端切片）', async () => {
    const rows = Array.from({ length: 16 }, (_, index) => sessionRow(String(100 + index), `会话 ${index + 1}`, null))
    const harness = installFetchMock({ sessions: rows, total: 40 })

    renderAdmin()
    await screen.findByText('会话 1')

    fireEvent.click(screen.getByRole('button', { name: '下一页' }))
    await waitFor(() => expect(harness.calls.some(call => call.url.includes('offset=16'))).toBe(true))
  })
})
