import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportCenterPage } from './ReportCenterPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const catalog = [
  { moduleId: 149801, moduleDesc: '客户资料明细', domainDesc: '销售报表查询', reportId: 'R149801', reportName: '客户资料明细', isDefault: true, favorite: true, sortIndex: 1, lastRunAt: '2026-08-30T10:00:00Z' },
  { moduleId: 149802, moduleDesc: '客户计价明细', domainDesc: '销售报表查询', reportId: 'R149802', reportName: '客户计价明细', isDefault: false, favorite: true, sortIndex: 2, lastRunAt: null },
  { moduleId: 169801, moduleDesc: '供应商资料明细', domainDesc: '采购报表查询', reportId: 'R169801', reportName: '供应商资料明细', isDefault: true, favorite: false, sortIndex: 0, lastRunAt: '2026-08-29T09:00:00Z' },
]

function renderPage() {
  return renderWithProviders(
      <MemoryRouter>
        <ReportCenterPage />
      </MemoryRouter>
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
    await waitFor(() => expect(screen.getAllByText('销售报表查询').length).toBeGreaterThan(0))
    expect(screen.getAllByText('采购报表查询').length).toBeGreaterThan(0)
    expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0)
    expect(screen.getAllByText('供应商资料明细').length).toBeGreaterThan(0)
  })

  it('点击报表行跳转到对应查看器并记录最近使用', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('客户计价明细').length).toBeGreaterThan(0))
    fireEvent.click(screen.getAllByText('客户计价明细')[0])
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
    await waitFor(() => expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0))
    fireEvent.change(screen.getByLabelText('搜索报表'), { target: { value: '供应商' } })
    expect(screen.queryAllByText('客户资料明细')).toHaveLength(0)
    expect(screen.getAllByText('供应商资料明细').length).toBeGreaterThan(0)
  })

  it('仅显示收藏过滤', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0))
    fireEvent.click(screen.getByLabelText('仅显示收藏'))
    expect(screen.queryAllByText('供应商资料明细')).toHaveLength(0)
    expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0)
  })

  it('最近使用区块展示最近运行报表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('最近使用')).toBeInTheDocument())
    expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0)
    expect(screen.getAllByText('供应商资料明细').length).toBeGreaterThan(0)
  })

  it('收藏上移提交重排', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('上移 客户计价明细')).toBeInTheDocument())
    fireEvent.click(screen.getByLabelText('上移 客户计价明细'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/reorder', expect.any(Object)))
    const lastCall = apiClientMock.post.mock.calls[apiClientMock.post.mock.calls.length - 1]
    expect(lastCall[0]).toBe('/report-center/reorder')
    expect(lastCall[1].items).toHaveLength(2)
    expect(lastCall[1].items[0]).toMatchObject({ moduleId: 149802, reportId: 'R149802' })
    expect(lastCall[1].items[1]).toMatchObject({ moduleId: 149801, reportId: 'R149801' })
  })
})
