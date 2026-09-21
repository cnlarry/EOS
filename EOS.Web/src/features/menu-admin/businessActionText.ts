/**
 * 2301「行为动作」配置的可读化渲染：
 * 把落库的存储形态（公式行 + 结构化 JSON）渲染成「加工单」人话行
 * （`目标表.字段 运算 本单来源 @定位键 ?条件`），供列表摘要、加工单视图与编辑弹窗的实时预览使用。
 * 本模块只做展示，不改变任何落库结构。
 */

/** 运算符号（与 OP_CODE 闭式集一一对应）。 */
export const OP_SYMBOLS: Record<string, string> = {
  ACCUM: '+=',
  DEACCUM: '-=',
  ASSIGN: '=',
  ASSIGN_MAX: '=MAX()',
  ASSIGN_MIN: '=MIN()',
  APPEND: '+=',
  APPEND_UNIQ: '+=UNIQ()',
  SET_WHEN: ':=',
}

const SCOPE_WORDS: Record<string, string> = {
  MASTER: '本单主表',
  DETAIL: '本单明细',
  TABLE: '上下文表',
  CONSTANT: '常量',
}

const AGG_WORDS: Record<string, string> = {
  SUM: '合计',
  MAX: '最大',
  MIN: '最小',
  DISTINCT: '去重',
  PICK: '唯一行',
}

const COMPARE_SYMBOLS: Record<string, string> = {
  EQ: '=',
  NEQ: '<>',
  GT: '>',
  GE: '>=',
  LT: '<',
  LE: '<=',
}

export interface MatchItem {
  target: string
  source: { scope?: string | null; field?: string | null; table?: string | null }
}

export interface SourceTerm {
  field: string
  coef?: number
}

/** 表/字段显示名查询器：有中文元数据时显示「中文名(标识符)」，否则退回标识符本身。 */
export interface BusinessNameLookup {
  /** 字段显示名（`中文名(列名)`）。 */
  field: (table: string | null | undefined, column: string | null | undefined) => string
  /** 表显示名（`中文描述(表名)`）。 */
  table: (table: string | null | undefined) => string
  /** 把来源范围解析成实际表名（MASTER/DETAIL 取本单主/副表，TABLE 取显式上下文表）。 */
  scopeTable: (scope: string | null | undefined, explicitTable: string | null | undefined) => string | null
}

/** 无元数据时的默认显示（纯标识符）。 */
export const plainNames: BusinessNameLookup = {
  field: (_table, column) => (column == null || column === '' ? '?' : column),
  table: (table) => (table == null || table === '' ? '?' : table),
  scopeTable: (_scope, explicitTable) => (explicitTable == null || explicitTable === '' ? null : explicitTable),
}

/**
 * 派生一个把 TARGET 域解析到指定目标表的查询器：
 * 条件里的 `TARGET` 指的是"正在被更新的那一行"，只有调用点知道它属于哪张表。
 */
export function withTargetTable(names: BusinessNameLookup, targetTable: string | null | undefined): BusinessNameLookup {
  const table = (targetTable ?? '').trim()
  if (table === '') return names
  return {
    field: names.field,
    table: names.table,
    scopeTable: (scope, explicitTable) =>
      (scope ?? '').trim().toUpperCase() === 'TARGET' ? table : names.scopeTable(scope, explicitTable),
  }
}

/**
 * 由服务端下发的表/字段中文名与模块主/副表构造显示查询器
 * （键：表名 / `表名.列名`，大小写不敏感；缺元数据的键回落显示标识符）。
 */
