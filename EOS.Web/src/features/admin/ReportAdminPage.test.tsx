import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportAdminPage } from './ReportAdminPage'
import { serializeReportFilter } from './reportFilter'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const modules = [
  { moduleId: 1209, description: '客户订单' },
  { moduleId: 1305, description: '库存日志' },
]

const reports = [
  { reportId: 'RPT_A', reportName: '送货单', moduleId: 1209, isoNo: null, headerId: 'H1', tailId: null, footerText: null, isDefault: true, reportFilter: null, remark: null },
  { reportId: 'RPT_B', reportName: '订单', moduleId: 1209, isoNo: null, headerId: null, tailId: null, footerText: null, isDefault: false, reportFilter: null, remark: null },
  { reportId: 'RPT_C', reportName: '库存日志', moduleId: 1305, isoNo: null, headerId: null, tailId: null, footerText: null, isDefault: false, reportFilter: null, remark: null },
]

const headers = [{ id: 'H1', name: '默认页头' }]
const tails = [{ id: 'T1', name: '默认表尾' }]

const sorts = [
  { serialNo: 1, sortName: '按日期', sortFields: 'COP_SEND_M.SEND_DATE', sortDesc: null, groupName: null, groupFields: null, groupDesc: null },
]

const fieldOptions = [
  { table: 'SYSQR_DA', column: 'F_TYPE', label: '字段类型' },
  { table: 'SYSQR_DA', column: 'REMARK', label: '备注' },
]

const reportFormats = [
  { formatId: '1406-pick', title: '拣货单', moduleId: 1209 },
  { formatId: '1406', title: '送货单', moduleId: 1209 },
]

const chooserModules = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int' },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar' },
  ],
  rows: modules.map((item) => ({ M_IDX: item.moduleId, M_DESC: item.description })),
  total: modules.length,
}

function renderPage() {
  return renderWithProviders(<ReportAdminPage />)
}

/**
 * 等首屏数据到位。超时给足：满量跑（上千条用例并行）时这一页的数据要几秒才到，
 * 默认 1s 会偶发失败——而那条失败的报错是"找不到 RPT_A"，看着像渲染错了，其实只是慢
 * （手册 06：这类用例断言的是**行为**而不是速度）。
 */
async function loaded() {
  await screen.findByText('RPT_A', {}, { timeout: 10_000 })
}

function rowOf(reportId: string) {
  return screen.getByText(reportId).closest('tr')!
}

/**
 * 打开新增报表弹窗并通过通用选择器选中 1305 库存日志。
 *
 * <p>这是本文件里最重的一条链路（开弹窗 → 选择器取数 → 回填后再取字段选项），
 * 所以两处等待都给足超时：满量跑时它比单跑慢一个数量级，默认的 1s / 5s 都顶不住。</p>
 */
async function openNewReportWithModule() {
  fireEvent.click(screen.getByRole('button', { name: '新增报表' }))
  fireEvent.click(screen.getByRole('button', { name: '选择模块…' }))
  const dialogs = screen.getAllByRole('dialog')
  const chooser = dialogs[dialogs.length - 1]
  const moduleRow = await within(chooser).findByText('库存日志', {}, { timeout: 10_000 })
  fireEvent.click(moduleRow.closest('tr')!)
  fireEvent.click(screen.getByRole('button', { name: '确认' }))
  await waitFor(() => {
    expect(apiClientMock.get).toHaveBeenCalledWith('/report-admin/field-options?moduleId=1305')
  }, { timeout: 10_000 })
}

