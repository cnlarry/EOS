import { describe, expect, it } from 'vitest'
import {
  formatCondition,
  formatMatch,
  formatOpSentence,
  formatSourceTerms,
  labelWithCode,
  makeLabelLookup,
  makeNameLookup,
  matchItemsFromRelation,
  parseMatchItems,
  serializeMatchItems,
  summarizeAction,
} from './businessActionText'

describe('businessActionText', () => {
  it('公式行渲染成「目标.字段 运算 来源」句式', () => {
    expect(formatOpSentence({
      targetTable: 'COP_ORDER_D',
      targetField: 'FINISHED_SEND_QTY',
      opCode: 'ACCUM',
      sourceScope: 'DETAIL',
      sourceField: 'QTY',
      sourceAgg: 'SUM',
    })).toBe('COP_ORDER_D.FINISHED_SEND_QTY += 本单明细.QTY（合计）')
  })

  it('覆盖取最值/常量/上下文表来源各有可读形态', () => {
    expect(formatOpSentence({
      targetTable: 'PUR_PURCHASE_D',
      targetField: 'REAL_DELIVERY_DATE',
      opCode: 'ASSIGN_MAX',
      sourceScope: 'MASTER',
      sourceField: 'RECEIVE_DATE',
    })).toBe('PUR_PURCHASE_D.REAL_DELIVERY_DATE =MAX() 本单主表.RECEIVE_DATE')

    expect(formatOpSentence({
      targetTable: 'PUR_PURCHASE_D',
      targetField: 'FINISHED_TAG',
      opCode: 'SET_WHEN',
      sourceScope: 'CONSTANT',
      sourceConstant: '1',
    })).toBe("PUR_PURCHASE_D.FINISHED_TAG := 常量 '1'")

    expect(formatOpSentence({
      targetTable: 'MOC_PRODUCE_D',
      targetField: 'PLAN_QTY',
      opCode: 'ACCUM',
      sourceScope: 'TABLE',
      sourceTable: 'MOC_GET_MORE',
      sourceField: 'QTY',
      sourceAgg: 'SUM',
    })).toBe('MOC_PRODUCE_D.PLAN_QTY += 上下文表 MOC_GET_MORE.QTY（合计）')
  })

  it('源加减项渲染为闭式加减式，并优先于源字段', () => {
    const terms = JSON.stringify([
      { field: 'QTY', coef: 1 },
      { field: 'SPARE_QTY', coef: 1 },
      { field: 'FINISHED_SEND_QTY', coef: -1 },
    ])
    expect(formatSourceTerms(terms)).toBe('QTY + SPARE_QTY − FINISHED_SEND_QTY')
    expect(formatOpSentence({
      targetTable: 'COP_ORDER_D',
      targetField: 'PLAN_QTY',
      opCode: 'ACCUM',
      sourceScope: 'DETAIL',
      sourceTerms: terms,
      sourceField: 'IGNORED',
    })).toBe('COP_ORDER_D.PLAN_QTY += 本单明细(QTY + SPARE_QTY − FINISHED_SEND_QTY)')
  })

  it('定位键渲染成人话并支持往返解析', () => {
    const json = JSON.stringify([
      { target: 'PURCHASE_NO', source: { scope: 'DETAIL', field: 'PURCHASE_NO' } },
      { target: 'SERIAL_NO', source: { scope: 'TABLE', field: 'SERIAL_NO', table: 'PUR_PURCHASE_MORE' } },
    ])
    expect(formatMatch(json)).toBe('@PURCHASE_NO ← 本单明细.PURCHASE_NO、SERIAL_NO ← 上下文表 PUR_PURCHASE_MORE.SERIAL_NO')
    expect(serializeMatchItems(parseMatchItems(json)!)).toBe(json)
    expect(serializeMatchItems([])).toBeNull()
    expect(parseMatchItems('{not json')).toBeNull()
  })

  it('关系边组按键的来源表判定来源范围', () => {
    const items = matchItemsFromRelation(
      [
        { fromTable: 'PUR_RECEIVE_M', fromColumn: 'SUPPLIER_ID', toColumn: 'SUPPLIER_ID' },
        { fromTable: 'PUR_RECEIVE_D', fromColumn: 'SERIAL_NO', toColumn: 'SERIAL_NO' },
        { fromTable: 'PUR_PURCHASE_MORE', fromColumn: 'TYPE', toColumn: 'TYPE' },
      ],
      'PUR_RECEIVE_M',
      'PUR_RECEIVE_D',
    )
    expect(items).toEqual([
      { target: 'SUPPLIER_ID', source: { scope: 'MASTER', field: 'SUPPLIER_ID' } },
      { target: 'SERIAL_NO', source: { scope: 'DETAIL', field: 'SERIAL_NO' } },
      { target: 'TYPE', source: { scope: 'TABLE', field: 'TYPE', table: 'PUR_PURCHASE_MORE' } },
    ])
  })

  it('条件 JSON 渲染为判据句式', () => {
    expect(formatCondition(JSON.stringify({
      logic: 'AND',
      items: [
        { type: 'field-compare', left: { scope: 'TARGET', field: 'RECEIVE_QTY' }, op: 'GE', right: { scope: 'TARGET', field: 'QTY' } },
        { type: 'value-eq', field: { scope: 'TARGET', field: 'FINISHED_TAG' }, value: 1 },
      ],
    }))).toBe('目标行.RECEIVE_QTY >= 目标行.QTY 且 目标行.FINISHED_TAG = 1')
    expect(formatCondition(JSON.stringify({ type: 'switch', key: 'PRO_MRP', value: true }))).toBe('系统开关 PRO_MRP = true')
    expect(formatCondition(null)).toBeNull()
  })

  it('动作摘要区分公式型与参数型', () => {
    expect(summarizeAction({
      ops: [
        { opSeq: 1, targetTable: 'PUR_PURCHASE_D', targetField: 'RECEIVE_QTY', opCode: 'ACCUM', sourceScope: 'DETAIL', sourceField: 'QTY', sourceAgg: 'SUM' },
      ],
    })).toBe('PUR_PURCHASE_D.RECEIVE_QTY += 本单明细.QTY（合计）')

    expect(summarizeAction({ params: JSON.stringify({ direction: 'OUT', fieldMap: {} }) }))
      .toBe('参数型：direction、fieldMap')
  })

  it('目录标签查询大小写不敏感，缺标签时回落目录码', () => {
    const lookup = makeLabelLookup({ 'field-accumulate': '量额/日期累加回写' })
    expect(lookup('FIELD-ACCUMULATE')).toBe('量额/日期累加回写')
    expect(labelWithCode(lookup, 'field-accumulate')).toBe('量额/日期累加回写（field-accumulate）')
    expect(labelWithCode(lookup, 'unknown-key')).toBe('unknown-key')
  })

  it('表/字段中文名查询：命中显示「中文名(标识符)」，缺失回落标识符', () => {
    const names = makeNameLookup(
      { tables: { PUR_PURCHASE_D: '采购单明细' }, fields: { 'PUR_PURCHASE_D.RECEIVE_QTY': '已收数量' } },
      'PUR_RECEIVE_M',
      'PUR_RECEIVE_D',
    )
    expect(names.field('pur_purchase_d', 'receive_qty')).toBe('已收数量(receive_qty)')
    expect(names.table('PUR_PURCHASE_D')).toBe('采购单明细(PUR_PURCHASE_D)')
    // 元数据缺失时只显示标识符；范围解析出本单主/副表后同样按表取中文名。
    expect(names.field('PUR_RECEIVE_D', 'UNKNOWN')).toBe('UNKNOWN')
    expect(names.scopeTable('MASTER', null)).toBe('PUR_RECEIVE_M')
    expect(formatOpSentence({
      targetTable: 'PUR_PURCHASE_D',
      targetField: 'RECEIVE_QTY',
      opCode: 'ACCUM',
      sourceScope: 'DETAIL',
      sourceField: 'QTY',
      sourceAgg: 'SUM',
    }, names)).toBe('采购单明细(PUR_PURCHASE_D).已收数量(RECEIVE_QTY) += 本单明细.QTY（合计）')
  })
})
