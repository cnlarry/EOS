import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleButtonsPage } from './ModuleButtonsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

/** 总览：全库按钮行（跨模块）+ 授权镜子（0/0 = 配了没人能用）。 */
const overview = {
  canWrite: true,
  actions: [
    {
      moduleId: 1607, moduleDesc: '收料单', seq: 1, eventCode: 'MANUAL', effectKey: 'recalc-account',
      effectName: '重算科目', enabled: true, failMode: 'BLOCK', label: '重算科目', remark: '月结前用',
    },
    {
      moduleId: 170204, moduleDesc: '其它付款凭证', seq: 2, eventCode: 'MANUAL', effectKey: 'print-voucher',
      effectName: '打印凭证', enabled: false, failMode: 'BLOCK', label: null, remark: null,
    },
  ],
  buttonAuthorization: [
    { moduleId: 1607, buttons: [{ seq: 1, key: 'recalc-account', label: '重算科目', users: 0, groups: 0 }] },
  ],
}

/** 单模块的完整按钮行：保存是整面替换，回传要带上界面不显示的字段。 */
const buttonsView = {
  moduleId: 1607,
  buttons: [
    {
      seq: 1, eventCode: 'MANUAL', effectKey: 'recalc-account', enabled: true, failMode: 'BLOCK',
      label: '重算科目', remark: '月结前用', condition: null, params: null, ops: [],
    },
  ],
}

const chooserData = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int', format: null },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar', format: null },
  ],
  rows: [{ M_IDX: 170204, M_DESC: '其它付款凭证', MASTER_TABLE: 'PUR_PAY_OTHER' }],
  total: 1,
}

