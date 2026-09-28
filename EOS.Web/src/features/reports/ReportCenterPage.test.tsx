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
    </MemoryRouter>,
  )
}

describe('ReportCenterPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(catalog)
    apiClientMock.post.mockResolvedValue(undefined)
  })

  afterEach(() => { vi.clearAllMocks() })

  it('三区结构：收藏磁贴 + 最近使用 + 按业务域折叠的全部报表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByRole('heading', { name: '我的收藏' })).toBeInTheDocument())
    expect(screen.getByRole('heading', { name: '最近使用' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '全部报表' })).toBeInTheDocument()
    // 收藏项在磁贴区直接可见
    expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0)
    expect(screen.getAllByText('客户计价明细').length).toBeGreaterThan(0)
    // 全部报表默认折叠成业务域索引：域标题在，域内报表的编号只出现在展开后的分组行里
    expect(screen.getByTitle('展开 采购报表查询')).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('R169801')).not.toBeInTheDocument()
  })

  it('展开业务域后显示域内报表', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByTitle('展开 采购报表查询')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('展开 采购报表查询'))
    expect(screen.getByText('R169801')).toBeInTheDocument()
    expect(screen.getAllByText('供应商资料明细').length).toBeGreaterThan(1)
  })

  it('点击收藏磁贴跳转查看器并登记最近使用', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByTitle('打开 客户计价明细')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('打开 客户计价明细'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/touch', { moduleId: 149802, reportId: 'R149802' }))
  })

  it('星标切换收藏状态（新收藏追加到收藏末尾）', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByTitle('展开 采购报表查询')).toBeInTheDocument())
    fireEvent.click(screen.getByTitle('展开 采购报表查询'))
    fireEvent.click(screen.getByLabelText('收藏 供应商资料明细'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/favorite', expect.objectContaining({ moduleId: 169801, reportId: 'R169801', favorite: true, sortIndex: 3 })))
  })

  it('搜索时只呈现过滤结果（分组自动展开）', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0))
    fireEvent.change(screen.getByLabelText('搜索报表'), { target: { value: '供应商' } })
    expect(screen.queryAllByText('客户资料明细')).toHaveLength(0)
    expect(screen.getByText('供应商资料明细')).toBeInTheDocument()
  })

  it('仅显示收藏过滤', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0))
    fireEvent.click(screen.getByLabelText('仅显示收藏'))
    expect(screen.queryAllByText('供应商资料明细')).toHaveLength(0)
    expect(screen.getAllByText('客户资料明细').length).toBeGreaterThan(0)
  })

  it('「编辑排序」模式下上移收藏并提交重排', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('编辑排序')).toBeInTheDocument())
    fireEvent.click(screen.getByText('编辑排序'))
    fireEvent.click(screen.getByLabelText('上移 客户计价明细'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith('/report-center/reorder', expect.any(Object)))
    const lastCall = apiClientMock.post.mock.calls[apiClientMock.post.mock.calls.length - 1]
    expect(lastCall[0]).toBe('/report-center/reorder')
    expect(lastCall[1].items).toHaveLength(2)
    expect(lastCall[1].items[0]).toMatchObject({ moduleId: 149802, reportId: 'R149802' })
    expect(lastCall[1].items[1]).toMatchObject({ moduleId: 149801, reportId: 'R149801' })
  })

  it('收藏顺序在目录返回顺序之外独立生效（跨业务域）', async () => {
    // 目录接口按业务域优先返回；收藏磁贴必须按 SORT_IDX 排，否则跨域的上移/下移点了没反应
    apiClientMock.get.mockResolvedValue([
      { moduleId: 169801, moduleDesc: '供应商资料明细', domainDesc: '采购报表查询', reportId: 'R169801', reportName: '供应商资料明细', isDefault: false, favorite: true, sortIndex: 2, lastRunAt: null },
      { moduleId: 149801, moduleDesc: '客户资料明细', domainDesc: '销售报表查询', reportId: 'R149801', reportName: '客户资料明细', isDefault: false, favorite: true, sortIndex: 1, lastRunAt: null },
    ])
    renderPage()
    await waitFor(() => expect(screen.getByText('编辑排序')).toBeInTheDocument())
    const tiles = screen.getAllByTitle(/^打开 /)
    expect(tiles[0]).toHaveAttribute('title', '打开 客户资料明细')
    expect(tiles[1]).toHaveAttribute('title', '打开 供应商资料明细')
  })
})
