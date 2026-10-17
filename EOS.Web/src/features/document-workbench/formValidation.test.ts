import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { validateDetailRows, validateField, validateMasterFields } from './formValidation'

function field(overrides: Partial<FormFieldDefinition>): FormFieldDefinition {
  return {
    key: 'code',
    label: '编号',
    dataType: 'nvarchar',
    displayLength: 100,
    displayFormat: null,
    isRequired: false,
    verifyIndex: null,
    regex: null,
    defaultValue: null,
    isReadonly: false,
    isVisible: true,
    onlyChoose: false,
    chooseMultiple: false,
    choosePage: null,
    choosers: [],
    isPrimaryKey: false,
    isAutoIncrement: false,
    isVirtual: false,
    isCost: false,
    isSecrecy: false,
    serverFilled: false,
    maxLength: null,
    tabNo: 1,
    formOrder: null,
    span: 1,
    newLine: false,
    cellGroup: null,
    cellRole: 0,
    options: [],
    displayOnly: false, canCopy: true,
    ...overrides,
  }
}

describe('formValidation', () => {
  it('必填字段空值报错', () => {
    expect(validateField(field({ isRequired: true }), '')).toBe('该字段不能为空。')
    expect(validateField(field({ isRequired: true }), '  ')).toBe('该字段不能为空。')
    expect(validateField(field({ isRequired: true }), 'A')).toBeNull()
  })

  it('超长字段报错', () => {
    expect(validateField(field({ maxLength: 3 }), 'abcd')).toBe('内容长度超出限制（最多 3 字符）。')
    expect(validateField(field({ maxLength: 3 }), 'abc')).toBeNull()
  })

  it('正则校验且空值放行', () => {
    const f = field({ regex: '^[A-Z]+$' })
    expect(validateField(f, 'AB')).toBeNull()
    expect(validateField(f, 'ab')).toBe('内容不符合格式要求。')
    expect(validateField(f, '')).toBeNull()
  })

  it('非法正则被忽略避免崩溃', () => {
    expect(validateField(field({ regex: '(' }), 'x')).toBeNull()
  })

  it('布尔字段不做必填校验', () => {
    expect(validateField(field({ dataType: 'bit', isRequired: true }), '')).toBeNull()
  })

  it('只读与服务端填充字段跳过校验', () => {
    expect(validateField(field({ isReadonly: true, isRequired: true }), '')).toBeNull()
    expect(validateField(field({ serverFilled: true, isRequired: true }), '')).toBeNull()
  })

  it('validateMasterFields 汇总多个字段错误', () => {
    const errors = validateMasterFields(
      [field({ key: 'a', isRequired: true }), field({ key: 'b', regex: '^\\d+$' })],
      { a: '', b: 'x' },
    )
    expect(errors.a).toBeDefined()
    expect(errors.b).toBeDefined()
  })

  it('validateDetailRows 检测 DF_VERIFY 重复明细', () => {
    const fields = [field({ key: 'product' }), field({ key: 'qty', isRequired: true })]
    const rows = [
      { product: 'P1', qty: '1' },
      { product: 'P1', qty: '2' },
      { product: 'P2', qty: '1' },
    ]
    const errors = validateDetailRows(fields, rows, 'product')
    expect(errors[0]).toEqual({})
    expect(errors[1].product).toBe('明细表资料重复。')
    expect(errors[2]).toEqual({})
  })

  it('validateDetailRows 无 dfVerify 时只做字段校验', () => {
    const fields = [field({ key: 'qty', isRequired: true })]
    const errors = validateDetailRows(fields, [{ qty: '' }])
    expect(errors[0].qty).toBeDefined()
  })

  it('业务精度优先于存储精度（ADR-037）：日元 0 位、个 0 位', () => {
    // 存储是 decimal(18,4)/decimal(18,6)，能放 4/6 位；业务精度由币别与单位决定
    const amount = field({ key: 'AMOUNT', dataType: 'decimal', precision: 18, scale: 4 })
    const qty = field({ key: 'QTY', dataType: 'decimal', precision: 18, scale: 6 })
    const context = {
      currencyDecimals: { JPY: 0, RMB: 2 },
      currencyRounding: { JPY: 'AwayFromZero', RMB: 'AwayFromZero' },
      unitDecimals: { PCS: 0, KG: 3 },
      fieldRoles: { AMOUNT: 'AMOUNT', QTY: 'QTY' },
    }

    // 日元 0 位：2 位小数必须拒绝
    expect(validateField(amount, '1234.56', { context, amountDecimals: 0 })).toContain('小数位')
    // 人民币 2 位：放行
    expect(validateField(amount, '1234.56', { context, amountDecimals: 2 })).toBeNull()
    // 未提供金额精度时回落到存储 scale(4)，不做拒绝
    expect(validateField(amount, '1234.56', { context })).toBeNull()

    // 单位"个"0 位：1.5 拒绝；"千克"3 位：1.5 放行
    expect(validateDetailRows([qty], [{ QTY: '1.5', UNIT_ID: 'PCS' }], '', { context })[0].QTY).toBeDefined()
    expect(validateDetailRows([qty], [{ QTY: '1.5', UNIT_ID: 'KG' }], '', { context })[0].QTY).toBeUndefined()
  })
})
