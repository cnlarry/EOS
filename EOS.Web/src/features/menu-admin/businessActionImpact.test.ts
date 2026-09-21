import { describe, expect, it } from 'vitest'
import { analyzeImpact, collectSwitchKeys, type ImpactAction, type ImpactMatchEdge } from './businessActionImpact'

const master = 'PUR_RECEIVE_M'
const detail = 'PUR_RECEIVE_D'

const action = (over: Partial<ImpactAction> = {}): ImpactAction => ({
  seq: 1,
  eventCode: 'APPROVE_EFFECT',
  effectKey: 'field-accumulate',
  effectName: '收料量回写采购单',
  enabled: true,
  ops: [],
  ...over,
})

const matchJson = JSON.stringify([
  { target: 'PURCHASE_NO', source: { scope: 'DETAIL', field: 'PURCHASE_NO' } },
])

const edge = (fromTable: string, fromColumn: string, toColumn: string): ImpactMatchEdge => ({
  fromTable,
  fromColumn,
  toTable: 'PUR_PURCHASE_D',
  toColumn,
})

describe('analyzeImpact', () => {
  it('汇总涉及的表列、事件与步骤数', () => {
    const report = analyzeImpact([
      action({
        ops: [
          { opSeq: 1, targetTable: 'PUR_PURCHASE_D', targetField: 'RECEIVE_QTY', opCode: 'ACCUM', match: matchJson },
          { opSeq: 2, targetTable: 'PUR_PURCHASE_D', targetField: 'REAL_DELIVERY_DATE', opCode: 'ASSIGN_MAX', match: matchJson },
          { opSeq: 3, targetTable: 'SUPPLIER', targetField: 'LAST_TRADE_DATE', opCode: 'ASSIGN_MAX', condition: '{"logic":"AND","items":[]}' },
        ],
      }),
      action({ seq: 2, eventCode: 'SAVE', effectKey: 'cop-send-mo-flag', params: '{"typeField":"SEND_TYPE"}', ops: [] }),
    ], [], { masterTable: master, detailTable: detail })

    expect(report.actionCount).toBe(2)
    expect(report.stepCount).toBe(3)
    expect(report.tables.map((item) => item.table)).toEqual(['PUR_PURCHASE_D', 'SUPPLIER'])
    expect(report.tables[0].columns).toEqual(['REAL_DELIVERY_DATE', 'RECEIVE_QTY'])
    expect(report.eventCounts).toEqual([
      { eventCode: 'APPROVE_EFFECT', count: 1 },
      { eventCode: 'SAVE', count: 1 },
    ])
    expect(report.issues).toEqual([])
  })

  it('跨表写入既无定位键也无条件 ⇒ 会拦住保存', () => {
    const report = analyzeImpact([
      action({ ops: [{ opSeq: 1, targetTable: 'PUR_PURCHASE_D', targetField: 'RECEIVE_QTY', opCode: 'ACCUM' }] }),
    ], [], { masterTable: master, detailTable: detail })

    expect(report.issues).toHaveLength(1)
    expect(report.issues[0].severity).toBe('block')
    expect(report.issues[0].message).toContain('既无定位键也无条件')
  })

  it('写本单主表不要求定位键', () => {
    const report = analyzeImpact([
      action({ ops: [{ opSeq: 1, targetTable: master, targetField: 'TOTAL_QTY', opCode: 'ASSIGN' }] }),
    ], [], { masterTable: master, detailTable: detail })

    expect(report.issues).toEqual([])
  })

  it('定位键必须被已登记关系边覆盖；目标表未参与关系目录时跳过', () => {
    const op = { opSeq: 1, targetTable: 'PUR_PURCHASE_D', targetField: 'RECEIVE_QTY', opCode: 'ACCUM' as const, match: matchJson }
    const registered: ImpactMatchEdge[][] = [[edge(detail, 'PURCHASE_NO', 'PURCHASE_NO')]]

    const covered = analyzeImpact([action({ ops: [op] })], [], {
      masterTable: master, detailTable: detail, relationsByTable: { PUR_PURCHASE_D: registered },
    })
    expect(covered.issues).toEqual([])

    const drifted = analyzeImpact([action({ ops: [op] })], [], {
      masterTable: master, detailTable: detail, relationsByTable: { PUR_PURCHASE_D: [[edge(detail, 'OTHER_NO', 'PURCHASE_NO')]] },
    })
    expect(drifted.issues).toHaveLength(1)
    expect(drifted.issues[0].message).toContain('定位键未登记效果关系边')

    const notParticipating = analyzeImpact([action({ ops: [op] })], [], {
      masterTable: master, detailTable: detail, relationsByTable: { PUR_PURCHASE_D: [] },
    })
    expect(notParticipating.issues).toEqual([])
  })

  it('系统开关键必须在已登记参数目录内；目录不可读时不误报', () => {
    const withSwitch = action({
      ops: [],
      params: '{"direction":"OUT"}',
      condition: '{"logic":"AND","items":[{"type":"switch","key":"SEND_ORDER_TAG","value":true}]}',
    })

    const unknown = analyzeImpact([withSwitch], [], { knownSwitchKeys: ['PRO_MRP'] })
    expect(unknown.issues.map((issue) => issue.message).join()).toContain("系统开关 'SEND_ORDER_TAG' 不是已登记的系统参数键")

    const known = analyzeImpact([withSwitch], [], { knownSwitchKeys: ['SEND_ORDER_TAG'] })
    expect(known.issues).toEqual([])
    expect(known.switchKeys).toEqual(['SEND_ORDER_TAG'])

    // 目录读不到（无权限/接口异常）时不做这项判定，避免把可用配置误报成阻断。
    expect(analyzeImpact([withSwitch], [], { knownSwitchKeys: null }).issues).toEqual([])
  })

  it('顺序号重复与结构化 JSON 非法都会拦住保存', () => {
    const report = analyzeImpact([
      action({ seq: 1, ops: [], params: '{"a":1}' }),
      action({ seq: 1, effectName: '重复顺序', ops: [], params: '{"a":1}' }),
      action({ seq: 2, condition: '{"logic":"AND"', ops: [], params: '{"a":1}' }),
    ], [{ stage: 'SAVE', seq: 1, validationKey: 'qty-not-exceed', enabled: true, params: '{bad' }], {})

    const blocks = report.issues.filter((issue) => issue.severity === 'block').map((issue) => issue.message)
    expect(blocks.some((message) => message.includes('顺序号 1 重复'))).toBe(true)
    expect(blocks.some((message) => message.includes('条件不是合法 JSON'))).toBe(true)
    expect(blocks.some((message) => message.includes('校验规则 qty-not-exceed'))).toBe(true)
  })

  it('顺序依赖：完成判定/库存移动必须排在量额回写之后（链内确有该前置效果时）', () => {
    const accumulate = (seq: number): ImpactAction => action({
      seq,
      effectName: '量额回写',
      ops: [{ opSeq: 1, targetTable: 'PUR_PURCHASE_D', targetField: 'RECEIVE_QTY', opCode: 'ACCUM', match: matchJson }],
    })
    const close = (seq: number): ImpactAction => action({
      seq,
      effectKey: 'completion-close',
      effectName: '自动结案',
      ops: [{ opSeq: 1, targetTable: 'PUR_PURCHASE_D', targetField: 'FINISHED_TAG', opCode: 'SET_WHEN', match: matchJson }],
    })

    const wrongOrder = analyzeImpact([close(1), accumulate(2)], [], { masterTable: master, detailTable: detail })
    expect(wrongOrder.issues.some((issue) => issue.message.includes('顺序 lint'))).toBe(true)

    const rightOrder = analyzeImpact([accumulate(1), close(2)], [], { masterTable: master, detailTable: detail })
    expect(rightOrder.issues).toEqual([])

    // 链里没有量额回写时，纯状态链合法，不误报。
    const onlyClose = analyzeImpact([close(1)], [], { masterTable: master, detailTable: detail })
    expect(onlyClose.issues).toEqual([])
  })

  it('动作/规则数量上限与服务端一致', () => {
    const many = Array.from({ length: 301 }, (_, index) => action({ seq: index + 1, params: '{"a":1}' }))
    const report = analyzeImpact(many, [], {})
    expect(report.issues.some((issue) => issue.message.includes('业务动作数量超过上限'))).toBe(true)

    const rules = Array.from({ length: 101 }, (_, index) => ({
      stage: 'SAVE', seq: index + 1, validationKey: 'qty-not-exceed', enabled: true, params: '{}',
    }))
    const ruleReport = analyzeImpact([], rules, {})
    expect(ruleReport.issues.some((issue) => issue.message.includes('校验规则数量超过上限'))).toBe(true)
  })

  it('既无公式行也无参数的动作只给提示，不算阻断', () => {
    const report = analyzeImpact([action({ ops: [], params: null })], [], {})
    expect(report.issues).toHaveLength(1)
    expect(report.issues[0].severity).toBe('warn')
  })

  it('空公式行按服务型占位行提示，不计入阻断', () => {
    const report = analyzeImpact([
      action({ ops: [{ opSeq: 1, targetTable: '', targetField: '', opCode: '', condition: '{"logic":"AND","items":[]}' }] }),
    ], [], {})
    expect(report.issues).toHaveLength(1)
    expect(report.issues[0].severity).toBe('warn')
    expect(report.issues[0].message).toContain('空公式行')
  })

  it('校验规则按阶段单独计数，不与动作事件混在一起', () => {
    const report = analyzeImpact(
      [action({ eventCode: 'SAVE' })],
      [
        { stage: 'SAVE', seq: 1, validationKey: 'qty-not-exceed', enabled: true, params: '{}' },
        { stage: 'APPROVE', seq: 1, validationKey: 'reference-exists', enabled: true, params: '{}' },
      ],
      {},
    )
    expect(report.eventCounts).toEqual([{ eventCode: 'SAVE', count: 1 }])
    expect(report.stageCounts).toEqual([
      { stage: 'APPROVE', count: 1 },
      { stage: 'SAVE', count: 1 },
    ])
  })
})

describe('collectSwitchKeys', () => {
  it('递归收集嵌套条件里的开关键', () => {
    const json = JSON.stringify({
      logic: 'AND',
      items: [
        { type: 'switch', key: 'SEND_ORDER_TAG', value: true },
        {
          type: 'not-exists',
          targetTable: 'PRODUCT',
          condition: { logic: 'AND', items: [{ type: 'switch', key: 'PRO_MRP', value: true }] },
        },
        { type: 'value-eq', field: { scope: 'TARGET', field: 'X' }, value: 1 },
      ],
    })
    expect(collectSwitchKeys(json)).toEqual(['SEND_ORDER_TAG', 'PRO_MRP'])
    expect(collectSwitchKeys(null)).toEqual([])
    expect(collectSwitchKeys('{bad')).toEqual([])
  })
})
