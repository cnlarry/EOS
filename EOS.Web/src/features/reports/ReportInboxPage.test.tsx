import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportInboxPage } from './ReportInboxPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const subscriptions = [
  { id: 1, userId: 'admin', moduleId: 149808, reportId: 'COP_SEND_M_149808', scheduleType: 'DAILY', runHour: 8, runMinute: 0, weekday: null, monthDay: null, enabled: true, lastRunAt: '2026-08-30T08:00:00Z', lastUpdateBy: null, lastUpdateDate: null },
]
const inbox = [
  { id: 10, userId: 'admin', moduleId: 149808, reportId: 'COP_SEND_M_149808', title: '客户送货明细（2026-08-30 08:00）', pdfPath: 'admin/149808_COP_SEND_M_149808_20260830080000.pdf', generatedAt: '2026-08-30T08:00:00Z', read: false },
]

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReportInboxPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('ReportInboxPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation((url: string) => {
      if (url.includes('/subscriptions')) return Promise.resolve(subscriptions)
      if (url.includes('/inbox')) return Promise.resolve(inbox)
      return Promise.resolve([])
    })
    apiClientMock.post.mockResolvedValue({ id: 2 })
    apiClientMock.delete.mockResolvedValue(undefined)
  })

  afterEach(() => { vi.clearAllMocks() })

  it('渲染订阅列表与收件箱', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('COP_SEND_M_149808')).toBeInTheDocument())
    expect(screen.getByText('客户送货明细（2026-08-30 08:00）')).toBeInTheDocument()
    expect(screen.getByText('未读')).toBeInTheDocument()
  })

  it('点击新增订阅展开表单并提交', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('新增订阅')).toBeInTheDocument())
    fireEvent.click(screen.getByText('新增订阅'))
    fireEvent.change(screen.getByPlaceholderText('如 COP_SEND_M_149808'), { target: { value: 'COP_ORDER_M_149805' } })
    fireEvent.click(screen.getByText('保存'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/subscriptions', expect.objectContaining({ reportId: 'COP_ORDER_M_149805', scheduleType: 'DAILY' })))
  })

  it('标记已读调用接口', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('标记已读 客户送货明细（2026-08-30 08:00）')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('标记已读 客户送货明细（2026-08-30 08:00）'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/inbox/10/read'))
  })

  it('删除订阅调用接口', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('删除订阅 COP_SEND_M_149808')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('删除订阅 COP_SEND_M_149808'))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/report-center/subscriptions/1'))
  })
})
