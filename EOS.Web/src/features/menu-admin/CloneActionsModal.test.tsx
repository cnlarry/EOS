import { fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import { CloneActionsModal } from './CloneActionsModal'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const moduleChooserData = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int', format: null },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar', format: null },
    { key: 'MASTER_TABLE', label: '操作主表', dataType: 'nvarchar', format: null },
    { key: 'DETAIL_TABLE', label: '操作副表', dataType: 'nvarchar', format: null },
    { key: 'ACTION_COUNT', label: '动作数', dataType: 'int', format: null },
  ],
  rows: [
    { M_IDX: 1505, M_DESC: '生产入库单', MASTER_TABLE: 'MOC_PRODUCT_M', DETAIL_TABLE: 'MOC_PRODUCT_D', ACTION_COUNT: 6 },
  ],
  total: 1,
}

const sourceConfig = {
  moduleId: 1505,
  actions: [
    {
      seq: 1,
      eventCode: 'APPROVE_EFFECT',
      effectKey: 'field-accumulate',
      effectName: '制令已入库量累加',
      enabled: true,
      failMode: 'BLOCK',
      ops: [
        {
          opSeq: 1,
          targetTable: 'MOC_PRODUCE_D',
          targetField: 'FINISHED_IN_QTY',
          opCode: 'ACCUM',
          sourceScope: 'DETAIL',
          sourceField: 'QTY',
          sourceAgg: 'SUM',
        },
      ],
    },
  ],
  validationRules: [],
}

describe('CloneActionsModal', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/1505')) return sourceConfig
      return {}
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return moduleChooserData
      throw new Error(`unexpected POST ${path}`)
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('选来源模块后列出其动作，勾选后把整条动作交回调用方', async () => {
    const onAppend = vi.fn()
    renderWithProviders(
      <CloneActionsModal open currentModuleId={1607} onClose={() => {}} onAppend={onAppend} />,
    )

    fireEvent.click(screen.getByRole('button', { name: '选择模块' }))
    await waitFor(() => expect(screen.getByText('选择来源模块')).toBeInTheDocument())
    fireEvent.click(screen.getByText('生产入库单'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    // 来源模块的动作用于勾选；未勾选时追加按钮禁用。
    expect(await screen.findByText('制令已入库量累加')).toBeInTheDocument()
    expect(screen.getByText('MOC_PRODUCE_D')).toBeInTheDocument()
    const append = screen.getByRole('button', { name: '追加到本模块' })
    expect(append).toBeDisabled()

    fireEvent.click(screen.getAllByLabelText('选择该动作')[0])
    await waitFor(() => expect(screen.getByRole('button', { name: '追加到本模块' })).toBeEnabled())
    expect(screen.getByText('已选 1 个动作')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '追加到本模块' }))
    await waitFor(() => expect(onAppend).toHaveBeenCalledTimes(1))
    expect(onAppend.mock.calls[0][0]).toHaveLength(1)
    expect(onAppend.mock.calls[0][0][0].ops).toHaveLength(1)
  })

  it('未选来源模块时不发起来源配置请求', () => {
    renderWithProviders(
      <CloneActionsModal open currentModuleId={1607} onClose={() => {}} onAppend={() => {}} />,
    )

    expect(apiClientMock.get).not.toHaveBeenCalled()
    expect(screen.getByText('先选择一个来源模块，再勾选要复制的业务动作。')).toBeInTheDocument()
  })
})
