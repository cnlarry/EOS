import { describe, expect, it } from 'vitest'
import {
  addFromPool,
  addTab,
  deleteTab,
  mergeCompanion,
  moveRow,
  moveRowToTab,
  normalizeTabs,
  renameTab,
  resetRow,
  setHidden,
  setPlacement,
  setSection,
  toDraft,
  toSavePayload,
  validateDraft,
} from './formDesignerDraft'
import type { DesignRow, DesignState, PoolField } from './types'

function row(key: string, overrides: Partial<DesignRow> = {}): DesignRow {
  return {
    key,
    label: key,
    dataType: 'nvarchar',
    tabNo: 1,
    orderNo: 1,
    span: 1,
    rowSpan: 1,
    newLine: false,
    sectionId: null,
    cellGroup: null,
    cellRole: 0,
    hidden: false,
    locked: false,
    lockReason: null,
    userVisible: true,
    required: false,
    isPrimaryKey: false,
    hasChooser: false,
    isVirtual: false,
    ...overrides,
  }
}

function pool(key: string, overrides: Partial<PoolField> = {}): PoolField {
  return {
    key,
    label: key,
    dataType: 'nvarchar',
    userVisible: true,
    required: false,
    isPrimaryKey: false,
    hasChooser: false,
    isVirtual: false,
    locked: false,
    lockReason: null,
    ...overrides,
  }
}

function state(overrides: Partial<DesignState> = {}): DesignState {
  return {
    moduleId: 1405,
    title: '客户订单',
    masterTable: 'COP_ORDER_M',
    detailTable: 'COP_ORDER_D',
    columns: 2,
    tabs: [{ no: 1, title: '' }],
    master: {
      table: 'COP_ORDER_M',
      layout: [row('A', { orderNo: 1 }), row('B', { orderNo: 2 }), row('C', { orderNo: 3 })],
      pool: [pool('D')],
    },
    detail: {
      table: 'COP_ORDER_D',
      layout: [row('PRO_NO', { orderNo: 1 }), row('QTY', { orderNo: 2 })],
      pool: [],
    },
    baseUpdatedAt: '2026-09-25 05:00:00.000',
    ...overrides,
  }
}

describe('toDraft', () => {
  it('按放置顺序排序并保留字段池', () => {
    const draft = toDraft(state())
    expect(draft.master.map(item => item.key)).toEqual(['A', 'B', 'C'])
    expect(draft.masterPool.map(item => item.key)).toEqual(['D'])
    expect(draft.baseline.master.map(item => item.key)).toEqual(['A', 'B', 'C'])
    expect(draft.columns).toBe(2)
  })
})

describe('normalizeTabs', () => {
  it('补常驻页签并按序号排序去重', () => {
    expect(normalizeTabs([{ no: 3, title: '结尾' }, { no: 1, title: '主' }])).toEqual([
      { no: 1, title: '主' },
      { no: 3, title: '结尾' },
    ])
    expect(normalizeTabs([])).toEqual([{ no: 1, title: '' }])
  })
})

describe('moveRow', () => {
  it('前后移动只换位置，不改属性', () => {
    const draft = toDraft(state())
    const moved = moveRow(draft, 'master', 'C', -1)
    expect(moved.master.map(item => item.key)).toEqual(['A', 'C', 'B'])
    expect(moved.master.map(item => item.orderNo)).toEqual([1, 2, 3])
  })

  it('越界与明细移动', () => {
    const draft = toDraft(state())
    expect(moveRow(draft, 'master', 'A', -1)).toBe(draft)
    expect(moveRow(draft, 'detail', 'QTY', -1).detail.map(item => item.key)).toEqual(['QTY', 'PRO_NO'])
  })
})

describe('setHidden', () => {
  it('移除即隐藏（保留排版属性，不删行）', () => {
    const draft = toDraft(state())
    const next = setHidden(draft, 'master', 'B', true)
    expect(next.master.find(item => item.key === 'B')?.hidden).toBe(true)
    expect(next.master.find(item => item.key === 'B')?.orderNo).toBe(2)
    expect(setHidden(next, 'master', 'B', false).master.find(item => item.key === 'B')?.hidden).toBe(false)
  })

  it('不可移除字段拒绝隐藏', () => {
    const locked = state({
      master: {
        table: 'COP_ORDER_M',
        layout: [row('PK', { locked: true, lockReason: '主键列，始终显示', isPrimaryKey: true })],
        pool: [],
      },
    })
    const draft = toDraft(locked)
    expect(setHidden(draft, 'master', 'PK', true)).toBe(draft)
  })
})

describe('setPlacement', () => {
  it('夹取到模块列数与行跨度上限', () => {
    const draft = toDraft(state())
    const next = setPlacement(draft, 'A', { span: 9, rowSpan: 9 })
    expect(next.master[0].span).toBe(2)
    expect(next.master[0].rowSpan).toBe(3)
  })

  it('单列模块只能占 1 列', () => {
    const draft = toDraft(state({ columns: 1 }))
    expect(setPlacement(draft, 'A', { span: 4 }).master[0].span).toBe(1)
  })
})

describe('setSection', () => {
  it('空白分节归一为 null', () => {
    const draft = toDraft(state())
    expect(setSection(draft, 'A', '  ').master[0].sectionId).toBeNull()
    expect(setSection(draft, 'A', ' 基本信息 ').master[0].sectionId).toBe('基本信息')
  })
})

