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

const conditions1209 = [
  { serialNo: 1, type: 1, field: 'COP_ORDER_M.ORDER_DATE', expression: null, description: '订单日期', defaultValue: '2026-01-01', parameterName: 'P_DATE', remark: null },
  { serialNo: 2, type: 2, field: 'COP_ORDER_M.STATUS', expression: '是:1;否:0', description: '状态', defaultValue: '1', parameterName: null, remark: null },
]

const conditions1305 = [
  { serialNo: 1, type: 3, field: 'INV_LOG.WAREHOUSE', expression: 'SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE', description: '仓库', defaultValue: null, parameterName: null, remark: null },
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
  await screen.findByText('客户订单')
  await waitFor(() => {
    expect(apiClientMock.get).toHaveBeenCalledWith('/report-conditions/conditions?moduleId=1209')
  })
}

function moduleRowOf(description: string) {
  return screen.getByText(description).closest('tr')!
}

function conditionRowOf(description: string) {
  return screen.getByText(description).closest('tr')!
}

describe('ReportConditionsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.includes('/conditions?moduleId=1305')) return conditions1305
      if (path.includes('/conditions?moduleId=1209')) return conditions1209
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

  it('主子表框架：上方模块列表默认选中第一条，下方显示该模块过滤条件', async () => {
    const { container } = renderPage()
    await loaded()
    expect(screen.getByText('库存日志')).toBeInTheDocument()
    // 主子表框架：上方模块（主）、下方条件行（明细）
    expect(container.querySelector('.erp-workbench-page')).not.toBeNull()
    expect(container.querySelector('.erp-master-table-region')).not.toBeNull()
    expect(container.querySelector('.erp-detail-card')).not.toBeNull()
    expect(screen.getByLabelText('搜索模块')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增条件' })).toBeInTheDocument()
    // 明细：条件行、类型中文、DSL 原文
    expect(screen.getByText('订单日期')).toBeInTheDocument()
    expect(screen.getByText('范围')).toBeInTheDocument()
    expect(screen.getByText('是:1;否:0')).toBeInTheDocument()
    // 明细卡标题带模块上下文
    expect(screen.getByText(/过滤条件（SYSQR_DEFAULT）——模块：1209/)).toBeInTheDocument()
  })

  it('点击模块行切换单选并加载该模块条件（rowClickSingleSelect）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(moduleRowOf('库存日志'))
    await waitFor(() => {
      expect(apiClientMock.get).toHaveBeenCalledWith('/report-conditions/conditions?moduleId=1305')
    })
    expect(await screen.findByText('仓库')).toBeInTheDocument()
    expect(screen.getByText(/过滤条件（SYSQR_DEFAULT）——模块：1305/)).toBeInTheDocument()
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

  it('新增条件弹窗：选字段+固定单选选项行序列化为 DSL 后 POST（序号默认最大+1）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增条件' }))
    const dialog = screen.getByRole('dialog')
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

  it('新增条件弹窗：统一选择器选查询字段（report-conditions.fields）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增条件' }))
    const dialog = screen.getByRole('dialog')
    await pickField(dialog)
    expect(within(dialog).getByLabelText('条件类型')).toBeInTheDocument()
  })

  it('编辑预填：固定单选 DSL 解析回选项行，序号禁改', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(conditionRowOf('状态')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect(dialog.querySelector('#condition-serial')).toBeDisabled()
    expect(within(dialog).getByLabelText('选项1标签')).toHaveValue('是')
    expect(within(dialog).getByLabelText('选项1值')).toHaveValue('1')
  })

  it('编辑预填：数据表单选回填数据源语句', async () => {
    renderPage()
    await loaded()
    fireEvent.click(moduleRowOf('库存日志'))
    await screen.findByText('仓库')
    fireEvent.click(within(conditionRowOf('仓库')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect((dialog.querySelector('#condition-expression') as HTMLInputElement).value).toBe('SELECT W_ID C_ID, W_NAME C_VALUE FROM WAREHOUSE')
    expect(within(dialog).getByLabelText('条件类型')).toHaveValue('3')
  })

  it('模块列表加载失败显示错误与重新加载入口', async () => {
    apiClientMock.get.mockRejectedValue(new Error('boom'))
    renderPage()
    expect(await screen.findByText('加载失败。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '重新加载' })).toBeInTheDocument()
  })
})
