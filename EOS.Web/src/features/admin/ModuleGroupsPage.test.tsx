import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleGroupsPage } from './ModuleGroupsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

/** 总览：全库分组行（跨模块），同一模块的多行是常态。 */
const overview = {
  canWrite: true,
  groups: [
    {
      moduleId: 170204, moduleDesc: '其它付款凭证', groupId: 1, sortIdx: 10, description: '应付月份',
      expression: 'CAST(YEAR(PAY_DATE) AS VARCHAR)', available: true, error: null,
    },
    {
      moduleId: 170204, moduleDesc: '其它付款凭证', groupId: 2, sortIdx: 20, description: '坏表达式',
      expression: 'NOT_A_FIELD', available: false, error: '未知列 NOT_A_FIELD',
    },
    {
      moduleId: 180651, moduleDesc: '排班', groupId: 3, sortIdx: 10, description: '按班别',
      expression: 'SHIFT_NO', available: true, error: null,
    },
  ],
}

/** 按模块读的那条路径多一组样例值（弹窗里显示"当前生效值"）。 */
const groupView = {
  moduleId: 170204,
  moduleDesc: '其它付款凭证',
  nodeKind: 'WORKBENCH',
  masterTable: 'PUR_PAY_OTHER',
  groups: [
    {
      groupId: 1, sortIdx: 10, description: '应付月份', expression: 'CAST(YEAR(PAY_DATE) AS VARCHAR)',
      available: true, error: null, samples: ['2026', '2027'], sampleError: null,
    },
    {
      groupId: 2, sortIdx: 20, description: '坏表达式', expression: 'NOT_A_FIELD',
      available: false, error: '未知列 NOT_A_FIELD', samples: [], sampleError: null,
    },
  ],
}

const chooserData = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int', format: null },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar', format: null },
  ],
  rows: [{ M_IDX: 180651, M_DESC: '排班', MASTER_TABLE: 'HRM_PLAN_M' }],
  total: 1,
}

class NoopIntersectionObserver {
  static instances: NoopIntersectionObserver[] = []
  callback: IntersectionObserverCallback

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback
    NoopIntersectionObserver.instances.push(this)
  }

  observe = vi.fn()
  unobserve = vi.fn()
  disconnect = vi.fn()
}

