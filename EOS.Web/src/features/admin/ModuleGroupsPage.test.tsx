import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleGroupsPage } from './ModuleGroupsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const overview = {
  canWrite: true,
  targets: [
    {
      moduleId: 170204, moduleDesc: '其它付款凭证', masterTable: 'PUR_PAY_OTHER', masterTableDesc: '付款凭证主表',
      detailTable: '', detailTableDesc: '', configured: 2,
    },
    {
      moduleId: 180651, moduleDesc: '排班', masterTable: 'HRM_PLAN_M', masterTableDesc: '排班主表',
      detailTable: '', detailTableDesc: '', configured: 0,
    },
  ],
  groups: [
    { moduleId: 170204, moduleDesc: '其它付款凭证', groupId: 1, sortIdx: 10, description: '应付月份', expression: 'CAST(YEAR(PAY_DATE) AS VARCHAR)', available: true, error: null, samples: ['2026', '2027'], sampleError: null },
    { moduleId: 170204, moduleDesc: '其它付款凭证', groupId: 2, sortIdx: 20, description: '坏表达式', expression: 'NOT_A_FIELD', available: false, error: '未知列 NOT_A_FIELD', samples: [], sampleError: null },
  ],
}

const groupView = {
  moduleId: 170204,
  moduleDesc: '其它付款凭证',
  nodeKind: 'WORKBENCH',
  masterTable: 'PUR_PAY_OTHER',
  groups: [
    { groupId: 1, sortIdx: 10, description: '应付月份', expression: 'CAST(YEAR(PAY_DATE) AS VARCHAR)', available: true, error: null, samples: ['2026', '2027'], sampleError: null },
    { groupId: 2, sortIdx: 20, description: '坏表达式', expression: 'NOT_A_FIELD', available: false, error: '未知列 NOT_A_FIELD', samples: [], sampleError: null },
  ],
}

describe('ModuleGroupsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-groups/overview') return overview
      if (path.endsWith('/fields')) return { fields: ['PAY_DATE', 'FINISHED_TAG'] }
      return groupView
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  async function openModule() {
    renderWithProviders(<ModuleGroupsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^其它付款凭证/ }, { timeout: 5000 }))
    await screen.findByText('可改', undefined, { timeout: 5000 })
  }

  it('左栏默认只列已配过的模块，勾「显示全部模块」后列出全集', async () => {
    renderWithProviders(<ModuleGroupsPage />)

    // 全量跑时这个文件在并行 worker 里首屏会慢，默认 1s 的 findBy 窗口偶发不够（本仓既有用例同样显式放宽）
    expect(await screen.findByRole('button', { name: /^其它付款凭证/ }, { timeout: 5000 })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^排班/ })).toBeNull()

    fireEvent.click(screen.getByLabelText('显示全部模块'))
    expect(await screen.findByRole('button', { name: /^排班/ })).toBeInTheDocument()
  })

  it('表格给出样例值与不可用原因，点行才进编辑区', async () => {
    await openModule()

    // 样例值来自服务端在主表上真跑一次；不可用的表达式给原因、不给样例
    expect(await screen.findByText('2026 / 2027')).toBeInTheDocument()
    expect(screen.getByText('表达式不可用')).toHaveAttribute('title', '未知列 NOT_A_FIELD')
    // 未选中行时编辑区不出现，并提示怎么进来
    expect(screen.getByText('点表格里的一行来编辑它；「新增分组」会直接进编辑区。')).toBeInTheDocument()

    fireEvent.click(screen.getByText('应付月份'))
    expect(await screen.findByText('编辑分组（1）')).toBeInTheDocument()
    expect(screen.getByLabelText('分组名称')).toHaveValue('应付月份')
  })

  it('编辑区改名称与表达式（含插入字段），点一次「保存」按分组提交', async () => {
    await openModule()
    fireEvent.click(await screen.findByText('应付月份'))

    const name = await screen.findByLabelText('分组名称')
    fireEvent.change(name, { target: { value: '应付年月' } })
    expect(name).toHaveValue('应付年月')

    fireEvent.change(screen.getByLabelText('分组表达式'), { target: { value: 'CAST(YEAR(PAY_DATE) AS VARCHAR)' } })
    fireEvent.change(screen.getByLabelText('插入主表字段'), { target: { value: 'FINISHED_TAG' } })
    expect(screen.getByLabelText('分组表达式')).toHaveValue('CAST(YEAR(PAY_DATE) AS VARCHAR)FINISHED_TAG')

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-groups/170204/1')
    expect(body).toEqual({
      description: '应付年月',
      expression: 'CAST(YEAR(PAY_DATE) AS VARCHAR)FINISHED_TAG',
    })
    // 未改动的第二行不该被提交
    expect(apiClientMock.put).toHaveBeenCalledTimes(1)
  })

  it('新增分组直接进编辑区，保存走新增端点', async () => {
    await openModule()

    fireEvent.click(await screen.findByRole('button', { name: '新增分组' }))
    expect(await screen.findByText('新增分组（未保存）')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('分组名称'), { target: { value: '结案' } })
    fireEvent.change(screen.getByLabelText('分组表达式'), { target: { value: 'FINISHED_TAG' } })

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalled())
    const [path, body] = apiClientMock.post.mock.calls[0]
    expect(path).toBe('/admin/module-groups/170204')
    expect(body).toEqual({ description: '结案', expression: 'FINISHED_TAG' })
  })

  it('删除要确认，确认后点「保存」才走删除端点', async () => {
    await openModule()

    const deleteButton = await screen.findByLabelText('删除-2')
    expect(deleteButton).toBeEnabled()
    fireEvent.click(deleteButton)

    const banner = await screen.findByRole('alert')
    expect(within(banner).getByText(/列表页的分组下拉会少一项/)).toBeInTheDocument()
    fireEvent.click(within(banner).getByRole('button', { name: '确认删除' }))

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.delete).toHaveBeenCalled())
    expect(apiClientMock.delete.mock.calls[0][0]).toBe('/admin/module-groups/170204/2')
  })

  it('只读账号：编辑区禁用并说明原因', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-groups/overview') return { ...overview, canWrite: false }
      if (path.endsWith('/fields')) return { fields: ['PAY_DATE'] }
      return groupView
    })
    renderWithProviders(<ModuleGroupsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^其它付款凭证/ }))

    expect(await screen.findByText('只读')).toBeInTheDocument()
    expect(screen.getByText(/只给了查看权/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()

    fireEvent.click(await screen.findByText('应付月份'))
    expect(await screen.findByLabelText('分组名称')).toBeDisabled()
  })
})
