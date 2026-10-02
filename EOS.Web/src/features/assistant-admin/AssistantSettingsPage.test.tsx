import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { AssistantSettingsPage } from './AssistantSettingsPage'

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

/**
 * 缺行 = 代码默认值：value 为 null 的项就是"没改过"。
 *
 * 键用 SYSSS 里的**大写键**（`USER_DAILY_CAP_YUAN`），与参数目录同名——界面按它做少数几处特判
 * （如提示词渲染成多行框），用旧的小驼峰键会让那些分支静默走不到。
 */
function settingItem(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    key: 'USER_DAILY_CAP_YUAN',
    displayName: '每人日上限（元）',
    groupCode: 'GOVERNANCE',
    groupLabel: '成本与熔断',
    seqNo: 10,
    valueType: 'decimal',
    unit: '元',
    description: '单个用户当日上限。必须大于 0。',
    rangeHint: '大于 0',
    defaultValue: '5',
    // 读取方非空：否则页头会多出一枚"目前无读取方"的警示徽标，把按徽标计数的断言带偏
    consumers: ['UserDailyCapYuan'],
    value: null,
    isOverridden: false,
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  }
}

/** 分组顺序由服务端给；空分组不渲染（目录按批生长，先占号的域此刻可能还没有参数行）。 */
const SETTINGS_GROUPS = [{ code: 'GOVERNANCE', label: '成本与熔断', seq: 50 }]

