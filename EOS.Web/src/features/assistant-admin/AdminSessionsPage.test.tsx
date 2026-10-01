import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { AdminSessionsPage } from './AdminSessionsPage'

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

function sessionRow(
  id: string,
  userId: string,
  title: string,
  archivedAt: string | null,
  extra: { employeeName?: string | null; messageTokens?: number } = {},
) {
  return {
    id, userId, title,
    createdAt: '2026-09-30T10:00:00Z', lastActiveAt: '2026-10-01T09:00:00Z',
    archivedAt, messageCount: 4,
    employeeName: extra.employeeName ?? null,
    messageTokens: extra.messageTokens ?? 0,
  }
}

function installFetchMock(options: { sessions?: unknown[]; total?: number; owners?: string[] } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })

    if (url.includes('/admin/assistant/sessions/owners')) {
      return jsonResponse(options.owners ?? ['admin', 'zhangsan'])
    }
    if (url.includes('/admin/assistant/sessions')) {
      if (method === 'PUT' || method === 'DELETE') return new Response(null, { status: 204 })
      const items = options.sessions ?? []
      return jsonResponse({ items, total: options.total ?? items.length })
    }
    return jsonResponse({})
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, calls }
}

/**
 * 等列表首屏那几轮重渲染落定，再查询/点击行内按钮。
 *
 * `findByRole` 可能在节点刚插入时就把元素交出来，而 React 紧接着会替换这批行内节点——
 * 拿旧节点去点，事件走不到处理器（点了等于没点，现象与"功能没做"一模一样）。
 *
 * 这里等的是"归属用户"下拉而不是表头：**空列表时表格没有表头**，等表头会漏掉空数据用例。
 */
async function settleTable() {
  await screen.findByLabelText('按归属用户筛选')
  await new Promise(resolve => setTimeout(resolve, 0))
}

