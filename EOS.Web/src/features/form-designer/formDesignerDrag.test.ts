import { describe, expect, it } from 'vitest'
import { toDraft } from './formDesignerDraft'
import { applyDrop, dragId, parseDragId, zoneOf, type DragSource, type DropTarget } from './formDesignerDrag'
import type { DesignRow, DesignState, PoolField } from './types'

function row(key: string, overrides: Partial<DesignRow> = {}): DesignRow {
  return {
    key,
    label: key,
    dataType: 'nvarchar',
    tabNo: 1,
    orderNo: 1,
    span: 2,
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

function state(): DesignState {
  return {
    moduleId: 1405,
    title: '客户订单',
    masterTable: 'COP_ORDER_M',
    detailTable: 'COP_ORDER_D',
    columns: 4,
    tabs: [
      { no: 1, title: '' },
      { no: 2, title: '其它' },
    ],
    master: {
      table: 'COP_ORDER_M',
      layout: [
        row('A', { orderNo: 1 }),
        row('CLIENT_ID', { orderNo: 2, hasChooser: true }),
        row('CLIENT_NAME', { orderNo: 3 }),
        row('LOCKED', { orderNo: 4, locked: true, lockReason: '必填字段，始终显示' }),
      ],
      pool: [pool('POOLED')],
    },
    detail: {
      table: 'COP_ORDER_D',
      layout: [row('PRO_NO', { orderNo: 1, locked: true, lockReason: '主键列，始终显示' }), row('QTY', { orderNo: 2 })],
      pool: [],
    },
    baseUpdatedAt: 'stamp',
  }
}

const canvas = (key: string, table: 'master' | 'detail' = 'master'): DragSource => ({ from: 'canvas', table, key })
const fromPool = (key: string): DragSource => ({ from: 'pool', table: 'master', key })

describe('拖拽标识', () => {
  it('编码与解析往返一致，非法标识返回 null', () => {
    const source = canvas('CLIENT_ID')
    expect(parseDragId(dragId(source))).toEqual(source)
    expect(parseDragId('canvas:master')).toBeNull()
    expect(parseDragId('weird:master:A')).toBeNull()
    expect(parseDragId('canvas:other:A')).toBeNull()
  })
})

describe('zoneOf', () => {
  it('左 30% 前插、右 30% 后插、中间合并', () => {
    expect(zoneOf(10, 200)).toBe('before')
    expect(zoneOf(100, 200)).toBe('merge')
    expect(zoneOf(190, 200)).toBe('after')
    expect(zoneOf(0, 0)).toBe('merge')
  })
})

describe('applyDrop', () => {
  it('拖到左/右边缘：在该字段前/后插入', () => {
    const draft = toDraft(state())
    const before = applyDrop(draft, canvas('A'), { kind: 'insert', key: 'CLIENT_ID', before: true })
    expect('draft' in before && before.draft.master.map(item => item.key)).toEqual(['A', 'CLIENT_ID', 'CLIENT_NAME', 'LOCKED'])

    const after = applyDrop(draft, canvas('A'), { kind: 'insert', key: 'CLIENT_NAME', before: false })
    expect('draft' in after && after.draft.master.map(item => item.key)).toEqual(['CLIENT_ID', 'CLIENT_NAME', 'A', 'LOCKED'])
  })

  it('拖到格中心：合并为复合格；主字段无选择器来源时拒绝并说明', () => {
    const draft = toDraft(state())
    const merged = applyDrop(draft, canvas('CLIENT_NAME'), { kind: 'merge', key: 'CLIENT_ID' })
    expect('draft' in merged).toBe(true)
    if ('draft' in merged) {
      expect(merged.draft.master.find(item => item.key === 'CLIENT_ID')?.cellRole).toBe(1)
      expect(merged.draft.master.find(item => item.key === 'CLIENT_NAME')?.cellRole).toBe(2)
    }

    const rejected = applyDrop(draft, canvas('A'), { kind: 'merge', key: 'CLIENT_NAME' })
    expect('rejected' in rejected && rejected.rejected).toContain('选择器来源')
  })

  it('拖到页签标签：移动到该页签；拖到分节标题：归入该分节', () => {
    const draft = toDraft(state())
    const toTab = applyDrop(draft, canvas('A'), { kind: 'tab', tabNo: 2 })
    expect('draft' in toTab && toTab.draft.master.find(item => item.key === 'A')?.tabNo).toBe(2)

    const toSection = applyDrop(draft, canvas('A'), { kind: 'section', sectionId: '基本信息' })
    expect('draft' in toSection && toSection.draft.master.find(item => item.key === 'A')?.sectionId).toBe('基本信息')
  })

  it('拖到字段池：移除此字段；不可移除字段拒绝并给出原因', () => {
    const draft = toDraft(state())
    const removed = applyDrop(draft, canvas('A'), { kind: 'remove' })
    expect('draft' in removed && removed.draft.master.find(item => item.key === 'A')?.hidden).toBe(true)

    const locked = applyDrop(draft, canvas('LOCKED'), { kind: 'remove' })
    expect('rejected' in locked && locked.rejected).toContain('必填字段')
  })

  it('从字段池拖入：插入到目标字段前，并从池里移除', () => {
    const draft = toDraft(state())
    const result = applyDrop(draft, fromPool('POOLED'), { kind: 'insert', key: 'CLIENT_ID', before: true })
    expect('draft' in result).toBe(true)
    if ('draft' in result) {
      expect(result.draft.master.map(item => item.key)).toEqual(['A', 'POOLED', 'CLIENT_ID', 'CLIENT_NAME', 'LOCKED'])
      expect(result.draft.masterPool).toHaveLength(0)
    }
  })

  it('明细没有页签/分节/复合格：相应落点给出理由', () => {
    const draft = toDraft(state())
    const tab = applyDrop(draft, canvas('QTY', 'detail'), { kind: 'tab', tabNo: 2 } as DropTarget)
    expect('rejected' in tab && tab.rejected).toContain('明细没有页签')
    const merge = applyDrop(draft, canvas('QTY', 'detail'), { kind: 'merge', key: 'PRO_NO' })
    expect('rejected' in merge && merge.rejected).toContain('明细没有复合格')
  })
})
