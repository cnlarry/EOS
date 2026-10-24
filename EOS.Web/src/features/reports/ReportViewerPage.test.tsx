import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { createMemoryRouter, RouterProvider, useParams } from 'react-router-dom'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PageBreadcrumbContext } from '../../components/layout/PageBreadcrumbContext'
import { ReportViewerPage } from './ReportViewerPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const printSettings = {
  reports: [{ reportId: 'RPT_A', reportName: '库存报表', headerId: null, tailId: null, footerText: null, isoNo: null, isDefault: true }],
  headers: [],
  tails: [],
  sortSchemesByReport: {},
  userSettings: null,
}

const definition = {
  moduleId: 1405,
  title: '库存报表',
  masterTable: 'COP_ORDER_M',
  conditions: [
    {
      serialNo: 1, field: 'COP_ORDER_M.WAREHOUSE', desc: '仓库', type: 5,
      expression: 'SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE',
      defaultValue: null, parameterName: null, options: [],
      selectSource: { table: 'WAREHOUSE', idColumn: 'W_ID', valueColumn: 'W_NAME' },
      defaultValueTo: null,
    },
  ],
  columns: [{ key: 'ORDER_NO', label: '订单号', dataType: 'nvarchar' }],
  masterPkOrder: ['ORDER_NO'],
  spName: null,
  spParameters: [],
  dataSource: 'table',
  parameters: [],
}

const conditionOptions = [
  { label: '一号仓', value: 'W1' },
  { label: '二号仓', value: 'W2' },
]

function installApiMocks() {
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p.includes('/print-settings')) return printSettings
    if (p.includes('/definition')) return definition
    if (p.includes('/condition-options/')) return conditionOptions
    throw new Error(`unexpected GET ${p}`)
  })
  apiClientMock.post.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p.includes('/query')) return { rows: [], total: 0, page: 1, pageSize: 50 }
    return {}
  })
}

/**
 * 报表身份路由的替身：模块号在测试里是已知的（由入口地址决定），不额外打身份接口，
 * 只把编号喂给查看器——与真实外壳（ReportIdentityRoute）的分工一致。
 */
function IdentityRouteStub({ moduleId }: { moduleId: string }) {
  const { reportId = '' } = useParams()
  return <ReportViewerPage moduleIdOverride={moduleId} reportIdOverride={reportId} />
}

function renderViewer(initialEntry = '/report/RPT_A') {
  const router = createMemoryRouter(
    // 查看器就是报表身份地址的组件（老模块地址由 ReportViewerRoute 负责先跳转再渲染）
    [{ path: '/report/:reportId', element: <IdentityRouteStub moduleId="1405" /> }],
    { initialEntries: [initialEntry] },
  )
  return renderWithProviders(<RouterProvider router={router} />)
}

