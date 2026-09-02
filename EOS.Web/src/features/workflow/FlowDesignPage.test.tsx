import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FlowDesignPage } from './FlowDesignPage'


vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const flows = [
  { moduleId: 1906, title: '油卡充值单', flowName: '油卡充值单二级审批', remark: 'EOS-PILOT-1906', stepCount: 2, updatedBy: 'EOS-PILOT-1906', updatedAt: '2026-08-26 22:48:48' },
  { moduleId: 1404, title: '报价单', flowName: '报价二级审批', remark: '', stepCount: 2, updatedBy: 'admin', updatedAt: null },
]
const eligible = [
  { moduleId: 1906, title: '油卡充值单', masterTable: 'CAR_FEE_M', updateSproc: 'P_WF_CAR_FEE', autoApprove: false },
  { moduleId: 1404, title: '报价单', masterTable: 'COP_QUOTE_M', updateSproc: 'P_WF_COP_QUOTE', autoApprove: false },
  { moduleId: 1405, title: '客户订单', masterTable: 'COP_ORDER_M', updateSproc: 'P_WF_COP_ORDER', autoApprove: false },
]
const flowDetail = {
  moduleId: 1404,
  title: '报价单',
  masterTable: 'COP_QUOTE_M',
  flowName: '报价二级审批',
  remark: '',
  updatedBy: 'admin',
  updatedAt: null,
  steps: [
    { sortNo: '001', desc: '一级审批', people: ['admin'], execCondition: '', personConditions: [], approvePowers: [], forwardPowers: [], autoExecCondition: '', isAutoExec: false, isSign: false, passPercent: 0, isEffect: true, preMustUnder: false, canSirAgency: false, mustSigners: [], remark: '' },
    { sortNo: '002', desc: '二级审批', people: ['admin'], execCondition: '', personConditions: [], approvePowers: [], forwardPowers: [], autoExecCondition: '', isAutoExec: false, isSign: false, passPercent: 0, isEffect: true, preMustUnder: false, canSirAgency: false, mustSigners: [], remark: '' },
  ],
}

const flowDetail1906 = {
  ...flowDetail,
  moduleId: 1906,
  title: '油卡充值单',
  flowName: '油卡充值单二级审批',
  steps: [
    { ...flowDetail.steps[0] },
    { ...flowDetail.steps[1], people: ['admin'] },
  ],
}

function renderPage() {
  return renderWithProviders(
      <FlowDesignPage />
)
}

describe('FlowDesignPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/workflow/definitions') return { flows, eligible }
      if (path.includes('people')) return { people: [{ userId: 'admin', name: '管理员', deptId: 'D1', active: true }] }
      const detailMatch = path.match(/^\/workflow\/definitions\/(\d+)$/)
      if (detailMatch) return Number(detailMatch[1]) === 1906 ? flowDetail1906 : flowDetail
      throw new Error(`unexpected GET ${path}`)
    })
    apiClientMock.post.mockResolvedValue({ saved: true })
    apiClientMock.delete.mockResolvedValue({ deleted: true })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('列出具备批核能力的模块并标注已配置流程', async () => {
    renderPage()
    expect(await screen.findByText('油卡充值单')).toBeInTheDocument()
    expect(screen.getByText('报价单')).toBeInTheDocument()
    expect(screen.getByText('客户订单')).toBeInTheDocument()
    expect(screen.getByText('油卡充值单二级审批')).toBeInTheDocument()
    expect(screen.getByText('未配置')).toBeInTheDocument()
  })

  it('选择模块后载入流程详情并进入编辑', async () => {
    renderPage()
    await screen.findByText('报价单')
    fireEvent.click(screen.getByText('报价单'))
    expect(await screen.findByDisplayValue('报价二级审批')).toBeInTheDocument()
    expect(screen.getByDisplayValue('一级审批')).toBeInTheDocument()
    expect(screen.getByDisplayValue('二级审批')).toBeInTheDocument()
    expect(screen.getByText('已配置流程')).toBeInTheDocument()
  })

  it('未配置模块进入新增模式（空流程名 + 空步骤）', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/workflow/definitions') return { flows, eligible }
      const error = new Error('404') as Error & { status?: number }
      error.status = 404
      throw error
    })
    renderPage()
    await screen.findByText('客户订单')
    fireEvent.click(screen.getByText('客户订单'))
    expect((await screen.findAllByText('未配置')).length).toBeGreaterThan(0)
    expect(screen.getByPlaceholderText('如：油卡充值单二级审批')).toBeInTheDocument()
    expect(screen.getByText('尚未添加审批步骤')).toBeInTheDocument()
  })

  it('添加步骤并保存流程', async () => {
    renderPage()
    await screen.findByText('报价单')
    fireEvent.click(screen.getByText('报价单'))
    await screen.findByDisplayValue('报价二级审批')
    fireEvent.click(screen.getByText('添加步骤'))
    expect(await screen.findByText('步骤 3')).toBeInTheDocument()
    fireEvent.click(screen.getByText('保存流程'))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalled())
    expect(apiClientMock.post.mock.calls[0][0]).toBe('/workflow/definitions/1404')
  })

  it('删除流程需确认', async () => {
    renderPage()
    await screen.findByText('油卡充值单')
    fireEvent.click(screen.getByText('油卡充值单'))
    await screen.findByDisplayValue('油卡充值单二级审批')
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true)
    fireEvent.click(screen.getByText('删除'))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledWith('/workflow/definitions/1906'))
    confirmSpy.mockRestore()
  })
})
