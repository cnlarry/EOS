import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleActionsPage } from './ModuleActionsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

/**
 * 编辑面板本身有自己的用例（`BusinessActionsPanel.test.tsx`，它被三个面共用）。
 * 这里只测**页面自己的职责**：选模块 → 两个页签 → 保存走 `actions` 作用域 → 展示"已保存但未生效"。
 * 故把面板换成一个只上报草稿的桩，避免把面板的全部查询搬进本文件的 mock。
 */
// vi.hoisted：桩在模块导入期就会执行，草稿夹具必须先于导入初始化（否则落进 TDZ）。
const { panelDraft } = vi.hoisted(() => ({
  panelDraft: {
    moduleId: 1607,
    dirty: true,
    actions: [
      { seq: 1, eventCode: 'SAVE', effectKey: 'field-accumulate', enabled: true, failMode: 'BLOCK', ops: [] },
      { seq: 2, eventCode: 'MANUAL', effectKey: 'recalc-account', enabled: true, failMode: 'BLOCK', ops: [], label: '重算' },
    ],
    validationRules: [{ seq: 1, stage: 'SAVE', validationKey: 'reference-exists', enabled: true, params: '{}' }],
  },
}))

vi.mock('../menu-admin/BusinessActionsPanel', async () => {
  const React = await import('react')
  return {
    BusinessActionsPanel: ({ view, onDraftChange }: {
      view?: string | null
      onDraftChange?: (draft: unknown) => void
    }) => {
      React.useEffect(() => {
        onDraftChange?.(panelDraft)
      }, [onDraftChange])
      return <div data-testid="actions-panel" data-view={view ?? ''} />
    },
  }
})

const moduleChooserData = {
  columns: [
    { key: 'M_IDX', label: '模块号', dataType: 'int' },
    { key: 'M_DESC', label: '模块名', dataType: 'nvarchar' },
    { key: 'MASTER_TABLE', label: '主表', dataType: 'nvarchar' },
  ],
  rows: [{ M_IDX: 1607, M_DESC: '收料单', MASTER_TABLE: 'PUR_RECEIVE_M' }],
  total: 1,
}

const targetView = {
  M_IDX: 1607,
  M_DESC: '收料单',
  MASTER_TABLE: 'PUR_RECEIVE_M',
  DETAIL_TABLE: 'PUR_RECEIVE_D',
  MASTER_TABLE_DESC: '收料单主表',
  DETAIL_TABLE_DESC: '收料单明细',
}

/** 发布被校验拦下：保存已提交，但没生效——界面必须如实这么说，并给出逐条原因。 */
const blockedPublish = {
  saved: true,
  publish: {
    moduleId: 1607,
    title: '收料单',
    published: false,
    version: null,
    definitionVersion: null,
    passed: false,
    checks: [
      { code: 'lifecycle_columns', passed: false, message: '主表缺少 CONFIRM_TAG。', severity: 'error' },
      { code: 'effect_engine_keys_implemented', passed: true, message: '效果键全部可执行。', severity: 'info' },
    ],
  },
}

describe('ModuleActionsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-business-config/1607/target') return targetView
      return {}
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return moduleChooserData
      return undefined
    })
    apiClientMock.put.mockImplementation(async () => blockedPublish)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  async function pickModule() {
    renderWithProviders(<ModuleActionsPage />)
    fireEvent.click(screen.getByRole('button', { name: '选择模块' }))
    const dialog = await screen.findByRole('dialog')
    await within(dialog).findByText('收料单')
    fireEvent.click(within(dialog).getAllByLabelText('选择此行')[0])
    fireEvent.click(within(dialog).getByRole('button', { name: '确认' }))
  }

  it('未选择模块时提示先选模块，保存不可用', () => {
    renderWithProviders(<ModuleActionsPage />)
    expect(screen.getByText('还没有选择模块')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
  })

  it('选定模块后取目标模块形状，并渲染两个页签', async () => {
    await pickModule()

    expect(await screen.findByText(/收料单 · 主表 PUR_RECEIVE_M · 副表 PUR_RECEIVE_D/)).toBeInTheDocument()
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0])))
      .toContain('/admin/module-business-config/1607/target')
    expect(screen.getByRole('tab', { name: '行为动作' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: '校验规则' })).toBeInTheDocument()
    expect(screen.getByTestId('actions-panel')).toHaveAttribute('data-view', 'actions')

    fireEvent.click(screen.getByRole('tab', { name: '校验规则' }))
    expect(screen.getByTestId('actions-panel')).toHaveAttribute('data-view', 'rules')
    expect(screen.getByRole('tab', { name: '校验规则' })).toHaveAttribute('aria-selected', 'true')
  })

  it('保存走 actions 作用域：只带效果链动作与校验规则，不带按钮行', async () => {
    await pickModule()
    await screen.findByTestId('actions-panel')

    // 草稿由面板上报后才可保存（面板桩在装载副作用里上报）
    const saveButton = screen.getByRole('button', { name: '保存' })
    await waitFor(() => expect(saveButton).toBeEnabled())
    fireEvent.click(saveButton)
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())

    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-business-config/1607?scope=actions')
    const typed = body as { actions: { eventCode: string }[]; validationRules: unknown[] }
    // 按钮行归 230103：本面不带它（服务端也会拒越域的行）
    expect(typed.actions.map((action) => action.eventCode)).toEqual(['SAVE'])
    expect(typed.validationRules).toHaveLength(1)
  })

  it('发布被拦下时明说"已保存，但未生效"并逐条给出原因', async () => {
    await pickModule()
    await screen.findByTestId('actions-panel')

    const saveButton = screen.getByRole('button', { name: '保存' })
    await waitFor(() => expect(saveButton).toBeEnabled())
    fireEvent.click(saveButton)

    expect(await screen.findByText('未生效')).toBeInTheDocument()
    expect(screen.getByText(/发布未通过校验/)).toBeInTheDocument()
    expect(screen.getByText('主表缺少 CONFIRM_TAG。')).toBeInTheDocument()
    // 通过的那条不列出来
    expect(screen.queryByText('效果键全部可执行。')).toBeNull()
  })
})
