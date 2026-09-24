import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiClientMock } from '../../test/apiMock'
import { renderWithProviders } from '../../test/renderWithProviders'
import {
  BusinessActionsPanel,
  type BusinessActionsView,
  type ModuleBusinessConfigDraft,
} from './BusinessActionsPanel'
import type { MenuAdminModule } from './MenuAdminPage'

vi.mock('../../services/api', async () => ({ apiClient: (await import('../../test/apiMock')).apiClientMock }))

const moduleWithTables = (id: number, desc: string): MenuAdminModule => ({
  M_IDX: id,
  M_ALIAS: null,
  M_DESC: desc,
  M_URL: null,
  NEW_URL: null,
  MODI_URL: null,
  HELP_URL: null,
  DETAIL_NO_FIELDS: null,
  DETAIL_NO_SAVE: false,
  SEARCH_1: false,
  SEARCH_2: false,
  M_P_IDX: null,
  SORT_IDX: 0,
  M_TAG: true,
  AUTO_APPROVE: false,
  IF_COPY: false,
  ERROR_NO_SAVE: false,
  SORT_FIELDS: null,
  MASTER_TABLE: 'PUR_RECEIVE_M',
  FILTER: null,
  DETAIL_TABLE: 'PUR_RECEIVE_D',
  NOT_BACK_FIELDS_M: null,
  NOT_BACK_FIELDS: null,
  GROUP1: false, GROUP_EXP1: null, GROUP_DESC1: null,
  GROUP2: false, GROUP_EXP2: null, GROUP_DESC2: null,
  GROUP3: false, GROUP_EXP3: null, GROUP_DESC3: null,
  GROUP4: false, GROUP_EXP4: null, GROUP_DESC4: null,
  GROUP5: false, GROUP_EXP5: null, GROUP_DESC5: null,
  FORM_TABS: null,
  FORM_COLUMNS: null,
  FORM_BUTTONS: null,
  LAST_UPDATE_BY: null,
  LAST_UPDATE_DATE: null,
  M_ICON: null,
  Icon: null,
  EFFECT_ENGINE_TAG: false,
})

const catalog = {
  events: ['SAVE', 'APPROVE_EFFECT', 'DEAPPROVE', 'ENDCASE', 'UNENDCASE'],
  failModes: ['BLOCK', 'WARN'],
  effectKeys: ['field-accumulate', 'inventory-move'],
  opCodes: ['ACCUM', 'ASSIGN'],
  sourceScopes: ['MASTER', 'DETAIL', 'TABLE', 'CONSTANT'],
  sourceAggregates: ['SUM', 'MAX', 'MIN', 'DISTINCT'],
  validationStages: ['SAVE', 'APPROVE', 'DEAPPROVE'],
  validationKeys: ['qty-not-exceed', 'reference-exists', 'duplicate-check'],
  labels: {
    events: {
      SAVE: '保存后',
      APPROVE_EFFECT: '批核生效',
      DEAPPROVE: '解批',
      ENDCASE: '结案（占位）',
      UNENDCASE: '取消结案（占位）',
    },
    failModes: { BLOCK: '失败整链回滚', WARN: '警告后继续' },
    effectKeys: { 'field-accumulate': '量额/日期累加回写', 'inventory-move': '库存移动' },
    opCodes: { ACCUM: '累加', ASSIGN: '覆盖' },
    sourceScopes: { MASTER: '本单主表', DETAIL: '本单明细', TABLE: '已登记上下文表', CONSTANT: '常量' },
    sourceAggregates: { SUM: '合计' },
    validationStages: { SAVE: '保存前', APPROVE: '批核前', DEAPPROVE: '解批前' },
    validationKeys: { 'qty-not-exceed': '不超量' },
  },
}

