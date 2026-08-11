import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { buildFormCells, buildFormRows } from './formLayout'

function field(key: string, overrides: Partial<FormFieldDefinition> = {}): FormFieldDefinition {
  return {
    key, label: key, dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: null, isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null,
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: [], displayOnly: false,
    ...overrides,
  }
}

describe('buildFormCells', () => {
  it('普通字段各自成格', () => {
    const cells = buildFormCells([field('A'), field('B')])
    expect(cells).toEqual([[expect.objectContaining({ key: 'A' })], [expect.objectContaining({ key: 'B' })]])
  })

  it('同组从字段跟随主字段进入同一格', () => {
    const main = field('CLIENT_ID', { cellGroup: 'CLIENT', cellRole: 1, choosers: [{ active: true, table: 'CLIENT', description: null, moduleId: null, filter: null, returnMapping: null }] })
    const companion = field('CLIENT_NAME', { cellGroup: 'CLIENT', cellRole: 2, displayOnly: true })
    const cells = buildFormCells([companion, main])
    expect(cells).toHaveLength(1)
    expect(cells[0].map(item => item.key)).toEqual(['CLIENT_ID', 'CLIENT_NAME'])
  })
})

describe('buildFormRows', () => {
  it('按每行对数切分', () => {
    const cells = [[field('A')], [field('B')], [field('C')], [field('D')], [field('E')]]
    const rows = buildFormRows(cells, 2)
    expect(rows.map(row => row.map(cell => cell[0].key))).toEqual([['A', 'B'], ['C', 'D'], ['E']])
  })

  it('整行独占字段单独成行', () => {
    const cells = [[field('A')], [field('REMARK', { span: 2 })], [field('B')]]
    const rows = buildFormRows(cells, 2)
    expect(rows.map(row => row.map(cell => cell[0].key))).toEqual([['A'], ['REMARK'], ['B']])
  })

  it('REMARK 类字段（无 span 配置）回退整行', () => {
    const rows = buildFormRows([[field('A')], [field('REMARK')], [field('B')]], 2)
    expect(rows.map(row => row.map(cell => cell[0].key))).toEqual([['A'], ['REMARK'], ['B']])
  })

  it('强制换行字段另起一行', () => {
    const cells = [[field('A')], [field('C', { newLine: true })], [field('B')]]
    const rows = buildFormRows(cells, 2)
    expect(rows.map(row => row.map(cell => cell[0].key))).toEqual([['A'], ['C', 'B']])
  })
})