function renderAdmin() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/admin/assistant/sessions']}>
        <ToastProvider>
          <AdminSessionsPage />
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('AdminSessionsPage', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('跨用户列出会话：归属用户与消息数都看得见', async () => {
    installFetchMock({
      sessions: [
        sessionRow('11', 'zhangsan', '十月采购对账', null, { employeeName: '张三', messageTokens: 12345 }),
        sessionRow('12', 'lisi', '八月盘点', '2026-09-20T02:00:00Z', { employeeName: '李四', messageTokens: 678 }),
        // 账号不在 SYSDL 里（例如测试账号）：姓名取不到，界面要如实说，不假装它有姓名
        sessionRow('13', 'eosdev-assistant-test-a', '测试会话', null),
      ],
      owners: ['lisi', 'zhangsan'],
    })

    renderAdmin()
    await settleTable()

    // 归属用户要显示**姓名**（USER_ID 是账号编号，看列表的人要的是"这是谁的会话"），
    // 账号作为次行小字保留给排查用
    expect(screen.getByText('张三')).toBeInTheDocument()
    expect(screen.getByText('李四')).toBeInTheDocument()
    expect(screen.getByText('（未登记用户）')).toBeInTheDocument()

    // Tokens 列：千分位显示，看得出量级
    expect(screen.getByText('12,345')).toBeInTheDocument()
    expect(screen.getByText('678')).toBeInTheDocument()

    // 管理侧的关键能力：同一条列表里同时出现不同用户的会话。
    // 账号在"按归属用户筛选"的 option 与行内次行各有一份，所以按"至少两处"来断言
    expect(screen.getAllByText('zhangsan').length).toBeGreaterThanOrEqual(2)
    expect(screen.getAllByText('lisi').length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText('十月采购对账')).toBeInTheDocument()
    // 「已归档」既是筛选按钮也是行上的状态徽标
    expect(screen.getAllByText('已归档')).toHaveLength(2)
    // 表头要有"归属用户"与"消息"两列
    const heads = screen.getAllByRole('columnheader').map(node => node.textContent ?? '')
    expect(heads.some(text => text.includes('归属用户'))).toBe(true)
    expect(heads.some(text => text.includes('消息'))).toBe(true)
    expect(heads.some(text => text.includes('Tokens'))).toBe(true)
  })

  it('删除只对已归档开放：在列的行不给删除按钮', async () => {
    installFetchMock({
      sessions: [
        sessionRow('11', 'zhangsan', '十月采购对账', null),
        sessionRow('12', 'lisi', '八月盘点', '2026-09-20T02:00:00Z'),
      ],
    })

    renderAdmin()
    await settleTable()

    expect(screen.getAllByRole('button', { name: '归档' })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: '取消归档' })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1)
  })

  it('归档别人的会话：请求带 archived=true，提示说明历史保留', async () => {
    const harness = installFetchMock({ sessions: [sessionRow('11', 'zhangsan', '十月采购对账', null)] })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByRole('button', { name: '归档' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/sessions/11/archive')),
    ).toBe(true))
    expect(harness.calls.find(call => call.method === 'PUT')?.body).toContain('"archived":true')
    expect(await screen.findByText(/历史完整保留/)).toBeInTheDocument()
  })

  it('删除要二次确认，且确认框里点明这是谁的会话、不可恢复', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const harness = installFetchMock({ sessions: [sessionRow('12', 'lisi', '八月盘点', '2026-09-20T02:00:00Z')] })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByRole('button', { name: '删除' }))

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    const message = String(confirmSpy.mock.calls[0][0])
    expect(message).toContain('无法恢复')
    // 管理侧删的是**别人的**会话：提示里必须写清是谁的
    expect(message).toContain('lisi')
    expect(message).toContain('八月盘点')
    expect(harness.calls.some(call => call.method === 'DELETE')).toBe(false)

    confirmSpy.mockReturnValue(true)
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'DELETE' && call.url.includes('/sessions/12')),
    ).toBe(true))
  })

  it('按归属用户筛选走服务端 owner 参数，不是前端筛', async () => {
    const harness = installFetchMock({ sessions: [], owners: ['admin', 'zhangsan'] })

    renderAdmin()
    await settleTable()
    fireEvent.change(screen.getByLabelText('按归属用户筛选'), { target: { value: 'zhangsan' } })

    await waitFor(() => expect(
      harness.calls.some(call => call.url.includes('owner=zhangsan')),
    ).toBe(true))
  })

  it('切到「已归档」视图时走服务端 state 过滤', async () => {
    const harness = installFetchMock({ sessions: [] })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByRole('button', { name: '已归档' }))

    await waitFor(() => expect(harness.calls.some(call => call.url.includes('state=archived'))).toBe(true))
  })

  it('首列是可勾选的复选框，勾选后出现批量操作条', async () => {
    installFetchMock({
      sessions: [sessionRow('11', 'zhangsan', '十月采购对账', null), sessionRow('12', 'lisi', '八月盘点', null)],
    })

    renderAdmin()
    await settleTable()

    const first = screen.getByLabelText('选择 十月采购对账')
    expect(first).not.toBeChecked()
    fireEvent.click(first)
    expect(first).toBeChecked()
    expect(screen.getByText(/已选 1 项/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /批量归档/ })).toBeInTheDocument()

    // 表头全选：本页两行一起勾上
    fireEvent.click(screen.getByLabelText('全选本页'))
    expect(screen.getByText(/已选 2 项/)).toBeInTheDocument()
  })

  it('批量归档：对选中的每条各发一次 PUT', async () => {
    const harness = installFetchMock({
      sessions: [sessionRow('11', 'zhangsan', '甲', null), sessionRow('12', 'lisi', '乙', null)],
    })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByLabelText('全选本页'))
    fireEvent.click(screen.getByRole('button', { name: /批量归档/ }))

    await waitFor(() => {
      const targets = harness.calls
        .filter(call => call.method === 'PUT' && call.url.includes('/archive'))
        .map(call => (call.url.includes('/sessions/11/archive') ? '11' : '12'))
        .sort()
      expect(targets).toEqual(['11', '12'])
    })
  })

  it('批量删除只对已归档生效：在列的跳过，并在确认框里说清', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const harness = installFetchMock({
      sessions: [
        sessionRow('11', 'zhangsan', '在列的', null),
        sessionRow('12', 'lisi', '已归档的', '2026-09-20T02:00:00Z'),
      ],
    })

    renderAdmin()
    await settleTable()
    fireEvent.click(screen.getByLabelText('全选本页'))
    fireEvent.click(screen.getByRole('button', { name: /批量删除/ }))

    const message = String(confirmSpy.mock.calls[0][0])
    // 只算可删的那个，并说明另一个被跳过——不能让人以为两条都删了
    expect(message).toContain('1 个会话')
    expect(message).toContain('还在「在列」')
    expect(harness.calls.some(call => call.method === 'DELETE')).toBe(false)
  })

  it('排序由服务端做：列表请求带排序参数（前端只排当前页是错的）', async () => {
    const harness = installFetchMock({ sessions: [sessionRow('11', 'zhangsan', '甲', null)] })

    renderAdmin()
    await settleTable()

    const listCall = harness.calls.find(call => call.url.includes('/admin/assistant/sessions?'))
    expect(listCall?.url).toContain('sortBy=lastActive')
    expect(listCall?.url).toContain('sortDir=desc')
  })
})