describe('ReportViewerPage F_TYPE 5（数据源多选条件）', () => {
  beforeEach(() => {
    installApiMocks()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('按数据源选项渲染复选框（type 5），而非 type 3 的下拉', async () => {
    renderViewer()
    fireEvent.click(await screen.findByRole('button', { name: /查询条件/ }))
    expect(await screen.findByText('一号仓')).toBeInTheDocument()
    expect(screen.getByText('二号仓')).toBeInTheDocument()
    // 仅统计条件弹窗内该字段的复选框（打印设置默认折叠，其显示分组/显示明细复选框不在 DOM 中）
    const conditionArea = screen.getByText('仓库').closest<HTMLElement>('.erp-query-field')!
    expect(within(conditionArea).getAllByRole('checkbox')).toHaveLength(2)
    expect(within(conditionArea).getAllByRole('checkbox')[0]).not.toBeChecked()
    // 不应渲染 type 3 的「全部」下拉选项
    expect(screen.queryByText('全部')).not.toBeInTheDocument()
  })

  it('勾选复选框后点查询，以逗号分隔实际值 POST（type 4 同语义）', async () => {
    renderViewer()
    fireEvent.click(await screen.findByRole('button', { name: /查询条件/ }))
    await screen.findByText('一号仓')
    fireEvent.click(screen.getByText('一号仓').closest('label')!.querySelector('input')!)
    fireEvent.click(screen.getByText('二号仓').closest('label')!.querySelector('input')!)
    const dialog = screen.getByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: '查询' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/reports/1405/query?page=1&pageSize=50&reportId=RPT_A',
      expect.objectContaining({ values: { 1: 'W1,W2' } }),
    ))
  })

  it('取消勾选后重新查询，POST 值同步收缩', async () => {
    renderViewer()
    fireEvent.click(await screen.findByRole('button', { name: /查询条件/ }))
    await screen.findByText('一号仓')
    fireEvent.click(screen.getByText('一号仓').closest('label')!.querySelector('input')!)
    fireEvent.click(screen.getByText('二号仓').closest('label')!.querySelector('input')!)
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '查询' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/reports/1405/query?page=1&pageSize=50&reportId=RPT_A',
      expect.objectContaining({ values: { 1: 'W1,W2' } }),
    ))
    // 已应用的值回填到弹窗草稿；重开条件弹窗改选后再查询
    fireEvent.click(screen.getByRole('button', { name: /查询条件/ }))
    await screen.findByText('一号仓')
    fireEvent.click(screen.getByText('一号仓').closest('label')!.querySelector('input')!)
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: '查询' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/reports/1405/query?page=1&pageSize=50&reportId=RPT_A',
      expect.objectContaining({ values: { 1: 'W2' } }),
    ))
  })
})

describe('ReportViewerPage 页面结构', () => {
  beforeEach(() => {
    installApiMocks()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('工具栏显示模块身份并回链报表中心', async () => {
    renderViewer()
    expect(await screen.findByText('库存报表 1405')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '返回报表中心' })).toBeInTheDocument()
  })

  it('打印设置默认折叠，点「打印」才展开', async () => {
    renderViewer()
    await screen.findByText('库存报表 1405')
    expect(screen.queryByText('打印设置')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '打印' }))
    expect(screen.getByText('打印设置')).toBeInTheDocument()
  })

  it('未查询时提示尚未查询，查询后 0 行提示放宽条件', async () => {
    renderViewer()
    expect(await screen.findByText('尚未查询')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '查询' }))
    expect(await screen.findByText('没有符合条件的数据')).toBeInTheDocument()
  })

  it('条件初值从 URL 带入并直接出结果（深链）', async () => {
    renderViewer('/report/RPT_A?f1=W1&page=2&pageSize=100')
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/reports/1405/query?page=2&pageSize=100&reportId=RPT_A',
      expect.objectContaining({ values: { 1: 'W1' } }),
    ))
  })

  it('页内切换报表时上抛页签标题，不重开标签', async () => {
    const twoReports = {
      ...printSettings,
      reports: [
        printSettings.reports[0],
        { reportId: 'RPT_B', reportName: '订单报表', headerId: null, tailId: null, footerText: null, isoNo: null, isDefault: false },
      ],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/print-settings')) return twoReports
      if (p.includes('/definition')) return definition
      if (p.includes('/condition-options/')) return conditionOptions
      throw new Error(`unexpected GET ${p}`)
    })
    const titles: string[] = []
    const router = createMemoryRouter(
      [{
        path: '/report/:reportId',
        element: (
          <PageBreadcrumbContext.Provider value={{ breadcrumb: null, setBreadcrumb: (value) => { if (value) titles.push(value.title) } }}>
            <IdentityRouteStub moduleId="1405" />
          </PageBreadcrumbContext.Provider>
        ),
      }],
      { initialEntries: ['/report/RPT_A'] },
    )
    renderWithProviders(<RouterProvider router={router} />)
    await waitFor(() => expect(titles.at(-1)).toBe('库存报表'))
    const select = await screen.findByLabelText('报表')
    fireEvent.change(select, { target: { value: 'RPT_B' } })
    await waitFor(() => expect(titles.at(-1)).toBe('订单报表'))
  })
})