const publishOk = {
  saved: true,
  publish: {
    moduleId: 1607, title: '收料单', published: true, version: 3,
    definitionVersion: 'module-1607-v3', passed: true, checks: [],
  },
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

describe('ModuleButtonsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockReset()
    apiClientMock.post.mockReset()
    apiClientMock.put.mockReset()
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-buttons/overview') return overview
      if (path === '/admin/module-buttons/meta') {
        return { buttons: [{ key: 'recalc-account', label: '重算科目', placement: 'TOOLBAR' }] }
      }
      // 170204 还没有按钮行：新增到它上面时 SEQ 从 1 起
      if (path === '/admin/module-buttons/170204') return { moduleId: 170204, buttons: [] }
      return buttonsView
    })
    apiClientMock.post.mockResolvedValue(chooserData)
    apiClientMock.put.mockResolvedValue(publishOk)
    vi.stubGlobal('IntersectionObserver', NoopIntersectionObserver)
    NoopIntersectionObserver.instances = []
  })

  afterEach(() => {
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('扁平列出全库按钮：模块是行上的一列，关键字能收窄', async () => {
    renderWithProviders(<ModuleButtonsPage />)

    expect(await screen.findByText('收料单')).toBeInTheDocument()
    expect(screen.getByText('1607')).toBeInTheDocument()
    expect(screen.getByText('其它付款凭证')).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('搜索按钮'), { target: { value: '170204' } })
    await waitFor(() => expect(screen.queryByText('收料单')).toBeNull())
    expect(screen.getByText('其它付款凭证')).toBeInTheDocument()
  })

  it('「谁能用」把"配了没人能用"摆在行上，停用的行不计', async () => {
    renderWithProviders(<ModuleButtonsPage />)

    expect(await screen.findByText('⚠ 无人可用')).toBeInTheDocument()
    expect(screen.getByText('停用不计')).toBeInTheDocument()
  })

  it('操作列「编辑」开弹窗并预填，保存按模块整面替换且只发按钮行', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click((await screen.findAllByRole('button', { name: '编辑' }))[0])

    const dialog = within(await screen.findByRole('dialog'))
    const label = dialog.getByLabelText('按钮文案')
    expect(label).toHaveValue('重算科目')
    fireEvent.change(label, { target: { value: '重算成本' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-buttons/1607')
    const typed = body as { actions: Record<string, unknown>[]; validationRules: unknown[] }
    expect(typed.validationRules).toEqual([])
    expect(typed.actions).toHaveLength(1)
    expect(typed.actions[0].label).toBe('重算成本')
    expect(typed.actions[0].eventCode).toBe('MANUAL')
    // 界面不显示的字段（条件 / 参数 / 公式行）必须原样带回，否则一次编辑就抹掉了
    expect(typed.actions[0].ops).toEqual([])

    expect(await screen.findByText(/已保存并发布/)).toBeInTheDocument()
  })

  it('「新增」先选模块（统一选择器）再填表，保存把新行加进那个模块', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    // 列表数据到位前「新增」是禁用的（写权随总览一起回来），先等第一行渲染出来
    await screen.findByText('收料单')
    fireEvent.click(screen.getByRole('button', { name: '新增' }))

    const dialog = within(await screen.findByRole('dialog'))
    // 还没选模块时不能保存
    expect(dialog.getByRole('button', { name: '保存' })).toBeDisabled()

    fireEvent.click(dialog.getByRole('button', { name: '选择模块' }))
    // 候选集走的是服务端注册的配置对象数据源（与保存时的目标模块同源）
    await waitFor(() => expect(apiClientMock.post).toHaveBeenCalledWith(
      '/chooser/query',
      expect.objectContaining({ sourceKey: 'module-buttons.targets' }),
    ))
    fireEvent.click(await screen.findByLabelText('选择此行'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))

    await waitFor(() => expect(screen.queryByRole('button', { name: '确认' })).toBeNull())
    fireEvent.change(dialog.getByLabelText('按钮文案'), { target: { value: '打凭证' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-buttons/170204')
    const typed = body as { actions: Record<string, unknown>[] }
    // 目标模块原本没有按钮行 → 整面替换后只剩新加的这一个（SEQ 从 1 起）
    expect(typed.actions).toHaveLength(1)
    expect(typed.actions[0].label).toBe('打凭证')
    expect(typed.actions[0].seq).toBe(1)
    expect(typed.actions[0].eventCode).toBe('MANUAL')
  })

  it('删除先确认再执行：确认后整面替换把该行去掉', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click((await screen.findAllByRole('button', { name: '删除' }))[0])

    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/确认删除模块/)).toBeInTheDocument()
    fireEvent.click(dialog.getByRole('button', { name: '确认删除' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-buttons/1607')
    expect((body as { actions: unknown[] }).actions).toEqual([])
  })

  it('授权入口给出可用数与去处，并说明"能配不等于能用"', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click((await screen.findAllByRole('button', { name: '授权' }))[0])

    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/0 个用户 \/ 0 个组/)).toBeInTheDocument()
    expect(dialog.getByText(/配了没人能用/)).toBeInTheDocument()
    expect(dialog.getByRole('link', { name: '去用户权限设定' })).toHaveAttribute('href', '/admin/users')
  })

  it('只读账号：新增 / 编辑 / 删除禁用，看授权不受影响', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-buttons/overview') return { ...overview, canWrite: false }
      if (path === '/admin/module-buttons/meta') {
        return { buttons: [{ key: 'recalc-account', label: '重算科目', placement: 'TOOLBAR' }] }
      }
      return buttonsView
    })
    renderWithProviders(<ModuleButtonsPage />)

    await screen.findByText('收料单')
    expect(screen.getByRole('button', { name: '新增' })).toBeDisabled()
    expect(screen.getAllByRole('button', { name: '编辑' })[0]).toBeDisabled()
    expect(screen.getAllByRole('button', { name: '删除' })[0]).toBeDisabled()
    expect(screen.getAllByRole('button', { name: '授权' })[0]).toBeEnabled()
    expect(screen.getByText(/只给了查看权/)).toBeInTheDocument()
  })
})
