import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { createMemoryRouter, RouterProvider, useParams } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { FormEditorPage } from './FormEditorPage'
import type { FormDefinition } from './formDefinition'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

function field(key: string, label: string, overrides: Partial<FormDefinition['masterFields'][number]> = {}) {
  return {
    key, label, dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: '', isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null,
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: [], displayOnly: false, canCopy: true,
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
  ifCopy: true,
  searchMaster: false,
  searchDetail: false,
  masterFields: [
    field('PRO_NO', '产品编号', { isRequired: true, isPrimaryKey: true, choosers: [{ active: true, table: 'PRODUCT', description: null, moduleId: null, filter: null, returnMapping: '[{"target":"PRO_NO","column":"PRO_NO"},{"target":"PRO_NAME","column":"PRO_NAME"}]', serialNo: 1 }] }),
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
  columns: 4,
  buttons: null,
  hasWorkflow: false,
  hasStatelessApprove: false,
  defaultValues: {},
  canDelete: true,
  canApprove: true,
  canDeapprove: true,
  canEndCase: false,
  canUnEndCase: false,
  canAddNew: true,
  canEdit: true,
  canFileView: false,
  canFileUpda: false,
  canFileEdit: false,
  canFileDele: false,
  canSetup: false,
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

/** 返回目标探针：渲染当前 moduleId，用于断言返回按钮的去向。 */
function BackProbe() {
  const { moduleId } = useParams()
  return <div>BACK_LIST_{moduleId}</div>
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
  const router = createMemoryRouter(
    [
      { path: '/workbench/:moduleId', element: <div>BACK_LIST</div> },
      { path: '/workbench/:moduleId/new', element: <FormEditorPage /> },
      { path: '/workbench/:moduleId/edit/*', element: <FormEditorPage /> },
      { path: '/workbench/:moduleId/view/*', element: <FormEditorPage /> },
    ],
    { initialEntries: [initialEntry] },
  )
  return renderWithProviders(<RouterProvider router={router} />)
}

function masterInputs(container: HTMLElement): HTMLInputElement[] {
  return Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control:not([disabled])'))
}

describe('FormEditorPage', () => {
  beforeEach(() => {
    installApiMocks()
    vi.stubGlobal('confirm', vi.fn(() => true))
    vi.stubGlobal('alert', vi.fn())
    vi.stubGlobal('open', vi.fn())
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载中显示 LoadingState', () => {
    apiClientMock.get.mockReturnValue(new Promise(() => undefined))
    renderEditor('/workbench/1209/new')
    expect(screen.getByText('正在加载表单…')).toBeInTheDocument()
  })

  it('模块未启用（404）显示明确提示', async () => {
    apiClientMock.get.mockRejectedValue(new ApiError(404, { code: 'NOT_FOUND', message: 'not found' }))
    renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByText('该模块未启用统一表单编辑（含存盘后业务逻辑的模块暂不开放，或不在白名单内）。')).toBeInTheDocument())
  })

  it('跨模块关联浏览（from 参数）：返回按钮回到来源工作台列表', async () => {
    const router = createMemoryRouter(
      [
        { path: '/workbench/:moduleId', element: <BackProbe /> },
        { path: '/workbench/:moduleId/view/*', element: <FormEditorPage /> },
      ],
      { initialEntries: ['/workbench/1209/view/P1/A?from=1405'] },
    )
    renderWithProviders(<RouterProvider router={router} />)
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '返回' }))
    await waitFor(() => expect(screen.getByText('BACK_LIST_1405')).toBeInTheDocument())
  })

  it('常规浏览（无 from 参数）：返回按钮回到当前模块列表', async () => {
    const router = createMemoryRouter(
      [
        { path: '/workbench/:moduleId', element: <BackProbe /> },
        { path: '/workbench/:moduleId/view/*', element: <FormEditorPage /> },
      ],
      { initialEntries: ['/workbench/1209/view/P1/A'] },
    )
    renderWithProviders(<RouterProvider router={router} />)
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '返回' }))
    await waitFor(() => expect(screen.getByText('BACK_LIST_1209')).toBeInTheDocument())
  })

  it('新增模式按默认值初始化字段', async () => {
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(container.querySelector('.erp-form-grid')).not.toBeNull()
    expect(screen.getByDisplayValue('5')).toBeInTheDocument()
    expect(container.querySelector('input[type="checkbox"]')).toBeChecked()
    expect(screen.queryByText('由系统维护')).not.toBeInTheDocument()
  })

  it('浏览模式（有工作流、未批核）显示批核/打印，无解批', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '批核' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '解批' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '打印' })).toBeInTheDocument()
  })

  it('浏览模式（已批核）显示解批/打印，无批核', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: true } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '解批' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '批核' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '打印' })).toBeInTheDocument()
  })

  it('浏览模式点击批核打开送审弹窗，确认后调用 approve 携带送审说明并刷新记录', async () => {
    const postMock = vi.fn().mockResolvedValue({ key: ['P1', 'A'] })
    apiClientMock.post.mockImplementation(postMock)
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    const approveButton = await screen.findByRole('button', { name: '批核' })
    fireEvent.click(approveButton)
    // 送审弹窗出现，可填写说明
    expect(screen.getByText('送审确认')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('送审说明'), { target: { value: '请加急审批' } })
    fireEvent.click(screen.getByRole('button', { name: '确认送审' }))
    await waitFor(() => expect(postMock).toHaveBeenCalledWith(
      '/document-workbench/1209/approve',
      expect.objectContaining({ key: JSON.stringify(['P1', 'A']), message: '请加急审批' }),
    ))
  })

  it('流程模块浏览态显示审批历史按钮，点击加载时间线弹窗', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: false } }
      if (p.includes('/workflow/1209/history')) return { rows: [
        { kind: 'task', step: '000', stepDesc: '提交送审', approver: 'admin', state: 'A', message: '请加急', date: '2026-08-26 10:00' },
        { kind: 'task', step: '001', stepDesc: '一级审批', approver: 'admin', state: 'Y', message: '同意', date: '2026-08-26 10:01' },
        { kind: 'confirm', step: '', stepDesc: '流程审批完成，单据已确认', approver: 'admin', state: 'Y', message: '', date: '' },
      ] }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    const historyButton = await screen.findByRole('button', { name: '审批历史' })
    fireEvent.click(historyButton)
    expect(await screen.findByText('审批历史（流程信息）')).toBeInTheDocument()
    expect(await screen.findByText('提交送审')).toBeInTheDocument()
    expect(screen.getByText('请加急')).toBeInTheDocument()
    expect(screen.getByText('流程完成')).toBeInTheDocument()
  })

  it('浏览模式点击结案调用 endcase 并刷新记录（FINISHED_TAG=false）', async () => {
    const postMock = vi.fn().mockResolvedValue({ key: ['P1', 'A'] })
    apiClientMock.post.mockImplementation(postMock)
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, canEndCase: true, canUnEndCase: false }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, FINISHED_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    const endcaseButton = await screen.findByRole('button', { name: '结案' })
    fireEvent.click(endcaseButton)
    await waitFor(() => expect(postMock).toHaveBeenCalledWith(
      '/document-workbench/1209/endcase',
      expect.objectContaining({ key: JSON.stringify(['P1', 'A']) }),
    ))
  })

  it('浏览模式未结案权限时不显示结案按钮', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, canEndCase: false, canUnEndCase: false }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, FINISHED_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '结案' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '取消结案' })).not.toBeInTheDocument()
  })

  it('浏览模式已审批（CONFIRM_TAG=true）时编辑按钮禁用，批核/解批互斥仍显示解批', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: true, FINISHED_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '解批' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '批核' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '编辑' })).toBeDisabled()
  })

  it('浏览模式无批核权限（canApprove=false）不显示批核按钮', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true, canApprove: false, canDeapprove: false }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: false, FINISHED_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '批核' })).not.toBeInTheDocument()
  })

  it('浏览模式已结案显示取消结案按钮（FINISHED_TAG=true + canUnEndCase）', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, canEndCase: true, canUnEndCase: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, FINISHED_TAG: true } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '取消结案' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '结案' })).not.toBeInTheDocument()
  })

  it('浏览模式已结案（FINISHED_TAG=true）时编辑/解批按钮禁用', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: true, FINISHED_TAG: true } }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '解批' })).toBeInTheDocument())
    expect(screen.getByRole('button', { name: '解批' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '编辑' })).toBeDisabled()
  })

  it('浏览模式显示删除按钮（红色危险样式、canDelete 控制）；按钮顺序按固定约定', async () => {
    const { unmount } = renderEditor('/workbench/1209/view/P1/A')
    const deleteButton = await screen.findByRole('button', { name: '删除' })
    expect(deleteButton).toHaveClass('btn-danger')
    expect(deleteButton).not.toBeDisabled()
    const addNew = screen.getByRole('button', { name: '新增' })
    expect(addNew).toHaveClass('btn-primary')
    const toolbar = addNew.closest('[role="toolbar"]')!
    const order = Array.from(toolbar.querySelectorAll('button')).map(button => (button.textContent ?? '').trim())
    expect(order).toEqual(['返回', '新增', '复制', '编辑', '删除', '打印'])
    unmount()
    installApiMocks()
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, canDelete: false }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '删除' })).not.toBeInTheDocument()
  })

  it('浏览态工具栏完整顺序：批核/审批历史/结案/附件/打印/帮助按约定排列', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true, canEndCase: true, canFileView: true, helpUrl: '/help/1209.html' }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    const toolbar = (await screen.findByRole('button', { name: '返回' })).closest('[role="toolbar"]')!
    const order = Array.from(toolbar.querySelectorAll('button')).map(button => (button.textContent ?? '').trim())
    expect(order).toEqual(['返回', '新增', '复制', '编辑', '删除', '批核', '审批历史', '结案', '附件', '打印', '帮助'])
  })

  it('浏览模式已批核时删除按钮禁用；点击删除（确认后）调用删除接口并返回列表', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return { ...recordBundle, master: { ...recordBundle.master, CONFIRM_TAG: true, FINISHED_TAG: false } }
      throw new Error(`unexpected GET ${p}`)
    })
    const { unmount: unmountConfirmed } = renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '删除' })).toBeDisabled())
    unmountConfirmed()
    installApiMocks()
    const { unmount } = renderEditor('/workbench/1209/view/P1/A')
    fireEvent.click(await screen.findByRole('button', { name: '删除' }))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith(
      '/document-workbench/1209/record?key=%5B%22P1%22%2C%22A%22%5D',
      expect.objectContaining({ headers: expect.objectContaining({ 'X-Idempotency-Key': expect.any(String) }) }),
    ))
    unmount()
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
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const inputs = Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control:not([disabled])'))
    expect(inputs.find(input => input.value === 'DD')).toBeTruthy()
    expect(inputs.find(input => input.value === 'DD26080015')).toBeTruthy()
    expect(inputs.find(input => input.value.startsWith('2026-08-10'))).toBeTruthy()
  })

  it('明细网格显示序号与操作列', async () => {
    renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(screen.getByText('序号')).toBeInTheDocument()
    expect(screen.getByText('操作')).toBeInTheDocument()
    expect(screen.getByText('1')).toBeInTheDocument()
  })

  it('明细网格不再渲染补空行', async () => {
    const { container } = renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 编辑模式有 1 行真实明细，无任何占位空行
    expect(container.querySelectorAll('tr.erp-detail-filler')).toHaveLength(0)
    expect(container.querySelectorAll('.erp-detail-grid tbody tr')).toHaveLength(1)
  })

  it('新增模式 0 行明细时空态显示「+ 新增一行」入口', async () => {
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    expect(container.querySelectorAll('.erp-detail-grid tbody tr')).toHaveLength(0)
    expect(screen.getByRole('button', { name: '+ 新增一行' })).toBeInTheDocument()
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
    renderEditor('/workbench/1209/new')
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
    const { container } = renderEditor('/workbench/1209/new')
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
        field('CLIENT_ID', '客户', { isRequired: true, cellGroup: 'CLIENT', cellRole: 1, choosers: [{ active: true, table: 'CLIENT', description: null, moduleId: null, filter: null, returnMapping: '[{"target":"CLIENT_ID","column":"CLIENT_ID"},{"target":"CLIENT_NAME","column":"CLIENT_NAME"}]', serialNo: 1 }] }),
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
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const cell = container.querySelector('.erp-form-cell')
    expect(cell).not.toBeNull()
    expect(cell?.querySelectorAll('input')).toHaveLength(2)
    expect(container.querySelectorAll('.erp-form-field')).toHaveLength(0)
    expect(cell?.textContent).toContain('客户')
    expect(cell?.textContent).not.toContain('客户名称')
  })

  it('客户端校验拦截必填为空并展示字段错误', async () => {
    renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText(/数据校验未通过/)).toBeInTheDocument())
    expect(screen.getByText('该字段不能为空。')).toBeInTheDocument()
    expect(apiClientMock.post).not.toHaveBeenCalled()
  })

  it('新增保存成功后进入浏览态', async () => {
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/document-workbench/1209/record',
      expect.objectContaining({ values: { PRO_NO: 'P9', EDITION: '', QTY: '5', FLAG: '1' }, details: [] }),
    ))
    // Save request must carry an idempotency key
    const saveBody = apiClientMock.post.mock.calls.find(([path]) => path === '/document-workbench/1209/record')?.[1] as { idempotencyKey?: string }
    expect(saveBody?.idempotencyKey).toBeTruthy()
    // 保存成功跳浏览态（返回按钮出现），不再回列表
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
  })

  it('保存 400 展示服务端字段错误', async () => {
    apiClientMock.post.mockRejectedValue(new ApiError(400, {
      code: 'VALIDATION',
      message: '校验失败',
      fieldErrors: [{ field: 'QTY', message: '数量不能超过库存。', code: 'RANGE' }],
    }))
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText(/数据校验未通过：数量不能超过库存。/)).toBeInTheDocument())
  })

  it('编辑模式加载记录、保存时携带 original 与主键 key', async () => {
    renderEditor('/workbench/1209/edit/P1/A')
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
    // 保存成功进入浏览态
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
  })

  it('主表必填缺失时拒绝新增明细行', async () => {
    renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    await waitFor(() => expect(screen.getByText('请先填写主表字段：PRO_NO，再新增明细。')).toBeInTheDocument())
  })

  it('新增与删除明细行', async () => {
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    // Row delete uses an icon button (aria-label 删除第N行)
    await waitFor(() => expect(screen.getAllByRole('button', { name: '删除第1行' })).toHaveLength(1))
    fireEvent.click(screen.getByRole('button', { name: '删除第1行' }))
    await waitFor(() => expect(screen.queryByRole('button', { name: '删除第1行' })).not.toBeInTheDocument())
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
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 主表 QTY 默认 5，新增明细行后 QTY 应带入
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.change(masterInputs(container)[2], { target: { value: '8' } })
    fireEvent.click(screen.getByRole('button', { name: '新增一行' }))
    const detailInputs = container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control')
    expect(detailInputs[0]).toHaveValue('8')
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
    const { container } = renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // Server-filled fields without a chooser render as readonly text, not a disabled input
    const staticCells = container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler) .erp-form-static')
    expect(staticCells.length).toBeGreaterThan(0)
    expect(container.querySelector('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control[data-field-key="PRO_NO"], .erp-detail-grid tbody tr:not(.erp-detail-filler) .erp-form-control[data-field-key="PRO_NO"] input')).toBeNull()
  })

  it('全选并删除所选明细行', async () => {
    const { container } = renderEditor('/workbench/1209/new')
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
    const { container } = renderEditor('/workbench/1209/edit/P1/A')
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

  it('明细排序为快照：排序后编辑行值不触发实时跳行', async () => {
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
    const { container } = renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const detailInputs = () => Array.from(container.querySelectorAll<HTMLInputElement>('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control'))
    fireEvent.click(screen.getByLabelText('表头操作数量'))
    fireEvent.click(screen.getByRole('button', { name: '升序' }))
    expect(detailInputs()[0]).toHaveValue('A')
    // 编辑首行数量后视图顺序保持（快照语义：排序只在切换/增删行时重算）
    fireEvent.change(detailInputs()[1], { target: { value: '99' } })
    expect(detailInputs()[0]).toHaveValue('A')
    expect(detailInputs()[2]).toHaveValue('B')
    expect(container.querySelectorAll('.erp-detail-grid tbody tr:not(.erp-detail-filler)')).toHaveLength(2)
  })

  it('选择器按 returnMapping 回填主表字段并置脏', async () => {
    renderEditor('/workbench/1209/new')
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
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'X' } })
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
  })

  it('保存失败展示通用错误', async () => {
    apiClientMock.post.mockRejectedValue(new Error('网络错误'))
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(screen.getByText('网络错误')).toBeInTheDocument())
  })

  it('明细金额联动：QTY/PRICE/税型/税率/折扣变更实时预览金额并汇总主表', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) {
        return {
          ...formDefinition,
          masterFields: [
            ...formDefinition.masterFields,
            field('AMOUNT', '金额', { dataType: 'decimal' }),
            field('TAX_SUM', '税额', { dataType: 'decimal', isReadonly: true }),
            field('AMOUNT_TAX', '价税合计', { dataType: 'decimal', isReadonly: true }),
          ],
          detailFields: [
            field('QTY', '数量', { dataType: 'decimal' }),
            field('PRICE', '单价', { dataType: 'decimal' }),
            field('TAX_RATE', '税率', { dataType: 'decimal' }),
            field('TAX_TYPE', '税型', { dataType: 'nvarchar' }),
            field('REBATE', '折扣', { dataType: 'decimal' }),
            field('AMOUNT', '金额', { dataType: 'decimal', isReadonly: true }),
            field('TAX_SUM', '税额', { dataType: 'decimal', isReadonly: true }),
            field('AMOUNT_TAX', '价税合计', { dataType: 'decimal', isReadonly: true }),
          ],
        }
      }
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 新增明细前先填主表字段（detailNoFields 校验 PRO_NO）
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(await screen.findByRole('button', { name: '新增一行' }))
    const detailInputs = () => Array.from(container.querySelectorAll<HTMLInputElement>(
      '.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control'))
    await waitFor(() => expect(detailInputs().length).toBeGreaterThan(0))
    fireEvent.change(detailInputs()[0], { target: { value: '10' } })   // QTY
    fireEvent.change(detailInputs()[1], { target: { value: '100' } })  // PRICE
    fireEvent.change(detailInputs()[3], { target: { value: 'O' } })    // TAX_TYPE
    fireEvent.change(detailInputs()[2], { target: { value: '13' } })   // TAX_RATE
    await waitFor(() => expect(detailInputs()[5].value).toBe('1000'))  // AMOUNT 预览
    expect(detailInputs()[6].value).toBe('130')                        // TAX_SUM 预览
    expect(detailInputs()[7].value).toBe('1130')                       // AMOUNT_TAX 预览
    // 主表金额汇总预览（明细走汇总时主表金额列强制只读，预览值写入 disabled input）
    const masterAmount = Array.from(container.querySelectorAll<HTMLInputElement>('.erp-form-grid input.form-control'))
      .find(input => input.value === '1000')
    expect(masterAmount).toBeTruthy()
  })
})

