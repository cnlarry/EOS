import { fireEvent, render, screen, waitFor } from '@testing-library/react'
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
  columns: 2,
  tabs: [
    { no: 1, title: '' },
    { no: 2, title: '明细信息' },
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

  it('加载后渲染画布与明细表头，并给出添加字段/添加列入口', async () => {
    renderPage()
    expect(await screen.findByText('ORDER_NO')).toBeInTheDocument()
    // 明细列以真实表头横铺（而不是竖排列表）
    expect(screen.getByText(/明细列（COP_ORDER_D）/)).toBeInTheDocument()
    // 表头显示字段名（与运行态明细网格一致），而不是字段代号
    expect(screen.getByText('产品编号')).toBeInTheDocument()
    // 主表加字段走画布末尾；明细加列走明细面板标题栏右上角
    expect(screen.getByRole('button', { name: /添加字段/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /添加列/ })).toBeInTheDocument()
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
