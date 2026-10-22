import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import { ApiError } from '../../types/api'
import { SearchCenterPage } from './SearchCenterPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const searchableModules = [
  { moduleId: 1505, title: '入库单', masterTable: 'INV_STOCK_M', detailTable: 'INV_STOCK_D', searchMaster: true, searchDetail: true },
  // 只登记了明细位（真实库里 1505 的 SEARCH_1=0/SEARCH_2=1）：默认必须查明细，按主表查会 404
  { moduleId: 1411, title: '备货单', masterTable: 'SHP_M', detailTable: 'SHP_D', searchMaster: false, searchDetail: true },
  { moduleId: 1401, title: '客户基本资料', masterTable: 'CLIENT', detailTable: null, searchMaster: true, searchDetail: false },
]

const masterDefinition = {
  moduleId: 1505,
  title: '入库单',
  table: 'INV_STOCK_M',
  fields: [
    { key: 'INV_NO', label: '单号', dataType: 'nvarchar' },
    { key: 'PRODUCT_NO', label: '料号', dataType: 'nvarchar' },
  ],
  columns: [
    // 后端按工作台装配好的顺序下发（个人列配置 + 系统默认），前端不再自己排
    { key: 'PRODUCT_NO', label: '料号', dataType: 'nvarchar' },
    { key: 'INV_NO', label: '单号', dataType: 'nvarchar' },
  ],
  pkOrder: ['INV_NO'],
  masterPkOrder: ['INV_NO'],
}

const detailDefinition = {
  ...masterDefinition,
  table: 'INV_STOCK_D',
  pkOrder: ['INV_NO', 'ITEM'],
  masterPkOrder: ['INV_NO'],
}

function installApiMocks() {
  apiClientMock.get.mockImplementation(async (path: string) => {
    if (path === '/search-center/modules') return searchableModules
    if (path.includes('/definition')) {
      return path.includes('table=INV_STOCK_D') ? detailDefinition : masterDefinition
    }
    // 统一工作台的列配置：查询中心按它排序列、定显隐
    if (path.includes('/column-editor')) {
      return {
        current: {
          master: [
            { key: 'PRODUCT_NO', label: '料号', isVisible: true },
            { key: 'INV_NO', label: '单号', isVisible: true },
          ],
          detail: [],
        },
        defaults: { master: [], detail: [] },
      }
    }
    return {}
  })
  apiClientMock.post.mockResolvedValue({
    rows: [{ INV_NO: 'INV001', PRODUCT_NO: 'P1' }],
    total: 1,
    page: 1,
    pageSize: 50,
  })
  apiClientMock.postFile.mockResolvedValue(new Blob(['a,b']))
}

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="location">{`${location.pathname}${location.search}`}</div>
}

