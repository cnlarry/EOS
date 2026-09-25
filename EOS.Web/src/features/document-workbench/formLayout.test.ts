import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import {
  buildFormCells,
  buildFormRows,
  buildFormSections,
  deriveDefaultLayout,
  moveLifecycleToTail,
  packFormGrid,
  toDetailRows,
} from './formLayout'

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

describe('packFormGrid', () => {
  const at = (key: string, row: number, col: number, span = 1, rowSpan = 1) => ({ key, row, col, span, rowSpan })

  it('按列数顺序装满一行，放不下的另起一行', () => {
    expect(packFormGrid([{ key: 'A' }, { key: 'B' }, { key: 'C' }, { key: 'D' }], 3))
      .toEqual([at('A', 1, 1), at('B', 1, 2), at('C', 1, 3), at('D', 2, 1)])
  })

  it('列跨度放不下时另起一行（不挤压、不改宽度）', () => {
    expect(packFormGrid([{ key: 'A', span: 2 }, { key: 'B', span: 2 }], 3, false))
      .toEqual([at('A', 1, 1, 2), at('B', 2, 1, 2)])
  })

  it('强制换行另起一行，且该行之前不再回填', () => {
    // fillHoles 开着也不许把 C 回填到第 1 行的空位：NEW_LINE 是显式的断点
    expect(packFormGrid([{ key: 'A' }, { key: 'B', newLine: true }, { key: 'C' }], 2))
      .toEqual([at('A', 1, 1), at('B', 2, 1), at('C', 2, 2)])
  })

  it('默认回填空洞：后续放得下的格提前填入（返回顺序即视觉顺序）', () => {
    const packed = packFormGrid([{ key: 'A' }, { key: 'B' }, { key: 'C', span: 2 }, { key: 'D' }], 3)
    expect(packed).toEqual([at('A', 1, 1), at('B', 1, 2), at('D', 1, 3), at('C', 2, 1, 2)])
  })

  it('关闭回填时保留空洞', () => {
    expect(packFormGrid([{ key: 'A' }, { key: 'B' }, { key: 'C', span: 2 }, { key: 'D' }], 3, false))
      .toEqual([at('A', 1, 1), at('B', 1, 2), at('C', 2, 1, 2), at('D', 2, 3)])
  })

  it('行跨度按占位处理：跨行格覆盖的行不再放别的格', () => {
    expect(packFormGrid([{ key: 'A', rowSpan: 2 }, { key: 'B' }, { key: 'C' }, { key: 'D' }], 2))
      .toEqual([at('A', 1, 1, 1, 2), at('B', 1, 2), at('C', 2, 2), at('D', 3, 1)])
  })

  it('列跨度超列数被截断到列数', () => {
    expect(packFormGrid([{ key: 'A', span: 5 }], 2)).toEqual([at('A', 1, 1, 2)])
  })

  it('单列（窄屏）每格独占一行', () => {
    expect(packFormGrid([{ key: 'A' }, { key: 'B', span: 2 }], 1))
      .toEqual([at('A', 1, 1), at('B', 2, 1)])
  })

  it('空输入返回空', () => {
    expect(packFormGrid([], 3)).toEqual([])
  })
})

describe('deriveDefaultLayout', () => {
  it('顺序与占位取字段级配置，备注类整行两行高', () => {
    const doc = deriveDefaultLayout([
      field('CLIENT_ID', { formOrder: 3, span: 2 }),
      field('REMARK'),
      field('QTY', { formOrder: 7 }),
    ], 4)
    expect(doc.columns).toBe(4)
    expect(doc.tabs).toEqual([])
    expect(doc.rows).toEqual([
      expect.objectContaining({ key: 'CLIENT_ID', orderNo: 3, span: 2, rowSpan: 1, hidden: false }),
      expect.objectContaining({ key: 'REMARK', span: 4, rowSpan: 2 }),
      expect.objectContaining({ key: 'QTY', orderNo: 7 }),
    ])
  })

  it('顺序缺省按传入次序，页签缺省为 1', () => {
    const doc = deriveDefaultLayout([field('A', { tabNo: 0, formOrder: null }), field('B')], 3)
    expect(doc.rows.map(row => [row.key, row.orderNo, row.tabNo]))
      .toEqual([['A', 1, 1], ['B', 2, 1]])
  })

  it('隐藏只针对本来进不了表单的字段：必填与复合格从字段不隐藏', () => {
    const doc = deriveDefaultLayout([
      field('HIDDEN_PLAIN', { isVisible: false }),
      field('HIDDEN_REQUIRED', { isVisible: false, isRequired: true }),
      field('HIDDEN_COMPANION', { isVisible: false, cellGroup: 'CLIENT', cellRole: 2 }),
    ], 2)
    expect(doc.rows.map(row => [row.key, row.hidden]))
      .toEqual([['HIDDEN_PLAIN', true], ['HIDDEN_REQUIRED', false], ['HIDDEN_COMPANION', false]])
  })

  it('复合格主从与分节：主从透传，分节不推导', () => {
    const doc = deriveDefaultLayout([
      field('CLIENT_ID', { cellGroup: 'CLIENT', cellRole: 1 }),
      field('CLIENT_NAME', { cellGroup: 'CLIENT', cellRole: 2 }),
    ], 2)
    expect(doc.rows.map(row => [row.cellGroup, row.cellRole, row.sectionId]))
      .toEqual([['CLIENT', 1, null], ['CLIENT', 2, null]])
  })

  it('列数非法时回落 2，列跨度按下限截断', () => {
    const doc = deriveDefaultLayout([field('A', { span: 0 })], 0)
    expect(doc.columns).toBe(2)
    expect(doc.rows[0].span).toBe(1)
  })

  it('明细版式行只保留列顺序与列显隐', () => {
    const doc = deriveDefaultLayout([field('A', { formOrder: 2 }), field('B', { isVisible: false })], 2)
    expect(toDetailRows(doc.rows)).toEqual([
      { key: 'A', orderNo: 2, hidden: false },
      { key: 'B', orderNo: 2, hidden: true },
    ])
  })
})

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