const config = {
  moduleId: 1607,
  actions: [
    {
      seq: 1,
      eventCode: 'APPROVE_EFFECT',
      effectKey: 'field-accumulate',
      effectName: '收料量回写采购单',
      enabled: true,
      failMode: 'BLOCK',
      reverse: JSON.stringify({ kind: 'auto-reverse', note: '日期不回退' }),
      ops: [
        {
          opSeq: 1,
          targetTable: 'PUR_PURCHASE_D',
          targetField: 'RECEIVE_QTY',
          opCode: 'ACCUM',
          sourceScope: 'DETAIL',
          sourceField: 'QTY',
          sourceAgg: 'SUM',
          match: JSON.stringify([{ target: 'PURCHASE_NO', source: { scope: 'DETAIL', field: 'PURCHASE_NO' } }]),
        },
        {
          opSeq: 2,
          targetTable: 'PUR_PURCHASE_D',
          targetField: 'RECEIVE_SPARE_QTY',
          opCode: 'ACCUM',
          sourceScope: 'DETAIL',
          sourceField: 'SPARE_QTY',
          sourceAgg: 'SUM',
        },
      ],
    },
  ],
  validationRules: [],
}

const fieldLabels = {
  tables: { PUR_PURCHASE_D: '采购单明细', PUR_RECEIVE_D: '收料明细' },
  fields: {
    'PUR_PURCHASE_D.RECEIVE_QTY': '已收数量',
    'PUR_RECEIVE_D.QTY': '收料数量',
  },
}

