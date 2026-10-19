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

  it('未选模块时右栏给出全库总览：按模块聚合在前，明细行可切，保存不可用', async () => {
    renderWithProviders(<ModuleActionsPage />)

    // 全库总览：先给"谁配了什么"的模块级答案，而不是一上来就铺 328 行
    const overview = await screen.findByTestId('config-overview')
    expect(screen.getByText('全库总览')).toBeInTheDocument()
    await waitFor(() => expect(overview).toHaveTextContent('2 个模块有配置'))
    expect(overview).toHaveTextContent('1 条动作')
    expect(overview).toHaveTextContent('1 条规则')

    // 左栏筛选是一个下拉、默认「全部」；选项里带条数（一个总数看不出配的是动作还是规则）
    const filter = screen.getByRole('combobox', { name: '模块筛选' }) as HTMLSelectElement
    expect(filter.value).toBe('all')
    expect(Array.from(filter.options).map((option) => option.textContent)).toEqual([
      '有配置（2）', '有动作（1）', '有规则（1）', '全部（2）',
    ])
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0])))
      .toContain('/admin/module-business-config/overview')

    // 逐行事实仍在：动作明细 / 规则明细
    fireEvent.click(screen.getByRole('button', { name: '动作明细' }))
    expect(await screen.findByText('field-accumulate')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '规则明细' }))
    expect(await screen.findByText('reference-exists')).toBeInTheDocument()
    expect(screen.getByText('金额不为零')).toBeInTheDocument()
  })

  it('总览按模块点一行即进那个模块', async () => {
    renderWithProviders(<ModuleActionsPage />)

    const overview = await screen.findByTestId('config-overview')
    await waitFor(() => expect(within(overview).getByText('收料单')).toBeInTheDocument())
    fireEvent.click(within(overview).getByText('收料单'))
    expect(await screen.findByTestId('actions-panel')).toBeInTheDocument()
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0])))
      .toContain('/admin/module-business-config/1607/target')
  })

  it('选中模块后取目标模块形状，并渲染带计数的两个页签', async () => {
    await pickModule()

    expect(await screen.findByText('可改')).toBeInTheDocument()
    // 「收料单」左栏与右栏头部各一处（这是正常的：左栏是清单、右栏是选中对象）
    expect(screen.getAllByText('收料单').length).toBeGreaterThanOrEqual(2)
    // 页头只有一条：模块身份 + 主表 / 明细 + 条数（面板不再重复一条身份行）
    const header = document.querySelector('.erp-config-detail-head')
    expect(header).toHaveTextContent('主表 收料单主表')
    expect(header).toHaveTextContent('明细 收料单明细')
    expect(header).toHaveTextContent('1 条动作 / 1 条规则')
    expect(apiClientMock.get.mock.calls.map((call) => String(call[0])))
      .toContain('/admin/module-business-config/1607/target')
    // 页签带条数：切页签前就知道每一面有多少条
    expect(screen.getByRole('tab', { name: /行为动作（1）/ })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: /校验规则（1）/ })).toBeInTheDocument()
    expect(screen.getByTestId('actions-panel')).toHaveAttribute('data-view', 'actions')

    fireEvent.click(screen.getByRole('tab', { name: /校验规则/ }))
    expect(screen.getByTestId('actions-panel')).toHaveAttribute('data-view', 'rules')
    expect(screen.getByRole('tab', { name: /校验规则/ })).toHaveAttribute('aria-selected', 'true')
  })

  it('只读账号不能保存（不是点完才被服务端拒）', async () => {
    apiClientMock.get.mockImplementation(async (path: string) => {
      if (path === '/admin/module-business-config/overview') return { ...overviewView, canWrite: false }
      if (path === '/admin/module-business-config/1607/target') return targetView
      return {}
    })

    await pickModule()
    expect(await screen.findByText('只读')).toBeInTheDocument()
    // 面板桩会上报 dirty=true：保存仍必须是禁用的，否则只读账号点下去才吃 403
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
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
