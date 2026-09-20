import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DepotStockPolicyPage } from './DepotStockPolicyPage'
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

describe('DepotStockPolicyPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) =>
      path.includes('/tiers') ? tiers : policies)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  async function selectCp() {
    const { container } = renderWithProviders(<DepotStockPolicyPage />)
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
})
