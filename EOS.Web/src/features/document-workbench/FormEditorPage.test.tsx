import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { createMemoryRouter, RouterProvider, useParams } from 'react-router-dom'
import { WorkspaceDirtyContext, WorkspaceTabContext, type TabDirtyHandlers } from '../../components/layout/workspaceDirty'
import { ToastProvider } from '../../components/ui/Toast'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { AuthContext, type AuthContextValue } from '../auth/authContext'
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
  canFormDesign: false,
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

function renderEditor(initialEntry: string, dirty?: {
  setDirty: (tabId: string, dirty: boolean) => void
  register: (tabId: string, handlers: TabDirtyHandlers) => () => void
}, state?: unknown) {
  const router = createMemoryRouter(
    [
      { path: '/workbench/:moduleId', element: <div>BACK_LIST</div> },
      { path: '/workbench/:moduleId/new', element: <FormEditorPage /> },
      { path: '/workbench/:moduleId/edit/*', element: <FormEditorPage /> },
      { path: '/workbench/:moduleId/view/*', element: <FormEditorPage /> },
    ],
    { initialEntries: [state === undefined ? initialEntry : { pathname: initialEntry, state }] },
  )
  // 脏位登记给外壳（WorkspaceDirtyContext），未注入时按无外壳独立渲染。
  // 弹窗方式的"工作台底图"会渲染列表页，它要外壳的认证上下文：这里给一份只读替身（不发起会话请求）。
  const authStub: AuthContextValue = {
    bootstrap: null, loading: false,
    login: async () => undefined, logout: async () => undefined,
    hasPermission: () => true,
  }
  return renderWithProviders(
    <WorkspaceTabContext.Provider value="t1">
      <AuthContext.Provider value={authStub}>
        <WorkspaceDirtyContext.Provider value={dirty ? { register: dirty.register, setDirty: dirty.setDirty } : null}>
          {/* 单据操作的结果走全局轻提示，页面依赖 ToastProvider */}
          <ToastProvider>
            <RouterProvider router={router} />
          </ToastProvider>
        </WorkspaceDirtyContext.Provider>
      </AuthContext.Provider>
    </WorkspaceTabContext.Provider>,
  )
}

function masterInputs(container: HTMLElement): HTMLInputElement[] {
  return Array.from(container.querySelectorAll<HTMLInputElement>('input.form-control:not([disabled])'))
}

/** 弹窗方式的背景底图会渲染该模块的工作台列表：测试给它一份最小可用的列表定义与空数据。 */
const underlayListDefinition = {
  moduleId: 1209, title: '产品版次', masterTable: 'PRODUCT_EDITION', detailTable: '', masterFields: [], detailFields: [],
  hasAdd: true, hasEdit: true, masterPkOrder: ['PRO_NO'], hasWorkflow: false, ifCopy: false, searchMaster: false,
  searchDetail: false, buttons: null, formOpenMode: 'DIALOG',
}

