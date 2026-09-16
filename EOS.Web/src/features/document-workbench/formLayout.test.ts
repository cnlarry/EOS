import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import { buildFormCells, buildFormRows, buildFormSections, moveLifecycleToTail } from './formLayout'

function field(key: string, overrides: Partial<FormFieldDefinition> = {}): FormFieldDefinition {
  return {
    key, label: key, dataType: 'nvarchar', displayLength: 100, displayFormat: null,
    isRequired: false, verifyIndex: null, regex: null, defaultValue: null, isReadonly: false, isVisible: true,
    onlyChoose: false, chooseMultiple: false, choosePage: null, choosers: [],
    isPrimaryKey: false, isAutoIncrement: false, isVirtual: false, isCost: false, isSecrecy: false,
    serverFilled: false, maxLength: null,
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: [], displayOnly: false, canCopy: true,
    ...overrides,
  }
}

describe('buildFormCells', () => {
  it('普通字段各自成格', () => {
    const cells = buildFormCells([field('A'), field('B')])
    expect(cells).toEqual([[expect.objectContaining({ key: 'A' })], [expect.objectContaining({ key: 'B' })]])
  })

  it('同组从字段跟随主字段进入同一格', () => {
    const main = field('CLIENT_ID', { cellGroup: 'CLIENT', cellRole: 1, choosers: [{ active: true, table: 'CLIENT', description: null, moduleId: null, filter: null, returnMapping: null, serialNo: 1 }] })
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

describe('buildFormSections', () => {
  it('无分组字段全部归入默认节（无标题、排最前）', () => {
    const sections = buildFormSections([[field('A')], [field('B')]])
    expect(sections).toEqual([{ title: null, cells: [[expect.objectContaining({ key: 'A' })], [expect.objectContaining({ key: 'B' })]] }])
  })

  it('组名下 ≥2 个主字段格成节，标题取组名原文', () => {
    const sections = buildFormSections([
      [field('A')],
      [field('S1', { cellGroup: '发货信息' })],
      [field('S2', { cellGroup: '发货信息' })],
    ])
    expect(sections).toHaveLength(2)
    expect(sections[0].title).toBeNull()
    expect(sections[0].cells.map(cell => cell[0].key)).toEqual(['A'])
    expect(sections[1].title).toBe('发货信息')
    expect(sections[1].cells.map(cell => cell[0].key)).toEqual(['S1', 'S2'])
  })

  it('复合三件套（ID+名称同格）不立节——单格组归默认节', () => {
    const main = field('CLIENT_ID', { cellGroup: 'CLIENT', cellRole: 1 })
    const companion = field('CLIENT_NAME', { cellGroup: 'CLIENT', cellRole: 2 })
    const cells = buildFormCells([main, companion])
    expect(cells).toHaveLength(1)
    const sections = buildFormSections(cells)
    expect(sections).toHaveLength(1)
    expect(sections[0].title).toBeNull()
  })

  it('分节内保持字段出现顺序，节按首现顺序排列', () => {
    const sections = buildFormSections([
      [field('G2A', { cellGroup: 'G2' })],
      [field('M1')],
      [field('G1B', { cellGroup: 'G1' })],
      [field('G2B', { cellGroup: 'G2' })],
      [field('G1A', { cellGroup: 'G1' })],
    ])
    expect(sections.map(section => section.title)).toEqual([null, 'G2', 'G1'])
    expect(sections[1].cells.map(cell => cell[0].key)).toEqual(['G2A', 'G2B'])
    expect(sections[2].cells.map(cell => cell[0].key)).toEqual(['G1B', 'G1A'])
  })

  it('空输入返回单个默认节', () => {
    expect(buildFormSections([])).toEqual([])
  })
})

describe('moveLifecycleToTail', () => {
  it('生命周期列从各节摘出，追加为末尾一节', () => {
    const sections = buildFormSections([
      [field('NAME')],
      [field('CREATE_PERSON')],
      [field('REMARK')],
      [field('CONFIRM_TAG')],
    ])
    const result = moveLifecycleToTail(sections)
    expect(result.map(section => section.title)).toEqual([null, null])
    expect(result[0].cells.map(cell => cell[0].key)).toEqual(['NAME', 'REMARK'])
    expect(result[1].cells.map(cell => cell[0].key)).toEqual(['CREATE_PERSON', 'CONFIRM_TAG'])
  })

  it('尾部按固定次序排列，不受元数据顺序影响', () => {
    const sections = buildFormSections([[field('CONFIRM_TAG')], [field('CREATE_DATE')], [field('CREATE_PERSON')]])
    const result = moveLifecycleToTail(sections)
    expect(result[result.length - 1].cells.map(cell => cell[0].key))
      .toEqual(['CREATE_PERSON', 'CREATE_DATE', 'CONFIRM_TAG'])
  })

  it('分组内的生命周期列同样摘出，尾部块排在最后一节之后', () => {
    const sections = buildFormSections([
      [field('A')],
      [field('G1', { cellGroup: '发货信息' })],
      [field('G2', { cellGroup: '发货信息' })],
      [field('FINISHED_TAG')],
    ])
    const result = moveLifecycleToTail(sections)
    expect(result.map(section => section.title)).toEqual([null, '发货信息', null])
    expect(result[2].cells.map(cell => cell[0].key)).toEqual(['FINISHED_TAG'])
  })

  it('字段名大小写不敏感', () => {
    const sections = buildFormSections([[field('create_person')], [field('A')]])
    const result = moveLifecycleToTail(sections)
    expect(result[0].cells.map(cell => cell[0].key)).toEqual(['A'])
    expect(result[1].cells.map(cell => cell[0].key)).toEqual(['create_person'])
  })

  it('无生命周期列时保持原分节不变', () => {
    const sections = buildFormSections([
      [field('S1', { cellGroup: '发货信息' })],
      [field('S2', { cellGroup: '发货信息' })],
      [field('A')],
    ])
    expect(moveLifecycleToTail(sections)).toEqual(sections)
  })

  it('空输入返回空', () => {
    expect(moveLifecycleToTail([])).toEqual([])
  })
})
