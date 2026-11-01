import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { MetricCatalogPage } from './MetricCatalogPage'

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

/** 两条口径：一条已确认（不可删）、一条候选（可删可确认）。 */
function metric(overrides: Record<string, unknown> = {}) {
  return {
    metricId: 'sales_amount',
    metricName: '营业额',
    definition: 'SUM(AMOUNT_TAX)',
    sourceTable: 'COP_ORDER_D',
    dimensionKeys: 'ORDER_DATE,CLIENT_ID',
    domain: '销售',
    version: 1,
    confirmStatus: 'CONFIRMED',
    rowFilter: '{"table":"COP_ORDER_M","on":["ORDER_TYPE","ORDER_NO"],"conditions":[{"column":"APPROVED","op":"=","value":true}]}',
    description: null,
    createdBy: 'MIGRATION',
    modifiedTime: null,
    approvedBy: 'admin',
    approvedTime: '2026-10-01T08:00:00Z',
    ...overrides,
  }
}

function installFetchMock(options: { rows?: unknown[]; failSave?: boolean } = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null })
    if (url.includes('/report-admin/metrics')) {
      if (method === 'GET') return jsonResponse(options.rows ?? [metric(), metric({ metricId: 'draft_amount', metricName: '草稿口径', confirmStatus: 'CANDIDATE' })])
      if (options.failSave) {
        return new Response(JSON.stringify({ code: 'INVALID_ARGUMENT', message: '口径校验未通过：维度列不存在' }), {
          status: 400,
          headers: { 'Content-Type': 'application/json' },
        })
      }
      if (method === 'DELETE') return new Response(null, { status: 204 })
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
      <MemoryRouter initialEntries={['/admin/metrics']}>
        <ToastProvider>
          <MetricCatalogPage />
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('MetricCatalogPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出结果：已确认与候选分得清，已确认口径不可删', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('sales_amount')).toBeInTheDocument()
    expect(screen.getByText('已确认')).toBeInTheDocument()
    expect(screen.getByText('候选')).toBeInTheDocument()
    // 已确认口径可能被报表与结论引用：删除必须禁用（先改回候选是一次显式决策）
    expect(screen.getByRole('button', { name: '删除 sales_amount' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '删除 draft_amount' })).toBeEnabled()
  })

  it('确认候选口径：PUT 到同一条并带 CONFIRMED', async () => {
    const harness = installFetchMock()

    renderPage()
    await screen.findByText('draft_amount')
    fireEvent.click(screen.getByRole('button', { name: '确认 draft_amount' }))

    await waitFor(() => {
      const put = harness.calls.find(call =>
        call.method === 'PUT' && call.url.includes('/report-admin/metrics/draft_amount'))
      expect(put).toBeTruthy()
      expect(put!.body ?? '').toContain('CONFIRMED')
    })
  })

  it('编辑保存：标识只读，改定义后 PUT 回原标识', async () => {
    const harness = installFetchMock()

    renderPage()
    await screen.findByText('sales_amount')
    fireEvent.click(screen.getByRole('button', { name: '编辑 sales_amount' }))

    const idInput = await screen.findByLabelText('口径标识')
    expect(idInput).toBeDisabled()
    fireEvent.change(screen.getByLabelText('口径定义'), { target: { value: 'SUM(AMOUNT)' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      const put = harness.calls.find(call =>
        call.method === 'PUT' && call.url.includes('/report-admin/metrics/sales_amount'))
      expect(put).toBeTruthy()
      expect(put!.body ?? '').toContain('SUM(AMOUNT)')
    })
  })

  it('保存失败时把服务端原因显示在弹窗里，而不是关掉弹窗', async () => {
    installFetchMock({ failSave: true })

    renderPage()
    await screen.findByText('sales_amount')
    fireEvent.click(screen.getByRole('button', { name: '新增口径' }))
    fireEvent.change(await screen.findByLabelText('口径标识'), { target: { value: 'new_metric' } })
    fireEvent.change(screen.getByLabelText('口径名称'), { target: { value: '新口径' } })
    fireEvent.change(screen.getByLabelText('口径定义'), { target: { value: 'SUM(QTY)' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/口径校验未通过/)
    // 弹窗还在：失败时不该把用户填的内容一起丢掉
    expect(within(screen.getByRole('dialog')).getByLabelText('口径名称')).toHaveValue('新口径')
  })
})
