import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { writableFields } from './formEditorUtils'

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
    canCopy: true,
    ...overrides,
  }
}

/**
 * 可提交字段的口径（与服务端 `RecordPayloadValidator` 同一把尺子）：
 * 只读字段只在"界面确实会带值"时提交（必填联动列、带选择器的回填列）；
 * **主档编号例外**——它是记录的身份，服务端在编辑态标只读并拒收改过的值，
 * 放进提交体（与并发快照）只会让请求打到别的记录上。
 */
describe('可提交字段（writableFields）', () => {
  it('主档编号被标只读后不进提交体（即使必填）', () => {
    const fields = [
      field({ key: 'DEPOT_ID', isPrimaryKey: true, isRequired: true, isReadonly: true }),
      field({ key: 'DEPOT_NAME' }),
    ]

    expect(writableFields(fields).map(item => item.key)).toEqual(['DEPOT_NAME'])
  })

  it('新增态的主键可填，照常提交', () => {
    const fields = [field({ key: 'DEPOT_ID', isPrimaryKey: true, isRequired: true, isReadonly: false })]

    expect(writableFields(fields).map(item => item.key)).toEqual(['DEPOT_ID'])
  })

  it('只读的必填联动列与带选择器的回填列仍提交', () => {
    const fields = [
      field({ key: 'CURR_RATE', isReadonly: true, isRequired: true }),
      field({
        key: 'TAX_ID',
        isReadonly: true,
        choosers: [{ active: true, table: 'TAX', description: '税别', moduleId: null, returnMapping: null, filter: null, serialNo: 1 }],
      }),
      field({ key: 'HIDDEN', isVisible: false }),
      field({ key: 'AUDIT', serverFilled: true }),
    ]

    expect(writableFields(fields).map(item => item.key)).toEqual(['CURR_RATE', 'TAX_ID'])
  })
})
