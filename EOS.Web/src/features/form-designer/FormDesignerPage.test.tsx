import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import FormDesignerPage from './FormDesignerPage'
import type { DesignRow, DesignState } from './types'

vi.mock('../../services/api', async () => ({
  apiClient: (await import('../../test/apiMock')).apiClientMock,
}))

function row(key: string, overrides: Partial<DesignRow> = {}): DesignRow {
  return {
    key,
    label: key,
    dataType: 'nvarchar',
    tabNo: 1,
    orderNo: 1,
    span: 1,
    rowSpan: 1,
    newLine: false,
    sectionId: null,
    cellGroup: null,
    cellRole: 0,
    hidden: false,
    locked: false,
    lockReason: null,
    userVisible: true,
    required: false,
    isPrimaryKey: false,
    hasChooser: false,
    isVirtual: false,
    ...overrides,
  }
}

const designState: DesignState = {
  moduleId: 1405,
  title: '客户订单',
  masterTable: 'COP_ORDER_M',
  detailTable: 'COP_ORDER_D',
  tabs: [
    { no: 1, title: '', columns: 2 },
    { no: 2, title: '明细信息', columns: 2 },
  ],
  master: {
    table: 'COP_ORDER_M',
    layout: [
      row('ORDER_NO', { label: '单号', orderNo: 1, isPrimaryKey: true, locked: true, lockReason: '主键列，始终显示', required: true }),
      row('CLIENT_ID', { label: '客户', orderNo: 2, hasChooser: true }),
      row('REMARK', { label: '备注', orderNo: 3 }),
      row('OLD_TAB_FIELD', { label: '旧页签字段', orderNo: 4, tabNo: 2 }),
    ],
    pool: [
      {
        key: 'CLIENT_NAME',
        label: '客户名称',
        dataType: 'nvarchar',
        userVisible: true,
        required: false,
        isPrimaryKey: false,
        hasChooser: false,
        isVirtual: false,
        locked: false,
        lockReason: null,
      },
    ],
  },
  detail: {
    table: 'COP_ORDER_D',
    layout: [
      row('PRO_NO', { label: '产品编号', orderNo: 1, isPrimaryKey: true, locked: true, required: true }),
      row('QTY', { label: '数量', orderNo: 2 }),
    ],
    pool: [],
  },
  baseUpdatedAt: '2026-09-25 05:00:00.000',
  openMode: 'TAB',
  dialogWidth: null,
  dialogHeight: null,
}

function renderPage() {
  // 保存/重置成功后会失效运行态缓存（['workbench', moduleId]），必须给一个真实 QueryClient
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <FormDesignerPage moduleId={1405} onExit={() => undefined} />
    </QueryClientProvider>,
  )
}

/** 右键某一格并点菜单项：格上不再挂动作按钮，全部版式动作走右键精修。 */
function rightClickCell(value: string) {
  const cell = screen.getByText(value).closest('.erp-designer-cell') as HTMLElement
  fireEvent.contextMenu(cell, { clientX: 10, clientY: 10 })
}

/** 明细表头的列名顺序（= 明细列的版式顺序）。 */
function detailHeadLabels(): (string | null)[] {
  return [...document.querySelectorAll('.erp-designer-detail-head .erp-designer-detail-label')]
    .map(node => node.textContent)
}

