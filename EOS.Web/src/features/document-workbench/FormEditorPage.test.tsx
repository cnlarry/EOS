import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { FormEditorPage } from './FormEditorPage'
import type { FormDefinition } from './formDefinition'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

function field(key: string, label: string, overrides: Partial<FormDefinition['masterFields'][number]> = {}) {
  return {
    key, label, dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: '', isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null,
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: [], displayOnly: false,
    ...overrides,
  }
}

const formDefinition: FormDefinition = {
  moduleId: 1209,
  title: '产品版次',
  masterTable: 'PRODUCT_EDITION',
  detailTable: 'PRODUCT_DETAIL',
  hasAdd: true,
  hasEdit: true,
  mode: 'new',
  masterFields: [
    field('PRO_NO', '产品编号', { isRequired: true, isPrimaryKey: true, choosers: [{ active: true, table: 'PRODUCT', description: null, moduleId: null, filter: null, returnMapping: 'txt_PRO_NO=PRO_NO;txt_PRO_NAME=PRO_NAME' }] }),
    field('EDITION', '版次', { isPrimaryKey: true }),
    field('QTY', '数量', { dataType: 'decimal', defaultValue: '5' }),
    field('FLAG', '启用', { dataType: 'bit', defaultValue: '1' }),
    field('CREATE_DATE', '创建时间', { dataType: 'datetime', serverFilled: true, isReadonly: true }),
  ],
  detailFields: [field('ITEM', '明细项', { isRequired: true })],
  masterPkOrder: ['PRO_NO', 'EDITION'],
  detailNoFields: 'PRO_NO',
  detailDfVerify: 'ITEM',
  tabs: [],
  columns: 2,
  buttons: null,
  defaultValues: {},
}

const recordBundle = {
  master: { PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: true, CREATE_DATE: '2026-08-01T00:00:00Z' },
  details: [{ ITEM: 'X1' }],
}

const chooserData = {
  columns: [{ key: 'PRO_NO', label: '产品编号' }, { key: 'PRO_NAME', label: '名称' }],
  rows: [{ PRO_NO: 'P9', PRO_NAME: '高强钢' }],
  total: 1,
}

function installApiMocks() {
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p.includes('/form-definition')) return formDefinition
    if (p.includes('/record')) return recordBundle
    if (p.includes('/form-chooser/')) return chooserData
    throw new Error(`unexpected GET ${p}`)
  })
  apiClientMock.post.mockResolvedValue({ key: ['P9', '1'] })
  apiClientMock.put.mockResolvedValue({ key: ['P1', 'A'] })
}

function renderEditor(initialEntry: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createMemoryRouter(
    [
      { path: '/document-workbench/:moduleId', element: <div>BACK_LIST</div> },
      { path: '/document-workbench/:moduleId/new', element: <FormEditorPage /> },
      { path: '/document-workbench/:moduleId/edit', element: <FormEditorPage /> },
    ],
    { initialEntries: [initialEntry] },
  )
  return render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>)
}

function masterInputs(container: HTMLElement): HTMLInputElement[] {
  return Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control:not([disabled])'))
}

