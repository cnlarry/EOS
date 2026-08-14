/** 过滤条件行：字段 + 运算符 + 逻辑连接 + 比较值。 */
export interface FilterRow {
  field: string
  op: string
  logic: 'AND' | 'OR'
  value: string
}

export const FILTER_OPS = ['=', '<>', '>', '<', '>=', '<=']

const FILTER_PART = /^\s*\(?\s*([A-Za-z_][A-Za-z0-9_]*)\s*(=|<>|>=|<=|>|<)\s*(N?'(?:[^']|'')*'|[+-]?\d+(?:\.\d+)?)\s*\)?\s*$/

/**
 * 将主表过滤条件文本解析为条件行（构建器可编辑的简单子集）。
 * 空文本返回 []；含 ISNULL、getdate()、列运算等高级写法时返回 null（构建器无法表达）。
 */
export function parseFilter(value: string): FilterRow[] | null {
  if (!value.trim()) return []
  const parts = value.split(/\s+(AND|OR)\s+/i)
  const logics = value.match(/\s+(AND|OR)\s+/gi) ?? []
  const rows: FilterRow[] = []
  for (let i = 0; i < parts.length; i++) {
    const match = parts[i].trim().match(FILTER_PART)
    if (!match) return null
    const raw = match[3]
    rows.push({
      field: match[1],
      op: match[2],
      value: raw.startsWith("'") ? raw.replace(/^N?'|'$/g, '').replace(/''/g, "'") : raw,
      logic: i === 0 ? 'AND' : ((logics[i - 1] ?? 'AND').trim().toUpperCase() as 'AND' | 'OR'),
    })
  }
  return rows
}

export function quoteValue(value: string): string {
  return /^[+-]?\d+(\.\d+)?$/.test(value) ? value : `'${value.replace(/'/g, "''")}'`
}

/** 构建器行级判断所需的字段元数据最小结构（与 MenuFieldOption 结构兼容）。 */
export interface MenuFieldLike {
  F_ID: string
  F_DESC: string
  F_TYPE: string
}

const NUMERIC_TYPE_RE = /^(?:int|bigint|smallint|tinyint|decimal|numeric|float|real|money|smallmoney)/i
const BIT_TYPE_RE = /^bit$/i
const DATE_TYPE_RE = /^(?:datetime|datetime2|smalldatetime|date)/i
const NUMBER_VALUE_RE = /^[+-]?\d+(?:\.\d+)?$/
const BOOL_VALUE_RE = /^(?:0|1|true|false)$/i

export interface FilterRowIssue {
  kind: 'error' | 'warning'
  message: string
}

/**
 * 对单行过滤条件做基本判断：
 * - 字段必选且必须存在于当前表字段列表（阻断）；
 * - 数值/布尔字段要求值格式匹配（阻断），日期字段给出格式提示（不阻断）；
 * - `=`/`<>` 且比较值为空视为「匹配空字符串」，给出提示而非报错。
 */
export function rowIssues(row: FilterRow, fieldMeta: MenuFieldLike | undefined): FilterRowIssue[] {
  const issues: FilterRowIssue[] = []
  if (!row.field) {
    issues.push({ kind: 'error', message: '请选择字段' })
    return issues
  }
  if (!fieldMeta) {
    issues.push({ kind: 'error', message: `字段「${row.field}」不在当前表的字段列表中` })
    return issues
  }
  const value = row.value.trim()
  if (!value) {
    if (row.op === '>' || row.op === '<' || row.op === '>=' || row.op === '<=') {
      issues.push({ kind: 'error', message: '该运算符需要填写比较值' })
    } else {
      issues.push({ kind: 'warning', message: '比较值为空时将匹配空字符串' })
    }
    return issues
  }
  const dataType = fieldMeta.F_TYPE ?? ''
  if (NUMERIC_TYPE_RE.test(dataType) && !NUMBER_VALUE_RE.test(value)) {
    issues.push({ kind: 'error', message: `字段「${fieldMeta.F_DESC}」为数值类型，比较值应为数字` })
  }
  if (BIT_TYPE_RE.test(dataType) && !BOOL_VALUE_RE.test(value)) {
    issues.push({ kind: 'error', message: `字段「${fieldMeta.F_DESC}」为布尔类型，比较值应为 0/1 或 true/false` })
  }
  if (DATE_TYPE_RE.test(dataType) && Number.isNaN(Date.parse(value))) {
    issues.push({ kind: 'warning', message: `字段「${fieldMeta.F_DESC}」为日期类型，建议使用 yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss 格式` })
  }
  return issues
}
