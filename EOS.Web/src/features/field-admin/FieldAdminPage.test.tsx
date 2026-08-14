import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { FieldAdminPage } from './FieldAdminPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const tables = [
  { tableId: 'PRODUCT_EDITION', description: '产品版次', kind: '主表', type: 'M' },
  { tableId: 'LINE', description: '线别', kind: '主表', type: 'M' },
]

const fieldsPage = {
  items: [
    { tableId: 'PRODUCT_EDITION', fieldId: 'PRO_NO', description: '产品编号', dataType: 'nvarchar', isVirtual: false, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isCost: false, isSecrecy: false, isPrimaryKey: true, physicalExists: true },
    { tableId: 'PRODUCT_EDITION', fieldId: 'QTY', description: '数量', dataType: 'decimal', isVirtual: false, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isCost: false, isSecrecy: false, isPrimaryKey: false, physicalExists: true },
  ],
  total: 2,
  page: 1,
  pageSize: 16,
}

const fieldMeta = {
  tableId: 'PRODUCT_EDITION',
  fieldId: 'PRO_NO',
  field: {
    label: '产品编号', dataType: 'nvarchar', width: 120, align: 'left', headerAlign: 'center', format: null,
    isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isRequired: true, isCost: false,
    isSecrecy: false, defaultValue: null, verifyIndex: null, regex: null, remark: null, browseUrl: null,
    browseModuleId: null, onlyChoose: false, chooseMultiple: false, choosePage: null,
    choosers: Array.from({ length: 4 }, () => ({ active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null })),
  canCopy: true,
  },
  isPrimaryKey: true,
  physicalExists: true,
  physicalType: 'nvarchar',
  typeMatches: true,
  isVirtual: false,
  virtualExpression: null,
  isAutoIncrement: false,
  convertFunction: null,
  dataSourceSql: null,
  lastUpdatedBy: 'admin',
  lastUpdatedAt: '2026-08-01T00:00:00Z',
}

function installMocks() {
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p === '/admin/tables') return tables
    if (p.includes('/admin/tables/') && p.endsWith('/fields')) return fieldsPage
    if (p.endsWith('/fields/unmanaged')) return [{ fieldId: 'UNMANAGED_1', dataType: 'nvarchar' }, { fieldId: 'UNMANAGED_2', dataType: 'int' }]
    if (p.includes('/admin/fields/')) return fieldMeta
    if (p === '/admin/lookups/modules') return [{ id: 1305, label: '库存仓别' }]
    throw new Error(`unexpected GET ${p}`)
  })
  apiClientMock.post.mockResolvedValue(undefined)
  apiClientMock.post.mockImplementation(async (path: string) => {
    if (path.endsWith('/fields/batch')) return { created: 1, skipped: 0, skippedReasons: [] }
    return undefined
  })
  apiClientMock.put.mockResolvedValue(undefined)
  apiClientMock.delete.mockResolvedValue(undefined)
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin/tables/PRODUCT_EDITION/fields']}>
        <Routes>
          <Route path="/admin/tables/:tableId/fields" element={<FieldAdminPage />} />
          <Route path="/admin/tables" element={<div>TABLE_LIST</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

async function loaded() {
  await waitFor(() => expect(screen.queryByText('正在加载字段列表…')).not.toBeInTheDocument())
}

describe('FieldAdminPage', () => {
  beforeEach(() => {
    installMocks()
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载失败显示错误', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(500, { code: 'X', message: '字段列表挂了' }))
    renderPage()
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('字段列表挂了'))
  })

  it('渲染字段列表与标题', async () => {
    renderPage()
    await loaded()
    expect(screen.getByText('产品版次')).toBeInTheDocument()
    expect(screen.getByText('PRO_NO')).toBeInTheDocument()
    expect(screen.getByText('产品编号')).toBeInTheDocument()
    expect(screen.getByText('共 2 个字段')).toBeInTheDocument()
    expect(screen.getAllByRole('radio', { name: '选择此行' })).toHaveLength(2)
    expect(document.querySelectorAll('input[type="checkbox"]:disabled').length).toBeGreaterThan(0)
  })

  it('搜索关键词触发带 keyword 的查询', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByRole('searchbox', { name: '搜索字段' }), { target: { value: 'QTY' } })
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields',
      expect.objectContaining({ query: expect.objectContaining({ keyword: 'QTY' }) }),
    ))
  })

  it('编辑按钮打开字段管理弹窗并加载元数据', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    await waitFor(() => expect(screen.getByText('字段管理（PRO_NO）')).toBeInTheDocument())
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith('/admin/fields/PRODUCT_EDITION/PRO_NO'))
    await waitFor(() => expect(screen.getByDisplayValue('产品编号')).toBeInTheDocument())
  })

  it('新增字段完整保存流程', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    await waitFor(() => expect(screen.getByText('新增字段（PRODUCT_EDITION）')).toBeInTheDocument())
    const dialog = screen.getByRole('dialog')
    const inputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input.form-control'))
    fireEvent.change(inputs[1], { target: { value: 'NEW_CODE' } })
    fireEvent.change(inputs[2], { target: { value: '新字段' } })
    const save = within(dialog).getByRole('button', { name: '新增字段' })
    await waitFor(() => expect(save).toBeEnabled())
    fireEvent.click(save)
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/fields',
      expect.objectContaining({ tableId: 'PRODUCT_EDITION', fieldId: 'NEW_CODE', field: expect.objectContaining({ label: '新字段' }) }),
    ))
  })

  it('删除字段需要确认并调用删除接口', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '删除' })[0])
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/admin/fields/PRODUCT_EDITION/PRO_NO'))
  })

  it('删除取消时不调用接口', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false))
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '删除' })[0])
    expect(apiClientMock.delete).not.toHaveBeenCalled()
  })

  it('返回按钮回到数据表列表', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '返回' }))
    expect(screen.getByText('TABLE_LIST')).toBeInTheDocument()
  })

  it('未管理字段弹窗可批量生成并展示结果', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '未管理字段' }))
    await waitFor(() => expect(screen.getByText('未管理字段批量生成（产品版次）')).toBeInTheDocument())
    await waitFor(() => expect(screen.getAllByRole('checkbox', { name: '选择此行' })).toHaveLength(2))
    fireEvent.click(screen.getAllByRole('checkbox', { name: '选择此行' })[0])
    fireEvent.click(screen.getByRole('button', { name: /^生成$/ }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields/batch',
      expect.objectContaining({ tableId: 'PRODUCT_EDITION', fieldIds: ['UNMANAGED_1'] }),
    ))
    await waitFor(() => expect(screen.getByText(/已生成 1 个字段元数据/)).toBeInTheDocument())
  })
})
