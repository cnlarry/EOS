import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { useAuth } from '../auth/authContext'
import { DocumentWorkbenchPage } from './DocumentWorkbenchPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))
vi.mock('../auth/authContext', () => ({ useAuth: vi.fn() }))

const definition = {
  moduleId: 1209,
  title: '产品版次',
  masterTable: 'PRODUCT_EDITION',
  detailTable: 'PRODUCT_DETAIL',
  masterFields: [
    { key: 'PRO_NO', label: '产品编号', dataType: 'nvarchar', width: 120, align: 'left', isPrimaryKey: true, isQueryable: true, headerAlign: 'center', format: null, browseUrl: null, browseModuleId: null },
    { key: 'EDITION', label: '版次', dataType: 'nvarchar', width: 80, align: 'center', isPrimaryKey: true, isQueryable: true, headerAlign: 'center', format: null, browseUrl: null, browseModuleId: null },
    { key: 'QTY', label: '数量', dataType: 'decimal', width: 100, align: 'right', isPrimaryKey: false, isQueryable: true, headerAlign: 'right', format: null, browseUrl: null, browseModuleId: null },
    { key: 'FLAG', label: '启用', dataType: 'bit', width: 60, align: 'center', isPrimaryKey: false, isQueryable: true, headerAlign: 'center', format: null, browseUrl: null, browseModuleId: null },
  ],
  detailFields: [
    { key: 'ITEM', label: '明细项', dataType: 'nvarchar', width: 100, align: 'left', isPrimaryKey: false, isQueryable: true, headerAlign: 'center', format: null, browseUrl: null, browseModuleId: null },
  ],
  hasAdd: true,
  hasEdit: true,
  masterPkOrder: ['PRO_NO', 'EDITION'],
}

const records = {
  rows: [
    { PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: true },
    { PRO_NO: 'P2', EDITION: 'B', QTY: '20', FLAG: false },
  ],
  total: 2,
  page: 1,
  pageSize: 16,
}

const details = { rows: [{ ITEM: 'X1' }], total: 1, page: 1, pageSize: 10 }

const columnEditor = {
  current: {
    master: [
      { key: 'PRO_NO', label: '产品编号', isVisible: true, order: 1 },
      { key: 'EDITION', label: '版次', isVisible: true, order: 2 },
      { key: 'QTY', label: '数量', isVisible: true, order: 3 },
    ],
    detail: [{ key: 'ITEM', label: '明细项', isVisible: true, order: 1 }],
  },
  defaults: {
    master: [{ key: 'PRO_NO', label: '产品编号', isVisible: true, order: 1 }],
    detail: [],
  },
}

const fieldMeta = {
  key: 'PRO_NO', tableId: 'PRODUCT_EDITION', label: '产品编号', dataType: 'nvarchar', width: 120, align: 'left', headerAlign: 'center',
  format: null, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isRequired: true, isCost: false,
  isSecrecy: false, defaultValue: null, verifyIndex: null, regex: null, remark: null, browseUrl: null, browseModuleId: null,
  onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [], canCopy: true, isVirtual: false,
  virtualExpression: null, isAutoIncrement: false, convertFunction: null, dataSourceSql: null,
  lastUpdatedBy: 'admin', lastUpdatedAt: '2026-08-01T00:00:00Z',
}

function installApiMocks(overrides: { records?: unknown; details?: unknown; definition?: unknown } = {}) {
  const recordData = overrides.records ?? records
  const detailData = overrides.details ?? details
  const definitionData = overrides.definition ?? definition
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p.endsWith('/definition')) return definitionData
    if (p.includes('/records')) return recordData
    if (p.includes('/details')) return detailData
    if (p.includes('/column-editor')) return columnEditor
    if (p.includes('/lookups/')) return []
    if (p.includes('/field-settings/')) return fieldMeta
    throw new Error(`unexpected GET ${p}`)
  })
  apiClientMock.post.mockResolvedValue({ key: [] })
  apiClientMock.put.mockResolvedValue(undefined)
  apiClientMock.delete.mockResolvedValue(undefined)
  apiClientMock.postFile.mockResolvedValue(new Blob(['a,b']))
}

function renderPage(initialEntry = '/document-workbench/1209') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/document-workbench/:moduleId" element={<DocumentWorkbenchPage />} />
          <Route path="/document-workbench/:moduleId/new" element={<div>NEW_FORM</div>} />
          <Route path="/document-workbench/:moduleId/edit" element={<div>EDIT_FORM</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载单据定义…')).not.toBeInTheDocument())
  await waitFor(() => expect(screen.queryByText('正在加载主表数据…')).not.toBeInTheDocument())
}

