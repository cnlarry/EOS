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

/** 缺行 = 代码默认值：value 为 null 的项就是"没改过"。 */
function settingItem(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    key: 'UserDailyCapYuan',
    displayName: '每人日上限（元）',
    valueType: 'decimal',
    unit: '元',
    description: '单个用户当日上限。必须大于 0。',
    defaultValue: '5',
    value: null,
    isOverridden: false,
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  }
}

function installFetchMock(options: { items?: unknown[]; problems?: string[] } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })

    if (url.includes('/admin/assistant/settings')) {
      if (method === 'GET') {
        return jsonResponse({
          items: options.items ?? [
            settingItem(),
            settingItem({
              key: 'SystemPrompt', displayName: '系统提示词', valueType: 'string', unit: null,
              defaultValue: '你是 EOS ERP 的工作助手。', value: '你是被改过的提示词。', isOverridden: true,
              updatedBy: 'admin', updatedAt: '2026-10-01T08:00:00Z',
            }),
            settingItem({
              key: 'EnableAutoDistill', displayName: '会话结束自动提炼记忆', valueType: 'bool', unit: null,
              defaultValue: 'true', value: null,
            }),
          ],
          problems: options.problems ?? [],
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

  it('保存写回该键，且未改动时保存按钮不可点', async () => {
    const harness = installFetchMock()

    renderPage()
    const input = await screen.findByLabelText('每人日上限（元）')
    // 按行定位：每一行都有自己的保存按钮（改哪条存哪条），不能全局找
    const row = input.closest('section')!
    // 草稿没变就不该能保存：避免一次毫无意义的写库
    expect(within(row).getByRole('button', { name: '保存' })).toBeDisabled()

    fireEvent.change(input, { target: { value: '8.5' } })
    fireEvent.click(within(row).getByRole('button', { name: '保存' }))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/settings/UserDailyCapYuan')),
    ).toBe(true))
    expect(harness.calls.find(call => call.method === 'PUT')?.body).toContain('8.5')

    // 保存完要重新拉一次：否则界面上还显示着旧值，看不出到底存进去没有
    await waitFor(() => expect(
      harness.calls.filter(call => call.method === 'GET' && call.url.includes('/settings')).length,
    ).toBeGreaterThanOrEqual(2))
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
      harness.calls.some(call => call.method === 'DELETE' && call.url.includes('/settings/SystemPrompt')),
    ).toBe(true))
  })

  it('布尔项点了即时保存，不用再按保存', async () => {
    const harness = installFetchMock()

    renderPage()
    fireEvent.click(await screen.findByLabelText('会话结束自动提炼记忆'))

    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'PUT' && call.url.includes('/settings/EnableAutoDistill')),
    ).toBe(true))
    expect(harness.calls.find(call => call.method === 'PUT')?.body).toContain('false')
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
