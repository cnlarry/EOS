import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleGroupsPage } from './ModuleGroupsPage'
import { ApiError } from '../../types/api'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const moduleChooserData = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int' },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar' },
    { key: 'MASTER_TABLE', label: '主表', dataType: 'nvarchar' },
  ],
  rows: [
    { M_IDX: 170204, M_DESC: '其它付款凭证', MASTER_TABLE: 'PUR_PAY_OTHER' },
    { M_IDX: 1201, M_DESC: '产品/料件基本资料', MASTER_TABLE: 'PRODUCT' },
  ],
  total: 2,
}

const groupView = {
  moduleId: 170204,
  moduleDesc: '其它付款凭证',
  nodeKind: 'WORKBENCH',
  masterTable: 'PUR_PAY_OTHER',
  groups: [
    {
      groupId: 1, sortIdx: 10, description: '应付月份',
      expression: '(CAST(YEAR(PAY_DATE) AS VARCHAR))', available: true, error: null,
    },
    {
      groupId: 2, sortIdx: 20, description: '结案',
      expression: 'case PUR_PAY_OTHER.FINISHED_TAG when 1 then \'YES\' else \'NO\' end',
      available: true, error: null,
    },
  ],
}

const customPageView = {
  moduleId: 1201,
  moduleDesc: '产品/料件基本资料',
  nodeKind: 'CUSTOMPAGE',
  masterTable: 'PRODUCT',
  groups: [],
}

describe('ModuleGroupsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path.endsWith('/fields')) return { fields: ['PAY_DATE', 'FINISHED_TAG'] }
      if (path === '/admin/module-groups/1201') return customPageView
      return groupView
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return moduleChooserData
      if (path.endsWith('/move')) return undefined
      return groupView.groups[0]
    })
    apiClientMock.put.mockImplementation(async () => groupView.groups[0])
    apiClientMock.delete.mockImplementation(async () => undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  async function pickModule(name = '其它付款凭证') {
    renderWithProviders(<ModuleGroupsPage />)
    fireEvent.click(screen.getByRole('button', { name: '选择模块' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByText(name)
    fireEvent.click(within(dialog).getAllByLabelText('选择此行')[0])
    fireEvent.click(within(dialog).getByRole('button', { name: '确认' }))
    return dialog
  }

  it('未选择模块时提示先选模块，列表工具不可用', () => {
    renderWithProviders(<ModuleGroupsPage />)
    expect(screen.getByText('还没有选择模块')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增分组' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '刷新' })).toBeDisabled()
  })

  it('选定模块后按模块号取分组，并显示表达式与校验列', async () => {
    await pickModule()
    expect(await screen.findByText('应付月份')).toBeInTheDocument()
    expect(screen.getByText('结案')).toBeInTheDocument()
    expect(screen.getByText('其它付款凭证（170204）· 主表 PUR_PAY_OTHER · 形态 统一工作台模块')).toBeInTheDocument()
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0]))).toContain('/admin/module-groups/170204')
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0]))).toContain('/admin/module-groups/170204/fields')
  })

  it('非工作台模块只能查看，新增分组按钮禁用并说明原因', async () => {
    renderWithProviders(<ModuleGroupsPage />)
    fireEvent.click(screen.getByRole('button', { name: '选择模块' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByText('产品/料件基本资料')
    fireEvent.click(within(dialog).getAllByLabelText('选择此行')[1])
    fireEvent.click(within(dialog).getByRole('button', { name: '确认' }))

    expect(await screen.findByText('产品/料件基本资料（1201）· 主表 PRODUCT · 形态 自定义承载页')).toBeInTheDocument()
    expect(screen.getByText(/没有分组消费方：只能查看，不能配分组/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增分组' })).toBeDisabled()
  })

  it('新增分组走 POST，并把服务端的拒绝理由显示在弹窗里', async () => {
    await pickModule()
    await screen.findByText('应付月份')
    fireEvent.click(screen.getByRole('button', { name: '新增分组' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('分组名称'), { target: { value: '客户' } })
    fireEvent.change(within(dialog).getByLabelText('分组表达式'), { target: { value: 'NOT_A_FIELD' } })

    apiClientMock.post.mockRejectedValueOnce(
      new ApiError(400, {
        type: 'about:blank',
        title: '请求参数不合法',
        status: 400,
        detail: '',
        instance: '',
        code: 'INVALID_ARGUMENT',
        message: '列 NOT_A_FIELD 不在主表 PUR_PAY_OTHER 的分组可用字段里（虚拟字段与未登记字段都不在其中）。',
      } as never),
    )
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))

    expect(await within(dialog).findByText(/列 NOT_A_FIELD 不在主表 PUR_PAY_OTHER 的分组可用字段里/)).toBeInTheDocument()
    // 第 0 次 POST 是选择器的 /chooser/query，取模块分组那一次
    const saveCall = apiClientMock.post.mock.calls.find(([path]) => String(path).startsWith('/admin/module-groups'))
    expect(saveCall?.[0]).toBe('/admin/module-groups/170204')
    expect(saveCall?.[1]).toEqual({ description: '客户', expression: 'NOT_A_FIELD' })
  })

  it('可用字段点一下追加到表达式', async () => {
    await pickModule()
    await screen.findByText('应付月份')
    fireEvent.click(screen.getByRole('button', { name: '新增分组' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(await within(dialog).findByRole('button', { name: 'PAY_DATE' }))
    expect(within(dialog).getByLabelText('分组表达式')).toHaveValue('PAY_DATE')
  })

  it('编辑走 PUT、删除走 DELETE、上下移走 move，路径都带模块号与分组号', async () => {
    await pickModule()
    await screen.findByText('应付月份')
    fireEvent.click(screen.getByLabelText('选择 结案'))

    fireEvent.click(screen.getByRole('button', { name: '编辑' }))
    const editDialog = await screen.findByRole('dialog')
    fireEvent.change(within(editDialog).getByLabelText('分组表达式'), { target: { value: 'FINISHED_TAG' } })
    fireEvent.click(within(editDialog).getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put.mock.calls[0][0]).toBe('/admin/module-groups/170204/2'))
    expect(apiClientMock.put.mock.calls[0][1]).toEqual({ description: '结案', expression: 'FINISHED_TAG' })

    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    const deleteDialog = await screen.findByRole('dialog')
    fireEvent.click(within(deleteDialog).getByRole('button', { name: '删除' }))
    await waitFor(() => expect(apiClientMock.delete.mock.calls[0][0]).toBe('/admin/module-groups/170204/2'))

    fireEvent.click(screen.getByLabelText('选择 结案'))
    fireEvent.click(screen.getByRole('button', { name: '上移' }))
    await waitFor(() => expect(apiClientMock.post.mock.calls.at(-1)?.[0]).toBe('/admin/module-groups/170204/2/move'))
    expect(apiClientMock.post.mock.calls.at(-1)?.[1]).toEqual({ action: 'up' })
  })
})