describe('FormEditorPage', () => {
  beforeEach(() => {
    installApiMocks()
    vi.stubGlobal('confirm', vi.fn(() => true))
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载中显示 LoadingState', () => {
    apiClientMock.get.mockReturnValue(new Promise(() => undefined))
    renderEditor('/document-workbench/1209/new')
    expect(screen.getByText('正在加载表单…')).toBeInTheDocument()
  })

  it('模块未启用（404）显示明确提示', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(404, { code: 'NOT_FOUND', message: 'not found' }))
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByText('该模块未启用统一表单编辑（含存盘后业务逻辑的模块暂不开放，或不在白名单内）。')).toBeInTheDocument())
  })

  it('新增模式按默认值初始化字段', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(container.querySelector('.erp-form-grid')).not.toBeNull()
    expect(screen.getByDisplayValue('5')).toBeInTheDocument()
    expect(container.querySelector('input[type="checkbox"]')).toBeChecked()
    expect(screen.queryByText('由系统维护')).not.toBeInTheDocument()
  })

  it('新增模式应用服务端默认值（单别/单号/日期）', async () => {
    const withDefaults: FormDefinition = {
      ...formDefinition,
      masterFields: [
        field('ORDER_TYPE', '订单别'),
        field('ORDER_NO', '订单号'),
        field('ORDER_DATE', '订单日期', { dataType: 'datetime' }),
      ],
      defaultValues: { ORDER_TYPE: 'DD', ORDER_NO: 'DD26080015', ORDER_DATE: '2026-08-10' },
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withDefaults
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const inputs = Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control:not([disabled])'))
    expect(inputs.find(input => input.value === 'DD')).toBeTruthy()
    expect(inputs.find(input => input.value === 'DD26080015')).toBeTruthy()
    expect(inputs.find(input => input.value === '2026-08-10')).toBeTruthy()
  })

  it('明细网格显示序号与操作列', async () => {
    renderEditor('/document-workbench/1209/edit?key=["P1","A"]')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(screen.getByText('序号')).toBeInTheDocument()
    expect(screen.getByText('操作')).toBeInTheDocument()
    expect(screen.getByText('1')).toBeInTheDocument()
  })

  it('明细行不足 5 行时渲染空行占位', async () => {
    const { container } = renderEditor('/document-workbench/1209/edit?key=["P1","A"]')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 编辑模式有 1 行真实明细 → 补 4 行占位；新增模式 0 行 → 补 5 行
    expect(container.querySelectorAll('tr.erp-detail-filler')).toHaveLength(4)
    expect(container.querySelectorAll('tbody tr')).toHaveLength(5)
  })

  it('新增模式 0 行明细时渲染 5 行空行占位', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(container.querySelectorAll('tr.erp-detail-filler')).toHaveLength(5)
  })

  it('按页签分组渲染并可切换', async () => {
    const tabbed: FormDefinition = {
      ...formDefinition,
      tabs: [{ no: 1, title: '基本资料' }, { no: 2, title: '其它' }],
      masterFields: [field('A', '字段A', { tabNo: 1 }), field('B', '字段B', { tabNo: 2 })],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return tabbed
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '基本资料' })).toHaveClass('active')
    expect(screen.getByText('字段A')).toBeInTheDocument()
    expect(screen.queryByText('字段B')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '其它' }))
    expect(screen.getByText('字段B')).toBeInTheDocument()
    expect(screen.queryByText('字段A')).not.toBeInTheDocument()
  })

  it('页内不显示大标题，操作按钮与页签同排', async () => {
    const tabbed: FormDefinition = {
      ...formDefinition,
      tabs: [{ no: 1, title: '基本资料' }],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return tabbed
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(screen.queryByText('新建产品版次')).not.toBeInTheDocument()
    const toolbar = container.querySelector('.erp-form-toolbar')
    expect(toolbar).not.toBeNull()
    // 工具栏独立一行（保存/取消按钮），页签单独一行在表单网格上方
    expect(toolbar?.querySelector('button')).not.toBeNull()
    expect(container.querySelector('.erp-form-tabs')).not.toBeNull()
  })

  it('复合单元格：从字段与主字段同格、无独立标签', async () => {
    const composite: FormDefinition = {
      ...formDefinition,
      masterFields: [
        field('CLIENT_ID', '客户', { isRequired: true, cellGroup: 'CLIENT', cellRole: 1, choosers: [{ active: true, table: 'CLIENT', description: null, moduleId: null, filter: null, returnMapping: 'txt_CLIENT_ID=CLIENT_ID;txt_CLIENT_NAME=CLIENT_NAME' }] }),
        field('CLIENT_NAME', '客户名称', { cellGroup: 'CLIENT', cellRole: 2, displayOnly: true, isReadonly: true }),
      ],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return composite
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const cell = container.querySelector('.erp-form-cell')
    expect(cell).not.toBeNull()
    expect(cell?.querySelectorAll('input')).toHaveLength(2)
    expect(container.querySelectorAll('.erp-form-field')).toHaveLength(0)
    expect(cell?.textContent).toContain('客户')
    expect(cell?.textContent).not.toContain('客户名称')
  })

  it('客户端校验拦截必填为空并展示字段错误', async () => {
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText(/数据校验未通过/)).toBeInTheDocument())
    expect(screen.getByText('该字段不能为空。')).toBeInTheDocument()
    expect(apiClientMock.post).not.toHaveBeenCalled()
  })

  it('新增保存成功后回列表页', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/document-workbench/1209/record',
      { values: { PRO_NO: 'P9', EDITION: '', QTY: '5', FLAG: '1' }, details: [] },
    ))
    await waitFor(() => expect(screen.getByText('BACK_LIST')).toBeInTheDocument())
  })

  it('保存 400 展示服务端字段错误', async () => {
    apiClientMock.post.mockRejectedValue(new ApiError(400, {
      code: 'VALIDATION',
      message: '校验失败',
      fieldErrors: [{ field: 'QTY', message: '数量不能超过库存。', code: 'RANGE' }],
    }))
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText(/数据校验未通过：数量不能超过库存。/)).toBeInTheDocument())
  })

  it('编辑模式加载记录、保存时携带 original 与主键 key', async () => {
    renderEditor('/document-workbench/1209/edit?key=["P1","A"]')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(screen.getByDisplayValue('P1')).toBeInTheDocument()
    expect(screen.getByDisplayValue('X1')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      expect.stringContaining('/document-workbench/1209/record?key='),
      expect.objectContaining({
        values: expect.objectContaining({ PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: 'true' }),
        original: expect.objectContaining({ PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: 'true' }),
        details: [{ ITEM: 'X1' }],
      }),
    ))
    await waitFor(() => expect(screen.getByText('BACK_LIST')).toBeInTheDocument())
  })

  it('主表必填缺失时拒绝新增明细行', async () => {
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    await waitFor(() => expect(screen.getByText('请先填写主表字段：PRO_NO，再新增明细。')).toBeInTheDocument())
  })

  it('新增与删除明细行', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: '删除' })).toHaveLength(1))
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument())
  })

  it('新增明细行预填主表同名值', async () => {
    const withQty: FormDefinition = {
      ...formDefinition,
      detailFields: [field('QTY', '数量', { dataType: 'decimal' }), field('ITEM', '明细项')],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withQty
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 主表 QTY 默认 5，新增明细行后 QTY 应带入
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.change(masterInputs(container)[2], { target: { value: '8' } })
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    const detailInputs = container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control')
    expect(detailInputs[0]).toHaveValue(8)
  })

  it('明细主键关联列只读（服务端持有）', async () => {
    const withPkDetail: FormDefinition = {
      ...formDefinition,
      masterPkOrder: ['PRO_NO', 'EDITION'],
      detailFields: [field('PRO_NO', '产品编号', { isPrimaryKey: true, isReadonly: true, serverFilled: true }), field('ITEM', '明细项')],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withPkDetail
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/document-workbench/1209/edit?key=["P1","A"]')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const detailInputs = container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control')
    expect(detailInputs[0]).toBeDisabled()
  })

  it('全选并删除所选明细行', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    expect(container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler)')).toHaveLength(2)
    fireEvent.click(screen.getByRole('checkbox', { name: '全选' }))
    fireEvent.click(screen.getByRole('button', { name: /删除所选/ }))
    expect(container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler)')).toHaveLength(0)
  })

  it('明细列点击排序（升序/降序切换）', async () => {
    const withRows: FormDefinition = {
      ...formDefinition,
      detailFields: [field('ITEM', '明细项'), field('QTY', '数量', { dataType: 'decimal' })],
    }
    const bundle = {
      master: { PRO_NO: 'P1', EDITION: 'A' },
      details: [{ ITEM: 'B', QTY: '10' }, { ITEM: 'A', QTY: '2' }],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withRows
      if (p.includes('/record')) return bundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/document-workbench/1209/edit?key=["P1","A"]')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const detailInputs = () => Array.from(container.querySelectorAll<HTMLInputElement>('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control'))
    // 初始顺序 B/A（每行两个输入：ITEM 在 0/2，QTY 在 1/3）
    expect(detailInputs()[0]).toHaveValue('B')
    // 按数量升序 → A(2)/B(10)
    fireEvent.click(screen.getByLabelText('表头操作数量'))
    fireEvent.click(screen.getByRole('button', { name: '升序' }))
    expect(detailInputs()[0]).toHaveValue('A')
    expect(detailInputs()[2]).toHaveValue('B')
    // 再点降序 → B(10)/A(2)
    fireEvent.click(screen.getByLabelText('表头操作数量'))
    fireEvent.click(screen.getByRole('button', { name: '降序' }))
    expect(detailInputs()[0]).toHaveValue('B')
    expect(detailInputs()[2]).toHaveValue('A')
  })

  it('选择器按 returnMapping 回填主表字段并置脏', async () => {
    renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '产品编号' })).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('高强钢')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '查询' }))
    fireEvent.click(screen.getByText('高强钢').closest('tr')! as HTMLElement)
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByDisplayValue('P9')).toBeInTheDocument())
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
  })

  it('修改字段后触发未保存离开提示', async () => {
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'X' } })
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
  })

  it('保存失败展示通用错误', async () => {
    apiClientMock.post.mockRejectedValue(new Error('网络错误'))
    const { container } = renderEditor('/document-workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('网络错误')).toBeInTheDocument())
  })
})
