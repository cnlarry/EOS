import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { AuthContext, type AuthContextValue } from '../auth/authContext'
import type { NavigationItem } from '../auth/types'
import { EvalRunPage } from './EvalRunPage'

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

const LAYERS = [
  { layer: 'situation', label: '处境' },
  { layer: 'diagnosis', label: '诊断' },
  { layer: 'action', label: '动作' },
  { layer: 'config', label: '配置' },
  { layer: 'authz', label: '越权' },
  { layer: 'inference', label: '推断' },
  { layer: 'functional', label: '检索' },
  { layer: 'business', label: '回答' },
]

function run(overrides: Record<string, unknown> = {}) {
  return {
    runId: 7,
    layers: 'diagnosis,functional',
    runBy: 'admin',
    startedTime: '2026-11-01T00:10:00',
    finishedTime: '2026-11-01T00:10:02',
    status: 'SUCCEEDED',
    caseTotal: 4,
    passed: 2,
    failed: 1,
    skipped: 1,
    passRate: 0.6667,
    note: '确定性层：只判用例与当前系统是否漂移，不调用模型。',
    ...overrides,
  }
}

function result(overrides: Record<string, unknown> = {}) {
  return {
    caseId: 'FUNC-001',
    layer: 'functional',
    layerLabel: '检索',
    outcome: 'PASS',
    checkCount: 3,
    reason: null,
    fixAt: 'prompt',
    fixAtLabel: '助手设置（提示词/规则）',
    fixSuggestion: '先看用例期望是否写错',
    fixPermission: '3105',
    ...overrides,
  }
}

const REPORT = {
  run: run(),
  results: [
    result({ caseId: 'FUNC-001', outcome: 'PASS' }),
    result({
      caseId: 'DG-01',
      layer: 'diagnosis',
      layerLabel: '诊断',
      outcome: 'FAIL',
      reason: '[DIAGNOSIS_RULE_NOT_FOUND] 规则 1403/SAVE/x 在 MODULE_VALIDATION_RULE 里找不到。',
      fixAt: 'rule',
      fixAtLabel: '模块动作/校验规则',
      fixSuggestion: '核规则是否仍在库',
      fixPermission: '230104',
    }),
    result({
      caseId: 'DG-02',
      layer: 'diagnosis',
      layerLabel: '诊断',
      outcome: 'SKIP',
      reason: '未取得库内元数据快照，规则是否仍在库未判。',
    }),
    result({ caseId: 'BUSI-001', layer: 'business', layerLabel: '回答', outcome: 'PASS' }),
  ],
  byFixAt: [{ fixAt: 'rule', label: '模块动作/校验规则', count: 1 }],
}

function installFetchMock(options: { report?: typeof REPORT } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })
    if (url.includes('/admin/assistant/eval/layers')) return jsonResponse(LAYERS)
    if (/\/admin\/assistant\/eval\/runs\/\d+/.test(url)) return jsonResponse(options.report ?? REPORT)
    if (url.includes('/admin/assistant/eval/runs') && method === 'POST') {
      return jsonResponse(options.report ?? REPORT)
    }
    if (url.includes('/admin/assistant/eval/runs') && method === 'GET') return jsonResponse([run()])
    return jsonResponse({})
  })
  vi.stubGlobal('fetch', fetchMock)
  return { fetchMock, calls }
}

function renderPage(navigation: NavigationItem[] = []) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const auth: AuthContextValue = {
    bootstrap: {
      user: {
        id: 'admin', username: 'admin', displayName: '管理员', employeeId: 'E1',
        avatarText: 'A', avatarUrl: null, groupNames: '', organization: { id: 'o', name: 'O' },
      },
      permissions: [],
      navigation,
    },
    loading: false,
    login: async () => undefined,
    logout: async () => undefined,
    hasPermission: () => true,
  }
  return render(
    <QueryClientProvider client={client}>
      <AuthContext.Provider value={auth}>
        <MemoryRouter initialEntries={['/admin/assistant/eval']}>
          <ToastProvider>
            <EvalRunPage />
            <LocationProbe />
          </ToastProvider>
        </MemoryRouter>
      </AuthContext.Provider>
    </QueryClientProvider>,
  )
}

/** 断言"去改"真的跳到了目标页（无标签外壳时 useOpenTab 退化为普通导航）。 */
function LocationProbe() {
  const location = useLocation()
  return <div data-testid="location">{location.pathname}</div>
}

/** 菜单里只有一个模块：模块动作（230104）→ /admin/module-actions。 */
const NAV_WITH_RULE_PAGE: NavigationItem[] = [
  {
    id: '31', label: '工作助手', icon: '', moduleId: 31,
    children: [
      {
        id: '230104', label: '模块动作', route: '/admin/module-actions', icon: '', moduleId: 230104,
      },
    ],
  },
]

