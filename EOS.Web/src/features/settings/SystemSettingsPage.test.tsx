import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SystemSettingsPage } from './SystemSettingsPage'
import type { SysSettingsField, SysSettingsPayload } from './settingsTypes'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const bitFields = [
  'PRO_MRP', 'PRO_EDITION_TAG', 'FITOUT_TAG', 'SEND_TAG', 'COP_RETURN_DEPOT_TAG',
  'FITOUT_ORDER_TAG', 'FITOUT_PRODUCE_TAG', 'FITOUT_PRODUCE_TRANSFER_TAG',
  'SEND_ORDER_TAG', 'SEND_PRODUCE_TAG', 'SEND_PRODUCE_TRANSFER_TAG',
  'SEND_ORDER_FITOUT_TAG', 'SEND_PRODUCE_FITOUT_TAG', 'SEND_FITOUT_TAG',
  'RETURN_SEND_TAG', 'RETURN_PRODUCE_SEND_TAG', 'RETURN_ORDER_SEND_TAG',
  'PUR_APPLY_TAG', 'PUR_PRODUCE_APPLY_TAG', 'PUR_APPLY_PLAN_PUR_TAG', 'PUR_CANCEL_DEPOT_TAG',
  'PRODUCE_IN_ORDER_TAG', 'PRODUCE_ORDER_TAG', 'PRODUCE_PLAN_MOC_TAG', 'QUICK_SEARCH_ALL',
] as const

const intFields = [
  'CLIENT_DAYS', 'SUPPLIER_DAYS', 'PRODUCT_DAYS', 'MRP_DAYS', 'MRP_WEEKS', 'MRP_MONTHS',
  'QTY_LINE1', 'QTY_LINE2', 'QTY_LINE3', 'QTY_PS_WIDTH',
] as const

const labels: Record<string, string> = {
  CLIENT_DAYS: '客户未交易天数',
  SUPPLIER_DAYS: '厂商未交易天数',
  PRODUCT_DAYS: '料件未交易天数',
  MRP_DAYS: 'MRP计算天数',
  MRP_WEEKS: 'MRP计算周数',
  MRP_MONTHS: 'MRP计算月数',
  QTY_LINE1: '数量小生产线1',
  QTY_LINE2: '数量小生产线2',
  QTY_LINE3: '数量小生产线3',
  QTY_PS_WIDTH: '超纸板数量纸度变更',
  PRO_MRP: '产品可用库存计算',
  PRO_EDITION_TAG: '料件是否管理版次',
  FITOUT_TAG: '是否需要开备货单流程',
  SEND_TAG: '送货单扣库存',
  COP_RETURN_DEPOT_TAG: '退货是否更新库存',
  FITOUT_ORDER_TAG: '判断已备不大于订单',
  FITOUT_PRODUCE_TAG: '判断已备不大于生产单',
  FITOUT_PRODUCE_TRANSFER_TAG: '判断生产单已调不大于已备',
  SEND_ORDER_TAG: '判断已送不大于订单',
  SEND_PRODUCE_TAG: '可超生产单送货',
  SEND_PRODUCE_TRANSFER_TAG: '判断生产单已送不大于已调',
  SEND_ORDER_FITOUT_TAG: '判断订单已送不大于已备货',
  SEND_PRODUCE_FITOUT_TAG: '判断生产单已送不大于已备货',
  SEND_FITOUT_TAG: '判断已送不大于备货单',
  RETURN_SEND_TAG: '判断已退不大于已送',
  RETURN_PRODUCE_SEND_TAG: '判断生产单已退不大于已送',
  RETURN_ORDER_SEND_TAG: '判断订单已退不大于已送',
  PUR_APPLY_TAG: '采购可超请购数量',
  PUR_PRODUCE_APPLY_TAG: '请购是否更新生产已转请购数量',
  PUR_APPLY_PLAN_PUR_TAG: '请购是否更新计划已转请购数量',
  PUR_CANCEL_DEPOT_TAG: '退料是否更新库存',
  PRODUCE_IN_ORDER_TAG: '生产出入库是否更新订单',
  PRODUCE_ORDER_TAG: '生产是否更新订单下单数量',
  PRODUCE_PLAN_MOC_TAG: '生产是否更新计划下单数量',
  QUICK_SEARCH_ALL: '使用快查全部字段',
  REMARK: '备注',
  CREATE_DATE: '建立日期',
}

function field(key: string, type: string, label: string): SysSettingsField {
  return { key, label, type, maxLength: type === 'nvarchar' ? 500 : null, nullable: true, order: 1 }
}

const sysssFields: SysSettingsField[] = [
  ...intFields.map((key) => field(key, 'int', labels[key] ?? key)),
  ...bitFields.map((key) => field(key, 'bit', labels[key] ?? key)),
  field('REMARK', 'nvarchar', labels.REMARK ?? '备注'),
  field('CREATE_DATE', 'datetime', labels.CREATE_DATE ?? '建立日期'),
]

