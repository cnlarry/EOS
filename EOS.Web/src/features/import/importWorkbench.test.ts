import { describe, expect, it } from 'vitest'
import { buildRowStates, countStatuses, filterIndexes, workSignature } from './importWorkbench'
import type { ImportRunResult } from './types'

function result(rows: ImportRunResult['rows']): ImportRunResult {
  return { dryRun: true, total: rows.length, succeeded: rows.filter((r) => r.ok).length, failed: rows.filter((r) => !r.ok).length, rows }
}

describe('buildRowStates', () => {
  it('没有校验结果时每行都是未校验', () => {
    const states = buildRowStates(null, 3, {})
    expect(states).toHaveLength(3)
    expect(states.every((state) => state.status === 'unknown')).toBe(true)
  })

  it('文件行号 2 对应该表第 0 行', () => {
    const states = buildRowStates(result([
      { rowNumber: 2, ok: true, code: null, message: null, fieldErrors: null },
      { rowNumber: 3, ok: false, code: 'VALIDATION_FAILED', message: '数据校验未通过。', fieldErrors: [{ field: 'CLIENT_ID', message: '不能为空。', code: 'REQUIRED_FIELD_MISSING' }] },
    ]), 2, { 0: 'CLIENT_ID' })

    expect(states[0].status).toBe('ok')
    expect(states[1].status).toBe('failed')
    expect(states[1].cellIssues[0]).toBe('不能为空。')
  })

  it('字段错误贴到映射到该字段的源列上（忽略大小写）', () => {
    const states = buildRowStates(result([
      { rowNumber: 2, ok: false, code: 'VALIDATION_FAILED', message: '数据校验未通过。', fieldErrors: [{ field: 'client_name', message: '该字段不能为空。', code: 'REQUIRED_FIELD_MISSING' }] },
    ]), 1, { 0: 'CLIENT_ID', 1: 'CLIENT_NAME' })

    expect(states[0].cellIssues[1]).toBe('该字段不能为空。')
    expect(states[0].cellIssues[0]).toBeUndefined()
  })

  it('对不上任何源列的字段错误落到整行原因里，不会丢', () => {
    const states = buildRowStates(result([
      { rowNumber: 2, ok: false, code: 'RECORD_OUT_OF_MODULE_FILTER', message: '新建记录不满足模块过滤条件。', fieldErrors: [{ field: 'CONFIRM_TAG', message: '不在范围内。', code: 'X' }] },
    ]), 1, { 0: 'CLIENT_ID' })

    expect(states[0].rowIssues).toEqual(['CONFIRM_TAG：不在范围内。'])
    expect(states[0].cellIssues).toEqual({})
  })

  it('超出数据范围的判定行被忽略（不越界、不串行）', () => {
    const states = buildRowStates(result([
      { rowNumber: 99, ok: true, code: null, message: null, fieldErrors: null },
    ]), 2, {})
    expect(states.every((state) => state.status === 'unknown')).toBe(true)
  })
})

describe('countStatuses / filterIndexes', () => {
  const states = buildRowStates(result([
    { rowNumber: 2, ok: true, code: null, message: null, fieldErrors: null },
    { rowNumber: 3, ok: false, code: 'X', message: 'y', fieldErrors: null },
  ]), 3, {})

  it('计数把未校验、通过、失败分开', () => {
    expect(countStatuses(states)).toEqual({ unknown: 1, ok: 1, failed: 1 })
  })

  it('按状态过滤返回的是行下标（不是行号）', () => {
    expect(filterIndexes(states, 'all')).toEqual([0, 1, 2])
    expect(filterIndexes(states, 'ok')).toEqual([0])
    expect(filterIndexes(states, 'failed')).toEqual([1])
  })
})

describe('workSignature', () => {
  it('映射一变签名就变（旧校验结论随之作废）', () => {
    const before = workSignature(['A', 'B'], { 0: 'F1', 1: 'F2' }, 2)
    expect(workSignature(['A', 'B'], { 0: 'F1', 1: 'F3' }, 2)).not.toBe(before)
  })

  it('列名、列序或行数变化同样使签名变化', () => {
    const before = workSignature(['A', 'B'], { 0: 'F1', 1: 'F2' }, 2)
    expect(workSignature(['A', 'C'], { 0: 'F1', 1: 'F2' }, 2)).not.toBe(before)
    expect(workSignature(['B', 'A'], { 0: 'F1', 1: 'F2' }, 2)).not.toBe(before)
    expect(workSignature(['A', 'B'], { 0: 'F1', 1: 'F2' }, 3)).not.toBe(before)
  })

  it('完全没变时签名稳定', () => {
    expect(workSignature(['A', 'B'], { 0: 'F1', 1: 'F2' }, 2))
      .toBe(workSignature(['A', 'B'], { 0: 'F1', 1: 'F2' }, 2))
  })
})