/** 弹窗方式专用的 GET 桩：表单定义 + 记录 + 底图列表（顺序敏感：form-definition、records 必须先判）。 */
function mockDialogGet(formDefinitionReply: unknown, recordReply: unknown = recordBundle) {
  apiClientMock.get.mockImplementation(async (path: string) => {
    const p = String(path)
    if (p.includes('/form-definition')) return formDefinitionReply
    if (p.includes('/records')) return { rows: [], total: 0, page: 1, pageSize: 50 }
    if (p.includes('/record')) return recordReply
    if (p.includes('/definition')) return underlayListDefinition
    throw new Error(`unexpected GET ${p}`)
  })
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
    renderWithProviders(<ToastProvider><RouterProvider router={router} /></ToastProvider>)
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
    renderWithProviders(<ToastProvider><RouterProvider router={router} /></ToastProvider>)
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

  it('弹窗打开方式：表单内容装进模块声明尺寸的统一弹窗，背后是工作台列表底图', async () => {
    mockDialogGet({ ...formDefinition, openMode: 'DIALOG', dialogWidth: 900, dialogHeight: 600 })
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 沿用统一弹窗组件（components/ui/Modal）：壳、标题、关闭按钮都是那一套
    const dialog = container.querySelector<HTMLElement>('.modal-dialog.erp-form-dialog')
    expect(dialog).not.toBeNull()
    expect(dialog!.style.width).toBe('900px')
    expect(dialog!.style.height).toBe('600px')
    expect(dialog!.querySelector('.modal-header .modal-title')?.textContent).toBe('产品版次')
    // 工具条在窗体第一行（旧系统 ModifyToolBar 同位）、字段区在其下；统一弹窗不再有 footer
    const body = dialog!.querySelector('.modal-body') as HTMLElement
    expect(body.firstElementChild?.classList.contains('erp-form-toolbar')).toBe(true)
    expect(body.firstElementChild?.textContent).toContain('保存')
    expect(body.querySelector(':scope > .erp-form-page')).not.toBeNull()
    expect(dialog!.querySelector('.modal-footer')).toBeNull()
    expect(body.querySelector('.erp-form-grid')).not.toBeNull()
    // 背后渲染该模块的工作台列表当底图：弹窗是"浮在工作台上"，不是"先开空白页再弹窗"
    expect(container.querySelector('.erp-form-dialog-underlay')).not.toBeNull()
  })

  it('弹窗打开方式：与整页同一套——摆页签、只看当前页签的字段，列数按页签取', async () => {
    const withTabs = {
      ...formDefinition,
      openMode: 'DIALOG',
      // 页签 1 一行一列、页签 2 一行两列：同一份表单里两页签各排各的
      tabs: [
        { no: 1, title: '甲', columns: 1 },
        { no: 2, title: '乙', columns: 2 },
      ],
      masterFields: [
        field('PRO_NO', '产品编号', { isPrimaryKey: true, tabNo: 1 }),
        field('EDITION', '版次', { tabNo: 2 }),
      ],
    }
    mockDialogGet(withTabs)
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByText('产品编号')).toBeInTheDocument())
    // 页签在弹窗里照摆（用户 2026-10-06 推翻了"弹窗内不实现多页签"）：当前是页签 1
    const tabs = container.querySelectorAll('.erp-form-dialog .erp-form-tabs .nav-link')
    expect([...tabs].map(node => node.textContent)).toEqual(['甲', '乙'])
    expect(screen.queryByText('版次')).toBeNull()
    // 页签 1 声明一列 → 栅格一行一列
    expect(container.querySelector<HTMLElement>('.erp-form-dialog .erp-form-row')?.style.gridTemplateColumns)
      .toBe('repeat(1, minmax(0, 1fr))')

    fireEvent.click(screen.getByRole('button', { name: '乙' }))
    await waitFor(() => expect(screen.getByText('版次')).toBeInTheDocument())
    expect(screen.queryByText('产品编号')).toBeNull()
    // 切到页签 2（声明两列）→ 同一个窗体里栅格变两列
    expect(container.querySelector<HTMLElement>('.erp-form-dialog .erp-form-row')?.style.gridTemplateColumns)
      .toBe('repeat(2, minmax(0, 1fr))')
  })

  it('本页签打开方式：摆页签、且不套窗体容器与底图', async () => {
    const withTabs = {
      ...formDefinition,
      tabs: [{ no: 1, title: '甲' }, { no: 2, title: '乙' }],
      masterFields: [
        field('PRO_NO', '产品编号', { isPrimaryKey: true, tabNo: 1 }),
        field('EDITION', '版次', { tabNo: 2 }),
      ],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withTabs
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByText('产品编号')).toBeInTheDocument())
    expect(container.querySelector('.erp-form-tabs')).not.toBeNull()
    expect(screen.queryByText('版次')).toBeNull()
    await waitFor(() => expect(container.querySelector('.erp-form-dialog')).toBeNull())
    expect(container.querySelector('.erp-form-dialog-underlay')).toBeNull()
  })

  it('弹窗打开方式：顶部工具条只常显主干动作，其余收进「更多」', async () => {
    mockDialogGet({ ...formDefinition, openMode: 'DIALOG', hasWorkflow: true })
    const { container } = renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '更多' })).toBeInTheDocument())
    const strip = container.querySelector('.modal-body > .erp-form-toolbar') as HTMLElement
    // 常显：返回 + 主干动作，都排在窗体第一行
    for (const label of ['返回', '新增', '编辑', '删除', '批核']) {
      expect(within(strip).getByRole('button', { name: label })).toBeInTheDocument()
    }
    // 低频动作不在工具条里，而在「更多」菜单里（顺序与整页一致）
    expect(within(strip).queryByRole('button', { name: '复制' })).toBeNull()
    fireEvent.click(within(strip).getByRole('button', { name: '更多' }))
    const menu = screen.getByRole('menu', { name: '更多动作' })
    expect(within(menu).getByRole('menuitem', { name: /复制/ })).toBeInTheDocument()
    expect(within(menu).getByRole('menuitem', { name: /审批历史/ })).toBeInTheDocument()
    // 菜单项就是同一个动作：点「审批历史」按原样打开审批历史窗体
    fireEvent.click(within(menu).getByRole('menuitem', { name: /审批历史/ }))
    await waitFor(() => expect(screen.getByRole('dialog', { name: '审批历史' })).toBeInTheDocument())
  })

  it('整页打开方式：不出现「更多」，动作仍一行铺开', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '复制' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '更多' })).toBeNull()
    expect(container.querySelector('.erp-form-toolbar [role="toolbar"]')).not.toBeNull()
  })

  it('弹窗打开方式：窗体右上角关闭与「取消」同路（回列表）', async () => {
    mockDialogGet({ ...formDefinition, openMode: 'DIALOG' })
    renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '关闭' }))
    await waitFor(() => expect(screen.getByText('BACK_LIST')).toBeInTheDocument())
  })

  it('弹窗打开方式：记录加载期间窗体不消失（不抖回整页加载态）', async () => {
    mockDialogGet({ ...formDefinition, openMode: 'DIALOG', dialogWidth: 900, dialogHeight: 600 }, new Promise(() => undefined))
    const { container } = renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(container.querySelector('.erp-form-dialog')).not.toBeNull())
    // 加载态渲染在窗体内部，而不是把窗体收掉换成整页；尺寸随定义到达后取定义值
    await waitFor(() => expect(container.querySelector('.erp-form-dialog')!.textContent ?? '').toContain('正在加载表单…'))
    await waitFor(() => expect(container.querySelector<HTMLElement>('.erp-form-dialog')!.style.width).toBe('900px'))
  })

  it('弹窗打开方式：带窗体提示进入时首帧就是窗体（列表页递来的尺寸）', async () => {
    apiClientMock.get.mockImplementation(() => new Promise(() => undefined))
    const { container } = renderEditor('/workbench/1209/new', undefined, {
      formShell: { openMode: 'DIALOG', title: '产品版次', width: 880, height: 620 },
    })
    const dialog = container.querySelector<HTMLElement>('.erp-form-dialog')
    expect(dialog).not.toBeNull()
    expect(dialog!.style.width).toBe('880px')
    expect(dialog!.querySelector('.modal-body')?.textContent).toContain('正在加载表单…')
  })

  it('弹窗打开方式：重新挂载（浏览→编辑）首帧就是窗体，不闪整页加载态', async () => {
    mockDialogGet({ ...formDefinition, openMode: 'DIALOG', dialogWidth: 900, dialogHeight: 600 })
    const first = renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(first.container.querySelector('.erp-form-dialog')).not.toBeNull())
    first.unmount()
    // 重新挂载时定义还没回来：容器按上一次已知的窗体给，加载态落在窗体内部
    apiClientMock.get.mockImplementation(() => new Promise(() => undefined))
    const second = renderEditor('/workbench/1209/edit/P1/A')
    expect(second.container.querySelector('.erp-form-dialog')).not.toBeNull()
    expect(second.container.querySelector('.modal-body')?.textContent).toContain('正在加载表单…')
  })

  it('页签声明的栅格列数决定一行几列', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      // 一行几列只看页签（模块级那层已随迁移 322 删除）
      if (p.includes('/form-definition')) {
        return { ...formDefinition, tabs: [{ no: 1, title: '', columns: 2 }] }
      }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const row = container.querySelector<HTMLElement>('.erp-form-row')
    expect(row!.style.gridTemplateColumns).toBe('repeat(2, minmax(0, 1fr))')
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

  it('浏览态工具栏完整顺序：批核/审批历史/结案/附件/打印按约定排列', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return { ...formDefinition, hasWorkflow: true, canEndCase: true, canFileView: true }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')
    const toolbar = (await screen.findByRole('button', { name: '返回' })).closest('[role="toolbar"]')!
    const order = Array.from(toolbar.querySelectorAll('button')).map(button => (button.textContent ?? '').trim())
    // 「帮助」已随 HELP_URL 退场（迁移 321）：定序止于打印
    expect(order).toEqual(['返回', '新增', '复制', '编辑', '删除', '批核', '审批历史', '结案', '附件', '打印'])
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

  it('浏览态明细只读：隐藏增删入口与选择/操作列，无输入控件', async () => {
    const { container } = renderEditor('/workbench/1209/view/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    await waitFor(() => expect(container.querySelector('.erp-detail-grid tbody tr')).not.toBeNull())
    expect(screen.queryByRole('button', { name: '新增一行' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /删除所选/ })).not.toBeInTheDocument()
    expect(screen.queryByText('操作')).not.toBeInTheDocument()
    expect(container.querySelector('.erp-detail-grid input[type="checkbox"]')).toBeNull()
    expect(container.querySelector('.erp-detail-grid input.form-control')).toBeNull()
    // 电子表格纯文本展示：无表单控件外壳，内容与工作台子表一致
    expect(container.querySelector('.erp-detail-grid .erp-form-control')).toBeNull()
    expect(container.querySelector('.erp-detail-grid tbody')).toHaveTextContent('X1')
  })

  it('浏览态点「新增」：原地切换后不残留上一张单据的明细行', async () => {
    const { container } = renderEditor('/workbench/1209/view/P1/A')
    const newButton = await screen.findByRole('button', { name: '新增' })
    await waitFor(() => expect(container.querySelector('.erp-detail-grid tbody')).toHaveTextContent('X1'))
    fireEvent.click(newButton)
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 新增态应是空明细：残留会把上一张单据的行按可编辑输入框渲染出来
    expect(container.querySelectorAll('.erp-detail-grid tbody tr')).toHaveLength(0)
    expect(screen.queryByDisplayValue('X1')).not.toBeInTheDocument()
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

  it('多来源字段选过来源后保存：把所选来源随载荷下发', async () => {
    const multiSource: FormDefinition = {
      ...formDefinition,
      masterFields: [
        field('CLIENT_ID', '客户', {
          isRequired: true,
          cellGroup: 'CLIENT',
          cellRole: 1,
          choosers: [
            { active: true, table: 'CLIENT', description: '客户基本资料', moduleId: null, filter: null, returnMapping: '[{"target":"CLIENT_ID","column":"CLIENT_ID"}]', serialNo: 1 },
            { active: true, table: 'SUPPLIER', description: '厂商基本资料', moduleId: null, filter: null, returnMapping: '[{"target":"CLIENT_ID","column":"SUPPLIER_ID"}]', serialNo: 2 },
          ],
        }),
        field('CLIENT_NAME', '客户名称', { cellGroup: 'CLIENT', cellRole: 2, displayOnly: true, isReadonly: true }),
      ],
    }
    const supplierChooser = {
      columns: [{ key: 'SUPPLIER_ID', label: '厂商代号' }, { key: 'SUPPLIER_NAME', label: '厂商名称' }],
      rows: [{ SUPPLIER_ID: 'S1', SUPPLIER_NAME: '双和' }],
      total: 1,
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return multiSource
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return supplierChooser
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    // 多来源先在选择器按钮下方弹来源菜单：选第 2 个来源（厂商），再在选择器里选一条记录
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    fireEvent.click(await screen.findByRole('menuitem', { name: '厂商基本资料' }))
    fireEvent.click(await screen.findByText('双和'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/document-workbench/1209/record',
      expect.objectContaining({ chooserSources: { CLIENT_ID: 2 } }),
    ))
  })

  it('未选过来源时不下发来源字段（保持服务端既有记忆）', async () => {
    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'P9' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalled())
    const body = apiClientMock.post.mock.calls.find(([path]) => path === '/document-workbench/1209/record')?.[1] as { chooserSources?: unknown, detailChooserSources?: unknown }
    expect(body?.chooserSources).toBeUndefined()
    expect(body?.detailChooserSources).toBeUndefined()
  })

  it('编辑保存时回传明细各行原有项次（行身份，服务端不再重编号）', async () => {
    const withSerial: FormDefinition = {
      ...formDefinition,
      detailFields: [
        field('SERIAL_NO', '项次', { isReadonly: true }),
        field('ITEM', '明细项', { isRequired: true }),
      ],
    }
    const bundle = {
      master: { PRO_NO: 'P1', EDITION: 'A', QTY: '10', FLAG: true },
      details: [{ SERIAL_NO: '1', ITEM: 'X1' }, { SERIAL_NO: '3', ITEM: 'X3' }],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withSerial
      if (p.includes('/record')) return bundle
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      expect.stringContaining('/document-workbench/1209/record'),
      expect.objectContaining({ detailSerials: ['1', '3'] }),
    ))
  })

  it('客户端校验拦截必填为空并展示字段错误', async () => {    renderEditor('/workbench/1209/new')
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

  it('明细数量连续输入不丢焦点', async () => {
    // 列定义里的 cell 必须是稳定身份：写成行内箭头函数时，每次渲染 React 都会把整格卸载重挂，
    // 输入框随之失焦（表现为每敲一个字符都要重新点一下）。
    const withQty: FormDefinition = {
      ...formDefinition,
      detailFields: [
        field('ITEM', '明细项'),
        field('QTY', '数量', { dataType: 'decimal' }),
        field('AMOUNT', '金额', { dataType: 'decimal', serverFilled: true, isReadonly: true }),
      ],
    }
    const bundle = { master: { PRO_NO: 'P1', EDITION: 'A' }, details: [{ ITEM: 'X1', QTY: '1', AMOUNT: '10' }] }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return withQty
      if (p.includes('/record')) return bundle
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/workbench/1209/edit/P1/A')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    const detailInputs = () => Array.from(container.querySelectorAll<HTMLInputElement>('.erp-detail-grid tbody tr:not(.erp-detail-filler) input.form-control'))
    // ITEM 在前、QTY 在后；只读金额列渲染为静态文本不占输入框
    const qty = detailInputs()[1]!
    qty.focus()
    // 逐字键入 1000：焦点全程留在同一个控件上，值逐字累加（每次渲染都换个输入框的话第二笔就落空）
    for (const text of ['1', '10', '100', '1000']) {
      fireEvent.change(detailInputs()[1]!, { target: { value: text } })
    }
    expect(detailInputs()[1]).toBe(qty)
    expect(qty).toHaveValue('1000')
    expect(document.activeElement).toBe(qty)
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
    const setDirty = vi.fn()
    renderEditor('/workbench/1209/new', { setDirty, register: vi.fn() })
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '产品编号' })).toBeInTheDocument())
    await waitFor(() => expect(screen.getByText('高强钢')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '查询' }))
    fireEvent.click(screen.getByText('高强钢').closest('tr')! as HTMLElement)
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    await waitFor(() => expect(screen.getByDisplayValue('P9')).toBeInTheDocument())
    await waitFor(() => expect(setDirty).toHaveBeenCalledWith('t1', true))
  })

  it('回填映射键名写成 PascalCase 的存量行同样回填（选客户不再"没有任何反应"）', async () => {
    const baselineCasing: FormDefinition = {
      ...formDefinition,
      masterFields: [
        field('CLIENT_ID', '客户', {
          isRequired: true,
          cellGroup: 'CLIENT',
          cellRole: 1,
          choosers: [{ active: true, table: 'CLIENT', description: '客户基本资料', moduleId: null, filter: null, returnMapping: '[{"Target":"CLIENT_ID","Column":"CLIENT_ID"},{"Target":"CLIENT_NAME","Column":"CLIENT_NAME"}]', serialNo: 1 }],
        }),
        field('CLIENT_NAME', '客户名称', { cellGroup: 'CLIENT', cellRole: 2, isReadonly: true }),
      ],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return baselineCasing
      if (p.includes('/record')) return recordBundle
      if (p.includes('/form-chooser/')) return { columns: [{ key: 'CLIENT_ID', label: '客户代号' }, { key: 'CLIENT_NAME', label: '客户名称' }], rows: [{ CLIENT_ID: 'C1', CLIENT_NAME: '张氏' }], total: 1 }
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1405/new')
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '选择' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: '客户（客户基本资料）' })).toBeInTheDocument())
    fireEvent.click((await screen.findByText('张氏')).closest('tr')! as HTMLElement)
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    // 主字段与同格从字段都要回填：只填主字段说明映射只读到了一半
    await waitFor(() => expect(screen.getByDisplayValue('C1')).toBeInTheDocument())
    expect(screen.getByDisplayValue('张氏')).toBeInTheDocument()
  })

  it('修改字段后把脏位上报告外壳（离开确认由外壳统一处理）', async () => {
    const setDirty = vi.fn()
    const { container } = renderEditor('/workbench/1209/new', { setDirty, register: vi.fn() })
    await waitFor(() => expect(screen.getByRole('button', { name: '保存' })).toBeInTheDocument())
    fireEvent.change(masterInputs(container)[0], { target: { value: 'X' } })
    await waitFor(() => expect(setDirty).toHaveBeenCalledWith('t1', true))
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

  // ===== 自定义按钮（单据操作）=====

  /** 只在下发名单里出现的按钮才渲染：未授权的操作不是"禁用"，而是根本不存在。 */
  it('浏览态按落点渲染自定义按钮：单据级在工具条尾部，明细级在子表标题栏', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) {
        return {
          ...formDefinition,
          userActions: [
            { key: 'relocate-stock', label: '归位到库位', confirmTag: false, failMode: 'BLOCK', placement: 'master', params: null },
            { key: 'generate-adjustment', label: '生成调整单', confirmTag: true, failMode: 'BLOCK', placement: 'detail', params: null },
          ],
        }
      }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    const { container } = renderEditor('/workbench/1209/view/P1/A')

    const masterButton = await screen.findByRole('button', { name: '归位到库位' })
    const detailButton = screen.getByRole('button', { name: '生成调整单' })
    // 明细级落在子表标题栏；单据级落在主表工具条（帮助之后）
    expect(container.querySelector('.erp-detail-toolbar')?.contains(detailButton)).toBe(true)
    expect(container.querySelector('.erp-form-toolbar')?.contains(masterButton)).toBe(true)
    expect(masterButton.compareDocumentPosition(detailButton)).toBeTruthy()
  })

  it('未下发自定义按钮时不渲染任何操作按钮', async () => {
    renderEditor('/workbench/1209/view/P1/A')

    await waitFor(() => expect(screen.getByRole('button', { name: '返回' })).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: '归位到库位' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '生成调整单' })).not.toBeInTheDocument()
  })

  it('单据级按钮点击发起操作请求并刷新单据（幂等键走请求头）', async () => {
    const postMock = vi.fn().mockResolvedValue({ outcome: 'refreshed', message: '账面数量已重算' })
    apiClientMock.post = postMock
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) {
        return {
          ...formDefinition,
          userActions: [
            { key: 'recalc-account', label: '重算账面数量', confirmTag: false, failMode: 'BLOCK', placement: 'master', params: null },
          ],
        }
      }
      if (p.includes('/record')) return recordBundle
      throw new Error(`unexpected GET ${p}`)
    })
    renderEditor('/workbench/1209/view/P1/A')

    fireEvent.click(await screen.findByRole('button', { name: '重算账面数量' }))

    await waitFor(() => expect(postMock).toHaveBeenCalled())
    const [url, body, init] = postMock.mock.calls[0]
    expect(url).toBe('/document-workbench/1209/action/recalc-account')
    expect(body).toMatchObject({ key: ['P1', 'A'], confirm: true })
    expect(String(init.headers['X-Idempotency-Key']).length).toBeGreaterThan(0)
    expect(await screen.findByText('账面数量已重算')).toBeInTheDocument()
  })

  it('整节单栅格：字段按显式装箱落位（半行两个一行、整行独占、备注整行两行高）', async () => {
    const gridForm = {
      ...formDefinition,
      masterFields: [
        field('A', '甲', { span: 2 }),
        field('B', '乙', { span: 2 }),
        field('C', '丙', { span: 2 }),
        field('REMARK', '备注', { span: 4, rowSpan: 2 }),
        field('D', '丁', { span: 2 }),
      ],
    }
    apiClientMock.get.mockImplementation(async (path: string) => {
      const p = String(path)
      if (p.includes('/form-definition')) return gridForm
      if (p.includes('/record')) return { master: { A: '1', B: '2', C: '3', D: '4', REMARK: '备注内容' }, details: [] }
      if (p.includes('/form-chooser/')) return chooserData
      throw new Error(`unexpected GET ${p}`)
    })

    const { container } = renderEditor('/workbench/1209/new')
    await waitFor(() => expect(screen.getByText('甲')).toBeInTheDocument())
    await waitFor(() => expect(container.querySelectorAll('.erp-form-slot')).toHaveLength(5))

    const slots = Array.from(container.querySelectorAll<HTMLElement>('.erp-form-slot'))
    // A/B 占第 1 行；C 另起第 2 行，D 回填第 2 行右侧（紧凑排列）；备注整行独占且两行高
    expect(slots.map(slot => [slot.style.gridColumn, slot.style.gridRow])).toEqual([
      ['1 / span 2', '1 / span 1'],
      ['3 / span 2', '1 / span 1'],
      ['1 / span 2', '2 / span 1'],
      ['3 / span 2', '2 / span 1'],
      ['1 / span 4', '3 / span 2'],
    ])
    // 页签没声明列数（历史快照）⇒ 兜底四子列
    const grid = container.querySelector<HTMLElement>('.erp-form-row')
    expect(grid?.style.gridTemplateColumns).toBe('repeat(4, minmax(0, 1fr))')
  })
})

