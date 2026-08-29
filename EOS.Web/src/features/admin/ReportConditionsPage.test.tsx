import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportConditionsPage } from './ReportConditionsPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const modules = [
  { moduleId: 1209, description: '客户订单' },
  { moduleId: 1305, description: '库存日志' },
]

const allConditions = [
  { moduleId: 1209, moduleDescription: '客户订单', serialNo: 1, type: 1, field: 'COP_ORDER_M.ORDER_DATE', expression: null, description: '订单日期', defaultValue: '2026-01-01', parameterName: 'P_DATE', remark: null },
  { moduleId: 1209, moduleDescription: '客户订单', serialNo: 2, type: 2, field: 'COP_ORDER_M.STATUS', expression: '是:1;否:0', description: '状态', defaultValue: '1', parameterName: null, remark: null },
  { moduleId: 1305, moduleDescription: '库存日志', serialNo: 1, type: 3, field: 'INV_LOG.WAREHOUSE', expression: 'SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE', description: '仓库', defaultValue: null, parameterName: null, remark: null },
]

const chooserFields = {
  columns: [
    { key: 'T_ID', label: '表名', dataType: 'nvarchar' },
    { key: 'F_ID', label: '字段', dataType: 'nvarchar' },
    { key: 'F_DESC', label: '描述', dataType: 'nvarchar' },
  ],
  rows: [{ T_ID: 'COP_ORDER_M', F_ID: 'CUST_ID', F_DESC: '客户', F_TYPE: 'nvarchar' }],
  total: 1,
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ReportConditionsPage />
    </QueryClientProvider>,
  )
}

async function loaded() {
  await screen.findByText('订单日期')
  await waitFor(() => {
    expect(apiClientMock.get).toHaveBeenCalledWith('/report-conditions/conditions/all')
  })
}

function conditionRowOf(description: string) {
  return screen.getByText(description).closest('tr')!
}

describe('ReportConditionsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.includes('/conditions/all')) return allConditions
      if (path.includes('/modules')) return modules
      throw new Error(`unexpected path: ${path}`)
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return chooserFields
      return undefined
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('单表框架：全部条件行同表展示，含模块编号/模块名称列与类型中文', async () => {
    const { container } = renderPage()
    await loaded()
    expect(container.querySelector('.erp-full-list-page')).not.toBeNull()
    expect(screen.getByLabelText('搜索过滤条件')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '刷新' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增条件' })).toBeInTheDocument()
    // 两个模块的条件行同表：模块列 + 条件列（1209 两行、1305 一行）
    expect(screen.getAllByText('客户订单')).toHaveLength(2)
    expect(screen.getByText('库存日志')).toBeInTheDocument()
    expect(screen.getByText('订单日期')).toBeInTheDocument()
    expect(screen.getByText('范围')).toBeInTheDocument()
    expect(screen.getByText('是:1;否:0')).toBeInTheDocument()
    expect(screen.getByText(/共 3 条过滤条件（覆盖 2 个模块）/)).toBeInTheDocument()
  })

  it('搜索按模块编号/名称/字段客户端过滤', async () => {
    renderPage()
    await loaded()
    fireEvent.change(screen.getByLabelText('搜索过滤条件'), { target: { value: '1305' } })
    await waitFor(() => expect(screen.queryByText('订单日期')).not.toBeInTheDocument(), { timeout: 2000 })
    expect(screen.getByText('仓库')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('搜索过滤条件'), { target: { value: 'WAREHOUSE' } })
    await waitFor(() => expect(screen.queryByText('状态')).not.toBeInTheDocument(), { timeout: 2000 })
    expect(screen.getByText('仓库')).toBeInTheDocument()
  })

  /** 在条件弹窗内通过统一选择器选中查询字段（COP_ORDER_M.CUST_ID） */
  async function pickField(dialog: HTMLElement) {
    fireEvent.click(within(dialog).getByRole('button', { name: '选择字段…' }))
    const dialogs = screen.getAllByRole('dialog')
    const chooser = dialogs[dialogs.length - 1]
    const fieldRow = await within(chooser).findByText('客户')
    fireEvent.click(fieldRow.closest('tr')!)
    fireEvent.click(within(chooser).getByRole('button', { name: '确认' }))
    await waitFor(() => {
      expect((dialog.querySelector('#condition-field') as HTMLInputElement).value).toBe('COP_ORDER_M.CUST_ID')
    })
  }

  it('新增条件弹窗：未选模块提交给出错误反馈', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增条件' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: '新增条件' }))
    expect(await within(dialog).findByText('请选择模块。')).toBeInTheDocument()
  })

  it('新增条件弹窗：弹窗内选模块（序号默认该模块最大+1）+ 选字段 + 选项 DSL 序列化后 POST', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增条件' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('模块'), { target: { value: '1209' } })
    // 模块 1209 已有序号 1/2：默认序号推算为 3
    expect((dialog.querySelector('#condition-serial') as HTMLInputElement).value).toBe('3')
    await pickField(dialog)
    fireEvent.change(within(dialog).getByLabelText('条件描述（报表用户所见标签）'), { target: { value: '是否有效' } })
    fireEvent.change(within(dialog).getByLabelText('条件类型'), { target: { value: '2' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '添加选项' }))
    fireEvent.change(within(dialog).getByLabelText('选项1标签'), { target: { value: '是' } })
    fireEvent.change(within(dialog).getByLabelText('选项1值'), { target: { value: '1' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '新增条件' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/report-conditions/conditions?moduleId=1209',
      expect.objectContaining({ serialNo: 3, type: 2, field: 'COP_ORDER_M.CUST_ID', expression: '是:1', description: '是否有效' }),
    ))
  })

  it('编辑预填：模块只读、固定单选 DSL 解析回选项行，序号禁改', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(conditionRowOf('状态')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect((dialog.querySelector('#condition-module') as HTMLInputElement).value).toBe('模块 1209 客户订单')
    expect(dialog.querySelector('#condition-serial')).toBeDisabled()
    expect(within(dialog).getByLabelText('选项1标签')).toHaveValue('是')
    expect(within(dialog).getByLabelText('选项1值')).toHaveValue('1')
  })

  it('编辑预填：数据表单选回填数据源语句', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(conditionRowOf('仓库')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect((dialog.querySelector('#condition-expression') as HTMLInputElement).value).toBe('SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE')
    expect(within(dialog).getByLabelText('条件类型')).toHaveValue('3')
  })

  it('删除条件需确认并按模块行调用 DELETE', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderPage()
    await loaded()
    fireEvent.click(within(conditionRowOf('仓库')).getByRole('button', { name: '删除' }))
    expect(confirmSpy).toHaveBeenCalled()
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/report-conditions/conditions/1?moduleId=1305'))
    confirmSpy.mockRestore()
  })

  it('加载失败显示错误与重新加载入口', async () => {
    apiClientMock.get.mockRejectedValue(new Error('boom'))
    renderPage()
    expect(await screen.findByText('加载失败。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '重新加载' })).toBeInTheDocument()
  })
})
