import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportCenterPage } from './ReportCenterPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const catalog = [
  { moduleId: 149801, moduleDesc: '客户资料明细', domainDesc: '销售报表查询', reportId: 'R149801', reportName: '客户资料明细', isDefault: true, favorite: true, sortIndex: 1, lastRunAt: '2026-08-30T10:00:00Z' },
  { moduleId: 149802, moduleDesc: '客户计价明细', domainDesc: '销售报表查询', reportId: 'R149802', reportName: '客户计价明细', isDefault: false, favorite: false, sortIndex: 0, lastRunAt: null },
  { moduleId: 169801, moduleDesc: '供应商资料明细', domainDesc: '采购报表查询', reportId: 'R169801', reportName: '供应商资料明细', isDefault: true, favorite: false, sortIndex: 0, lastRunAt: '2026-08-29T09:00:00Z' },
]

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ReportCenterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('ReportCenterPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(catalog)
    apiClientMock.post.mockResolvedValue(undefined)
  })

  afterEach(() => { vi.clearAllMocks() })

  it('按业务域分组平铺渲染全部报表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('销售报表查询')).toBeInTheDocument())
    expect(screen.getByText('采购报表查询')).toBeInTheDocument()
    expect(screen.getByText('客户资料明细')).toBeInTheDocument()
    expect(screen.getByText('供应商资料明细')).toBeInTheDocument()
  })

  it('点击报表行跳转到对应查看器并记录最近使用', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('客户计价明细')).toBeInTheDocument())
    fireEvent.click(screen.getByText('客户计价明细'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/favorite', expect.objectContaining({ moduleId: 149802, reportId: 'R149802', lastRunAt: expect.any(String) })))
  })

  it('星标切换收藏状态', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('收藏 供应商资料明细')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('收藏 供应商资料明细'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/favorite', expect.objectContaining({ moduleId: 169801, reportId: 'R169801', favorite: true })))
  })

  it('搜索过滤报表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('客户资料明细')).toBeInTheDocument())
    fireEvent.change(screen.getByLabelText('搜索报表'), { target: { value: '供应商' } })
    expect(screen.queryByText('客户资料明细')).not.toBeInTheDocument()
    expect(screen.getByText('供应商资料明细')).toBeInTheDocument()
  })

  it('仅显示收藏过滤', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('客户资料明细')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('仅显示收藏'))
    expect(screen.queryByText('供应商资料明细')).not.toBeInTheDocument()
    expect(screen.getByText('客户资料明细')).toBeInTheDocument()
  })
})
