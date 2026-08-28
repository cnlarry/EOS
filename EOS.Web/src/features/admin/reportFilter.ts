import type { QueryCondition } from '../../components/common/queryCondition'

const FILTER_OPS: Record<string, string> = { eq: '=', ne: '<>', gt: '>', gte: '>=', lt: '<', lte: '<=' }

/**
 * 把结构化查询条件序列化为 REPORT_FILTER 文本（DataFilterParser 可解析语法子集）：
 * 比较 =/<>/></>=/<=、LIKE '模式'、ISNULL(列,'') 空值判断、区间、NOT (...)。
 * 值一律单引号包覆（解析器参数化），单引号按 SQL 双写转义。
 */
export function serializeReportFilter(conditions: QueryCondition[]): string {
  if (conditions.length === 0) return ''
  const esc = (value: string) => String(value ?? '').replace(/'/g, "''")
  const quote = (value: string) => `'${esc(value)}'`
  const parts = conditions
    .map((condition) => {
      const field = condition.field.trim()
      if (!field) return null
      const op = FILTER_OPS[condition.operator]
      if (op) return `${field}${op}${quote(condition.value)}`
      switch (condition.operator) {
        case 'contains': return `${field} LIKE '%${esc(condition.value)}%'`
        case 'notcontains': return `NOT (${field} LIKE '%${esc(condition.value)}%')`
        case 'startswith': return `${field} LIKE '${esc(condition.value)}%'`
        case 'endswith': return `${field} LIKE '%${esc(condition.value)}'`
        case 'empty': return `ISNULL(${field},'')=''`
        case 'notempty': return `ISNULL(${field},'')<>''`
        case 'between': return `(${field}>=${quote(condition.value)} AND ${field}<=${quote(condition.valueTo)})`
        default: return null
      }
    })
    .filter((part): part is string => Boolean(part))
  if (parts.length === 0) return ''
  return parts
    .map((part, index) => index === 0 ? part : `${(conditions[index]?.logic ?? 'and').toUpperCase()} ${part}`)
    .join(' ')
}