export function makeNameLookup(
  labels?: { tables?: Record<string, string>; fields?: Record<string, string> } | null,
  masterTable?: string | null,
  detailTable?: string | null,
): BusinessNameLookup {
  const tables = new Map<string, string>()
  for (const [key, value] of Object.entries(labels?.tables ?? {})) tables.set(key.toUpperCase(), value)
  const fields = new Map<string, string>()
  for (const [key, value] of Object.entries(labels?.fields ?? {})) fields.set(key.toUpperCase(), value)
  const text = (value: string | undefined, id: string) => (value && value !== '' ? `${value}(${id})` : id)
  const scopeTable = (scope: string | null | undefined, explicitTable: string | null | undefined) => {
    const key = (scope ?? '').trim().toUpperCase()
    if (key === 'MASTER') return masterTable ?? null
    if (key === 'DETAIL') return detailTable ?? null
    if (key === 'TABLE') return explicitTable ?? null
    return explicitTable ?? null
  }
  return {
    field: (table, column) => {
      const columnId = (column ?? '').trim()
      if (columnId === '') return '?'
      const tableId = (table ?? '').trim().toUpperCase()
      return text(fields.get(`${tableId}.${columnId.toUpperCase()}`), columnId)
    },
    table: (table) => {
      const tableId = (table ?? '').trim()
      if (tableId === '') return '?'
      return text(tables.get(tableId.toUpperCase()), tableId)
    },
    scopeTable,
  }
}

/** 安全解析 JSON 对象；失败返回 null（编辑中的半成品文本不算错误）。 */
export function parseJsonObject(text: string | null | undefined): Record<string, unknown> | null {
  if (!text || text.trim() === '') return null
  try {
    const value = JSON.parse(text)
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null
  } catch {
    return null
  }
}

export function parseJsonArray(text: string | null | undefined): unknown[] | null {
  if (!text || text.trim() === '') return null
  try {
    const value = JSON.parse(text)
    return Array.isArray(value) ? value : null
  } catch {
    return null
  }
}

/** 定位键 JSON → 键项数组；结构非法时返回 null。 */
export function parseMatchItems(text: string | null | undefined): MatchItem[] | null {
  const raw = parseJsonArray(text)
  if (!raw) return null
  const items: MatchItem[] = []
  for (const entry of raw) {
    if (!entry || typeof entry !== 'object') return null
    const item = entry as { target?: unknown; source?: unknown }
    if (typeof item.target !== 'string' || !item.source || typeof item.source !== 'object') return null
    const source = item.source as { scope?: unknown; field?: unknown; table?: unknown }
    items.push({
      target: item.target,
      source: {
        scope: typeof source.scope === 'string' ? source.scope : null,
        field: typeof source.field === 'string' ? source.field : null,
        table: typeof source.table === 'string' ? source.table : null,
      },
    })
  }
  return items
}

/** 键项数组 → 定位键 JSON（空数组返回 null，保持"无定位键"= NULL 的落库口径）。 */
export function serializeMatchItems(items: MatchItem[]): string | null {
  const cleaned = items
    .filter((item) => item.target.trim() !== '')
    .map((item) => {
      const source: Record<string, string> = {}
      if (item.source.scope) source.scope = item.source.scope
      if (item.source.field) source.field = item.source.field
      if (item.source.table) source.table = item.source.table
      return { target: item.target.trim(), source }
    })
  return cleaned.length > 0 ? JSON.stringify(cleaned) : null
}

/** 已登记关系边组 → 定位键项数组（来源范围按"来源表是主表/副表/上下文表"判定）。 */
export function matchItemsFromRelation(
  keys: { fromTable: string; fromColumn: string; toColumn: string }[],
  masterTable: string | null | undefined,
  detailTable: string | null | undefined,
): MatchItem[] {
  return keys.map((key) => {
    const fromTable = key.fromTable.trim()
    const scope = sameTable(fromTable, masterTable)
      ? 'MASTER'
      : sameTable(fromTable, detailTable)
        ? 'DETAIL'
        : 'TABLE'
    return {
      target: key.toColumn,
      source: scope === 'TABLE'
        ? { scope, field: key.fromColumn, table: fromTable }
        : { scope, field: key.fromColumn },
    }
  })
}

function sameTable(left: string, right: string | null | undefined): boolean {
  return right != null && left.toUpperCase() === right.trim().toUpperCase()
}

function scopeWord(scope: string | null | undefined, resolvedTable: string | null, names: BusinessNameLookup): string {
  const key = (scope ?? '').trim().toUpperCase()
  if (key === 'TABLE') return `上下文表 ${names.table(resolvedTable)}`
  return SCOPE_WORDS[key] ?? key ?? '?'
}

