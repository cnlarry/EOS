import { describe, expect, it } from 'vitest'
import { readListState, writeListState } from './listStateUrl'

describe('listStateUrl', () => {
  it('空参数返回默认状态', () => {
    const state = readListState(new URLSearchParams())
    expect(state).toEqual({ keyword: '', sort: [], conditions: [], columnFilters: {} })
  })

  it('读写往返保持一致', () => {
    const state = {
      keyword: '备料',
      sort: [{ id: 'APPLY_DATE', desc: true }, { id: 'APPLY_NO', desc: false }],
      conditions: [{ field: 'APPLY_TYPE', operator: 'eq', value: 'QG', valueTo: '', logic: 'and' }],
      columnFilters: { APPLY_TYPE: { field: 'APPLY_TYPE', operator: 'eq', value: 'QG', valueTo: '', logic: 'and' } },
    }
    expect(readListState(writeListState(state))).toEqual(state)
  })

  it('page 参数不再参与状态（滚动加载由内部页码驱动）', () => {
    const state = readListState(new URLSearchParams('page=abc&keyword=x'))
    expect(state.keyword).toBe('x')
    expect(writeListState(state).get('page')).toBeNull()
  })

  it('非法 JSON 条件被忽略', () => {
    const state = readListState(new URLSearchParams('q=%7Bbroken&cf=not-json'))
    expect(state.conditions).toEqual([])
    expect(state.columnFilters).toEqual({})
  })
})
