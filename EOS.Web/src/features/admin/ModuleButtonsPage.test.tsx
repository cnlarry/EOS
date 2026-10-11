import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleButtonsPage } from './ModuleButtonsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const overview = {
  canWrite: true,
  targets: [
    {
      moduleId: 1607, moduleDesc: '收料单', masterTable: 'PUR_RECEIVE_M', masterTableDesc: '收料单主表',
      detailTable: 'PUR_RECEIVE_D', detailTableDesc: '收料单明细', configured: 1,
    },
    {
      moduleId: 170204, moduleDesc: '其它付款凭证', masterTable: 'PUR_PAY_OTHER', masterTableDesc: '付款凭证主表',
      detailTable: '', detailTableDesc: '', configured: 0,
    },
  ],
  actions: [
    { moduleId: 1607, moduleDesc: '收料单', seq: 1, eventCode: 'MANUAL', effectKey: 'recalc-account', enabled: true, label: '重算科目', remark: null },
  ],
}

const buttonsView = {
  moduleId: 1607,
  buttons: [
    { seq: 1, eventCode: 'MANUAL', effectKey: 'recalc-account', enabled: true, label: '重算科目', remark: '月结前用' },
  ],
}

/** 授权镜子：0/0 是 fail-closed 的正常态，界面必须显式说出来并指路。 */
const authorizationView = {
  buttons: [{ seq: 1, effectKey: 'recalc-account', label: '重算科目', users: 0, groups: 0 }],
}

describe('ModuleButtonsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-buttons/overview') return overview
      if (path === '/admin/module-buttons/meta') {
        return { buttons: [{ key: 'recalc-account', label: '重算科目', placement: 'TOOLBAR' }] }
      }
      if (path.endsWith('/action-authorization')) return authorizationView
      return buttonsView
    })
    apiClientMock.put.mockImplementation(async () => ({ saved: true, publish: { moduleId: 1607, title: '收料单', published: true, version: 3, definitionVersion: 'module-1607-v3', passed: true, checks: [] } }))
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('左栏默认只列已配过的模块，勾「显示全部模块」后列出全集', async () => {
    renderWithProviders(<ModuleButtonsPage />)

    expect(await screen.findByRole('button', { name: /^收料单/ })).toBeInTheDocument()
    // 未配过的模块默认折叠
    expect(screen.queryByRole('button', { name: /^其它付款凭证/ })).toBeNull()

    fireEvent.click(screen.getByLabelText('显示全部模块'))
    expect(await screen.findByRole('button', { name: /^其它付款凭证/ })).toBeInTheDocument()
  })

  it('单击左栏模块即切换右栏，并在状态条给出主/副表与可改标记', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^收料单/ }))

    expect(await screen.findByText('可改')).toBeInTheDocument()
    expect(screen.getByText(/主表 收料单主表 · 副表 收料单明细/)).toBeInTheDocument()
    // 行内编辑控件直接可用，不需要"编辑态"
    expect(await screen.findByLabelText('按钮文案-saved-0')).toHaveValue('重算科目')
  })

  it('行内编辑后「保存并生效」只发按钮行（不带校验规则、不带界面标识）', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^收料单/ }))

    const label = await screen.findByLabelText('按钮文案-saved-0')
    fireEvent.change(label, { target: { value: '重算成本' } })
    // 输入要跟手：表格行对象是快照，受控 value 读快照会把用户输的字顶回去
    expect(label).toHaveValue('重算成本')
    fireEvent.change(screen.getByLabelText('处理器-saved-0'), { target: { value: 'recalc-account' } })
    fireEvent.click(screen.getByRole('button', { name: '保存并生效' }))

    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())
    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-buttons/1607')
    const typed = body as { actions: Record<string, unknown>[]; validationRules: unknown[] }
    expect(typed.validationRules).toEqual([])
    expect(typed.actions).toHaveLength(1)
    expect(typed.actions[0].label).toBe('重算成本')
    expect(typed.actions[0].eventCode).toBe('MANUAL')
    expect(typed.actions[0]._key).toBeUndefined()

    // 保存即发布：给出已生效的版本
    expect(await screen.findByText(/已保存并发布/)).toBeInTheDocument()
  })

  it('「谁能用」打开授权抽屉：给出可用数、无人可用警示与去处', async () => {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^收料单/ }))

    fireEvent.click(await screen.findByLabelText('授权-saved-0'))
    const dialog = within(await screen.findByRole('dialog'))
    expect(dialog.getByText(/0 个用户 \/ 0 个组/)).toBeInTheDocument()
    expect(dialog.getByText(/配了没人能用/)).toBeInTheDocument()
    expect(dialog.getByRole('link', { name: '去用户权限设定' })).toHaveAttribute('href', '/admin/users')
  })

  it('只读账号：控件禁用并说明原因', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-buttons/overview') return { ...overview, canWrite: false }
      if (path === '/admin/module-buttons/meta') {
        return { buttons: [{ key: 'recalc-account', label: '重算科目', placement: 'TOOLBAR' }] }
      }
      if (path.endsWith('/action-authorization')) return authorizationView
      return buttonsView
    })
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^收料单/ }))

    expect(await screen.findByText('只读')).toBeInTheDocument()
    expect(screen.getByText(/只给了查看权/)).toBeInTheDocument()
    expect(await screen.findByLabelText('按钮文案-saved-0')).toBeDisabled()
    expect(screen.getByRole('button', { name: '保存并生效' })).toBeDisabled()
  })
})