function renderPage(entry = '/search-center') {
  return renderWithProviders(
    <MemoryRouter initialEntries={[entry]}>
      <LocationProbe />
      <Routes>
        <Route path="/search-center/:moduleId?" element={<SearchCenterPage />} />
        <Route path="/workbench/:moduleId/view/*" element={<div>VIEW_PAGE</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('SearchCenterPage', () => {
  beforeEach(() => {
    installApiMocks()
    window.localStorage.clear()
    if (!('createObjectURL' in URL)) {
      Object.defineProperty(URL, 'createObjectURL', { writable: true, value: vi.fn(() => 'blob:mock') })
    }
    Object.defineProperty(URL, 'revokeObjectURL', { writable: true, value: vi.fn() })
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.restoreAllMocks()
  })

  it('未选模块时不查询，并提示先选模块', async () => {
    renderPage()
    expect(await screen.findByText('先选一个模块')).toBeInTheDocument()
    expect(apiClientMock.post).not.toHaveBeenCalled()
  })

  it('菜单入口号 34 不在可检索清单里，按未选择处理', async () => {
    renderPage('/search-center/34')
    expect(await screen.findByText('先选一个模块')).toBeInTheDocument()
    expect(apiClientMock.post).not.toHaveBeenCalled()
  })

  it('从 URL 进入可检索模块后自动查询并渲染结果（不需要先点查询）', async () => {
    renderPage('/search-center/1505')
    expect(await screen.findByText('INV001')).toBeInTheDocument()
    expect(apiClientMock.post).toHaveBeenCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.objectContaining({ table: 'INV_STOCK_M', conditions: [] }),
    )
  })

  it('切到明细表后按明细表查询，并把主表主键带给结果行', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    fireEvent.change(screen.getByLabelText('数据表'), { target: { value: 'detail' } })
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.objectContaining({ table: 'INV_STOCK_D' }),
    ))
  })

  it('高级查询弹窗应用条件后，条件进入请求体且按钮出现计数角标', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    fireEvent.click(screen.getByRole('button', { name: '高级查询' }))
    fireEvent.change(screen.getByLabelText('条件1字段'), { target: { value: 'PRODUCT_NO' } })
    fireEvent.change(screen.getByLabelText('条件1值'), { target: { value: 'P1' } })
    fireEvent.click(screen.getByRole('button', { name: '应用查询' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.objectContaining({
        conditions: [expect.objectContaining({ field: 'PRODUCT_NO', operator: 'eq', value: 'P1' })],
      }),
    ))
    expect(await screen.findByRole('button', { name: '高级查询 (1)' })).toBeInTheDocument()
  })

  it('双击结果行按主键打开单据', async () => {
    renderPage('/search-center/1505')
    fireEvent.doubleClick(await screen.findByText('INV001'))
    expect(await screen.findByText('VIEW_PAGE')).toBeInTheDocument()
  })

  it('结果第一列是复选框；选中一行后可打开单据', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    fireEvent.click(screen.getAllByLabelText('选择此行')[0])
    const open = await screen.findByRole('button', { name: '打开选中的单据' })
    fireEvent.click(open)
    expect(await screen.findByText('VIEW_PAGE')).toBeInTheDocument()
  })

  it('只有明细位的模块（如 1505 入库单）默认按明细表查，不会因主表位为 0 而加载失败', async () => {
    renderPage('/search-center/1411')
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/search-center/1411/query?page=1&pageSize=50',
      expect.objectContaining({ table: 'SHP_D' }),
    ))
  })

  it('切换模块只改查询串，路径保持 /search-center（模块号不是页面身份）', async () => {
    renderPage('/search-center')
    await screen.findByText('先选一个模块')
    fireEvent.change(screen.getByLabelText('查询模块'), { target: { value: '1505' } })
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.anything(),
    ))
    // 路由仍是 /search-center + ?module=1505，不会被改成 /search-center/1505
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/search-center?module=1505'))
    expect(screen.getByLabelText('查询模块')).toHaveValue('1505')
  })

  it('显示列顺序取后端装配好的 columns（与工作台同一份个人列配置）', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    const headers = screen.getAllByRole('columnheader').map(th => th.textContent ?? '')
    const partNo = headers.findIndex(text => text.includes('料号'))
    const orderNo = headers.findIndex(text => text.includes('单号'))
    expect(partNo).toBeGreaterThan(-1)
    expect(orderNo).toBeGreaterThan(-1)
    // definition.columns 已按工作台列配置排好序：料号在前
    expect(partNo).toBeLessThan(orderNo)
  })

  it('排序按工作台同一形态进请求体（逗号分隔字段/方向，不再是单列参数）', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    fireEvent.click(screen.getByRole('button', { name: '表头操作单号' }))
    fireEvent.click(screen.getByRole('button', { name: '升序' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.objectContaining({ sortFields: 'INV_NO', sortDirections: 'asc' }),
    ))
  })

  it('「结果列」里上移/下移后，表格列顺序跟着变（选择列不只是显隐）', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    fireEvent.click(screen.getByRole('button', { name: '选择列' }))
    const visible = screen.getByLabelText('结果列已选字段') as HTMLSelectElement
    const orderOption = Array.from(visible.options).find(option => option.value === 'INV_NO')!
    orderOption.selected = true
    fireEvent.change(visible, { target: visible })
    fireEvent.click(screen.getByTitle('上移'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => {
      const headers = screen.getAllByRole('columnheader').map(th => th.textContent ?? '')
      const partNo = headers.findIndex(text => text.includes('料号'))
      const orderNo = headers.findIndex(text => text.includes('单号'))
      expect(orderNo).toBeLessThan(partNo)
    })
  })

  it('检索定义加载失败时显示错误，而不是一直转圈', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/search-center/modules') return searchableModules
      if (path.includes('/definition')) throw new ApiError(500, { code: 'X', message: 'boom' })
      return {}
    })
    renderPage('/search-center/1505')
    expect(await screen.findByText('检索定义加载失败。')).toBeInTheDocument()
    expect(screen.queryByText('正在查询…')).not.toBeInTheDocument()
  })

  it('明细表不提供导出（主表导出复用工作台管线）', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    expect(screen.getByRole('button', { name: '导出当前查询结果' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('数据表'), { target: { value: 'detail' } })
    await waitFor(() => expect(screen.queryByRole('button', { name: '导出当前查询结果' })).not.toBeInTheDocument())
  })

  it('从工作台右键带 ?find=字段:值 进来时，自动带上"包含"条件并查询', async () => {
    renderPage(`/search-center/1505?find=${encodeURIComponent('PRODUCT_NO:P1')}`)
    await screen.findByText('INV001')
    expect(apiClientMock.post).toHaveBeenLastCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.objectContaining({
        conditions: [expect.objectContaining({ field: 'PRODUCT_NO', operator: 'contains', value: 'P1' })],
      }),
    )
  })

  it('关键字搜索进请求体，切模块后保留（供跨模块倒查同一关键字）', async () => {
    renderPage('/search-center/1505')
    await screen.findByText('INV001')
    fireEvent.change(screen.getByRole('searchbox', { name: '关键字搜索' }), { target: { value: 'P1' } })
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith(
      '/search-center/1505/query?page=1&pageSize=50',
      expect.objectContaining({ keyword: 'P1' }),
    ))
    fireEvent.change(screen.getByLabelText('查询模块'), { target: { value: '1401' } })
    await waitFor(() => expect(apiClientMock.post).toHaveBeenLastCalledWith(
      '/search-center/1401/query?page=1&pageSize=50',
      expect.objectContaining({ keyword: 'P1' }),
    ))
  })
})