function installFetchMock(options: { items?: unknown[]; problems?: string[]; failKeys?: string[] } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })

    if (url.includes('/admin/assistant/settings/scopes')) {
      // 覆盖区块另拉一份清单；给它空清单，免得页面上的设置项被当成"覆盖行"渲染出来
      return jsonResponse({ items: [], scopable: [] })
    }
    if (url.includes('/admin/assistant/settings')) {
      if (method === 'GET') {
        return jsonResponse({
          groups: SETTINGS_GROUPS,
          items: options.items ?? [
            settingItem(),
            settingItem({
              key: 'SYSTEM_PROMPT', displayName: '系统提示词', valueType: 'string', unit: null,
              rangeHint: '', consumers: ['SystemPrompt'],
              defaultValue: '你是 EOS ERP 的工作助手。', value: '你是被改过的提示词。', isOverridden: true,
              updatedBy: 'admin', updatedAt: '2026-10-01T08:00:00Z',
            }),
            settingItem({
              key: 'MEM_ENABLE_AUTO_DISTILL', displayName: '会话结束自动提炼记忆', valueType: 'bit', unit: null,
              seqNo: 20, rangeHint: '', consumers: ['EnableAutoDistill'],
              defaultValue: '1', value: null,
            }),
          ],
          problems: options.problems ?? [],
        })
      }
      // 指定某个键写入失败：用来钉住"没存进去的那一项必须留在待保存里"
      if ((options.failKeys ?? []).some(key => url.includes(`/settings/${key}`))) {
        return new Response(JSON.stringify({ code: 'INVALID_ARGUMENT', message: '值不合法。' }), {
          status: 400,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      return new Response(null, { status: 204 })
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
      <MemoryRouter initialEntries={['/admin/assistant/settings']}>
        <ToastProvider>
          <AssistantSettingsPage />
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('AssistantSettingsPage', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出设置项，并区分"默认值"与"已改过"', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('每人日上限（元）')).toBeInTheDocument()
    expect(screen.getByText('系统提示词')).toBeInTheDocument()
    // 改过的项要标出来，否则看不出"哪些不是默认的了"，也就无从判断能不能恢复
    expect(screen.getByText('已改过')).toBeInTheDocument()
    // 3 项里有 1 项被改过，其余两项应当是"默认值"徽标。
    // 「默认值」三个字同时出现在徽标与"默认值 xxx"提示里，所以按徽标类名数，别按文本数。
    expect(document.querySelectorAll('.badge.bg-secondary-lt')).toHaveLength(2)
    expect(screen.getByText(/共 3 项，其中/)).toBeInTheDocument()
    // 改过的项要显示是谁改的、什么时候改的——出问题时这是唯一的线索
    expect(screen.getByText(/上次由 admin 于/)).toBeInTheDocument()
  })

  it('保存是整页一个动作：改完点右上角那颗保存，才写回该键', async () => {
    const harness = installFetchMock()

    renderPage()
    const input = await screen.findByLabelText('每人日上限（元）')
    const row = input.closest('section')!
    // 行内不再有自己的"保存"按钮——保存只有右上角那一个（与"刷新"并排）
    expect(within(row).queryByRole('button', { name: /^保存/ })).toBeNull()
    // 没有改动时不可点：避免一次毫无意义的写库
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()

    fireEvent.change(input, { target: { value: '8.5' } })
    // 改过哪一项要能在行里看出来，否则一排输入框里不知道动过哪些
    expect(within(row).getByText('未保存')).toBeInTheDocument()
    // 按钮上的数字就是"待保存几项"
    fireEvent.click(screen.getByRole('button', { name: '保存（1）' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/settings/USER_DAILY_CAP_YUAN')),
    ).toBe(true))
    expect(harness.calls.find(call => call.method === 'PUT')?.body).toContain('8.5')

    // 保存完要重新拉一次：否则界面上还显示着旧值，看不出到底存进去没有
    await waitFor(() => expect(
      harness.calls.filter(call => call.method === 'GET' && call.url.includes('/settings')).length,
    ).toBeGreaterThanOrEqual(2))
    // 存进去了就不该再挂着"未保存"
    await waitFor(() => expect(screen.queryByText('未保存')).toBeNull())
  })

  it('一次改多项，点一次保存全部写回', async () => {
    const harness = installFetchMock()

    renderPage()
    const cap = await screen.findByLabelText('每人日上限（元）')
    fireEvent.change(cap, { target: { value: '9' } })
    fireEvent.click(screen.getByLabelText('会话结束自动提炼记忆'))

    fireEvent.click(screen.getByRole('button', { name: '保存（2）' }))

    await waitFor(() => {
      const written = harness.calls.filter(call => call.method === 'PUT').map(call => call.url)
      expect(written.some(url => url.includes('/settings/USER_DAILY_CAP_YUAN'))).toBe(true)
      expect(written.some(url => url.includes('/settings/MEM_ENABLE_AUTO_DISTILL'))).toBe(true)
    })
  })

  it('没存进去的那一项要留在待保存里——失败不能被当成成功一起清掉', async () => {
    const harness = installFetchMock({ failKeys: ['USER_DAILY_CAP_YUAN'] })

    renderPage()
    const cap = await screen.findByLabelText('每人日上限（元）')
    fireEvent.change(cap, { target: { value: '9' } })
    fireEvent.click(screen.getByLabelText('会话结束自动提炼记忆'))
    fireEvent.click(screen.getByRole('button', { name: '保存（2）' }))

    await waitFor(() => expect(harness.calls.filter(call => call.method === 'PUT')).toHaveLength(2))
    // 存进去的那一项归位、失败的那一项仍标着"未保存"（草稿留着，用户改完能直接重试）
    await waitFor(() => expect(screen.getByRole('button', { name: '保存（1）' })).toBeEnabled())
    expect(document.querySelectorAll('.badge.bg-warning-lt')).toHaveLength(1)
  })

  it('设置项放在自己的滚动容器里——它们比一屏高，不能把下方项裁掉', async () => {
    installFetchMock()

    renderPage()
    await screen.findByText('每人日上限（元）')

    const body = document.querySelector('.erp-assistant-settings-body') as HTMLElement | null
    expect(body).not.toBeNull()
    // 命令栏（保存/刷新）与页头必须在滚动容器**之外**：否则往下滚就找不着保存按钮了
    expect(body!.querySelector('.erp-list-command-bar')).toBeNull()
    expect(body!.querySelector('.card-header')).toBeNull()
    expect(within(body!).getByText('系统提示词')).toBeInTheDocument()
    expect(within(body!).getByText('会话结束自动提炼记忆')).toBeInTheDocument()
  })

  it('恢复默认走 DELETE（服务端删掉覆盖行，而不是存空串）', async () => {
    const harness = installFetchMock()

    renderPage()
    // 未改过的项也渲染这个按钮（布局稳定，也说明"现在就是默认值"），但只有改过的能点
    const resetButtons = await screen.findAllByRole('button', { name: '恢复默认' })
    const enabled = resetButtons.filter(button => !button.hasAttribute('disabled'))
    expect(enabled).toHaveLength(1)

    fireEvent.click(enabled[0])

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'DELETE' && call.url.includes('/settings/SYSTEM_PROMPT')),
    ).toBe(true))
  })

  it('布尔项也走同一颗保存按钮，不再点了就写库', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByLabelText('会话结束自动提炼记忆'))

    // 点了只是改了草稿：这一页的约定是"改完一起保存"
    expect(harness.calls.some(call => call.method === 'PUT')).toBe(false)
    expect(screen.getByRole('button', { name: '保存（1）' })).toBeEnabled()

    fireEvent.click(screen.getByRole('button', { name: '保存（1）' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/settings/MEM_ENABLE_AUTO_DISTILL')),
    ).toBe(true))
    // 关掉 = 写 '0'（SYSSS 的 bit 就是 1/0，不是 true/false）
    const body = harness.calls.find(call => call.method === 'PUT')?.body ?? ''
    expect((JSON.parse(body) as { value: string }).value).toBe('0')
  })

  it('库里的值解析不了时要当场说出来——否则界面显示着一个其实没生效的值', async () => {
    installFetchMock({
      problems: ['每人日上限（元）：“abc”不是大于 0 的数字（日上限必须大于 0）（已忽略，仍用默认值 5）'],
    })

    renderPage()

    expect(await screen.findByText(/项设置没有生效/)).toBeInTheDocument()
    expect(screen.getByText(/不是大于 0 的数字/)).toBeInTheDocument()
  })
})