const values: Record<string, string | number | boolean | null> = {
  CLIENT_DAYS: 30,
  SUPPLIER_DAYS: 60,
  PRODUCT_DAYS: 90,
  MRP_DAYS: 7,
  MRP_WEEKS: 4,
  MRP_MONTHS: 12,
  QTY_LINE1: 100,
  QTY_LINE2: 1000,
  QTY_LINE3: 2000,
  QTY_PS_WIDTH: 2000,
  PRO_MRP: true,
  QUICK_SEARCH_ALL: false,
  CREATE_DATE: '2026-08-10T09:30:00',
  REMARK: '测试备注',
}

const sysssPayload: SysSettingsPayload = { values, fields: sysssFields }

function renderPage(path = '/settings/system') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/settings/:table" element={<SystemSettingsPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('SystemSettingsPage（110111 系统参数设置复刻）', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue(sysssPayload)
    apiClientMock.put.mockResolvedValue(undefined)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.spyOn(window, 'alert').mockImplementation(() => {})
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.restoreAllMocks()
  })

  it('按旧页面七分区渲染标题、中文标签与控件类型', async () => {
    renderPage()
    expect(await screen.findByText('一：未交易天数限制，超过以下设定天数，对应表单将不能批核。')).toBeInTheDocument()
    expect(screen.getByText('四：业务流程参数')).toBeInTheDocument()
    expect(screen.getByText('七：系统查询参数')).toBeInTheDocument()

    expect(screen.getByText('客户未交易天数')).toBeInTheDocument()
    expect(screen.getByLabelText('客户未交易天数')).toHaveValue(30)
    expect(screen.getByLabelText('厂商未交易天数')).toHaveValue(60)
    expect(screen.getByLabelText('使用快查全部字段')).not.toBeChecked()
    expect(screen.getByLabelText('产品可用库存计算')).toBeChecked()
    expect(screen.getByLabelText('建立日期')).toHaveValue('2026-08-10T09:30')
    expect(screen.getByLabelText('备注')).toHaveValue('测试备注')
    expect(screen.getByRole('button', { name: '确定(O)' })).toBeEnabled()
  })

  it('勾选/取消复选框并保存时提交表单值，成功后提示设置成功', async () => {
    renderPage()
    await screen.findByText('四：业务流程参数')
    fireEvent.click(screen.getByLabelText('使用快查全部字段'))
    fireEvent.click(screen.getByRole('button', { name: '确定(O)' }))

    await waitFor(() => {
      expect(apiClientMock.put).toHaveBeenCalledWith(
        '/settings/SYSSS',
        expect.objectContaining({ CLIENT_DAYS: '30', QUICK_SEARCH_ALL: 'true' }),
      )
    })
    expect(screen.getByRole('alert')).toHaveTextContent('设置成功！')
  })

  it('取消确认时不提交', async () => {
    vi.mocked(window.confirm).mockReturnValue(false)
    renderPage()
    await screen.findByText('四：业务流程参数')
    fireEvent.click(screen.getByRole('button', { name: '确定(O)' }))
    expect(apiClientMock.put).not.toHaveBeenCalled()
  })

  it('保存失败时弹出旧系统提示', async () => {
    apiClientMock.put.mockRejectedValue(new Error('boom'))
    renderPage()
    await screen.findByText('四：业务流程参数')
    fireEvent.click(screen.getByRole('button', { name: '确定(O)' }))
    await waitFor(() => {
      expect(window.alert).toHaveBeenCalledWith(expect.stringContaining('未知错误，设置失败！'))
    })
  })
})

describe('SystemSettingsPage（HR_SETUP 通用网格）', () => {
  beforeEach(() => {
    apiClientMock.get.mockResolvedValue({
      values: { MACHINE_START: '08:00', WORK_END: '18:00' },
      fields: [
        { key: 'MACHINE_START', label: '上班时间', type: 'nvarchar', maxLength: 50, nullable: true, order: 1 },
        { key: 'WORK_END', label: '下班时间', type: 'nvarchar', maxLength: 50, nullable: true, order: 2 },
      ],
    })
    apiClientMock.put.mockResolvedValue(undefined)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.spyOn(window, 'alert').mockImplementation(() => {})
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.restoreAllMocks()
  })

  it('读取 HR_SETUP 并渲染标签化参数网格', async () => {
    renderPage('/settings/hr-setup')
    expect(apiClientMock.get).toHaveBeenCalledWith('/settings/HR_SETUP')
    expect(await screen.findByLabelText('上班时间')).toHaveValue('08:00')
    expect(screen.getByLabelText('下班时间')).toHaveValue('18:00')
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => {
      expect(apiClientMock.put).toHaveBeenCalledWith('/settings/HR_SETUP', expect.objectContaining({ MACHINE_START: '08:00' }))
    })
  })
})