/** 源加减项 JSON → `QTY + SPARE_QTY − FINISHED_SEND_QTY`。 */
export function formatSourceTerms(text: string | null | undefined): string | null {
  const raw = parseJsonArray(text)
  if (!raw || raw.length === 0) return null
  const parts: string[] = []
  for (const entry of raw) {
    if (!entry || typeof entry !== 'object') return null
    const term = entry as { field?: unknown; coef?: unknown }
    if (typeof term.field !== 'string') return null
    const coef = typeof term.coef === 'number' ? term.coef : 1
    const sign = coef < 0 ? '−' : '+'
    parts.push(parts.length === 0
      ? (coef < 0 ? `−${term.field}` : term.field)
      : `${sign} ${term.field}`)
  }
  return parts.join(' ')
}

/** 公式行的来源描述（人话）。 */
export function formatOpSource(op: {
  sourceScope?: string | null
  sourceTable?: string | null
  sourceField?: string | null
  sourceAgg?: string | null
  sourceConstant?: string | null
  sourceTerms?: string | null
}, names: BusinessNameLookup = plainNames): string {
  const scope = (op.sourceScope ?? '').trim().toUpperCase()
  if (scope === 'CONSTANT') {
    return `常量 ${op.sourceConstant == null || op.sourceConstant === '' ? '(空)' : `'${op.sourceConstant}'`}`
  }
  const table = names.scopeTable(scope, op.sourceTable)
  const word = scopeWord(scope, table, names)
  const terms = formatSourceTerms(op.sourceTerms)
  if (terms) return `${word}(${terms})`
  if (!op.sourceField) return `${word}(?)`
  const agg = op.sourceAgg ? AGG_WORDS[op.sourceAgg.trim().toUpperCase()] : null
  const field = names.field(table, op.sourceField)
  return agg ? `${word}.${field}（${agg}）` : `${word}.${field}`
}

/** 公式行 → `目标表.字段 += 来源`（加工单句式）。 */
export function formatOpSentence(op: {
  targetTable?: string | null
  targetField?: string | null
  opCode?: string | null
  sourceScope?: string | null
  sourceTable?: string | null
  sourceField?: string | null
  sourceAgg?: string | null
  sourceConstant?: string | null
  sourceTerms?: string | null
}, names: BusinessNameLookup = plainNames): string {
  const target = `${names.table(op.targetTable)}.${names.field(op.targetTable, op.targetField)}`
  const symbol = OP_SYMBOLS[(op.opCode ?? '').trim().toUpperCase()] ?? (op.opCode ?? '?')
  return `${target} ${symbol} ${formatOpSource(op, names)}`
}

/** 条件 JSON → 人话摘要；结构不认识时回落到压缩 JSON。 */
export function formatCondition(text: string | null | undefined, names: BusinessNameLookup = plainNames): string | null {
  if (!text || text.trim() === '') return null
  const parsed = parseJsonObject(text)
  if (!parsed) return text.trim()
  return describeCondition(parsed, names)
}

function describeCondition(node: Record<string, unknown>, names: BusinessNameLookup): string {
  const type = typeof node.type === 'string' ? node.type : null
  if (!type && Array.isArray(node.items)) {
    const logic = typeof node.logic === 'string' ? node.logic : 'AND'
    const parts = (node.items as unknown[])
      .map((item) => (item && typeof item === 'object' ? describeCondition(item as Record<string, unknown>, names) : '?'))
    return parts.join(logic === 'OR' ? ' 或 ' : ' 且 ')
  }
  switch (type) {
    case 'field-compare': {
      const op = COMPARE_SYMBOLS[String(node.op ?? '').toUpperCase()] ?? String(node.op ?? '?')
      return `${describeOperand(node.left, names)} ${op} ${describeOperand(node.right, names)}`
    }
    case 'value-eq':
    case 'value-neq': {
      const op = type === 'value-eq' ? '=' : '<>'
      return `${describeOperand(node.field, names)} ${op} ${String(node.value ?? '')}`
    }
    case 'not-exists':
      return `${node.negate === true ? '存在' : '不存在'}：${String(node.targetTable ?? '?')}（${describeCondition(
        (node.condition ?? {}) as Record<string, unknown>,
        names,
      )}）`
    case 'switch':
      return `系统开关 ${String(node.key ?? '?')} = ${String(node.value ?? '1')}`
    default:
      return JSON.stringify(node)
  }
}

