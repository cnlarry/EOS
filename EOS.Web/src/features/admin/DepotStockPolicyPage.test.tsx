import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DepotStockPolicyPage } from './DepotStockPolicyPage'
import { ToastProvider } from '../../components/ui/Toast'
import { ApiError } from '../../types/api'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const policies = [
  {
    depotId: '*', locationMode: 0, storageMode: 'FIXED', batchMode: 0, capacityMode: 0,
    mixProduct: true, mixBatch: true, monthCloseByBatch: true, monthCloseByLocation: false,
  },
  {
    depotId: 'CP', locationMode: 0, storageMode: 'FIXED', batchMode: 0, capacityMode: 0,
    mixProduct: true, mixBatch: true, monthCloseByBatch: true, monthCloseByLocation: false,
  },
]

// 档位目录由服务端下发；未实现的档位 implemented=false（界面灰显，与服务端拒存同源）
const tiers = [
  {
    key: 'locationMode', label: '位置档位', description: '货在哪里记到多细',
    options: [
      { value: '0', label: '0 不管', implemented: true },
      { value: '3', label: '3 强制', implemented: true },
    ],
  },
  {
    key: 'batchMode', label: '批次档位', description: '批号记不记',
    options: [
      { value: '0', label: '0 归零', implemented: true },
      { value: '3', label: '3 必填 + 效期', implemented: false },
    ],
  },
]

/** 服务端按按钮级授权下发的名单；缺省为空（未授权时页面不认识任何操作）。 */
let availableActions: Array<{
  key: string; label: string; confirmTag: boolean; failMode: string; placement: string;
  params: { fields: Array<{ key: string; label: string; type: string; required: boolean; maxLength: number }> } | null
}> = []

describe('DepotStockPolicyPage', () => {
  beforeEach(() => {
    availableActions = []
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.includes('/tiers')) return tiers
      if (path.includes('/actions')) return availableActions
      return policies
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  async function selectCp() {
    // 页面上的单据操作按钮走全局轻提示反馈，缺 ToastProvider 会直接抛（不是一个静默失败）。
    const { container } = renderWithProviders(<ToastProvider><DepotStockPolicyPage /></ToastProvider>)
    expect(await screen.findByText('CP')).toBeInTheDocument()
    const radios = container.querySelectorAll('input[type="radio"]')
    fireEvent.click(radios[1])
    return container
  }

  it('未实现的档位可见但不可选（服务端下发 implemented=false）', async () => {
    const container = await selectCp()
    const selects = container.querySelectorAll('select')
    expect(selects.length).toBe(2)
    const batchOptions = Array.from(selects[1].querySelectorAll('option'))
    expect(batchOptions.map((option) => option.textContent)).toEqual(['0 归零', '3 必填 + 效期（本版未实现）'])
    expect(batchOptions[1].disabled).toBe(true)
    expect(batchOptions[0].disabled).toBe(false)
  })

  it('保存按官方策略端点提交，破坏性下调先要确认、确认后带 confirmDowngrade 重发', async () => {
    const container = await selectCp()
    apiClientMock.put
      .mockRejectedValueOnce(new ApiError(400, {
        code: 'VALIDATION_FAILED',
        message: '需要确认',
        ...({ errors: ['位置档位 3→0 会让系统只看未指定位置行'], warnings: [], requiresConfirmation: true } as object),
      } as never))
      .mockResolvedValueOnce({ saved: true, errors: [], warnings: [], requiresConfirmation: false })

    fireEvent.click(screen.getByRole('button', { name: /保存/ }))

    expect(await screen.findByText('这是一次破坏性下调，需要确认')).toBeInTheDocument()
    expect(apiClientMock.put).toHaveBeenCalledTimes(1)
    expect(apiClientMock.put.mock.calls[0][0]).toBe('/admin/depot-stock-policy/CP')
    expect(apiClientMock.put.mock.calls[0][1]).toMatchObject({ confirmDowngrade: false, locationMode: 0, batchMode: 0 })

    fireEvent.click(screen.getByRole('button', { name: '确认下调' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(2))
    expect(apiClientMock.put.mock.calls[1][1]).toMatchObject({ confirmDowngrade: true, relocateTo: null })
    expect(await screen.findByText('已保存。')).toBeInTheDocument()
    void container
  })

  it('服务端拒存时把错误原文显示出来，不吞成静默失败', async () => {
    await selectCp()
    apiClientMock.put.mockRejectedValueOnce(new ApiError(400, {
      code: 'VALIDATION_FAILED',
      message: '需要确认',
      ...({ errors: ['批次档位 3（必填 + 效期）本版未实现，不能保存为生效配置。'], warnings: [], requiresConfirmation: false } as object),
    } as never))

    fireEvent.click(screen.getByRole('button', { name: /保存/ }))

    expect(await screen.findByText(/本版未实现，不能保存为生效配置/)).toBeInTheDocument()
  })

  it('服务端没下发动作名单时不渲染任何操作按钮（页面不认识具体动作）', async () => {
    await selectCp()
    expect(screen.queryByRole('button', { name: '哨兵存量归位' })).not.toBeInTheDocument()
  })

  it('动作按名单渲染：填参数后先探路，确认才真执行', async () => {
    availableActions = [
      {
        key: 'relocate-sentinel', label: '哨兵存量归位', confirmTag: true, failMode: 'BLOCK', placement: 'master',
        params: { fields: [{ key: 'relocateTo', label: '目标库位', type: 'string', required: true, maxLength: 50 }] },
      },
    ]
    await selectCp()

    fireEvent.click(await screen.findByRole('button', { name: '哨兵存量归位' }))
    fireEvent.change(await screen.findByLabelText(/目标库位/), { target: { value: 'RACK-01' } })

    apiClientMock.post.mockResolvedValueOnce({ outcome: 'message', message: '将生成：…', requiresConfirmation: true })
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(1))
    // 第一次是探路：confirm=false，带着参数与选中库别的主键
    expect(apiClientMock.post.mock.calls[0][0]).toBe('/document-workbench/110310/action/relocate-sentinel')
    expect(apiClientMock.post.mock.calls[0][1]).toMatchObject({
      key: ['CP'], params: { relocateTo: 'RACK-01' }, confirm: false,
    })

    apiClientMock.post.mockResolvedValueOnce({ outcome: 'refreshed', message: '已完成归位：…' })
    fireEvent.click(screen.getByRole('button', { name: '确定' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(2))
    expect(apiClientMock.post.mock.calls[1][1]).toMatchObject({ confirm: true, params: { relocateTo: 'RACK-01' } })
    expect(await screen.findByText('已完成归位：…')).toBeInTheDocument()
  })
})
