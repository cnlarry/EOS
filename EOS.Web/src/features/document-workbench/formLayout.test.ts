import { describe, expect, it } from 'vitest'
import type { FormFieldDefinition } from './formDefinition'
import {
  buildPackedCells,
  deriveDefaultLayout,
  isLifecycleTailField,
  packFormGrid,
  packFormSections,
  toDetailRows,
} from './formLayout'

type Field = FormFieldDefinition

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
      // 字段级 span=2（整行独占）映射到 4 子列；span=1（半行）映射到 2 子列
      expect.objectContaining({ key: 'CLIENT_ID', orderNo: 3, span: 4, rowSpan: 1, hidden: false }),
      expect.objectContaining({ key: 'REMARK', span: 4, rowSpan: 2 }),
      expect.objectContaining({ key: 'QTY', orderNo: 7, span: 2 }),
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

  it('列数非法时回落 4（统一表单固定四子列），跨度按下限映射', () => {
    const doc = deriveDefaultLayout([field('A', { span: 0 })], 0)
    expect(doc.columns).toBe(4)
    expect(doc.rows[0].span).toBe(2)
  })

  it('明细版式行只保留列顺序与列显隐', () => {
    const doc = deriveDefaultLayout([field('A', { formOrder: 2 }), field('B', { isVisible: false })], 2)
    expect(toDetailRows(doc.rows)).toEqual([
      { key: 'A', orderNo: 2, hidden: false },
      { key: 'B', orderNo: 2, hidden: true },
    ])
  })
})

describe('buildPackedCells', () => {
  it('普通字段各自成格', () => {
    const cells = buildPackedCells([field('A'), field('B')])
    expect(cells.map(cell => cell.map(item => item.key))).toEqual([['A'], ['B']])
  })

  it('同组从字段跟随主字段进入同一格', () => {
    const main = field('CLIENT_ID', { cellGroup: 'CLIENT', cellRole: 1 })
    const companion = field('CLIENT_NAME', { cellGroup: 'CLIENT', cellRole: 2 })
    const cells = buildPackedCells([companion, main])
    expect(cells).toHaveLength(1)
    expect(cells[0].map(item => item.key)).toEqual(['CLIENT_ID', 'CLIENT_NAME'])
  })
})

describe('packFormSections 装箱（运行态与设计态共用）', () => {
  const keys = (sections: ReturnType<typeof packFormSections<Field>>) =>
    sections.map(section => section.cells.map(item => item.cell[0].key))

  it('半行字段每行两个（4 子列 × span2），满行换行', () => {
    const sections = packFormSections(
      [field('A', { span: 2 }), field('B', { span: 2 }), field('C', { span: 2 }), field('D', { span: 2 }), field('E', { span: 2 })],
      4,
    )
    expect(keys(sections)).toEqual([['A', 'B', 'C', 'D', 'E']])
    const placements = sections[0].cells.map(item => item.placement)
    expect(placements.map(item => [item.col, item.row, item.span])).toEqual([
      [1, 1, 2], [3, 1, 2], [1, 2, 2], [3, 2, 2], [1, 3, 2],
    ])
  })

  it('整行字段独占一行；紧凑排列把后续半行字段回填到空位（视觉顺序由装箱决定）', () => {
    const sections = packFormSections([field('A', { span: 2 }), field('REMARK', { span: 4 }), field('B', { span: 2 })], 4)
    // A 占 1-2 列；REMARK 整行落到第 2 行；B 回填第 1 行剩下的 3-4 列
    expect(sections[0].cells.map(item => [item.cell[0].key, item.placement.row, item.placement.col, item.placement.span])).toEqual([
      ['A', 1, 1, 2], ['B', 1, 3, 2], ['REMARK', 2, 1, 4],
    ])

    const sparse = packFormSections([field('A', { span: 2 }), field('REMARK', { span: 4 }), field('B', { span: 2 })], 4, { fillHoles: false })
    expect(sparse[0].cells.map(item => [item.cell[0].key, item.placement.row])).toEqual([
      ['A', 1], ['REMARK', 2], ['B', 3],
    ])
  })

  it('强制换行字段另起一行', () => {
    const sections = packFormSections([field('A', { span: 2 }), field('C', { span: 2, newLine: true }), field('B', { span: 2 })], 4)
    expect(sections[0].cells.map(item => [item.cell[0].key, item.placement.row, item.placement.col])).toEqual([
      ['A', 1, 1], ['C', 2, 1], ['B', 2, 3],
    ])
  })

  it('紧凑排列关闭时空洞保留（不提前回填）', () => {
    const filled = packFormSections([field('A', { span: 4 }), field('B', { span: 2 }), field('C', { span: 2 })], 4, { fillHoles: true })
    expect(filled[0].cells.map(item => [item.cell[0].key, item.placement.row])).toEqual([['A', 1], ['B', 2], ['C', 2]])

    const sparse = packFormSections(
      [field('A', { span: 4 }), field('B', { span: 4, newLine: true }), field('C', { span: 2 })],
      4,
      { fillHoles: false },
    )
    expect(sparse[0].cells.map(item => [item.cell[0].key, item.placement.row])).toEqual([['A', 1], ['B', 2], ['C', 3]])
  })

  it('空输入返回空', () => {
    expect(packFormSections([], 4)).toEqual([])
  })
})