function describeOperand(value: unknown, names: BusinessNameLookup): string {
  if (value == null || typeof value !== 'object') return String(value ?? '?')
  const operand = value as { scope?: unknown; field?: unknown; value?: unknown }
  if (operand.value !== undefined) return String(operand.value)
  if (typeof operand.field === 'string') {
    const scope = typeof operand.scope === 'string' ? operand.scope.toUpperCase() : ''
    const table = scope === 'TARGET' ? names.scopeTable('TARGET', null) : names.scopeTable(scope, null)
    const word = scope === 'TARGET' ? '目标行' : scopeWord(scope, table, names)
    return `${word}.${names.field(table, operand.field)}`
  }
  return JSON.stringify(value)
}

/** 定位键 JSON → `@目标列 ← 本单明细.来源列、…`。 */
export function formatMatch(
  text: string | null | undefined,
  names: BusinessNameLookup = plainNames,
): string | null {
  const items = parseMatchItems(text)
  if (!items || items.length === 0) return null
  return `@${items
    .map((item) => {
      const table = names.scopeTable(item.source.scope, item.source.table)
      return `${names.field(null, item.target)} ← ${scopeWord(item.source.scope, table, names)}.${names.field(table, item.source.field)}`
    })
    .join('、')}`
}

/** 摘要渲染所需的最小公式行形态（与 BusinessActionOp 结构兼容）。 */
export interface SummarizableOp {
  opSeq: number
  targetTable?: string | null
  targetField?: string | null
  opCode?: string | null
  sourceScope?: string | null
  sourceTable?: string | null
  sourceField?: string | null
  sourceAgg?: string | null
  sourceConstant?: string | null
  sourceTerms?: string | null
}

/** 动作摘要：名称之外的字段级事实（服务型效果说明为参数型）。 */
export function summarizeAction(action: {
  params?: string | null
  condition?: string | null
  ops?: SummarizableOp[] | null
}, names: BusinessNameLookup = plainNames): string {
  const ops = [...(action.ops ?? [])].sort((a, b) => a.opSeq - b.opSeq)
  const condition = formatCondition(action.condition, names)
  if (ops.length === 0) {
    const params = parseJsonObject(action.params)
    const keys = params ? Object.keys(params) : []
    const base = keys.length > 0 ? `参数型：${keys.join('、')}` : '无公式行（参数型 / 无参数）'
    return condition ? `${base}；当 ${condition}` : base
  }
  const sentences = ops.map((op) => formatOpSentence(op, names))
  const shown = sentences.slice(0, 3).join('；')
  const more = sentences.length > 3 ? `；等 ${sentences.length} 步` : ''
  return condition ? `${shown}${more}；当 ${condition}` : `${shown}${more}`
}

/** 目录值 → 中文标签查询器（大小写不敏感；服务端未给标签时回落目录码本身）。 */
export function makeLabelLookup(labels?: Record<string, string> | null): (code: string | null | undefined) => string {
  const map = new Map<string, string>()
  for (const [key, text] of Object.entries(labels ?? {})) map.set(key.toUpperCase(), text)
  return (code) => {
    if (code == null || code === '') return ''
    return map.get(code.toUpperCase()) ?? code
  }
}

/** 列表/下拉统一的「中文（目录码）」显示形态（无标签时只显示目录码）。 */
export function labelWithCode(lookup: (code: string | null | undefined) => string, code: string | null | undefined): string {
  if (code == null || code === '') return ''
  const text = lookup(code)
  return text === code ? code : `${text}（${code}）`
}

/** 已用过的定位键组（按 JSON 去重；用于一处配置、多处复用）。 */
export interface MatchGroupPreset {
  json: string
  targetTable: string
  count: number
  labels: string[]
}

/** 反向结构 JSON → 反向语义中文名（缺 kind 时视为无反向配置；目录码只作目录值本身保留在配置里）。 */
export function reverseTextOf(
  json: string | null | undefined,
  lookup: (code: string | null | undefined) => string,
): string {
  const parsed = parseJsonObject(json)
  if (!parsed || typeof parsed.kind !== 'string') return '未配置'
  const text = lookup(parsed.kind)
  const note = typeof parsed.note === 'string' && parsed.note.trim() !== '' ? `（${parsed.note.trim()}）` : ''
  return `${text}${note}`
}
