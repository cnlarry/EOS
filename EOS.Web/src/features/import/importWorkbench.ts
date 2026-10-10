import type { ImportRunResult } from './types'

/**
 * 逐行状态：把服务端的逐行判定**贴回数据行**。
 *
 * 这是这一版设计的核心——原来"预演"是一份独立报告，人要拿着行号回文件里找；
 * 现在判定成为网格里的状态列与单元格标红，判断和对象在同一屏，不用在自己脑子里 join 两张表。
 *
 * `unknown` = 还没校验（改过映射或换了文件之后，旧结论一律作废）。
 */
export type RowStatus = 'unknown' | 'ok' | 'failed'

export interface RowState {
  status: RowStatus
  /** 整行级错误码（稳定标识，与保存路径同源） */
  code: string | null
  message: string | null
  /** 源列下标 → 该格的失败原因（把错误标在具体单元格上） */
  cellIssues: Record<number, string>
  /** 落不到具体列的失败原因（整行级） */
  rowIssues: string[]
}

export interface StatusCounts {
  unknown: number
  ok: number
  failed: number
}

export type StatusFilter = 'all' | 'ok' | 'failed'

const EMPTY_STATE: RowState = { status: 'unknown', code: null, message: null, cellIssues: {}, rowIssues: [] }

/**
 * 按行构造状态。行号口径与写路径一致：**文件行号 = 数据行下标 + 2**（第 1 行是表头）。
 */
export function buildRowStates(
  preview: ImportRunResult | null,
  rowCount: number,
  mapping: Record<number, string>,
): RowState[] {
  const states: RowState[] = Array.from({ length: rowCount }, () => ({ ...EMPTY_STATE, cellIssues: {}, rowIssues: [] }))
  if (preview == null) return states

  // 目标字段键 → 源列下标：同名多列时取首个映射到它的列
  const columnOfField = new Map<string, number>()
  for (const [rawIndex, rawField] of Object.entries(mapping)) {
    const field = (rawField ?? '').trim().toLowerCase()
    const index = Number(rawIndex)
    if (field.length > 0 && Number.isFinite(index) && !columnOfField.has(field)) columnOfField.set(field, index)
  }

  for (const outcome of preview.rows) {
    const index = outcome.rowNumber - 2
    if (index < 0 || index >= states.length) continue
    if (outcome.ok) {
      states[index] = { status: 'ok', code: null, message: null, cellIssues: {}, rowIssues: [] }
      continue
    }
    const cellIssues: Record<number, string> = {}
    const rowIssues: string[] = []
    for (const issue of outcome.fieldErrors ?? []) {
      const column = columnOfField.get(issue.field.trim().toLowerCase())
      if (column == null) rowIssues.push(`${issue.field}：${issue.message}`)
      else cellIssues[column] = issue.message
    }
    states[index] = {
      status: 'failed',
      code: outcome.code,
      message: outcome.message,
      cellIssues,
      rowIssues,
    }
  }
  return states
}

export function countStatuses(states: RowState[]): StatusCounts {
  const counts: StatusCounts = { unknown: 0, ok: 0, failed: 0 }
  for (const state of states) counts[state.status] += 1
  return counts
}

export function filterIndexes(states: RowState[], filter: StatusFilter): number[] {
  const indexes: number[] = []
  for (let index = 0; index < states.length; index += 1) {
    if (filter === 'all' || states[index].status === filter) indexes.push(index)
  }
  return indexes
}

/**
 * 校验结论的有效性签名：**映射、列名或行数一变，上一次的结论就不再成立**。
 *
 * 没有这条，用户改完映射还能拿着旧结论点"提交"——那是拿过期判定去写生产库。
 */
export function workSignature(columns: string[], mapping: Record<number, string>, rowCount: number): string {
  const pairs = columns.map((column, index) => `${column}\u0001${mapping[index] ?? ''}`)
  return `${rowCount}\u0000${pairs.join('\u0002')}`
}
