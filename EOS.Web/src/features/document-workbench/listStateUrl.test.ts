import { describe, expect, it } from 'vitest'
import { readListState, writeListState } from './listStateUrl'

describe('listStateUrl', () => {
  it('空参数返回默认状态', () => {
    const state = readListState(new URLSearchParams())
    expect(state).toEqual({ page: 1, pageSize: null, keyword: '', sort: [], conditions: [], columnFilters: {} })
  })

  it('读写往返保持一致', () => {
    const state = {
      page: 3,
      pageSize: 25,
      keyword: '备料',
      sort: [{ id: 'APPLY_DATE', desc: true }, { id: 'APPLY_NO', desc: false }],
      conditions: [{ field: 'APPLY_TYPE', operator: 'eq', value: 'QG', valueTo: '', logic: 'and' }],
      columnFilters: { APPLY_TYPE: { field: 'APPLY_TYPE', operator: 'eq', value: 'QG', valueTo: '', logic: 'and' } },
    }
    expect(readListState(writeListState(state))).toEqual(state)
  })

  it('非法页码/每页条数回退默认', () => {
    const state = readListState(new URLSearchParams('page=abc&pageSize=100&keyword=x'))
    expect(state.page).toBe(1)
    expect(state.pageSize).toBeNull()
    expect(state.keyword).toBe('x')
  })

  it('非法 JSON 条件被忽略', () => {
    const state = readListState(new URLSearchParams('q=%7Bbroken&cf=not-json'))
    expect(state.conditions).toEqual([])
    expect(state.columnFilters).toEqual({})
  })
})
