import { renderWithProviders } from '../../test/renderWithProviders'
import { apiClientMock } from '../../test/apiMock'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ModuleActionsPage } from './ModuleActionsPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

/**
 * 编辑面板本身有自己的用例（`BusinessActionsPanel.test.tsx`，它被三个面共用）。
 * 这里只测**页面自己的职责**：左栏选模块 → 两个页签 → 保存走 `actions` 作用域 → 展示"已保存但未生效"。
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

const overviewView = {
  canWrite: true,
  targets: [
    {
      moduleId: 1607, moduleDesc: '收料单', masterTable: 'PUR_RECEIVE_M', masterTableDesc: '收料单主表',
      detailTable: 'PUR_RECEIVE_D', detailTableDesc: '收料单明细', configured: 2,
    },
    {
      moduleId: 170204, moduleDesc: '其它付款凭证', masterTable: 'PUR_PAY_OTHER', masterTableDesc: '付款凭证主表',
      detailTable: '', detailTableDesc: '', configured: 0,
    },
  ],
  actions: [
    { moduleId: 1607, moduleDesc: '收料单', eventCode: 'SAVE', seq: 1, effectKey: 'field-accumulate', enabled: true, failMode: 'BLOCK' },
  ],
  validationRules: [
    { moduleId: 170204, moduleDesc: '其它付款凭证', seq: 1, stage: 'SAVE', validationKey: 'reference-exists', enabled: true, message: '金额不为零' },
  ],
}

describe('ModuleActionsPage', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-business-config/1607/target') return targetView
      if (path === '/admin/module-business-config/overview') return overviewView
      return {}
    })
    apiClientMock.put.mockImplementation(async () => blockedPublish)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  /** 从左栏点模块进编辑（没有弹窗了）。 */
  async function pickModule() {
    renderWithProviders(<ModuleActionsPage />)
    fireEvent.click(await screen.findByRole('button', { name: /^收料单/ }))
    await screen.findByTestId('actions-panel')
  }

  it('未选模块时右栏给出全库总览（两个页签各列一份），保存不可用', async () => {
    renderWithProviders(<ModuleActionsPage />)

    // 行为动作页签：全库效果链动作
    expect(await screen.findByText('field-accumulate')).toBeInTheDocument()
    expect(screen.getByText('全库总览')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0])))
      .toContain('/admin/module-business-config/overview')

    // 校验规则页签：全库校验规则
    fireEvent.click(screen.getByRole('tab', { name: '校验规则' }))
    expect(await screen.findByText('reference-exists')).toBeInTheDocument()
    expect(screen.getByText('金额不为零')).toBeInTheDocument()
  })

  it('总览里点一行即进那个模块', async () => {
    renderWithProviders(<ModuleActionsPage />)

    fireEvent.click(await screen.findByText('field-accumulate'))
    expect(await screen.findByTestId('actions-panel')).toBeInTheDocument()
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0])))
      .toContain('/admin/module-business-config/1607/target')
  })

  it('选中模块后取目标模块形状，并渲染两个页签', async () => {
    await pickModule()

    expect(await screen.findByText('可改')).toBeInTheDocument()
    // 「收料单」左栏与右栏头部各一处（这是正常的：左栏是清单、右栏是选中对象）
    expect(screen.getAllByText('收料单').length).toBeGreaterThanOrEqual(2)
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

    // 草稿由面板上报后才可保存（面板桩在装载副作用里上报）
    const saveButton = screen.getByRole('button', { name: '保存' })
    await waitFor(() => expect(saveButton).toBeEnabled())
    fireEvent.click(saveButton)
    await waitFor(() => expect(apiClientMock.put).toHaveBeenCalled())

    const [path, body] = apiClientMock.put.mock.calls[0]
    expect(path).toBe('/admin/module-business-config/1607?scope=actions')
    expect((body as { actions: { eventCode: string }[] }).actions.map((action) => action.eventCode)).toEqual(['SAVE'])
    expect((body as { validationRules: unknown[] }).validationRules).toHaveLength(1)
  })

  it('发布被拦下时明说"已保存，但未生效"并逐条给出原因', async () => {
    await pickModule()

    const saveButton = screen.getByRole('button', { name: '保存' })
    await waitFor(() => expect(saveButton).toBeEnabled())
    fireEvent.click(saveButton)

    const alert = await screen.findByRole('alert')
    // 整句被 <span> 切成多个文本节点，按 textContent 断言（按纯文本匹配会碎）
    expect(alert).toHaveTextContent('已保存，但未生效')
    expect(within(alert).getByText('主表缺少 CONFIRM_TAG。')).toBeInTheDocument()
  })
})
