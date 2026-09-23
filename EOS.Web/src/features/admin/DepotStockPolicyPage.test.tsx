import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
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

const depots = [
  { depotId: 'CP', depotName: '成品仓' },
  { depotId: 'ZZ', depotName: '测试仓' },
]

const locations = [
  { locationNo: 'RACK-01', locationName: '货架一' },
]

describe('DepotStockPolicyPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.includes('/tiers')) return tiers
      if (path.includes('/locations')) return locations
      if (path.includes('/depots')) return depots
      return policies
    })
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
    await selectCp()
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    const dialog = await screen.findByRole('dialog')
    const selects = within(dialog).getAllByRole('combobox')
    expect(selects.length).toBe(3)
    const batchOptions = within(selects[1] as HTMLElement).getAllByRole('option')
    expect(batchOptions.map((option) => option.textContent)).toEqual(['0 归零', '3 必填 + 效期（本版未实现）'])
    expect((batchOptions[1] as HTMLOptionElement).disabled).toBe(true)
    expect((batchOptions[0] as HTMLOptionElement).disabled).toBe(false)
  })

  it('保存按官方策略端点提交，破坏性下调先要确认、确认后带 confirmDowngrade 重发', async () => {
    await selectCp()
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    await screen.findByRole('dialog')
    apiClientMock.put
      .mockRejectedValueOnce(new ApiError(400, {
        code: 'VALIDATION_FAILED',
        message: '需要确认',
        ...({ errors: ['位置档位 3→0 会让系统只看未指定位置行'], warnings: [], requiresConfirmation: true } as object),
      } as never))
      .mockResolvedValueOnce({ saved: true, errors: [], warnings: [], requiresConfirmation: false })

    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    expect(await screen.findByText('这是一次破坏性下调，需要确认')).toBeInTheDocument()
    expect(apiClientMock.put).toHaveBeenCalledTimes(1)
    expect(apiClientMock.put.mock.calls[0][0]).toBe('/admin/depot-stock-policy/CP')
    expect(apiClientMock.put.mock.calls[0][1]).toMatchObject({ confirmDowngrade: false, locationMode: 0, batchMode: 0 })

    fireEvent.click(screen.getByRole('button', { name: '确认下调' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(2))
    expect(apiClientMock.put.mock.calls[1][1]).toMatchObject({ confirmDowngrade: true, relocateTo: null })
    expect(await screen.findByText('已保存。')).toBeInTheDocument()
  })

  it('服务端拒存时把错误原文显示出来，不吞成静默失败', async () => {
    await selectCp()
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    await screen.findByRole('dialog')
    apiClientMock.put.mockRejectedValueOnce(new ApiError(400, {
      code: 'VALIDATION_FAILED',
      message: '需要确认',
      ...({ errors: ['批次档位 3（必填 + 效期）本版未实现，不能保存为生效配置。'], warnings: [], requiresConfirmation: false } as object),
    } as never))

    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    expect(await screen.findByText(/本版未实现，不能保存为生效配置/)).toBeInTheDocument()
  })

  it('新增走弹窗：只列未配置的库别，选中后按官方端点提交', async () => {
    renderWithProviders(<DepotStockPolicyPage />)
    expect(await screen.findByText('CP')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    const dialog = await screen.findByRole('dialog')
    // CP 已有策略行，下拉里只剩 ZZ；已配置的行不出现
    const depotOptions = within(dialog).getByLabelText('库别') as HTMLSelectElement
    expect(Array.from(depotOptions.querySelectorAll('option')).map((o) => o.value)).toEqual(['ZZ'])
    apiClientMock.put.mockResolvedValueOnce({ saved: true, errors: [], warnings: [], requiresConfirmation: false })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalledTimes(1))
    expect(apiClientMock.put.mock.calls[0][0]).toBe('/admin/depot-stock-policy/ZZ')
  })

  it('部署级默认行不允许删除；库别行删除走确认弹窗', async () => {
    const { container } = renderWithProviders(<DepotStockPolicyPage />)
    expect(await screen.findByText('CP')).toBeInTheDocument()
    const radios = container.querySelectorAll('input[type="radio"]')
    // 首行是部署级默认：删除按钮禁用
    fireEvent.click(radios[0])
    await waitFor(() => expect(screen.getByRole('button', { name: '删除' })).toBeDisabled())
    // 选中 CP：删除可用，确认后调删除端点（重渲染后节点过期，重新查询）
    fireEvent.click(container.querySelectorAll('input[type="radio"]')[1])
    await waitFor(() => expect(screen.getByRole('button', { name: '删除' })).not.toBeDisabled())
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    await screen.findByText(/确认删除库别/)
    apiClientMock.delete.mockResolvedValueOnce({})
    fireEvent.click(screen.getByRole('button', { name: '确认删除' }))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalledTimes(1))
    expect(apiClientMock.delete.mock.calls[0][0]).toBe('/admin/depot-stock-policy/CP')
  })

  it('双击行直接进入编辑：标题带仓名', async () => {
    const { container } = renderWithProviders(<DepotStockPolicyPage />)
    expect(await screen.findByText('CP')).toBeInTheDocument()
    const rows = container.querySelectorAll('tbody tr')
    fireEvent.doubleClick(rows[1])
    expect(await screen.findByText('修改CP策略')).toBeInTheDocument()
  })

  it('所有仓库行编辑标题为修改所有仓库策略，且没有归位目标栏', async () => {
    const { container } = renderWithProviders(<DepotStockPolicyPage />)
    expect(await screen.findByText('所有仓库')).toBeInTheDocument()
    const radios = container.querySelectorAll('input[type="radio"]')
    fireEvent.click(radios[0])
    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    expect(await screen.findByText('修改所有仓库策略')).toBeInTheDocument()
    expect(screen.queryByLabelText('升档归位目标库位')).not.toBeInTheDocument()
  })

  it('归位是本页自管的独立动作：先预览，确认后才真执行', async () => {
    await selectCp()
    fireEvent.change(await screen.findByLabelText('目标库位'), { target: { value: 'RACK-01' } })
    apiClientMock.post.mockResolvedValueOnce({
      saved: false, requiresConfirmation: true, pendingGroups: 2, message: '将把 2 组改记到 RACK-01，库别总量不变。',
    })
    fireEvent.click(screen.getByRole('button', { name: '预览' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(1))
    expect(apiClientMock.post.mock.calls[0][0]).toBe('/admin/depot-stock-policy/CP/relocate')
    expect(apiClientMock.post.mock.calls[0][1]).toMatchObject({ relocateTo: 'RACK-01', confirm: false })
    expect(await screen.findByText(/将把 2 组改记到/)).toBeInTheDocument()

    apiClientMock.post.mockResolvedValueOnce({ saved: true, message: '已完成归位：2 组已改记到 RACK-01，库别总量不变。' })
    fireEvent.click(screen.getByRole('button', { name: /确认执行/ }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledTimes(2))
    expect(apiClientMock.post.mock.calls[1][1]).toMatchObject({ relocateTo: 'RACK-01', confirm: true })
    expect(await screen.findByText(/已完成归位/)).toBeInTheDocument()
  })
})