describe('ModuleGroupsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockReset()
    apiClientMock.post.mockReset()
    apiClientMock.put.mockReset()
    apiClientMock.delete.mockReset()
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-groups/overview') return overview
      if (path.endsWith('/fields')) return { fields: ['PAY_DATE', 'FINISHED_TAG'] }
      return groupView
    })
    apiClientMock.post.mockResolvedValue(chooserData)
    apiClientMock.put.mockResolvedValue(undefined)
    apiClientMock.delete.mockResolvedValue(undefined)
    vi.stubGlobal('IntersectionObserver', NoopIntersectionObserver)
    NoopIntersectionObserver.instances = []
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('扁平列出全库分组行：模块是行上的一列，每行都写，关键字能收窄', async () => {
    renderWithProviders(<ModuleGroupsPage />)

    expect(await screen.findByText('应付月份')).toBeInTheDocument()
    expect(screen.getByText('按班别')).toBeInTheDocument()
    // 同一模块的两行都带模块名（不做跨行合并，复制与排序才不被破坏）
    expect(screen.getAllByText('其它付款凭证')).toHaveLength(2)
    expect(screen.getAllByText('170204')).toHaveLength(2)
    expect(screen.getByText('180651')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('搜索分组'), { target: { value: '排班' } })
    await waitFor(() => expect(screen.queryByText('应付月份')).toBeNull())
    expect(screen.getByText('按班别')).toBeInTheDocument()
  })

  it('同一模块的行在视觉上是一块：每块第一行挂分块标记', async () => {
    renderWithProviders(<ModuleGroupsPage />)
    await screen.findByText('应付月份')

    // 两个模块各一个块首 = 2 处标记；第二行（同模块）不该有
    expect(document.querySelectorAll('tr.erp-module-block-start')).toHaveLength(2)
  })

  it('表达式校验列给出可用性与不可用原因', async () => {
    renderWithProviders(<ModuleGroupsPage />)

    // 两条可用的、一条不可用的
    expect(await screen.findAllByText('可用')).toHaveLength(2)
    expect(screen.getByText('不可用')).toHaveAttribute('title', '未知列 NOT_A_FIELD')
  })

  it('操作列「编辑」开弹窗并预填，保存只 PUT 这一行', async () => {
    renderWithProviders(<ModuleGroupsPage />)
    fireEvent.click((await screen.findAllByRole('button', { name: '编辑' }))[0])

    const dialog = within(await screen.findByRole('dialog'))
    const name = dialog.getByLabelText('分组名称')
    expect(name).toHaveValue('应付月份')
    // 样例值随按模块读的那条路径回来，在弹窗里显示"当前生效值"
    expect(await dialog.findByText(/2026 \/ 2027/)).toBeInTheDocument()

    fireEvent.change(name, { target: { value: '应付年月' } })
    fireEvent.change(dialog.getByLabelText('分组表达式'), { target: { value: 'CAST(YEAR(PAY_DATE) AS VARCHAR)' } })
    fireEvent.change(dialog.getByLabelText('插入主表字段'), { target: { value: 'FINISHED_TAG' } })
    expect(dialog.getByLabelText('分组表达式')).toHaveValue('CAST(YEAR(PAY_DATE) AS VARCHAR)FINISHED_TAG')

    fireEvent.click(dialog.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-groups/170204/1')
    expect(body).toEqual({
      description: '应付年月',
      expression: 'CAST(YEAR(PAY_DATE) AS VARCHAR)FINISHED_TAG',
    })
    // 逐行写：未动的第二行不该被提交
    expect(apiClientMock.put).toHaveBeenCalledTimes(1)
    expect(await screen.findByText(/已保存/)).toBeInTheDocument()
  })

  it('「新增」先选模块（统一选择器）再填表，保存走新增端点', async () => {
    renderWithProviders(<ModuleGroupsPage />)
    await screen.findByText('应付月份')
    fireEvent.click(screen.getByRole('button', { name: '新增' }))

    const dialog = within(await screen.findByRole('dialog'))
    // 还没选模块时不能保存
    expect(dialog.getByRole('button', { name: '保存' })).toBeDisabled()

    fireEvent.click(dialog.getByRole('button', { name: '选择模块' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/chooser/query',
      expect.objectContaining({ sourceKey: 'module-groups.targets' }),
    ))
    fireEvent.click(await screen.findByLabelText('选择此行'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    await waitFor(() => expect(screen.queryByRole('button', { name: '确认' })).toBeNull())
    fireEvent.change(dialog.getByLabelText('分组名称'), { target: { value: '结案' } })
    fireEvent.change(dialog.getByLabelText('分组表达式'), { target: { value: 'FINISHED_TAG' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))

    // 新增走 POST（不是 PUT），且落到刚选的那个模块上
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/module-groups/180651',
      { description: '结案', expression: 'FINISHED_TAG' },
    ))
    expect(apiClientMock.put).not.toHaveBeenCalled()
  })

  it('删除要确认：确认后直接走删除端点', async () => {
    renderWithProviders(<ModuleGroupsPage />)
    fireEvent.click((await screen.findAllByRole('button', { name: '删除' }))[1])

    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/分组下拉会少一项/)).toBeInTheDocument()
    fireEvent.click(dialog.getByRole('button', { name: '确认删除' }))

    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalled())
    expect(apiClientMock.delete.mock.calls[0][0]).toBe('/admin/module-groups/170204/2')
  })

  it('顺序按模块内位置启用：块内第一行不能上移，第二行可以', async () => {
    renderWithProviders(<ModuleGroupsPage />)

    expect(await screen.findByRole('button', { name: '上移 应付月份' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '下移 应付月份' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '上移 坏表达式' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '下移 坏表达式' })).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: '上移 坏表达式' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/admin/module-groups/170204/2/move',
      { action: 'up' },
    ))
  })

  it('只读账号：新增 / 编辑 / 删除 / 调序禁用，看校验不受影响', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-groups/overview') return { ...overview, canWrite: false }
      if (path.endsWith('/fields')) return { fields: ['PAY_DATE'] }
      return groupView
    })
    renderWithProviders(<ModuleGroupsPage />)

    await screen.findByText('应付月份')
    expect(screen.getByRole('button', { name: '新增' })).toBeDisabled()
    expect(screen.getAllByRole('button', { name: '编辑' })[0]).toBeDisabled()
    expect(screen.getAllByRole('button', { name: '删除' })[0]).toBeDisabled()
    expect(screen.getByRole('button', { name: '上移 坏表达式' })).toBeDisabled()
    expect(screen.getByText(/只给了查看权/)).toBeInTheDocument()
  })
})