describe('BusinessActionsPanel', () => {
  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return catalog
      if (url.endsWith('/module-business-config/1607')) return config
      if (url.endsWith('/module-business-config/1607/field-labels')) return fieldLabels
      if (url.endsWith('/relations')) return []
      if (url.endsWith('/settings/system')) return { groups: [] }
      if (url.endsWith('/module-business-config/schemas')) {
        return {
          effects: catalog.effectKeys.map((effectKey) => ({ effectKey, rootKeys: ['direction', 'fieldMap', 'mrp'] })),
          reverseKinds: ['auto-reverse', 'no-reverse', 'recompute', 'reverse-flow', 'snapshot'],
          reverseKindLabels: { 'auto-reverse': '按公式行自动反向', 'no-reverse': '解批不反向' },
          validationParams: [
            { validationKey: 'qty-not-exceed', rootKeys: ['when', 'mode', 'checks'] },
            { validationKey: 'reference-exists', rootKeys: ['when', 'checks'] },
          ],
        }
      }
      return {}
    })
    apiClientMock.put.mockResolvedValue(undefined)
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('标题按主/副表显示「描述(表名)」', async () => {
    renderWithProviders(
      <BusinessActionsPanel
        module={{ ...moduleWithTables(1607, '收料单'), MASTER_TABLE_DESC: '收料主表', DETAIL_TABLE_DESC: '收料明细' }}
      />,
    )

    expect(await screen.findByText('收料主表(PUR_RECEIVE_M) / 收料明细(PUR_RECEIVE_D)')).toBeInTheDocument()
  })

  it('加载并展示业务动作与加工单', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)

    expect(await screen.findByText('业务动作（1）')).toBeInTheDocument()
    expect(screen.getByText('批核生效（APPROVE_EFFECT）')).toBeInTheDocument()
    fireEvent.click(screen.getByText('收料量回写采购单'))
    expect(await screen.findByText('加工单（2 步）')).toBeInTheDocument()
    expect(screen.getAllByText(/已收数量\(RECEIVE_QTY\)/).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/收料数量\(QTY\)/).length).toBeGreaterThan(0)
  })

  it('列表以中文目录标签与加工单句式呈现（不暴露裸英文码/裸 JSON）', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    expect(screen.getByText('量额/日期累加回写（field-accumulate）')).toBeInTheDocument()
    expect(screen.getByText('失败整链回滚（BLOCK）')).toBeInTheDocument()
    // 动作列表的「影响」列已是人话摘要（未选动作时唯一一处出现该句式）。
    expect(screen.getByText(/采购单明细\(PUR_PURCHASE_D\)\.已收数量\(RECEIVE_QTY\) \+= 本单明细\.收料数量\(QTY\)（合计）/))
      .toBeInTheDocument()

    fireEvent.click(screen.getByText('收料量回写采购单'))
    expect(await screen.findByText('加工单（2 步）')).toBeInTheDocument()
    expect(screen.getByText(/@PURCHASE_NO ← 本单明细\.PURCHASE_NO/)).toBeInTheDocument()
    expect(screen.getByText(/解批：按公式行自动反向（日期不回退）/)).toBeInTheDocument()
  })

  it('字段表视图与加工单视图可切换，字段级列在表里可见', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')
    fireEvent.click(screen.getByText('收料量回写采购单'))
    await screen.findByText('加工单（2 步）')

    fireEvent.click(screen.getByRole('button', { name: '字段表' }))
    expect(await screen.findByText('定位键')).toBeInTheDocument()
    expect(screen.getByText('条件')).toBeInTheDocument()
    expect(screen.getByText('本单明细.收料数量(QTY)（合计）')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '加工单' }))
    expect(await screen.findByText('加工单（2 步）')).toBeInTheDocument()
  })

  it('步骤编辑走选区工具栏：未选中步骤时编辑/删除禁用', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('收料量回写采购单'))
    await screen.findByText('加工单（2 步）')

    const editButtons = screen.getAllByRole('button', { name: '编辑' })
    expect(editButtons.filter((button) => !(button as HTMLButtonElement).disabled)).toHaveLength(1)
    // 点加工单里的步骤行即选中该步骤，编辑按钮随之可用。
    const steps = screen.getAllByRole('button').filter((element) => element.tagName === 'LI')
    expect(steps).toHaveLength(2)
    fireEvent.click(steps[1])
    await waitFor(() => {
      const buttons = screen.getAllByRole('button', { name: '编辑' })
      expect(buttons.filter((button) => !(button as HTMLButtonElement).disabled)).toHaveLength(2)
    })
  })

  it('复制步骤与复制动作各自顺延顺序号', async () => {
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('业务动作（1）')
    fireEvent.click(screen.getByText('收料量回写采购单'))
    await screen.findByText('加工单（2 步）')

    // 先选步骤再复制：复制步骤顺延 opSeq 且不改动作顺序号。
    const steps = screen.getAllByRole('button').filter((element) => element.tagName === 'LI')
    fireEvent.click(steps[0])
    fireEvent.click(screen.getByRole('button', { name: '复制步骤' }))
    await waitFor(() => {
      const actions = onDraftChange.mock.calls.at(-1)![0].actions
      expect(actions).toHaveLength(1)
      expect(actions[0].ops).toHaveLength(3)
      expect(actions[0].ops.map((op: { opSeq: number }) => op.opSeq).sort()).toEqual([1, 2, 3])
    })

    // 复制动作：同事件内顺延 seq，并把选中切到副本。
    fireEvent.click(screen.getByRole('button', { name: '复制' }))
    await waitFor(() => {
      const actions = onDraftChange.mock.calls.at(-1)![0].actions
      expect(actions).toHaveLength(2)
      expect(actions.map((action: { seq: number }) => action.seq).sort()).toEqual([1, 2])
      expect(actions.find((action: { seq: number }) => action.seq === 2).ops).toHaveLength(3)
    })
  })

  it('效果参数按 Schema 递归结构化渲染（嵌套对象与数组不再手写 JSON）', async () => {
    const paramsConfig = {
      moduleId: 1607,
      actions: [
        {
          seq: 1,
          eventCode: 'APPROVE_EFFECT',
          effectKey: 'inventory-move',
          effectName: '送货出库（服务级）',
          enabled: true,
          failMode: 'BLOCK',
          params: JSON.stringify({
            direction: 'OUT',
            mrp: false,
            fieldMap: { masterDate: 'SEND_DATE', qty: { terms: [{ field: 'QTY', coef: 1 }] } },
          }),
          ops: [],
        },
      ],
      validationRules: [],
    }
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return catalog
      if (url.endsWith('/module-business-config/1607')) return paramsConfig
      if (url.endsWith('/module-business-config/1607/field-labels')) return fieldLabels
      if (url.endsWith('/module-business-config/schemas')) {
        return {
          effects: [{ effectKey: 'inventory-move', rootKeys: ['direction', 'mrp', 'fieldMap'] }],
          reverseKinds: ['reverse-flow'],
          reverseKindLabels: { 'reverse-flow': '写反向流水（库存类）' },
        }
      }
      return {}
    })
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('送货出库（服务级）'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])

    const dialog = await screen.findByRole('dialog')
    // 根键与嵌套键都渲染成具名控件；数组项可增删。
    expect(within(dialog).getByDisplayValue('OUT')).toBeInTheDocument()
    expect(within(dialog).getByText('fieldMap')).toBeInTheDocument()
    expect(within(dialog).getByText('masterDate')).toBeInTheDocument()
    expect(within(dialog).getByText('terms')).toBeInTheDocument()
    expect(within(dialog).getByDisplayValue('SEND_DATE')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '添加一项' })).toBeInTheDocument()
    // 嵌套对象不再以整段 JSON 文本框呈现。
    expect(within(dialog).queryByDisplayValue(/"masterDate"/)).not.toBeInTheDocument()

    fireEvent.change(within(dialog).getByDisplayValue('SEND_DATE'), { target: { value: 'FACT_SEND_DATE' } })
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))

    await waitFor(() => {
      const params = JSON.parse(onDraftChange.mock.calls.at(-1)![0].actions[0].params)
      expect(params.fieldMap.masterDate).toBe('FACT_SEND_DATE')
      expect(params.fieldMap.qty.terms[0].field).toBe('QTY')
      expect(params.direction).toBe('OUT')
    })
  })

  it('影响面自检：汇总写入面并在有硬伤时标出会拦住保存', async () => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return catalog
      if (url.endsWith('/module-business-config/1607')) {
        return {
          moduleId: 1607,
          actions: [
            {
              seq: 1,
              eventCode: 'APPROVE_EFFECT',
              effectKey: 'field-accumulate',
              effectName: '收料量回写采购单',
              enabled: true,
              failMode: 'BLOCK',
              // 跨表写入却没有定位键：与服务端保存期校验同一口径，应被判为阻断项。
              ops: [{
                opSeq: 1,
                targetTable: 'PUR_PURCHASE_D',
                targetField: 'RECEIVE_QTY',
                opCode: 'ACCUM',
                sourceScope: 'DETAIL',
                sourceField: 'QTY',
                sourceAgg: 'SUM',
              }],
            },
          ],
          validationRules: [],
        }
      }
      if (url.endsWith('/module-business-config/1607/field-labels')) return fieldLabels
      if (url.endsWith('/module-business-config/1607/relations')) return []
      if (url.endsWith('/settings/system')) return { groups: [] }
      if (url.endsWith('/module-business-config/schemas')) {
        return { effects: [], reverseKinds: ['auto-reverse'], reverseKindLabels: {}, validationParams: [] }
      }
      return {}
    })

    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    expect(await screen.findByText('影响面与保存前自检')).toBeInTheDocument()
    // 未展开时不给通过/失败结论（定位键与系统开关的登记校验还没做），本地能判的阻断项照旧列出。
    expect(screen.getByText('展开后自检')).toBeInTheDocument()
    expect(screen.getByText(/既无定位键也无条件/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '展开明细' }))
    expect(await screen.findByText('1 项会拦住保存')).toBeInTheDocument()
    expect((await screen.findAllByText(/采购单明细\(PUR_PURCHASE_D\)/)).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/已收数量\(RECEIVE_QTY\)/).length).toBeGreaterThan(0)
  })

  it('影响面自检未展开时不查关系边与系统开关，展开后才查', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    const paths = () => apiClientMock.get.mock.calls.map((call) => String(call[0]))
    expect(paths().filter((path) => path.endsWith('/relations'))).toHaveLength(0)
    expect(paths().filter((path) => path.endsWith('/settings/system'))).toHaveLength(0)

    fireEvent.click(screen.getByRole('button', { name: '展开明细' }))
    await waitFor(() => {
      expect(paths().filter((path) => path.endsWith('/relations')).length).toBeGreaterThan(0)
      expect(paths().filter((path) => path.endsWith('/settings/system')).length).toBeGreaterThan(0)
    })
  })

  it('校验规则参数按模板 Schema 结构化渲染（数组可增删项）', async () => {
    const ruleConfig = {
      moduleId: 1607,
      actions: [],
      validationRules: [
        {
          seq: 1,
          stage: 'SAVE',
          validationKey: 'qty-not-exceed',
          enabled: true,
          params: JSON.stringify({
            mode: 'detail',
            checks: [{ targetTable: 'PUR_PURCHASE_D', limit: { scope: 'TARGET', field: 'QTY' } }],
          }),
        },
      ],
    }
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return catalog
      if (url.endsWith('/module-business-config/1607')) return ruleConfig
      if (url.endsWith('/module-business-config/1607/field-labels')) return fieldLabels
      if (url.endsWith('/module-business-config/1607/relations')) return []
      if (url.endsWith('/settings/system')) return { groups: [] }
      if (url.endsWith('/module-business-config/schemas')) {
        return {
          effects: [],
          reverseKinds: ['auto-reverse'],
          reverseKindLabels: {},
          validationParams: [{ validationKey: 'qty-not-exceed', rootKeys: ['when', 'mode', 'checks'] }],
        }
      }
      return {}
    })
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('校验规则（1）')

    fireEvent.click(screen.getByText('不超量（qty-not-exceed）'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' }).filter((button) => !(button as HTMLButtonElement).disabled)[0])

    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('mode')).toBeInTheDocument()
    expect(within(dialog).getByText('checks')).toBeInTheDocument()
    expect(within(dialog).getByDisplayValue('detail')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: '添加一项' })).toBeInTheDocument()
    // 结构化的嵌套项不再是整段 JSON 文本。
    expect(within(dialog).queryByDisplayValue(/"targetTable"/)).not.toBeInTheDocument()

    fireEvent.click(within(dialog).getByRole('button', { name: '添加一项' }))
    fireEvent.click(within(dialog).getByRole('button', { name: '保存' }))
    await waitFor(() => {
      const params = JSON.parse(onDraftChange.mock.calls.at(-1)![0].validationRules[0].params)
      expect(params.checks).toHaveLength(2)
      expect(params.mode).toBe('detail')
    })
  })

  it('新增动作打开编辑弹窗并可取消', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getAllByRole('button', { name: /新增/ })[0])
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('新增业务动作')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('不提供保存/发布按钮（保存与发布已统一到模块工具栏）', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    expect(screen.queryByRole('button', { name: /保存配置/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /发布配置/ })).not.toBeInTheDocument()
  })

  it('装载完成后把草稿上报给主页面（带模块编号与未改动标记）', async () => {
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('业务动作（1）')

    await waitFor(() => expect(onDraftChange).toHaveBeenCalledWith(expect.objectContaining({
      moduleId: 1607,
      dirty: false,
      validationRules: [],
    })))
    expect(onDraftChange.mock.calls.at(-1)![0].actions).toHaveLength(1)
  })

  it('编辑动作后上报的草稿标记为已改动', async () => {
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByRole('button', { name: '新增' }))
    await screen.findByRole('dialog')
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(onDraftChange.mock.calls.at(-1)![0].dirty).toBe(true))
    expect(onDraftChange.mock.calls.at(-1)![0].actions).toHaveLength(2)
  })

  it('容器常驻：视图切走再切回，未保存编辑不丢、脏标记不回退', async () => {
    const onDraftChange = vi.fn()
    renderWithProviders(<ViewSwitchHarness onDraftChange={onDraftChange} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('收料量回写采购单'))
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    const dialog = within(await screen.findByRole('dialog'))
    fireEvent.change(dialog.getByDisplayValue('收料量回写采购单'), { target: { value: '改过的名称' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))
    await waitFor(() => expect(onDraftChange.mock.calls.at(-1)![0].dirty).toBe(true))

    // 离开行为页签：容器仍在，只是不渲染内容。
    fireEvent.click(screen.getByRole('button', { name: '切换视图' }))
    expect(screen.queryByText('业务动作（1）')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '切换视图' }))

    // 动作列表名称列与影响面自检文案都会出现该名称，故只断言"仍在"。
    await waitFor(() => expect(screen.getAllByText('改过的名称').length).toBeGreaterThan(0))
    expect(onDraftChange.mock.calls.at(-1)![0].actions[0].effectName).toBe('改过的名称')
    expect(onDraftChange.mock.calls.at(-1)![0].dirty).toBe(true)
  })
})