describe('FormDesignerPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(designState)
    apiClientMock.put.mockResolvedValue({
      status: 'saved',
      message: '已保存并生效（所有人可见）。',
      definitionVersion: 'module-1405-v9',
      state: designState,
    })
    apiClientMock.post.mockResolvedValue({
      status: 'saved',
      message: '已重置为默认版式并生效。',
      definitionVersion: 'module-1405-v10',
      state: designState,
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('加载后渲染画布与明细表头，并给出添加字段/字段管理入口', async () => {
    renderPage()
    expect(await screen.findByText('ORDER_NO')).toBeInTheDocument()
    // 明细列以真实表头横铺（而不是竖排列表）
    expect(screen.getByText(/明细列（COP_ORDER_D）/)).toBeInTheDocument()
    // 表头显示字段名（与运行态明细网格一致），而不是字段代号
    expect(screen.getByText('产品编号')).toBeInTheDocument()
    // 主表加字段走画布末尾；明细的选列与排序走明细面板标题栏右上角的「字段管理」
    expect(screen.getByRole('button', { name: /添加字段/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /字段管理/ })).toBeInTheDocument()
  })

  it('呈现配置入口在页签行右端：弹窗里改打开方式与窗体尺寸，随版式同一笔提交', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    // 入口在页签行最右端（原本的空白处），点开是「表单呈现」弹窗
    fireEvent.click(screen.getByLabelText('表单呈现'))
    const dialog = await screen.findByRole('dialog', { name: '表单呈现' })
    fireEvent.change(within(dialog).getByLabelText('打开方式'), { target: { value: 'DIALOG' } })
    fireEvent.change(within(dialog).getByLabelText('弹窗宽度（px）'), { target: { value: '900' } })
    fireEvent.change(within(dialog).getByLabelText('弹窗高度（px）'), { target: { value: '600' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '完成' }))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    const [, payload] = apiClientMock.put.mock.calls[0] as [string, {
      openMode: string
      dialogWidth: number | null
      dialogHeight: number | null
      tabs: { no: number; columns: number | null }[]
    }]
    expect(payload.openMode).toBe('DIALOG')
    expect(payload.dialogWidth).toBe(900)
    expect(payload.dialogHeight).toBe(600)
    // 列数不在呈现配置里：它随页签提交（夹具两个页签都声明两列 → 提交 2）
    expect(payload.tabs).toEqual([
      { no: 1, title: '', columns: 2 },
      { no: 2, title: '明细信息', columns: 2 },
    ])
  })

  it('弹窗方式：画板锁到模块声明的窗体宽高（所见即所得）', async () => {
    apiClientMock.get.mockResolvedValue({ ...designState, openMode: 'DIALOG', dialogWidth: 900, dialogHeight: 600 })
    const { container } = renderPage()
    await screen.findByText('ORDER_NO')
    const canvas = container.querySelector<HTMLElement>('.erp-designer-canvas')
    expect(canvas?.classList.contains('is-dialog')).toBe(true)
    expect(canvas?.style.width).toBe('900px')
    expect(canvas?.style.minHeight).toBe('600px')
  })

  it('切回本页签：窗体宽高被清掉（那两种方式不消费尺寸）', async () => {
    apiClientMock.get.mockResolvedValue({ ...designState, openMode: 'DIALOG', dialogWidth: 900, dialogHeight: 600 })
    const { container } = renderPage()
    await screen.findByText('ORDER_NO')
    fireEvent.click(screen.getByLabelText('表单呈现'))
    const dialog = await screen.findByRole('dialog', { name: '表单呈现' })
    fireEvent.change(within(dialog).getByLabelText('打开方式'), { target: { value: 'TAB' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '完成' }))

    // 画板不再锁尺寸，宽高输入框禁用且值已清空
    expect(container.querySelector('.erp-designer-canvas')?.classList.contains('is-dialog')).toBe(false)
    fireEvent.click(screen.getByLabelText('表单呈现'))
    expect(await screen.findByLabelText('弹窗宽度（px）')).toBeDisabled()
    expect(screen.getByLabelText('弹窗宽度（px）')).toHaveValue(null)
  })

  it('页签右键「布局列数」：改该页签的列数并把越界跨度夹回来', async () => {
    apiClientMock.get.mockResolvedValue({
      ...designState,
      columns: 4,
      tabs: [{ no: 1, title: '', columns: 4 }],
      master: {
        ...designState.master,
        layout: [row('PRO_NAME', { label: '产品名称', orderNo: 1, span: 4 })],
      },
    })
    const { container } = renderPage()
    await screen.findByText('产品名称')
    expect(container.querySelector<HTMLElement>('.erp-designer-sections')?.style.getPropertyValue('--erp-form-cols')).toBe('4')

    fireEvent.contextMenu(screen.getByRole('tab', { name: '默认' }), { clientX: 10, clientY: 10 })
    fireEvent.click(screen.getByLabelText('一行 2 列'))
    // 画板立刻按两列排，且该页签内 span=4 的行被夹到 2（调整即合规）
    await waitFor(() => expect(container.querySelector<HTMLElement>('.erp-designer-sections')?.style.getPropertyValue('--erp-form-cols')).toBe('2'))

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    const [, payload] = apiClientMock.put.mock.calls[0] as [string, {
      tabs: { no: number; columns: number | null }[]
      master: { key: string; span: number }[]
    }]
    expect(payload.tabs[0].columns).toBe(2)
    expect(payload.master.find(item => item.key === 'PRO_NAME')?.span).toBe(2)
  })

  it('明细「字段管理」：已选列可上下排序，确认后明细表头按新顺序重排', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    expect(detailHeadLabels()).toEqual(['产品编号', '数量'])
    fireEvent.click(screen.getByRole('button', { name: /字段管理/ }))
    const dialog = await screen.findByRole('dialog', { name: '字段管理（COP_ORDER_D）' })
    // 在「已选字段」里选中「数量」并上移，再确认
    fireEvent.change(within(dialog).getByLabelText('已选字段'), { target: { value: 'QTY' } })
    fireEvent.click(within(dialog).getByTitle('上移'))
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(detailHeadLabels()).toEqual(['数量', '产品编号']))
  })

  it('虚拟列不挂「虚」角标，改用 is-virtual 样式区分实体列', async () => {
    apiClientMock.get.mockResolvedValue({
      ...designState,
      master: {
        ...designState.master,
        layout: [
          row('PRO_NAME', { label: '产品名称', orderNo: 1, isVirtual: true }),
          row('UNIT', { label: '单位', orderNo: 2 }),
        ],
      },
    })
    renderPage()
    const virtual = await screen.findByText('PRO_NAME')
    expect(virtual.closest('.erp-designer-field')?.className).toContain('is-virtual')
    expect(screen.getByText('UNIT').closest('.erp-designer-field')?.className).not.toContain('is-virtual')
    expect(screen.queryByText('虚')).toBeNull()
  })

  it('明细在表头下渲染占位体：列名在表头、字段代号在格里', async () => {
    apiClientMock.get.mockResolvedValue({
      ...designState,
      detail: {
        ...designState.detail,
        layout: [
          row('PRO_NO', { label: '产品编号', orderNo: 1 }),
          row('PRO_NAME', { label: '产品名称', orderNo: 2, isVirtual: true }),
        ],
      },
    })
    renderPage()
    // 表头 = 列名（与运行态一致），占位体 = 字段代号
    expect((await screen.findByText('产品编号')).closest('th')).not.toBeNull()
    expect(screen.getByText('PRO_NO').closest('td.erp-designer-detail-cell')).not.toBeNull()
    // 虚拟列的样式落点在占位框上，表头不再需要角标
    expect(screen.getByText('PRO_NAME').closest('.erp-designer-field')?.className).toContain('is-virtual')
  })

  it('选中一列时表头与占位格一起高亮（点表头或点占位格都选中该列）', async () => {
    renderPage()
    fireEvent.click((await screen.findByText('产品编号')).closest('th') as HTMLElement)
    expect(document.querySelectorAll('.erp-designer-detail-head.is-selected').length).toBe(1)
    expect(document.querySelectorAll('.erp-designer-detail-cell.is-selected').length).toBe(1)

    // 点占位格同样选中该列，且只有这一列处于选中态
    fireEvent.click(screen.getByText('QTY').closest('td') as HTMLElement)
    expect(screen.getByText('产品编号').closest('th')?.className).not.toContain('is-selected')
    expect(screen.getByText('数量').closest('th')?.className).toContain('is-selected')
    expect(screen.getByText('QTY').closest('td')?.className).toContain('is-selected')
  })

  it('已移出表单的明细列不出现在表头上', async () => {
    apiClientMock.get.mockResolvedValue({
      ...designState,
      detail: {
        ...designState.detail,
        layout: [
          row('PRO_NO', { label: '产品编号', orderNo: 1 }),
          row('OLD_COL', { label: '旧列', orderNo: 2, hidden: true }),
        ],
      },
    })
    renderPage()
    expect(await screen.findByText('产品编号')).toBeInTheDocument()
    expect(screen.queryByText('旧列')).toBeNull()
  })

  it('点「添加字段」打开统一选择器（数据源为设计态字段池）', async () => {
    apiClientMock.post.mockResolvedValue({
      columns: [
        { key: 'F_ID', label: '字段名', dataType: 'nvarchar' },
        { key: 'F_DESC', label: '描述', dataType: 'nvarchar' },
      ],
      defaultKeys: ['F_ID', 'F_DESC'],
      rows: [{ F_ID: 'CLIENT_NAME', F_DESC: '客户名称' }],
      total: 1,
    })
    renderPage()
    await screen.findByText('ORDER_NO')
    fireEvent.click(screen.getByRole('button', { name: /添加字段/ }))
    expect(await screen.findByText(/添加字段（COP_ORDER_M）/)).toBeInTheDocument()
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalled())
    const [path, body] = apiClientMock.post.mock.calls.at(-1) as [string, { sourceKey?: string; args?: Record<string, string> }]
    expect(path).toBe('/chooser/query')
    expect(body.sourceKey).toBe('form-designer.fields')
    // 排除项来自当前草稿（显示中的行），而不是库里的版式行
    expect(body.args).toEqual({
      moduleId: '1405',
      table: 'master',
      exclude: 'ORDER_NO,CLIENT_ID,REMARK,OLD_TAB_FIELD',
    })
  })

  it('移出表单的字段不再进排除列表（否则选择器里选不回来）', async () => {
    apiClientMock.post.mockResolvedValue({
      columns: [
        { key: 'F_ID', label: '字段名', dataType: 'nvarchar' },
        { key: 'F_DESC', label: '描述', dataType: 'nvarchar' },
      ],
      defaultKeys: ['F_ID', 'F_DESC'],
      rows: [{ F_ID: 'REMARK', F_DESC: '备注' }],
      total: 1,
    })
    renderPage()
    await screen.findByText('ORDER_NO')
    rightClickCell('REMARK')
    fireEvent.click(screen.getByRole('button', { name: '移出表单' }))
    fireEvent.click(screen.getByRole('button', { name: /添加字段/ }))

    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalled())
    const [, body] = apiClientMock.post.mock.calls.at(-1) as [string, { args?: Record<string, string> }]
    expect(body.args?.exclude?.split(',')).toEqual(['ORDER_NO', 'CLIENT_ID', 'OLD_TAB_FIELD'])
  })

  it('移出表单后保存，提交的是整份版式且带幂等键', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    // REMARK 未锁定 → 允许移出表单
    rightClickCell('REMARK')
    fireEvent.click(screen.getByRole('button', { name: '移出表单' }))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    const [path, payload] = apiClientMock.put.mock.calls[0] as [string, {
      baseUpdatedAt: string | null
      idempotencyKey: string
      tabs: { no: number; title: string }[]
      master: { key: string; hidden: boolean }[]
      detail: { key: string }[]
    }]
    expect(path).toBe('/admin/form-layout/1405')
    expect(payload.baseUpdatedAt).toBe('2026-09-25 05:00:00.000')
    expect(payload.idempotencyKey.length).toBeGreaterThan(0)
    expect(payload.tabs.map(tab => tab.no)).toEqual([1, 2])
    expect(payload.master.map(item => item.key)).toEqual(['ORDER_NO', 'CLIENT_ID', 'REMARK', 'OLD_TAB_FIELD'])
    expect(payload.master.find(item => item.key === 'REMARK')?.hidden).toBe(true)
    expect(payload.detail.map(item => item.key)).toEqual(['PRO_NO', 'QTY'])
  })

  it('锁定字段的移除动作禁用并说明原因', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    rightClickCell('ORDER_NO')
    const removeButton = screen.getByRole('button', { name: '移出表单' }) as HTMLButtonElement
    expect(removeButton.disabled).toBe(true)
    expect(removeButton.title).toBe('主键列，始终显示')
  })

  it('新增页签后保存请求包含新页签', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    fireEvent.click(screen.getByTitle('新增页签'))
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    const payload = apiClientMock.put.mock.calls[0][1] as { tabs: { no: number; title: string }[] }
    expect(payload.tabs.map(tab => tab.no)).toEqual([1, 2, 3])
  })

  it('撤销回到改动前（Ctrl+Z）', async () => {
    renderPage()
    await screen.findByText('ORDER_NO')
    rightClickCell('REMARK')
    fireEvent.click(screen.getByRole('button', { name: '移出表单' }))
    expect(screen.getByText('已移出表单')).toBeInTheDocument()
    fireEvent.keyDown(window, { key: 'z', ctrlKey: true })
    await waitFor(() => expect(screen.queryByText('已移出表单')).toBeNull())
  })
})
