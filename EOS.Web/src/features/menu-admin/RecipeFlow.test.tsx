import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import { BusinessActionsPanel, type ModuleBusinessConfigDraft } from './BusinessActionsPanel'
import type { MenuAdminModule } from './MenuAdminPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

/** 只喂面板真正会读的字段：夹具只服务本文件的断言，其余字段用断言收窄。 */
const moduleWithTables = (id: number, desc: string) =>
  ({
    M_IDX: id,
    M_DESC: desc,
    MASTER_TABLE: 'PUR_RECEIVE_M',
    DETAIL_TABLE: 'PUR_RECEIVE_D',
  }) as unknown as MenuAdminModule

const catalog = {
  events: ['SAVE', 'APPROVE_EFFECT', 'DEAPPROVE', 'ENDCASE', 'UNENDCASE'],
  failModes: ['BLOCK', 'WARN'],
  effectKeys: ['field-accumulate', 'inventory-move'],
  opCodes: ['ACCUM', 'ASSIGN'],
  sourceScopes: ['MASTER', 'DETAIL', 'TABLE', 'CONSTANT'],
  sourceAggregates: ['SUM'],
  validationStages: ['SAVE', 'APPROVE'],
  validationKeys: ['qty-not-exceed'],
  labels: {
    events: { SAVE: '保存后', APPROVE_EFFECT: '批核生效', DEAPPROVE: '解批', ENDCASE: '结案（占位）', UNENDCASE: '取消结案（占位）' },
    failModes: { BLOCK: '失败整链回滚', WARN: '警告后继续' },
    effectKeys: { 'field-accumulate': '量额/日期累加回写', 'inventory-move': '库存移动' },
    opCodes: { ACCUM: '累加', ASSIGN: '覆盖' },
    sourceScopes: { MASTER: '本单主表', DETAIL: '本单明细', TABLE: '已登记上下文表', CONSTANT: '常量' },
    sourceAggregates: { SUM: '合计' },
    validationStages: { SAVE: '保存前', APPROVE: '批核前' },
    validationKeys: { 'qty-not-exceed': '不超量' },
  },
  documentActions: [],
  // 代码侧事实：结案/取消结案接不到效果链；库内事实：ENDCASE 有 1 行、UNENDCASE 0 行。
  inertEvents: ['ENDCASE', 'UNENDCASE'],
  eventUsage: { SAVE: 47, APPROVE_EFFECT: 276, ENDCASE: 1 },
}

const schemas = {
  effects: [],
  reverseKinds: ['auto-reverse', 'reverse-flow', 'none'],
  reverseKindLabels: { 'auto-reverse': '按公式行自动反向', 'reverse-flow': '写反向流水（库存类）' },
  validationParams: [],
  paramFields: [],
  reverseKindsByEffect: {},
  recipes: [
    {
      key: 'write-upstream-qty',
      name: '回写上游单数量',
      summary: '把数量/金额按定位键累加回写上游单据。',
      eventCodes: ['APPROVE_EFFECT'],
      effectKeys: ['field-accumulate'],
      formulaMode: true,
      requiresRelation: true,
      reversePreset: 'auto-reverse',
      paramsHint: '参数区留空：语义全在公式行。',
      note: '存量 127 行全部不带参数。',
    },
  ],
}

const emptyConfig = { moduleId: 1607, actions: [], validationRules: [] }
const oneActionConfig = {
  moduleId: 1607,
  actions: [
    {
      seq: 1,
      eventCode: 'APPROVE_EFFECT',
      effectKey: 'field-accumulate',
      effectName: '收料量回写采购单',
      enabled: true,
      failMode: 'BLOCK',
      reverse: null,
      ops: [],
    },
  ],
  validationRules: [],
}

function stub(config: unknown) {
  apiClientMock.get.mockImplementation(async (url: string) => {
    if (url.endsWith('/module-business-config/meta')) return catalog
    if (url.endsWith('/module-business-config/schemas')) return schemas
    if (url.endsWith('/module-business-config/1607/field-labels')) return { tables: {}, fields: {} }
    if (url.endsWith('/module-business-config/1607')) return config
    return {}
  })
}