describe('DocumentWorkbenchPage', () => {
  beforeEach(() => {
    vi.mocked(useAuth).mockReturnValue({ bootstrap: null, loading: false, login: vi.fn(), logout: vi.fn(), hasPermission: () => true })
    installApiMocks()
    if (!('createObjectURL' in URL)) {
      Object.defineProperty(URL, 'createObjectURL', { writable: true, value: vi.fn(() => 'blob:mock') })
    }
    Object.defineProperty(URL, 'revokeObjectURL', { writable: true, value: vi.fn() })
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('定义加载中显示 LoadingState', () => {
    apiClientMock.get.mockReturnValue(new Promise(() => undefined))
    renderPage()
    expect(screen.getByText('正在加载单据定义…')).toBeInTheDocument()
  })

  it('定义加载失败显示错误', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(500, { code: 'X', message: 'boom' }))
    renderPage()
    await waitFor(() => expect(screen.getByText('无法加载模块定义。')).toBeInTheDocument())
  })

  it('主表数据加载失败显示错误并可重试', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.endsWith('/definition')) return definition
      throw new ApiError(503, { code: 'DOWN', message: '模拟服务暂时不可用，请清除关键词后重试。' })
    })
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('模拟服务暂时不可用，请清除关键词后重试。'))
    const before = apiClientMock.get.mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: '重新加载' }))
    await waitFor(() => expect(apiClientMock.get.mock.calls.length).toBeGreaterThan(before))
  })

  it('渲染主表行、位字段复选框与空值', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('P1')).toBeInTheDocument()
    expect(screen.getByText('P2')).toBeInTheDocument()
    expect(screen.getByText('10')).toBeInTheDocument()
    const bitBoxes = screen.getAllByRole('checkbox', { name: '启用' })
    expect(bitBoxes[0]).toBeChecked()
    expect(bitBoxes[1]).not.toBeChecked()
  })

  it('点击主表行后加载并渲染明细', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByText('P1'))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/document-workbench/1209/details',
      expect.objectContaining({ query: expect.objectContaining({ PRO_NO: 'P1', EDITION: 'A' }) }),
    ))
    await waitFor(() => expect(screen.getByText('X1')).toBeInTheDocument())
  })

  it('明细加载失败显示错误与重新加载', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.endsWith('/definition')) return definition
      if (p.includes('/records')) return records
      if (p.includes('/details')) throw new ApiError(500, { code: 'X', message: '明细挂了' })
      throw new Error(`unexpected ${p}`)
    })
    renderPage()
    await loaded()
    fireEvent.click(screen.getByText('P1'))
    await waitFor(() => expect(screen.getByText('明细数据加载失败。')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '重新加载' })).toBeInTheDocument()
  })

  it('行选择后导出按钮变为“导出所选”并调用 export-selected', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByLabelText('选择此行')[0])
    await waitFor(() => expect(screen.getByRole('button', { name: /导出所选 \(1\)/ })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: /导出所选 \(1\)/ }))
    await waitFor(() => expect(apiClientMock.postFile).toHaveBeenCalledWith(
      '/document-workbench/1209/export-selected',
      { keys: [['P1', 'A']] },
      {},
    ))
  })

  it('未选择行时导出当前条件', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '导出' }))
    await waitFor(() => expect(apiClientMock.postFile).toHaveBeenCalledWith(
      '/document-workbench/1209/export',
      { conditions: [] },
      { query: { keyword: undefined, sortFields: undefined, sortDirections: undefined } },
    ))
  })

  it('hasAdd 控制新增按钮，点击后跳转新增页', async () => {
    installApiMocks({ definition: { ...definition, hasAdd: false } })
    const { unmount } = renderPage()
    await loaded()
    expect(screen.getByRole('button', { name: '新增' })).toBeDisabled()
    unmount()
    installApiMocks()
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    expect(screen.getByText('NEW_FORM')).toBeInTheDocument()
  })

  it('选中行后编辑按钮可用并跳转带 key 的编辑页', async () => {
    renderPage()
    await loaded()
    expect(screen.getByRole('button', { name: '编辑' })).toBeDisabled()
    fireEvent.click(screen.getByText('P1'))
    await waitFor(() => expect(screen.getByRole('button', { name: '编辑' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    expect(screen.getByText('EDIT_FORM')).toBeInTheDocument()
  })

  it('搜索关键字触发带 keyword 的记录查询', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索' }), { target: { value: 'P1' } })
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/document-workbench/1209/records',
      expect.objectContaining({ query: expect.objectContaining({ keyword: 'P1' }) }),
    ))
  })

  it('表头菜单升序触发服务端排序参数', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '表头操作产品编号' }))
    fireEvent.click(screen.getByRole('button', { name: '升序' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/document-workbench/1209/records',
      expect.objectContaining({ query: expect.objectContaining({ sortFields: 'PRO_NO', sortDirections: 'asc' }) }),
    ))
  })

  it('高级查询应用条件后展示计数并走 POST 查询', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '高级' }))
    fireEvent.change(screen.getByLabelText('条件1字段'), { target: { value: 'PRO_NO' } })
    fireEvent.click(screen.getByRole('button', { name: '应用查询' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '高级 (1)' })).toBeInTheDocument())
    expect(apiClientMock.post).toHaveBeenCalledWith(
      '/document-workbench/1209/query?page=1&pageSize=10',
      { conditions: [expect.objectContaining({ field: 'PRO_NO' })] },
    )
    fireEvent.click(screen.getByRole('button', { name: '高级 (1)' }))
    fireEvent.click(screen.getByRole('button', { name: '清空' }))
    await waitFor(() => expect(screen.getByRole('button', { name: '高级' })).toBeInTheDocument())
  })

  it('选择列弹窗加载配置并保存列顺序', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '选择列' }))
    await waitFor(() => expect(screen.getByText('主表字段可选字段')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/document-workbench/1209/columns',
      { master: ['PRO_NO', 'EDITION', 'QTY'], detail: ['ITEM'] },
    ))
  })

  it('表头菜单打开单字段设置弹窗', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '表头操作产品编号' }))
    fireEvent.click(screen.getByRole('button', { name: '字段设置' }))
    await waitFor(() => expect(screen.getByRole('dialog')).toBeInTheDocument())
    expect(screen.getByText('主表字段设置')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByDisplayValue('产品编号')).toBeInTheDocument())
  })

  it('紧凑行高切换', async () => {
    renderPage()
    await loaded()
    const toggle = screen.getByRole('button', { name: '紧凑行高' })
    fireEvent.click(toggle)
    await waitFor(() => expect(document.querySelector('table.erp-data-table')).toHaveClass('erp-table-compact'))
  })

  it('URL 传入超界页码时钳制到有效页', async () => {
    renderPage('/document-workbench/1209?page=2')
    await loaded()
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/document-workbench/1209/records',
      expect.objectContaining({ query: expect.objectContaining({ page: 1 }) }),
    ))
  })

  it('空数据时渲染空表格而非错误', async () => {
    installApiMocks({ records: { rows: [], total: 0, page: 1, pageSize: 16 } })
    renderPage()
    await loaded()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('无权限浏览链接时按纯文本渲染', async () => {
    const withBrowse = {
      ...definition,
      masterFields: definition.masterFields.map((field: { key: string }) => field.key === 'PRO_NO'
        ? { ...field, browseUrl: '/x', browseModuleId: 1305 }
        : field),
    }
    installApiMocks({ definition: withBrowse })
    vi.mocked(useAuth).mockReturnValue({ bootstrap: null, loading: false, login: vi.fn(), logout: vi.fn(), hasPermission: (permission: string) => !permission.includes('1305') })
    renderPage()
    await loaded()
    expect(screen.getByText('P1')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'P1' })).not.toBeInTheDocument()
  })

  it('hasEdit=false 时编辑按钮始终禁用', async () => {
    installApiMocks({ definition: { ...definition, hasEdit: false } })
    renderPage()
    await loaded()
    expect(screen.getByRole('button', { name: '编辑' })).toBeDisabled()
    fireEvent.click(screen.getByText('P1'))
    await waitFor(() => expect(screen.getByRole('button', { name: '编辑' })).toBeDisabled())
  })

  it('导出失败弹出错误提示', async () => {
    const alertMock = vi.fn()
    vi.stubGlobal('alert', alertMock)
    apiClientMock.postFile.mockRejectedValue(new Error('网络断了'))
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '导出' }))
    await waitFor(() => expect(alertMock).toHaveBeenCalledWith('导出失败：网络断了'))
  })

  it('刷新按钮触发主表与明细重新查询', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByText('P1'))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/document-workbench/1209/details',
      expect.anything(),
    ))
    const before = apiClientMock.get.mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: '刷新' }))
    await waitFor(() => expect(apiClientMock.get.mock.calls.length).toBeGreaterThan(before))
  })

})
