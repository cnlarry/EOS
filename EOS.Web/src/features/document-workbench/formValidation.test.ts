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
    displayOnly: false,
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
})
