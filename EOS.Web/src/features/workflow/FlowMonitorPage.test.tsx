import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { BrowserRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FlowMonitorPage } from './FlowMonitorPage'

const apiClientMock = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn(),
  postFile: vi.fn(),
}))

vi.mock('../../services/api', () => ({ apiClient: apiClientMock }))

const rows = [
  { wfId: 1, moduleId: 1906, title: '油卡充值单', keyValueDesc: 'FEE_TYPE=FEE  FEE_NO=1001', keyValue: '[FEE_TYPE]=\'FEE\' AND [FEE_NO]=\'1001\'', keyValues: ['FEE', '1001'], startUser: 'admin', startDate: '2026-08-26T10:00:00', ageDays: 1.2, step: '002', stepDesc: '二级审批', state: '0', stateLabel: '在途', overdue: false },
  { wfId: 2, moduleId: 1906, title: '油卡充值单', keyValueDesc: 'FEE_TYPE=FEE  FEE_NO=1002', keyValue: '[FEE_TYPE]=\'FEE\' AND [FEE_NO]=\'1002\'', keyValues: ['FEE', '1002'], startUser: 'admin', startDate: '2026-08-20T10:00:00', ageDays: 7.0, step: '001', stepDesc: '一级审批', state: '0', stateLabel: '在途', overdue: true },
  { wfId: 3, moduleId: 1906, title: '油卡充值单', keyValueDesc: 'FEE_TYPE=FEE  FEE_NO=1003', keyValue: '[FEE_TYPE]=\'FEE\' AND [FEE_NO]=\'1003\'', keyValues: ['FEE', '1003'], startUser: 'admin', startDate: '2026-08-25T10:00:00', ageDays: 2.0, step: '002', stepDesc: '二级审批', state: '1', stateLabel: '已完成', overdue: false },
]

const detail = {
  monitor: rows[0],
  tasks: [
    { myTaskId: 11, step: '001', stepDesc: '一级审批', approver: 'admin', state: 'Y', message: '同意', approveDate: '2026-08-26 10:01', approvePower: true, isCurrent: false, isSign: false, isMustSign: false, isAutoExec: false, passPercent: 0 },
    { myTaskId: 12, step: '002', stepDesc: '二级审批', approver: 'admin', state: '', message: '', approveDate: null, approvePower: true, isCurrent: true, isSign: false, isMustSign: false, isAutoExec: false, passPercent: 0 },
  ],
  logs: [
    { step: '000', stepDesc: '提交送审', approver: 'admin', state: 'A', message: '请加急', date: '2026-08-26 10:00' },
    { step: '001', stepDesc: '一级审批', approver: 'admin', state: 'Y', message: '同意', date: '2026-08-26 10:01' },
  ],
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <BrowserRouter>
      <QueryClientProvider client={queryClient}>
        <FlowMonitorPage />
      </QueryClientProvider>
    </BrowserRouter>,
  )
}

describe('FlowMonitorPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.includes('/workflow/monitor/')) return detail
      return { rows, overdueDays: 3 }
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('列出流程实例并渲染状态徽标与超时标记', async () => {
    renderPage()
    expect((await screen.findAllByText('油卡充值单')).length).toBe(3)
    expect(screen.getAllByText('在途').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('已完成').length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText(/（超时）/)).toBeInTheDocument()
    expect(screen.getByText(/共 3 个实例/)).toBeInTheDocument()
  })

  it('按状态筛选调用带 status 参数的接口', async () => {
    renderPage()
    await screen.findAllByText('油卡充值单')
    fireEvent.change(screen.getByLabelText('状态筛选'), { target: { value: '0' } })
    await waitFor(() => expect(apiClientMock.get).toHaveBeenCalledWith(expect.stringContaining('status=0')))
  })

  it('查看明细弹窗展示任务与日志时间线', async () => {
    renderPage()
    await screen.findAllByText('油卡充值单')
    fireEvent.click(screen.getAllByText('明细')[0])
    expect(await screen.findByText('流程明细')).toBeInTheDocument()
    expect(await screen.findByText('步骤任务')).toBeInTheDocument()
    expect(screen.getByText('审批日志')).toBeInTheDocument()
    expect(screen.getByText('提交送审')).toBeInTheDocument()
    expect(screen.getByText('请加急')).toBeInTheDocument()
  })
})