describe('mergeCompanion', () => {
  it('主字段有启用来源才能成组，且一格最多一个从字段', () => {
    const base = state({
      master: {
        table: 'COP_ORDER_M',
        layout: [
          row('CLIENT_ID', { hasChooser: true }),
          row('CLIENT_NAME'),
          row('CLIENT_TYPE'),
          row('NO_CHOOSER'),
        ],
        pool: [],
      },
    })
    const draft = toDraft(base)

    const rejected = mergeCompanion(draft, 'NO_CHOOSER', 'CLIENT_NAME')
    expect(rejected.rejected).toBeTruthy()
    expect(rejected.draft).toBe(draft)

    const merged = mergeCompanion(draft, 'CLIENT_ID', 'CLIENT_NAME').draft
    const main = merged.master.find(item => item.key === 'CLIENT_ID')
    const companion = merged.master.find(item => item.key === 'CLIENT_NAME')
    expect(main?.cellRole).toBe(1)
    expect(main?.cellGroup).toBe('CLIENT_ID')
    expect(companion?.cellRole).toBe(2)

    // 换成另一个从字段：原来的从字段解组
    const swapped = mergeCompanion(merged, 'CLIENT_ID', 'CLIENT_TYPE').draft
    expect(swapped.master.find(item => item.key === 'CLIENT_NAME')?.cellRole).toBe(0)
    expect(swapped.master.find(item => item.key === 'CLIENT_TYPE')?.cellRole).toBe(2)

    const cleared = mergeCompanion(swapped, 'CLIENT_ID', null).draft
    expect(cleared.master.every(item => item.cellGroup === null && item.cellRole === 0)).toBe(true)
  })

  it('必填字段不能当从字段', () => {
    const draft = toDraft(state({
      master: {
        table: 'COP_ORDER_M',
        layout: [row('CLIENT_ID', { hasChooser: true }), row('CLIENT_NAME', { required: true, locked: true })],
        pool: [],
      },
    }))
    expect(mergeCompanion(draft, 'CLIENT_ID', 'CLIENT_NAME').rejected).toContain('必填')
  })
})

describe('字段池与页签', () => {
  it('从池里放回表单追加到末尾并移出池', () => {
    const draft = toDraft(state())
    const next = addFromPool(draft, 'master', 'D')
    expect(next.master.map(item => item.key)).toEqual(['A', 'B', 'C', 'D'])
    expect(next.masterPool).toHaveLength(0)
    expect(addFromPool(next, 'master', 'D')).toBe(next)
  })

  it('新增/改名/删除页签，删页签的字段回到默认页签', () => {
    let draft = toDraft(state())
    draft = addTab(draft, '基本信息')
    expect(draft.tabs.map(tab => tab.no)).toEqual([1, 2])
    draft = renameTab(draft, 2, '  基本信息  ')
    expect(draft.tabs[1].title).toBe('基本信息')
    draft = moveRowToTab(draft, 'B', 2)
    expect(draft.master.find(item => item.key === 'B')?.tabNo).toBe(2)
    draft = deleteTab(draft, 2)
    expect(draft.tabs.map(tab => tab.no)).toEqual([1])
    expect(draft.master.find(item => item.key === 'B')?.tabNo).toBe(1)
    expect(deleteTab(draft, 1)).toBe(draft)
  })
})

describe('resetRow', () => {
  it('恢复到加载时的位置与占位', () => {
    const draft = toDraft(state())
    const changed = setPlacement(moveRow(draft, 'master', 'A', 2), 'A', { span: 2, rowSpan: 2, newLine: true })
    const restored = resetRow(changed, 'master', 'A')
    const restoredA = restored.master.find(item => item.key === 'A')
    expect(restored.master.map(item => item.key)).toEqual(['A', 'B', 'C'])
    expect(restoredA?.span).toBe(1)
    expect(restoredA?.rowSpan).toBe(1)
    expect(restoredA?.newLine).toBe(false)
  })
})

describe('toSavePayload', () => {
  it('提交顺序即排序（服务端会再重排 1..n），明细只带 key 与隐藏位', () => {
    const draft = toDraft(state())
    const payload = toSavePayload(moveRow(draft, 'master', 'C', -1), 'stamp', 'key-1')
    expect(payload.baseUpdatedAt).toBe('stamp')
    expect(payload.idempotencyKey).toBe('key-1')
    expect(payload.master.map(item => item.key)).toEqual(['A', 'C', 'B'])
    expect(payload.detail).toEqual([
      { key: 'PRO_NO', hidden: false },
      { key: 'QTY', hidden: false },
    ])
    expect(payload.tabs).toEqual([{ no: 1, title: '' }])
  })
})

describe('validateDraft', () => {
  it('本地口径与后端一致：必填不得隐藏、跨度不得越界、复合格须成对', () => {
    const draft = toDraft(state())
    expect(validateDraft(draft)).toEqual([])

    const broken = {
      ...draft,
      master: [
        { ...draft.master[0], hidden: true, locked: true, lockReason: '主键列，始终显示' },
        { ...draft.master[1], span: 5 },
        { ...draft.master[2], cellRole: 2, cellGroup: 'X' },
      ],
    }
    const issues = validateDraft(broken)
    expect(issues.some(issue => issue.includes('主键列，始终显示'))).toBe(true)
    expect(issues.some(issue => issue.includes('列跨度'))).toBe(true)
    expect(issues.some(issue => issue.includes('没有配主字段'))).toBe(true)
  })

  it('池里的必填字段必须加入表单', () => {
    const missing = state({
      master: {
        table: 'COP_ORDER_M',
        layout: [row('A')],
        pool: [pool('MUST', { required: true })],
      },
    })
    expect(validateDraft(toDraft(missing)).some(issue => issue.includes('必须加入表单'))).toBe(true)
  })
})