describe('ReportAdminPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.includes('/sorts')) return sorts
      if (path.includes('/reports')) return reports
      if (path.includes('/field-options')) return fieldOptions
      if (path.includes('/formats')) return reportFormats
      if (path.includes('/headers')) return headers
      if (path.includes('/tails')) return tails
      if (path.includes('/modules')) return modules
      throw new Error(`unexpected path: ${path}`)
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return chooserModules
      return undefined
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('单一列表显示全部模块报表（含所属模块列），标准表格，默认选中第一条并显示其排序方案', async () => {
    const { container } = renderPage()
    await loaded()
    expect(screen.getByText('RPT_B')).toBeInTheDocument()
    expect(screen.getByText('RPT_C')).toBeInTheDocument()
    // 主子表框架：上方报表（主）、下方排序方案（明细）
    expect(container.querySelector('.erp-workbench-page')).not.toBeNull()
    expect(container.querySelector('.erp-master-table-region')).not.toBeNull()
    expect(container.querySelector('.erp-detail-card')).not.toBeNull()
    // 搜索框存在
    expect(screen.getByLabelText('搜索报表')).toBeInTheDocument()
    // 新增报表按钮在列表命令栏右侧（与 2305 一致的主操作）
    expect(screen.getByRole('button', { name: '新增报表' })).toBeInTheDocument()
    // 所属模块列
    expect(screen.getAllByText('1209 客户订单').length).toBeGreaterThan(0)
    expect(screen.getByText('1305 库存日志')).toBeInTheDocument()
    // 首列选择控件：全选 + 行选择
    expect(screen.getByLabelText('选择当前页')).toBeInTheDocument()
    expect(screen.getAllByLabelText('选择此行')).toHaveLength(3)
    // 可排序列渲染表头操作按钮（clientSideSorting）
    expect(screen.getByLabelText('表头操作报表编号')).toBeInTheDocument()
    // 默认选中第一条报表 → 排序/分组方案区出现
    await waitFor(() => {
      expect(apiClientMock.get).toHaveBeenCalledWith('/report-admin/sorts?reportId=RPT_A')
    })
    expect(screen.getByText('排序/分组方案（REPORT_SORT）——报表：RPT_A')).toBeInTheDocument()
  })

  it('点击行内选中该报表并清除其它行（rowClickSingleSelect 单选语义），排序区随之切换', async () => {
    renderPage()
    await loaded()
    // 默认已选中第一条（RPT_A）
    let boxes = screen.getAllByLabelText('选择此行')
    expect(boxes[0]).toBeChecked()
    expect(boxes[1]).not.toBeChecked()

    fireEvent.click(rowOf('RPT_B'))
    boxes = screen.getAllByLabelText('选择此行')
    expect(boxes[0]).not.toBeChecked()
    expect(boxes[1]).toBeChecked()
    await waitFor(() => {
      expect(apiClientMock.get).toHaveBeenCalledWith('/report-admin/sorts?reportId=RPT_B')
    })
    expect(screen.getByText('排序/分组方案（REPORT_SORT）——报表：RPT_B')).toBeInTheDocument()
  })

  // 第三条参数是**整条用例**的上限（全局 testTimeout 已是 20 秒，这里显式写一遍标明
  // "这条链路最重"）：它要走"开弹窗 → 选择器取数 → 回填 → 提交"，满量跑时会顶到上限，
  // 报的是 "Test timed out"，看着像死锁其实只是慢。内部每处等待另有 10 秒（见上面两个辅助函数）
  it('新增报表弹窗：通用选择器选所属模块后 POST', async () => {
    renderPage()
    await loaded()
    await openNewReportWithModule()
    fireEvent.change(screen.getByLabelText('报表编号'), { target: { value: 'RPT_NEW' } })
    fireEvent.change(screen.getByLabelText('报表名称'), { target: { value: '新报表' } })
    fireEvent.click(screen.getByRole('button', { name: '新增记录' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/report-admin/reports',
      expect.objectContaining({ reportId: 'RPT_NEW', reportName: '新报表', moduleId: 1305 }),
    ))
  }, 20_000)

  it('报表过滤支持构建辅助输入', async () => {
    renderPage()
    await loaded()
    await openNewReportWithModule()
    fireEvent.click(screen.getByRole('button', { name: '构建…' }))
    expect(screen.getByText('构建报表过滤条件')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('条件1字段'), { target: { value: 'SYSQR_DA.F_TYPE' } })
    fireEvent.change(screen.getByLabelText('条件1运算符'), { target: { value: 'gt' } })
    fireEvent.change(screen.getByLabelText('条件1值'), { target: { value: '1' } })
    fireEvent.click(screen.getByRole('button', { name: '应用查询' }))
    expect((screen.getByLabelText(/^报表过滤/) as HTMLInputElement).value).toBe("SYSQR_DA.F_TYPE>'1'")
  }, 20_000)

  it('行「编辑」打开报表编辑弹窗并预填（所属模块/编号禁改）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('RPT_A')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect(dialog.querySelector('#report-module')).toBeDisabled()
    expect(dialog.querySelector('#report-id')).toBeDisabled()
    expect((dialog.querySelector('#report-name') as HTMLInputElement).value).toBe('送货单')
  })

  it('行「编辑」打开报表编辑弹窗可绑定打印版式，保存时带 formatId', async () => {
    renderPage()
    await loaded()
    fireEvent.click(within(rowOf('RPT_A')).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    const select = dialog.querySelector('#report-format') as HTMLSelectElement
    expect(select).not.toBeNull()
    // 空选项 = 沿用模块默认版式；其余来自 /report-admin/formats（只列同模块的版式）
    await waitFor(() => expect(Array.from(select.options).map((option) => option.value)).toContain('1406-pick'))
    expect((select.options[0].textContent ?? '')).toContain('模块默认')
    fireEvent.change(select, { target: { value: '1406-pick' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存修改' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledWith(
      '/report-admin/reports/RPT_A',
      expect.objectContaining({ formatId: '1406-pick' }),
    ))
  })

  it('新增排序方案走弹窗并 POST', async () => {
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: '新增方案' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.change(screen.getByLabelText('序号'), { target: { value: '2' } })
    fireEvent.change(screen.getByLabelText('方案名'), { target: { value: '按客户' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '新增方案' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/report-admin/sorts?reportId=RPT_A',
      expect.objectContaining({ serialNo: 2, sortName: '按客户' }),
    ))
  })

  it('行「编辑」打开排序方案编辑弹窗并预填', async () => {
    renderPage()
    await loaded()
    await screen.findByText('按日期')
    fireEvent.click(within(screen.getByText('按日期').closest('tr')!).getByRole('button', { name: '编辑' }))
    const dialog = screen.getByRole('dialog')
    expect((dialog.querySelector('#sort-name') as HTMLInputElement).value).toBe('按日期')
  })

  it('取消选中报表后明细卡仍保留（未选择报表提示，不占满工作区）', async () => {
    renderPage()
    await loaded()
    fireEvent.click(rowOf('RPT_A'))
    expect(screen.getByText('未选择报表')).toBeInTheDocument()
    expect(screen.getByText('排序/分组方案（REPORT_SORT）').closest('.erp-detail-card')).not.toBeNull()
  })

  it('模块列表加载失败显示错误与重新加载入口', async () => {
    apiClientMock.get.mockRejectedValue(new Error('boom'))
    renderPage()
    expect(await screen.findByText('加载失败。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '重新加载' })).toBeInTheDocument()
  })
})

describe('serializeReportFilter', () => {
  const condition = (partial: Partial<import('../../components/common/queryCondition').QueryCondition>) => ({
    field: '', operator: 'eq', value: '', valueTo: '', logic: 'and', ...partial,
  })

  it('比较/包含/逻辑连接序列化', () => {
    expect(serializeReportFilter([
      condition({ field: 'SYSQR_DA.F_TYPE', operator: 'eq', value: 'I' }),
      condition({ field: 'SYSQR_DA.REMARK', operator: 'contains', value: 'abc', logic: 'or' }),
    ])).toBe("SYSQR_DA.F_TYPE='I' OR SYSQR_DA.REMARK LIKE '%abc%'")
  })

  it('区间/空值/不包含序列化', () => {
    expect(serializeReportFilter([
      condition({ field: 'X.Y', operator: 'between', value: '1', valueTo: '9' }),
    ])).toBe("(X.Y>='1' AND X.Y<='9')")
    expect(serializeReportFilter([
      condition({ field: 'X.Y', operator: 'empty' }),
    ])).toBe("ISNULL(X.Y,'')=''")
    expect(serializeReportFilter([
      condition({ field: 'X.Y', operator: 'notcontains', value: "it's" }),
    ])).toBe("NOT (X.Y LIKE '%it''s%')")
  })

  it('无有效条件返回空串', () => {
    expect(serializeReportFilter([])).toBe('')
    expect(serializeReportFilter([condition({ field: '' })])).toBe('')
  })
})
