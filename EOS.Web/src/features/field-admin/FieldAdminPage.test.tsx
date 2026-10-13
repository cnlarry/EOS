import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { FieldAdminPage } from './FieldAdminPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

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
    choosers: Array.from({ length: 4 }, (_, index) => ({ active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: index + 1 })),
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
  lastUpdatedBy: 'admin',
  lastUpdatedAt: '2026-08-01T00:00:00Z',
}

/** 统一选择器下发的未管理字段候选（数据源 field-admin.unmanaged-fields）。 */
const unmanagedChooserResult = {
  columns: [
    { key: 'COLUMN_NAME', label: '字段名', dataType: 'nvarchar', format: null },
    { key: 'DATA_TYPE', label: '类型', dataType: 'nvarchar', format: null },
  ],
  rows: [
    { COLUMN_NAME: 'UNMANAGED_1', DATA_TYPE: 'nvarchar' },
    { COLUMN_NAME: 'UNMANAGED_2', DATA_TYPE: 'int' },
  ],
  total: 2,
}

function installMocks() {
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p === '/admin/tables') return tables
    if (p.includes('/admin/tables/') && p.endsWith('/fields')) return fieldsPage
    if (p.includes('/admin/fields/')) return fieldMeta
    if (p === '/admin/lookups/modules') return [{ id: 1305, label: '库存仓别' }]
    throw new Error(`unexpected GET ${p}`)
  })
  // 候选清单统一走选择器数据源（POST /chooser/query）；写路径仍各走自己的端点
  apiClientMock.post.mockImplementation(async (path: string) => {
    if (path === '/chooser/query') return unmanagedChooserResult
    if (path.endsWith('/fields/batch')) return { created: 1, skipped: 0, skippedReasons: [] }
    return undefined
  })
  apiClientMock.put.mockResolvedValue(undefined)
  apiClientMock.delete.mockResolvedValue(undefined)
}

function renderPage() {
  return renderWithProviders(
      <MemoryRouter initialEntries={['/admin/tables/PRODUCT_EDITION/fields']}>
        <Routes>
          <Route path="/admin/tables/:tableId/fields" element={<FieldAdminPage />} />
          <Route path="/admin/fields/:tableId/:fieldId" element={<div>FIELD_PAGE</div>} />
          <Route path="/admin/tables" element={<div>TABLE_LIST</div>} />
        </Routes>
      </MemoryRouter>
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

  it('编辑按钮跳转全尺寸字段设置页', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    await waitFor(() => expect(screen.getByText('FIELD_PAGE')).toBeInTheDocument())
  })

  it('新增按钮跳转全尺寸字段设置页（新增态）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    await waitFor(() => expect(screen.getByText('FIELD_PAGE')).toBeInTheDocument())
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

  it('默认按 excludeSystem 查询并提供显示开关', async () => {
    renderPage()
    await loaded()
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields',
      expect.objectContaining({ query: expect.objectContaining({ excludeSystem: 'true' }) }),
    ))
    fireEvent.click(screen.getByRole('button', { name: '显示系统列' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields',
      expect.objectContaining({ query: expect.objectContaining({ excludeSystem: undefined }) }),
    ))
  })

  it('表头排序触发带 sortBy/sortDesc 的服务端查询（分页列表的排序只能服务端做）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '表头操作字段名' }))
    fireEvent.click(screen.getByRole('button', { name: '降序' }))
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields',
      expect.objectContaining({ query: expect.objectContaining({ sortBy: 'fieldId', sortDesc: 'true' }) }),
    ))
  })

  it('系统列显示徽标且无删除按钮', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p === '/admin/tables') return tables
      if (p.includes('/admin/tables/') && p.endsWith('/fields')) return {
        items: [
          fieldsPage.items[0],
          { ...fieldsPage.items[1], fieldId: 'CONFIRM_TAG', description: '批核状态', isSystemColumn: true },
        ],
        total: 2,
        page: 1,
        pageSize: 16,
      }
      if (p.endsWith('/fields/unmanaged')) return []
      if (p.includes('/admin/fields/')) return fieldMeta
      if (p === '/admin/lookups/modules') return []
      throw new Error(`unexpected GET ${p}`)
    })
    renderPage()
    await loaded()
    expect(screen.getByText('系统')).toBeInTheDocument()
    // 仅非系统行有删除按钮
    expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1)
  })

  it('没有幽灵字段时不显示清理入口', async () => {
    renderPage()
    await loaded()
    expect(screen.queryByRole('button', { name: '清理幽灵字段' })).toBeNull()
  })

  it('幽灵字段清理弹窗只列幽灵字段并可清理', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p === '/admin/tables') return [{ tableId: 'PRODUCT_EDITION', description: '产品版次', kind: 'P', type: 'TABLE', fieldCount: 2, unmanagedCount: 0, orphanCount: 2 }]
      if (p.includes('/admin/tables/') && p.endsWith('/fields')) return fieldsPage
      if (p.includes('/admin/fields/')) return fieldMeta
      if (p === '/admin/lookups/modules') return []
      throw new Error(`unexpected GET ${p}`)
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      // 幽灵候选由选择器数据源给出（虚拟字段不在其中，那条口径由服务端把守）
      if (path === '/chooser/query') {
        return {
          columns: [
            { key: 'F_ID', label: '字段名', dataType: 'nvarchar', format: null },
            { key: 'F_DESC', label: '说明', dataType: 'nvarchar', format: null },
            { key: 'DATA_TYPE', label: '类型', dataType: 'nvarchar', format: null },
          ],
          rows: [
            { F_ID: 'OLD_COL', F_DESC: '旧列', DATA_TYPE: 'nvarchar' },
            { F_ID: 'GONE_COL', F_DESC: '已删列', DATA_TYPE: 'int' },
          ],
          total: 2,
        }
      }
      if (path.endsWith('/fields/ghosts/cleanup')) return { removed: 1, skipped: 0, skippedReasons: [] }
      return undefined
    })
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '清理幽灵字段' }))
    await waitFor(() => expect(screen.getByText(/清理幽灵字段（产品版次）/)).toBeInTheDocument())
    await waitFor(() => expect(screen.getAllByRole('checkbox', { name: '选择此行' })).toHaveLength(2))
    fireEvent.click(screen.getAllByRole('checkbox', { name: '选择此行' })[0])
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields/ghosts/cleanup',
      expect.objectContaining({ tableId: 'PRODUCT_EDITION', fieldIds: ['OLD_COL'] }),
    ))
    await waitFor(() => expect(screen.getByText(/已清理 1 个幽灵字段/)).toBeInTheDocument())
  })

  it('未管理字段弹窗可批量生成并展示结果', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '未管理字段' }))
    await waitFor(() => expect(screen.getByText(/未管理字段批量生成（产品版次）/)).toBeInTheDocument())
    await waitFor(() => expect(screen.getAllByRole('checkbox', { name: '选择此行' })).toHaveLength(2))
    fireEvent.click(screen.getAllByRole('checkbox', { name: '选择此行' })[0])
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/tables/PRODUCT_EDITION/fields/batch',
      expect.objectContaining({ tableId: 'PRODUCT_EDITION', fieldIds: ['UNMANAGED_1'] }),
    ))
    await waitFor(() => expect(screen.getByText(/已生成 1 个字段元数据/)).toBeInTheDocument())
  })
})
