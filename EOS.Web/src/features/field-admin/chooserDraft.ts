/** 数据源过滤/回填构建器草稿类型与序列化（FieldEditorForm / DataSourceEditorModal 共用）。 */

/** 过滤条件构建器行：简单比较行可编辑；复杂 item（表达式/子查询/嵌套组）以 raw JSON 只读兜底。 */
export interface FilterRowDraft {
  key: string
  field: string
  operator: string
  value: string
  logic: 'AND' | 'OR' | null
  raw?: string
}

/** 回填映射构建器行（来源列 ↔ 目标字段配对）。 */
export interface ReturnRowDraft {
  key: string
  column: string
  target: string
}

export const FILTER_OPERATORS = ['EQ', 'NE', 'GT', 'GE', 'LT', 'LE', 'LIKE', 'NOT_LIKE', 'ISNULL_ZERO', 'DAYS_FROM_TODAY', 'IS_NULL', 'IS_NOT_NULL']

function isSimpleFilterItem(item: unknown): boolean {
  if (!item || typeof item !== 'object') return false
  const record = item as Record<string, unknown>
  return typeof record.field === 'string' && typeof record.operator === 'string'
    && record.left == null && record.right == null && record.group == null
    && record.subquery == null && record.negate == null
}

export function parseFilterRows(json: string | null): FilterRowDraft[] {
  if (!json) return []
  try {
    const parsed: unknown = JSON.parse(json)
    if (!parsed || typeof parsed !== 'object') return []
    const struct = parsed as { logic?: unknown; items?: unknown }
    if (struct.logic !== 'AND' && struct.logic !== 'OR') return []
    if (!Array.isArray(struct.items)) return []
    const logic = struct.logic as 'AND' | 'OR'
    return struct.items.map((item: unknown, index: number) => {
      if (!isSimpleFilterItem(item)) {
        return { key: `r${index}`, field: '', operator: '', value: '', logic: index === 0 ? null : logic, raw: JSON.stringify(item) }
      }
      const record = item as { field?: unknown; operator?: unknown; value?: unknown; nullSafe?: unknown }
      return {
        key: `r${index}`,
        field: String(record.field ?? ''),
        operator: String(record.operator ?? ''),
        value: record.value == null ? '' : String(record.value),
        logic: index === 0 ? null : logic,
        raw: record.nullSafe != null ? JSON.stringify(item) : undefined,
      }
    })
  } catch {
    return []
  }
}

export function serializeFilterRows(rows: FilterRowDraft[]): string {
  const items: unknown[] = []
  for (const row of rows) {
    if (row.raw) {
      try {
        items.push(JSON.parse(row.raw))
      } catch {
        // raw JSON 损坏则丢弃该行（保存时由校验器兜底）
      }
      continue
    }
    if (!row.field.trim() || !row.operator) continue
    // 简单行仅输出有值键（null 键省略），保持 FILTER_STRUCT 紧凑、便于结构识别
    const item: Record<string, unknown> = { field: row.field.trim(), operator: row.operator }
    if (row.value !== '') item.value = row.value
    items.push(item)
  }
  const logic = rows.find(row => row.logic)?.logic ?? 'AND'
  return JSON.stringify({ logic, items })
}

export function parseReturnRows(json: string | null): ReturnRowDraft[] {
  if (!json) return []
  try {
    const parsed: unknown = JSON.parse(json)
    if (!Array.isArray(parsed)) return []
    return parsed.map((item: unknown, index: number) => {
      const record = (item ?? {}) as { column?: unknown; target?: unknown }
      return { key: `m${index}`, column: String(record.column ?? ''), target: String(record.target ?? '') }
    })
  } catch {
    return []
  }
}

export function serializeReturnRows(rows: ReturnRowDraft[]): string {
  const items = rows
    .filter(row => row.column.trim() && row.target.trim())
    .map(row => ({ target: row.target.trim(), column: row.column.trim() }))
  return items.length > 0 ? JSON.stringify(items) : ''
}

/** 回填字段类型兼容（来源列 vs 目标字段）：完全同名或同族互通，避免保存类型异常。 */
export function isTypeCompatible(sourceType: string, targetType: string): boolean {
  const normalize = (type: string) => type.trim().toLowerCase()
  const s = normalize(sourceType)
  const t = normalize(targetType)
  if (s === t) return true
  const charFamily = new Set(['char', 'varchar', 'nchar', 'nvarchar', 'text', 'ntext', 'idcard', 'url', 'email', 'phoneno', 'zipcode', 'string'])
  const numericFamily = new Set(['int', 'bigint', 'smallint', 'tinyint', 'decimal', 'numeric', 'float', 'real', 'money', 'smallmoney', 'integer'])
  const dateFamily = new Set(['date', 'datetime', 'datetime2', 'datetimeoffset', 'smalldatetime', 'time'])
  const binaryFamily = new Set(['binary', 'varbinary', 'image'])
  if (charFamily.has(s) && charFamily.has(t)) return true
  if (numericFamily.has(s) && numericFamily.has(t)) return true
  if (dateFamily.has(s) && dateFamily.has(t)) return true
  if (binaryFamily.has(s) && binaryFamily.has(t)) return true
  return false
}