/** 页签归属在页面侧：这里用一个开关模拟"在行为页签"与"不在行为页签"。 */
function ViewSwitchHarness({ onDraftChange }: { onDraftChange?: (draft: ModuleBusinessConfigDraft | null) => void }) {
  const [view, setView] = useState<BusinessActionsView | null>('actions')
  return (
    <div>
      <button type="button" onClick={() => setView((current) => (current == null ? 'actions' : null))}>
        切换视图
      </button>
      <BusinessActionsPanel
        module={moduleWithTables(1607, '收料单')}
        view={view}
        onDraftChange={onDraftChange}
      />
    </div>
  )
}

/**
 * 自定义按钮（EVENT_CODE='MANUAL'）：这类行的键来自操作注册表而非效果目录，
 * 编辑时字段另一套（按钮标题/二次确认/入参声明），且不参与加工单；
 * 配置面还要显示"当前授权：N 用户 / M 组"，因为配了没人能用是它的正常状态。
 */
describe('BusinessActionsPanel 自定义按钮行', () => {
  const manualCatalog = {
    ...catalog,
    events: [...catalog.events, 'MANUAL'],
    labels: { ...catalog.labels, events: { ...catalog.labels.events, MANUAL: '用户点击（自定义按钮）' } },
    documentActions: [
      { key: 'recalc-account', label: '重算账面数', placement: 'detail' },
      { key: 'relocate-stock', label: '归位到库位', placement: 'master' },
    ],
  }
  const manualConfig = {
    moduleId: 1607,
    actions: [
      {
        seq: 1,
        eventCode: 'MANUAL',
        effectKey: 'recalc-account',
        label: '重算账面数量',
        confirmTag: true,
        enabled: true,
        failMode: 'BLOCK',
        params: JSON.stringify({ fields: [{ key: 'relocateTo', label: '目标库位', type: 'string', required: true }] }),
        ops: [],
      },
    ],
    validationRules: [],
  }
  const authorization = {
    buttons: [{ seq: 1, key: 'recalc-account', label: '重算账面数量', users: 0, groups: 0 }],
  }
  const emptyConfig = { moduleId: 1607, actions: [], validationRules: [] }
  /** 来源模块：一条效果 + 一条自定义按钮。 */
  const sourceConfig = {
    moduleId: 1505,
    actions: [
      {
        seq: 1,
        eventCode: 'APPROVE_EFFECT',
        effectKey: 'field-accumulate',
        effectName: '制令已入库量累加',
        enabled: true,
        failMode: 'BLOCK',
        ops: [],
      },
      {
        seq: 1,
        eventCode: 'MANUAL',
        effectKey: 'recalc-account',
        label: '重算账面数量',
        enabled: true,
        failMode: 'BLOCK',
        ops: [],
      },
    ],
    validationRules: [],
  }
  const moduleChooserData = {
    columns: [
      { key: 'M_IDX', label: '模块号', dataType: 'int', format: null },
      { key: 'M_DESC', label: '模块名', dataType: 'nvarchar', format: null },
      { key: 'MASTER_TABLE', label: '操作主表', dataType: 'nvarchar', format: null },
      { key: 'DETAIL_TABLE', label: '操作副表', dataType: 'nvarchar', format: null },
      { key: 'ACTION_COUNT', label: '动作数', dataType: 'int', format: null },
    ],
    rows: [{ M_IDX: 1505, M_DESC: '生产入库单', MASTER_TABLE: 'MOC_PRODUCT_M', DETAIL_TABLE: 'MOC_PRODUCT_D', ACTION_COUNT: 2 }],
    total: 1,
  }

  beforeEach(() => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return manualCatalog
      if (url.endsWith('/module-business-config/1607')) return manualConfig
      if (url.endsWith('/module-business-config/1607/field-labels')) return fieldLabels
      if (url.endsWith('/module-business-config/1607/action-authorization')) return authorization
      if (url.endsWith('/module-business-config/schemas')) {
        return { effects: [], reverseKinds: ['no-reverse'], reverseKindLabels: {}, validationParams: [] }
      }
      return {}
    })
  })

  it('克隆入口不带入自定义按钮行：追加后本模块按钮行数不变', async () => {
    apiClientMock.get.mockImplementation(async (url: string) => {
      if (url.endsWith('/module-business-config/meta')) return manualCatalog
      if (url.endsWith('/module-business-config/1607')) return emptyConfig
      if (url.endsWith('/module-business-config/1505')) return sourceConfig
      if (url.endsWith('/module-business-config/1607/field-labels')) return fieldLabels
      if (url.endsWith('/module-business-config/schemas')) {
        return { effects: [], reverseKinds: ['no-reverse'], reverseKindLabels: {}, validationParams: [] }
      }
      return {}
    })
    apiClientMock.post.mockImplementation(async (path: string) => {
      if (path === '/chooser/query') return moduleChooserData
      throw new Error(`unexpected POST ${path}`)
    })
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('业务动作（0）')

    fireEvent.click(screen.getByRole('button', { name: '从其它模块复制' }))
    fireEvent.click(screen.getByRole('button', { name: '选择模块' }))
    fireEvent.click(await screen.findByText('生产入库单'))
    fireEvent.click(screen.getByRole('button', { name: '确认' }))
    // 来源模块的按钮行不出现在克隆列表里，只有效果动作可勾选。
    await screen.findByText('制令已入库量累加')
    expect(screen.queryByText('recalc-account')).not.toBeInTheDocument()
    fireEvent.click(screen.getAllByLabelText('选择该动作')[0])
    fireEvent.click(screen.getByRole('button', { name: '追加到本模块' }))

    await waitFor(() => {
      const actions = onDraftChange.mock.calls.at(-1)![0].actions
      expect(actions).toHaveLength(1)
      expect(actions.filter((action: { eventCode: string }) => action.eventCode === 'MANUAL')).toHaveLength(0)
      expect(actions[0].effectName).toBe('制令已入库量累加')
    })
  })

  it('按按钮键渲染名称，并显示授权镜子（0 用户 / 0 组要显式说出来）', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)

    await screen.findByText('业务动作（1）')
    expect(await screen.findByText('重算账面数量')).toBeInTheDocument()
    expect(screen.getByText(/尚无任何授权，发布后无人可点/)).toBeInTheDocument()
  })

  it('选中按钮行时显示自定义按钮说明，而不是加工单', async () => {
    renderWithProviders(<BusinessActionsPanel module={moduleWithTables(1607, '收料单')} />)
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('重算账面数量'))

    expect(await screen.findByText('自定义按钮')).toBeInTheDocument()
    expect(screen.queryByText(/^加工单（/)).not.toBeInTheDocument()
  })

  it('编辑按钮行：有标题与二次确认与入参声明，没有反向语义', async () => {
    const onDraftChange = vi.fn()
    renderWithProviders(
      <BusinessActionsPanel module={moduleWithTables(1607, '收料单')} onDraftChange={onDraftChange} />,
    )
    await screen.findByText('业务动作（1）')

    fireEvent.click(screen.getByText('重算账面数量'))
    // 动作区与校验规则区各有一个「编辑」，动作区在上。
    fireEvent.click(screen.getAllByRole('button', { name: '编辑' })[0])
    const dialog = within(await screen.findByRole('dialog'))

    expect(dialog.getByText('按钮标题（显示在单据上）')).toBeInTheDocument()
    expect(dialog.getByLabelText('先返回"将会发生什么"')).toBeChecked()
    expect(dialog.getByText('点击时让用户填的参数')).toBeInTheDocument()
    expect(dialog.getByLabelText('参数 1 键')).toHaveValue('relocateTo')
    expect(dialog.queryByText('反向（解批语义）')).not.toBeInTheDocument()

    fireEvent.change(dialog.getByLabelText('参数 1 标签'), { target: { value: '目标库位（改）' } })
    fireEvent.click(dialog.getByRole('button', { name: '保存' }))

    await waitFor(() => expect(onDraftChange.mock.calls.at(-1)![0].dirty).toBe(true))
    const saved = onDraftChange.mock.calls.at(-1)![0].actions[0]
    expect(saved.label).toBe('重算账面数量')
    expect(saved.confirmTag).toBe(true)
    expect(JSON.parse(saved.params).fields[0].label).toBe('目标库位（改）')
  })
})