describe('EvalRunPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出逐用例结果：通过/失败/跳过分得清，失败行带原因与落点', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('DG-01')).toBeInTheDocument()
    // 结果徽标（汇总条里也有"失败"，故按徽标样式定位）
    expect(screen.getAllByText('通过').some(el => el.className.includes('text-bg-success'))).toBe(true)
    expect(screen.getAllByText('失败').some(el => el.className.includes('text-bg-danger'))).toBe(true)
    expect(screen.getAllByText('跳过').some(el => el.className.includes('text-bg-secondary'))).toBe(true)
    expect(screen.getByText(/DIAGNOSIS_RULE_NOT_FOUND/)).toBeInTheDocument()
    // 落点与所需权限都直接显示：运维要能一眼看出"该点哪里、需要什么权限"
    expect(screen.getByText('模块动作/校验规则')).toBeInTheDocument()
    expect(screen.getByText('230104')).toBeInTheDocument()
  })

  it('汇总条显示通过率与各状态计数', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('66.7%')).toBeInTheDocument()
    // 通过率与"通过 / 失败 / 跳过"同处一行：这一轮跑成什么样，看汇总条就够，不必再读一句说明
    expect(screen.getByText(/通过率/)).toBeInTheDocument()
  })

  it('点「跑一轮」默认按八层跑，并把新轮次切为当前', async () => {
    const harness = installFetchMock()

    renderPage()
    await screen.findByText('DG-01')
    fireEvent.click(screen.getByRole('button', { name: '跑一轮' }))

    await waitFor(() => {
      const post = harness.calls.find(call => call.method === 'POST' && call.url.includes('/admin/assistant/eval/runs'))
      expect(post).toBeTruthy()
      const body = JSON.parse(post!.body ?? '{}')
      expect(body.layers).toHaveLength(8)
      expect(body.layers).toContain('diagnosis')
    })
    expect(await screen.findByText(/已跑完第 7 轮/)).toBeInTheDocument()
  })

  it('取消勾选某一层后只跑剩下的层', async () => {
    const harness = installFetchMock()

    renderPage()
    await screen.findByText('DG-01')
    fireEvent.click(screen.getByRole('checkbox', { name: '层 诊断' }))
    fireEvent.click(screen.getByRole('button', { name: '跑一轮' }))

    await waitFor(() => {
      const post = harness.calls.find(call => call.method === 'POST' && call.url.includes('/admin/assistant/eval/runs'))
      expect(post).toBeTruthy()
      const body = JSON.parse(post!.body ?? '{}')
      expect(body.layers).toHaveLength(7)
      expect(body.layers).not.toContain('diagnosis')
    })
  })

  it('点落点徽标只留该落点的行，再点一次取消', async () => {
    installFetchMock()

    renderPage()
    await screen.findByText('DG-01')

    fireEvent.click(screen.getByRole('button', { name: '筛选落点 模块动作/校验规则' }))
    await waitFor(() => expect(screen.queryByText('FUNC-001')).not.toBeInTheDocument())
    expect(screen.getByText('DG-01')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '筛选落点 模块动作/校验规则' }))
    expect(await screen.findByText('FUNC-001')).toBeInTheDocument()
  })

  it('还没有跑过时给出可直接行动的提示', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url.includes('/admin/assistant/eval/layers')) return jsonResponse(LAYERS)
      return jsonResponse([])
    })
    vi.stubGlobal('fetch', fetchMock)

    renderPage()

    expect(await screen.findByText('还没有跑过评估')).toBeInTheDocument()
    expect(screen.getByText(/不调用模型、不产生费用/)).toBeInTheDocument()
  })

  it('有该模块权限时，失败行给出「去改」并能跳到对应页面', async () => {
    installFetchMock()

    renderPage(NAV_WITH_RULE_PAGE)
    await screen.findByText('DG-01')

    const jump = screen.getByRole('button', { name: '去改 DG-01' })
    fireEvent.click(jump)

    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent('/admin/module-actions')
    })
  })

  it('没有该模块权限时说明「需授权」，不给点进去会被拦的链接', async () => {
    installFetchMock()

    // 菜单里没有 230104：地址取不到，就不能假装能跳
    renderPage()

    await screen.findByText('DG-01')
    expect(screen.queryByRole('button', { name: '去改 DG-01' })).not.toBeInTheDocument()
    expect(screen.getByText('需授权')).toBeInTheDocument()
    // 通过/跳过的行不给入口：它们不需要改
    expect(screen.queryByRole('button', { name: '去改 FUNC-001' })).not.toBeInTheDocument()
  })
})