describe('配方视图与专家模式的切换（阶段二）', () => {
  beforeEach(() => {
    apiClientMock.get.mockReset()
    apiClientMock.post.mockReset()
    apiClientMock.post.mockImplementation(async () => ({}))
  })

  afterEach(() => vi.clearAllMocks())

  it('空模块默认给配方视图：三选一起步，模板库明确标注未建设', async () => {
    stub(emptyConfig)
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} view="actions" />)

    expect(await screen.findByText(/从配方开始（下面选一个）/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /从其它模块克隆/ })).toBeEnabled()
    const templateOption = screen.getByRole('button', { name: /从模板库选/ })
    expect(templateOption).toBeDisabled()
    expect(templateOption).toHaveAttribute('title', expect.stringContaining('模板库尚未建设'))
    // 配方卡片把"落到哪些键"讲清楚（可追溯，不是凭空发明）。
    expect(screen.getByText('回写上游单数量')).toBeInTheDocument()
    expect(screen.getByText(/量额\/日期累加回写/)).toBeInTheDocument()
  })

  it('用配方新增：落进草稿的那条与配方预填逐字段一致（事件/效果键/反向，参数留空）', async () => {
    stub(emptyConfig)
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} view="actions" onDraftChange={onDraftChange} />,
    )

    fireEvent.click(await screen.findByRole('button', { name: '用它新增' }))
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      const draft = onDraftChange.mock.calls.at(-1)?.[0] as ModuleBusinessConfigDraft | null
      expect(draft?.actions).toHaveLength(1)
      const created = draft!.actions[0]
      expect(created.seq).toBe(1)
      expect(created.eventCode).toBe('APPROVE_EFFECT')
      expect(created.effectKey).toBe('field-accumulate')
      expect(created.reverse).toBe('{"kind":"auto-reverse"}')
      expect(created.params).toBeNull()
      expect(created.ops).toEqual([])
    })
  })

  it('配方 ↔ 专家切换不丢改动，也不重新取配置（同一份草稿、同一个组件）', async () => {
    stub(oneActionConfig)
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} view="actions" />)

    // 已有配置的模块默认为专家：既有配置不该因为"默认换了个视图"而先被藏起来。
    expect(await screen.findByText('业务动作（1）')).toBeInTheDocument()
    const configLoads = () =>
      apiClientMock.get.mock.calls.filter(([url]) => String(url).endsWith('/module-business-config/1607')).length
    const before = configLoads()

    fireEvent.click(screen.getByRole('button', { name: '配方' }))
    expect(await screen.findByText('回写上游单数量')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '专家（键级）' }))
    // 切换回专家：草稿还是那一份（行数不变），且没有触发第二次装载。
    expect(await screen.findByText('业务动作（1）')).toBeInTheDocument()
    expect(screen.getByText('收料量回写采购单')).toBeInTheDocument()
    expect(configLoads()).toBe(before)
  })

  it('占位事件：库内 0 行的不可选并标"暂未启用"，库内已有行的保持可选但如实标注', async () => {
    stub(emptyConfig)
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} view="actions" />)

    fireEvent.click(await screen.findByRole('button', { name: '用它新增' }))
    const dialog = await screen.findByRole('dialog')
    const eventSelect = dialog.querySelector('select')!
    const optionOf = (value: string) =>
      Array.from(eventSelect.querySelectorAll('option')).find((option) => option.value === value)!

    const unendcase = optionOf('UNENDCASE')
    expect(unendcase).toBeDisabled()
    expect(unendcase.textContent).toContain('暂未启用')

    const endcase = optionOf('ENDCASE')
    expect(endcase).not.toBeDisabled()
    expect(endcase.textContent).toContain('该事件当前不会触发效果链')

    const save = optionOf('SAVE')
    expect(save).not.toBeDisabled()
    expect(save.textContent).not.toContain('暂未启用')
  })
})
