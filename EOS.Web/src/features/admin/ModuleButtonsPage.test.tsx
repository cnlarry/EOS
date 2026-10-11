import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleButtonsPage } from './ModuleButtonsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const moduleChooserData = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int' },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar' },
    { key: 'MASTER_TABLE', label: '主表', dataType: 'nvarchar' },
  ],
  rows: [{ M_IDX: 1607, M_DESC: '收料单', MASTER_TABLE: 'PUR_RECEIVE_M' }],
  total: 1,
}

const buttonsView = {
  moduleId: 1607,
  buttons: [
    { seq: 1, eventCode: 'MANUAL', effectKey: 'recalc-account', enabled: true, label: '重算科目', remark: null },
  ],
}

/** 授权镜子：0/0 是 fail-closed 的正常态，但界面必须显眼提示——它是静默失效，不是错误。 */
const authorizationView = {
  buttons: [{ seq: 1, effectKey: 'recalc-account', label: '重算科目', users: 0, groups: 0 }],
}

describe('ModuleButtonsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-buttons/meta') {
        return { buttons: [{ key: 'recalc-account', label: '重算科目', placement: 'TOOLBAR' }] }
      }
      if (path.endsWith('/action-authorization')) return authorizationView
      return buttonsView
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return moduleChooserData
      return undefined
    })
    apiClientMock.put.mockImplementation(async () => undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  async function pickModule() {
    renderWithProviders(<ModuleButtonsPage />)
    fireEvent.click(screen.getByRole('button', { name: '选择模块' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByText('收料单')
    fireEvent.click(within(dialog).getAllByLabelText('选择此行')[0])
    fireEvent.click(within(dialog).getByRole('button', { name: '确认' }))
  }

  it('未选择模块时提示先选模块，列表工具不可用', () => {
    renderWithProviders(<ModuleButtonsPage />)
    expect(screen.getByText('还没有选择模块')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增按钮' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
    expect(screen.getByRole('button', { name: '刷新' })).toBeDisabled()
  })

  it('选定模块后按模块号取按钮行，并显示授权镜子（0/0 显式提示无人可用）', async () => {
    await pickModule()

    expect(await screen.findByText('重算科目')).toBeInTheDocument()
    // 授权镜子：配了没人能用是正常状态，但必须是显式的，不能静默
    expect(screen.getByText('无人可用')).toBeInTheDocument()
    const calls = apiClientMock.get.mock.calls.map((call) => String(call[0]))
    expect(calls).toContain('/admin/module-buttons/1607')
    expect(calls).toContain('/admin/module-buttons/1607/action-authorization')
    expect(calls).toContain('/admin/module-buttons/meta')
  })

  it('保存走整面替换：PUT 只带按钮行、不带校验规则（manual 作用域的客户端一半）', async () => {
    await pickModule()
    await screen.findByText('重算科目')

    fireEvent.click(screen.getByRole('button', { name: '新增按钮' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('按钮文案'), { target: { value: '重算成本' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '确定' }))

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())

    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-buttons/1607')
    const typed = body as { actions: { eventCode: string; label?: string | null }[]; validationRules: unknown[] }
    // 越域的行服务端会拒，客户端就不该带：只发按钮行、不发校验规则
    expect(typed.validationRules).toEqual([])
    expect(typed.actions).toHaveLength(2)
    expect(typed.actions.every((action) => action.eventCode === 'MANUAL')).toBe(true)
    expect(typed.actions.some((action) => action.label === '重算成本')).toBe(true)
  })

  it('删除按钮后保存：请求体里不再有该行', async () => {
    await pickModule()
    await screen.findByText('重算科目')

    fireEvent.click(screen.getByText('重算科目'))
    fireEvent.click(screen.getByRole('button', { name: '删除' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: '删除' }))

    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())

    const [, body] = apiClientMock.put.mock.calls[0]
    const typed = body as { actions: unknown[] }
    expect(typed.actions).toHaveLength(0)
  })
})
