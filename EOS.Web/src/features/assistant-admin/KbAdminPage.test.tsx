import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider } from '../../components/ui/Toast'
import { KbAdminPage } from './KbAdminPage'

interface FetchCall {
  url: string
  method: string
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

function installFetchMock(options: {
  collections?: unknown[]
  documents?: unknown[]
} = {}) {
  const calls: FetchCall[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input.toString()
    calls.push({ url, method: init?.method ?? 'GET' })
    if (url.includes('/admin/assistant/kb/collections')) {
      return jsonResponse(options.collections ?? [{ collectionId: 'kb_general', title: '通用知识库', embeddingModel: 'bge-m3', dimension: 1024, defaultVisibility: 'ALL' }])
    }
    if (url.includes('/admin/assistant/kb/documents')) {
      if ((init?.method ?? 'GET') === 'DELETE') return new Response(null, { status: 204 })
      return jsonResponse(options.documents ?? [{
        docId: '7', collectionId: 'kb_general', title: '开发手册-规范',
        sourceUri: 'docs/guide/60.md', visibility: 'CONSULTANT', status: 'active', version: 2,
      }])
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
      <MemoryRouter initialEntries={['/admin/assistant/kb']}>
        <ToastProvider>
          <KbAdminPage />
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('KbAdminPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('列出文档，并显眼标明"检索与入库待嵌入接线"', async () => {
    installFetchMock()

    renderPage()

    expect(await screen.findByText('开发手册-规范')).toBeInTheDocument()
    // 可见性显示人话而不是枚举名
    expect(screen.getByText('实施顾问')).toBeInTheDocument()
    expect(screen.getByText('v2')).toBeInTheDocument()
    // 这句话必须显眼：否则管理员会以为"库里是空的"是自己配错了
    expect(screen.getByText(/检索与入库待嵌入接线/)).toBeInTheDocument()
    expect(screen.getByText(/KB_EMBEDDING_NOT_CONFIGURED/)).toBeInTheDocument()
  })

  it('删除要二次确认，确认后才发 DELETE', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const harness = installFetchMock()

    renderPage()
    await screen.findByText('开发手册-规范')
    fireEvent.click(screen.getByRole('button', { name: '删除' }))

    // 确认框要说清"行保留、向量移除"——删除不是把行抹掉
    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(String(confirmSpy.mock.calls[0][0])).toContain('向量会被同步移除')
    expect(harness.calls.some(call => call.method === 'DELETE')).toBe(false)

    confirmSpy.mockReturnValue(true)
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    await waitFor(() => expect(
      harness.calls.some(call => call.method === 'DELETE' && call.url.includes('/kb/documents/7')),
    ).toBe(true))
  })
})