describe('packFormSections 分节', () => {
  const titles = (sections: ReturnType<typeof packFormSections<Field>>) => sections.map(section => section.title)
  const keys = (sections: ReturnType<typeof packFormSections<Field>>) =>
    sections.map(section => section.cells.map(item => item.cell[0].key))

  it('显式 sectionId 分节：无题节排最前，其余按首现顺序', () => {
    const sections = packFormSections(
      [
        field('B', { sectionId: '基本信息' }),
        field('A'),
        field('C', { sectionId: '基本信息' }),
        field('D', { sectionId: '金额' }),
      ],
      4,
    )
    expect(titles(sections)).toEqual([null, '基本信息', '金额'])
    expect(keys(sections)).toEqual([['A'], ['B', 'C'], ['D']])
  })

  it('有 sectionId 时不再用复合格组名当分节', () => {
    const sections = packFormSections(
      [
        field('S1', { cellGroup: '发货信息', sectionId: '明细' }),
        field('S2', { cellGroup: '发货信息', sectionId: '明细' }),
      ],
      4,
    )
    expect(titles(sections)).toEqual(['明细'])
  })

  it('完全没有 sectionId 时回落到组名启发式（≥2 个主字段成节，单格组归默认节）', () => {
    const sections = packFormSections(
      [
        field('A'),
        field('S1', { cellGroup: '发货信息' }),
        field('S2', { cellGroup: '发货信息' }),
        field('CLIENT_ID', { cellGroup: 'CLIENT', cellRole: 1 }),
        field('CLIENT_NAME', { cellGroup: 'CLIENT', cellRole: 2 }),
      ],
      4,
    )
    expect(titles(sections)).toEqual([null, '发货信息'])
    expect(keys(sections)).toEqual([['A', 'CLIENT_ID'], ['S1', 'S2']])
  })

  it('尾部格自成末尾一节，按固定次序排列且大小写不敏感', () => {
    const sections = packFormSections(
      [field('NAME'), field('CONFIRM_TAG'), field('REMARK'), field('create_person')],
      4,
      { tailCells: item => item.key.toUpperCase().startsWith('CONFIRM') || item.key.toUpperCase().startsWith('CREATE') },
    )
    expect(titles(sections)).toEqual([null, null])
    expect(keys(sections)).toEqual([['NAME', 'REMARK'], ['create_person', 'CONFIRM_TAG']])
  })

  it('无尾部格时保持原分节不变', () => {
    const items = [field('S1', { cellGroup: '发货信息' }), field('S2', { cellGroup: '发货信息' }), field('A')]
    const withoutTail = packFormSections(items, 4)
    const withTail = packFormSections(items, 4, { tailCells: () => false })
    expect(titles(withTail)).toEqual(titles(withoutTail))
    expect(keys(withTail)).toEqual(keys(withoutTail))
  })
})

describe('isLifecycleTailField', () => {
  it('识别建立/修改/审核/结案的人·日期·状态，且大小写不敏感', () => {
    expect(isLifecycleTailField({ key: 'CREATE_PERSON' })).toBe(true)
    expect(isLifecycleTailField({ key: 'create_date' })).toBe(true)
    expect(isLifecycleTailField({ key: 'CONFIRM_TAG' })).toBe(true)
    expect(isLifecycleTailField({ key: 'FINISHED_DATE' })).toBe(true)
    expect(isLifecycleTailField({ key: 'REMARK' })).toBe(false)
  })
})
