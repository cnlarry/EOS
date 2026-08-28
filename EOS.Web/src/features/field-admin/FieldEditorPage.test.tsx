import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FieldEditorRoute } from './FieldEditorPage'
import type { FieldInput } from './FieldEditorForm'

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
]

const fieldsPage = {
  items: [
    { tableId: 'PRODUCT_EDITION', fieldId: 'PRO_NO', description: '产品编号', dataType: 'nvarchar', isVirtual: false, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isCost: false, isSecrecy: false, isPrimaryKey: true, physicalExists: true },
    { tableId: 'PRODUCT_EDITION', fieldId: 'QTY', description: '数量', dataType: 'decimal', isVirtual: false, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isCost: false, isSecrecy: false, isPrimaryKey: false, physicalExists: true },
  ],
  total: 2,
}

function fieldInput(overrides: Partial<FieldInput> = {}): FieldInput {
  return {
    label: '产品编号', dataType: 'nvarchar', width: 120, align: 'left', headerAlign: 'center',
    format: null, isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isRequired: true,
    isCost: false, isSecrecy: false, defaultValue: null, verifyIndex: null, regex: null, remark: null,
    browseUrl: null, browseModuleId: null, onlyChoose: false, chooseMultiple: false, choosePage: null,
    choosers: [], canCopy: true, tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0,
    options: null, ...overrides,
  }
}

const history = [
  {
    occurredAt: '2026-08-28 10:00:00',
    actorUserId: 'admin',
    action: 'UPDATE',
    summary: '字段维护更新',
    changes: [{ name: 'F_DESC', oldValue: '旧标题', newValue: '产品编号' }],
  },
]

function renderPage(initialEntry = '/admin/fields/PRODUCT_EDITION/PRO_NO') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/admin/fields/:tableId/:fieldId" element={<FieldEditorRoute />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function installMocks() {
  apiClientMock.get.mockImplementation((path: string) => {
    if (path === '/admin/tables') return Promise.resolve(tables)
    if (path === '/admin/fields/PRODUCT_EDITION/PRO_NO/history') return Promise.resolve(history)
    if (path.startsWith('/admin/tables/PRODUCT_EDITION/fields')) return Promise.resolve(fieldsPage)
    if (path === '/admin/lookups/modules') return Promise.resolve([])
    if (path === '/admin/fields/PRODUCT_EDITION/PRO_NO') {
      return Promise.resolve({
        tableId: 'PRODUCT_EDITION',
        fieldId: 'PRO_NO',
        field: fieldInput(),
        isVirtual: false,
        virtualExpression: null,
        isAutoIncrement: false,
        convertFunction: null,
        dataSourceSql: null,
        lastUpdatedBy: 'admin',
        lastUpdatedAt: '2026-08-01T00:00:00Z',
      })
    }
    return Promise.resolve(null)
  })
  apiClientMock.put.mockResolvedValue(undefined)
  apiClientMock.post.mockResolvedValue(undefined)
}

describe('FieldEditorRoute', () => {
  beforeEach(() => {
    installMocks()
  })
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('渲染全页两栏与选项卡，加载字段元数据与变更历史', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByText('字段设置：PRO_NO')).toBeInTheDocument())
    // 左栏字段导航
    await waitFor(() => expect(screen.getAllByText('PRO_NO').length).toBeGreaterThan(0))
    expect(screen.getAllByText('QTY').length).toBeGreaterThan(0)
    // 字段元数据加载
    await waitFor(() => expect(screen.getByDisplayValue('产品编号')).toBeInTheDocument())
    // 5 个分组 + 变更历史选项卡
    expect(screen.getByRole('tab', { name: '基本信息' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '数据来源' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '权限与行为' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '表单布局' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '高级设置' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '变更历史' })).toBeInTheDocument()
    // 变更历史内容
    fireEvent.click(screen.getByRole('tab', { name: '变更历史' }))
    await waitFor(() => expect(screen.getByText('修改')).toBeInTheDocument())
    expect(screen.getByText('旧标题')).toBeInTheDocument()
  })

  it('编辑态保存调用 PUT 字段维护接口', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByDisplayValue('产品编号')).toBeInTheDocument())
    fireEvent.change(screen.getByDisplayValue('产品编号'), { target: { value: '新标题' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [url, body] = apiClientMock.put.mock.calls[0]
    expect(url).toBe('/admin/fields/PRODUCT_EDITION/PRO_NO')
    expect(body).toEqual(expect.objectContaining({ field: expect.objectContaining({ label: '新标题' }) }))
  })

  it('新增态显示空表单且保存调用 POST 字段维护接口', async () => {
    renderPage('/admin/fields/PRODUCT_EDITION/new')
    await waitFor(() => expect(screen.getByText('新增字段（产品版次）')).toBeInTheDocument())
    const keyInput = screen.getAllByText('字段名称')[0].closest('.col-md-4')!.querySelector('input') as HTMLInputElement
    fireEvent.change(keyInput, { target: { value: 'NEW_CODE' } })
    const labelInput = screen.getAllByText('字段标题')[0].closest('.col-md-4')!.querySelector('input') as HTMLInputElement
    fireEvent.change(labelInput, { target: { value: '新字段' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalled())
    const [url, body] = apiClientMock.post.mock.calls[0]
    expect(url).toBe('/admin/fields')
    expect(body).toEqual(expect.objectContaining({ tableId: 'PRODUCT_EDITION', fieldId: 'NEW_CODE' }))
  })
})
