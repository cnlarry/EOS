import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportInboxPage } from './ReportInboxPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const subscriptions = [
  { id: 1, userId: 'admin', moduleId: 149808, reportId: 'COP_SEND_M_149808', scheduleType: 'DAILY', runHour: 8, runMinute: 0, weekday: null, monthDay: null, enabled: true, lastRunAt: '2026-08-30T08:00:00Z', lastUpdateBy: null, lastUpdateDate: null },
]
const inbox = [
  { id: 10, userId: 'admin', moduleId: 149808, reportId: 'COP_SEND_M_149808', title: '客户送货明细（2026-08-30 08:00）', pdfPath: 'admin/149808_COP_SEND_M_149808_20260830080000.pdf', generatedAt: '2026-08-30T08:00:00Z', read: false },
]

function renderPage() {
  return renderWithProviders(
    <MemoryRouter>
      <ReportInboxPage />
    </MemoryRouter>,
  )
}

function switchToSubscriptions() {
  fireEvent.click(screen.getByRole('tab', { name: /订阅/ }))
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
    apiClientMock.getFile.mockResolvedValue(new Blob(['pdf'], { type: 'application/pdf' }))
  })

  afterEach(() => { vi.clearAllMocks() })

  it('默认展示收件箱页签，切到订阅页签展示订阅列表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('客户送货明细（2026-08-30 08:00）')).toBeInTheDocument())
    expect(screen.getByText('未读')).toBeInTheDocument()
    // 订阅列表不在当前页签内
    expect(screen.queryByText('COP_SEND_M_149808')).not.toBeInTheDocument()

    switchToSubscriptions()
    expect(await screen.findByText('COP_SEND_M_149808')).toBeInTheDocument()
  })

  it('点击新增订阅展开表单并提交', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('tab', { name: /订阅/ })).toBeInTheDocument())
    switchToSubscriptions()
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

  it('下载 PDF 经统一传输层取文件（而非 window.open）', async () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null)
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('下载 客户送货明细（2026-08-30 08:00）')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('下载 客户送货明细（2026-08-30 08:00）'))
    await waitFor(() => expect(apiClientMock.getFile).toHaveBeenCalledWith('/report-center/inbox/10/download'))
    expect(open).not.toHaveBeenCalled()
    open.mockRestore()
  })

  it('删除订阅调用接口', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('tab', { name: /订阅/ })).toBeInTheDocument())
    switchToSubscriptions()
    await waitFor(() => expect(screen.getByLabelText('删除订阅 COP_SEND_M_149808')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('删除订阅 COP_SEND_M_149808'))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/report-center/subscriptions/1'))
  })
})
