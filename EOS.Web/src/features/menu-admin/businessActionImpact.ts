import { parseJsonObject, parseMatchItems } from './businessActionText'

/**
 * 保存前的影响面与自检：把当前草稿（动作链 + 校验规则）汇总成"会写哪些表哪些列"，
 * 并按**与服务端保存期校验同一口径**列出会拦住保存的硬伤（跨表无定位键写入、定位键未登记、
 * 系统开关键未登记、顺序号重复、结构化 JSON 非法）。
 *
 * 边界说清楚：这是**静态影响面**，不是真库试算——它不连库、不改数据，
 * 只回答"这批配置一旦生效会动到哪些表列、有没有必被拒的配置"。
 */

export interface ImpactOp {
  opSeq: number
  targetTable: string
  targetField: string
  opCode: string
  match?: string | null
  condition?: string | null
}

export interface ImpactAction {
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  condition?: string | null
  params?: string | null
  ops?: ImpactOp[] | null
}

export interface ImpactRule {
  stage: string
  seq: number
  validationKey: string
  enabled: boolean
  params?: string | null
}

/** 已登记的效果关系边的一段键映射。 */
export interface ImpactMatchEdge {
  fromTable: string
  fromColumn: string
  toTable: string
  toColumn: string
}

/**
 * 同链顺序依赖（与保存期顺序 lint 同一份口径）：
 * 完成判定/库存移动依赖同链先有量额回写——只有链里确实存在该前置效果、却排在后面时才判违规；
 * 纯库存/纯状态链（链内没有量额回写）是合法的。
 */
const CHAIN_PREREQUISITES: Record<string, string[]> = {
  'completion-close': ['field-accumulate'],
  'inventory-move': ['field-accumulate'],
}

/** 与服务端一致的规模上限。 */
const MAX_ACTIONS = 300
const MAX_RULES = 100

export interface ImpactOptions {
  masterTable?: string | null
  detailTable?: string | null
  /** 目标表 → 已登记关系边组；空数组表示该表未参与关系目录（按服务端口径跳过定位键校验）。 */
  relationsByTable?: Record<string, ImpactMatchEdge[][]>
  /** 已登记系统参数键；null/undefined 表示目录不可读（跳过该项检查，不误报）。 */
  knownSwitchKeys?: string[] | null
}

export interface ImpactIssue {
  severity: 'block' | 'warn'
  message: string
}

export interface ImpactTable {
  table: string
  columns: string[]
  actionCount: number
}

export interface ImpactReport {
  actionCount: number
  enabledActionCount: number
  stepCount: number
  ruleCount: number
  eventCounts: { eventCode: string; count: number }[]
  stageCounts: { stage: string; count: number }[]
  tables: ImpactTable[]
  switchKeys: string[]
  issues: ImpactIssue[]
}

export function analyzeImpact(
  actions: ImpactAction[],
  rules: ImpactRule[],
  options: ImpactOptions = {},
): ImpactReport {
  const issues: ImpactIssue[] = []
  const tables = new Map<string, { columns: Set<string>; actions: Set<string> }>()
  const eventCounts = new Map<string, number>()
  const stageCounts = new Map<string, number>()
  const switchKeys = new Set<string>()
  let stepCount = 0

  const seenSequences = new Map<string, number>()
  for (const action of actions) {
    const key = `${action.eventCode}|${action.seq}`
    seenSequences.set(key, (seenSequences.get(key) ?? 0) + 1)
    if (isBlankJson(action.condition) === false && parseJsonObject(action.condition) === null) {
      issues.push({ severity: 'block', message: `动作「${label(action)}」的条件不是合法 JSON，保存会被拒绝。` })
    }
    if (isBlankJson(action.params) === false && parseJsonObject(action.params) === null) {
      issues.push({ severity: 'block', message: `动作「${label(action)}」的参数不是合法 JSON，保存会被拒绝。` })
    }
    for (const key of collectSwitchKeys(action.condition)) switchKeys.add(key)

    const ops = action.ops ?? []
    if (ops.length === 0 && isBlankJson(action.params)) {
      issues.push({
        severity: 'warn',
        message: `动作「${label(action)}」既没有公式行也没有参数，发布校验可能拒绝。`,
      })
    }
    for (const op of [...ops].sort((a, b) => a.opSeq - b.opSeq)) {
      stepCount += 1
      const table = (op.targetTable ?? '').trim().toUpperCase()
      const column = (op.targetField ?? '').trim().toUpperCase()
      if (table === '' || column === '' || (op.opCode ?? '').trim() === '') {
        issues.push({
          severity: 'warn',
          message: `动作「${label(action)}」步骤 ${op.opSeq} 是空公式行（服务型效果的占位行），运行时会被跳过。`,
        })
      }
      if (isBlankJson(op.condition) === false && parseJsonObject(op.condition) === null) {
        issues.push({ severity: 'block', message: `动作「${label(action)}」步骤 ${op.opSeq} 的条件 JSON 非法。` })
      }
      for (const key of collectSwitchKeys(op.condition)) switchKeys.add(key)

      if (table !== '') {
        if (!tables.has(table)) tables.set(table, { columns: new Set(), actions: new Set() })
        const entry = tables.get(table)!
        if (column !== '') entry.columns.add(column)
        entry.actions.add(label(action))
      }

      // 跨表写入的兜底守卫：目标表不是本单主表、又既无定位键也无条件 ⇒ 服务端必拒（会更新整表）。
      const master = (options.masterTable ?? '').trim().toUpperCase()
      if (table !== '' && table !== master && isBlankJson(op.match) && isBlankJson(op.condition)) {
        issues.push({
          severity: 'block',
          message: `动作「${label(action)}」步骤 ${op.opSeq} 写入 ${table} 却既无定位键也无条件，保存会被拒绝（执行时会更新整表）。`,
        })
      }

      const matchIssue = checkMatch(action, op, options)
      if (matchIssue) issues.push(matchIssue)
    }
  }

  for (const [key, count] of seenSequences) {
    if (count > 1) {
      const [eventCode, seq] = key.split('|')
      issues.push({ severity: 'block', message: `事件 ${eventCode} 下顺序号 ${seq} 重复（${count} 条），保存会被拒绝。` })
    }
  }

  // 顺序依赖：同事件链内，前置效果若存在就必须排在依赖方之前。
  for (const action of actions) {
    const eventCode = (action.eventCode ?? '').trim().toUpperCase()
    const prerequisites = CHAIN_PREREQUISITES[action.effectKey]
    if (!prerequisites || eventCode === '') continue
    const chain = actions.filter((item) => (item.eventCode ?? '').trim().toUpperCase() === eventCode)
    for (const prerequisite of prerequisites) {
      const anywhere = chain.some((item) => item.effectKey === prerequisite)
      const before = chain.some((item) => item.seq < action.seq && item.effectKey === prerequisite)
      if (anywhere && !before) {
        issues.push({
          severity: 'block',
          message: `动作「${label(action)}」的效果 ${action.effectKey} 依赖同链前置效果 ${prerequisite}（顺序 lint：依赖效果须先于本动作执行）。`,
        })
      }
    }
  }

  if (actions.length > MAX_ACTIONS) {
    issues.push({ severity: 'block', message: `业务动作数量超过上限（${MAX_ACTIONS}），保存会被拒绝。` })
  }
  if (rules.length > MAX_RULES) {
    issues.push({ severity: 'block', message: `校验规则数量超过上限（${MAX_RULES}），保存会被拒绝。` })
  }

  for (const rule of rules) {
    stageCounts.set(rule.stage, (stageCounts.get(rule.stage) ?? 0) + 1)
    if (isBlankJson(rule.params) === false && parseJsonObject(rule.params) === null) {
      issues.push({ severity: 'block', message: `校验规则 ${rule.validationKey}（顺序 ${rule.seq}）的参数不是合法 JSON。` })
    }
  }

  for (const action of actions) {
    eventCounts.set(action.eventCode, (eventCounts.get(action.eventCode) ?? 0) + 1)
  }

  const known = options.knownSwitchKeys
  if (known && known.length > 0) {
    const knownSet = new Set(known.map((key) => key.toUpperCase()))
    for (const key of switchKeys) {
      if (!knownSet.has(key.toUpperCase())) {
        issues.push({ severity: 'block', message: `系统开关 '${key}' 不是已登记的系统参数键，执行期会被拒绝。` })
      }
    }
  }

  return {
    actionCount: actions.length,
    enabledActionCount: actions.filter((action) => action.enabled).length,
    stepCount,
    ruleCount: rules.length,
    eventCounts: [...eventCounts.entries()]
      .map(([eventCode, count]) => ({ eventCode, count }))
      .sort((a, b) => a.eventCode.localeCompare(b.eventCode)),
    stageCounts: [...stageCounts.entries()]
      .map(([stage, count]) => ({ stage, count }))
      .sort((a, b) => a.stage.localeCompare(b.stage)),
    tables: [...tables.entries()]
      .map(([table, entry]) => ({
        table,
        columns: [...entry.columns].sort(),
        actionCount: entry.actions.size,
      }))
      .sort((a, b) => b.columns.length - a.columns.length || a.table.localeCompare(b.table)),
    switchKeys: [...switchKeys].sort(),
    issues,
  }
}

/** 定位键校验：口径与服务端一致——目标表未参与关系目录时跳过，否则键必须被某一组边覆盖。 */
function checkMatch(action: ImpactAction, op: ImpactOp, options: ImpactOptions): ImpactIssue | null {
  if (isBlankJson(op.match)) return null
  const items = parseMatchItems(op.match)
  if (items === null || items.length === 0) {
    return { severity: 'block', message: `动作「${label(action)}」步骤 ${op.opSeq} 的定位键结构非法。` }
  }
  const table = (op.targetTable ?? '').trim().toUpperCase()
  const groups = options.relationsByTable?.[table]
  if (!groups || groups.length === 0) return null

  const master = (options.masterTable ?? '').trim().toUpperCase()
  const detail = (options.detailTable ?? '').trim().toUpperCase()
  const resolved = items.map((item) => {
    const scope = (item.source.scope ?? '').trim().toUpperCase()
    const fromTable = scope === 'MASTER'
      ? master
      : scope === 'DETAIL'
        ? detail
        : (item.source.table ?? '').trim().toUpperCase()
    return {
      fromTable,
      fromColumn: (item.source.field ?? '').trim().toUpperCase(),
      toColumn: item.target.trim().toUpperCase(),
    }
  })
  const covered = groups.some((group) => group.length > 0 && resolved.every((item) =>
    group.some((edge) =>
      edge.toTable.trim().toUpperCase() === table
      && edge.fromTable.trim().toUpperCase() === item.fromTable
      && edge.fromColumn.trim().toUpperCase() === item.fromColumn
      && edge.toColumn.trim().toUpperCase() === item.toColumn)))
  if (covered) return null
  return {
    severity: 'block',
    message: `动作「${label(action)}」步骤 ${op.opSeq}：定位键未登记效果关系边（目标表 ${table}），保存会被拒绝。`,
  }
}

/** 递归收集条件里的系统开关键。 */
export function collectSwitchKeys(condition: string | null | undefined): string[] {
  const parsed = parseJsonObject(condition)
  if (!parsed) return []
  const found: string[] = []
  const walk = (node: unknown) => {
    if (Array.isArray(node)) {
      node.forEach(walk)
      return
    }
    if (!node || typeof node !== 'object') return
    const record = node as Record<string, unknown>
    if (typeof record.type === 'string'
      && record.type.toLowerCase() === 'switch'
      && typeof record.key === 'string'
      && record.key.trim() !== '') {
      found.push(record.key.trim())
    }
    Object.values(record).forEach(walk)
  }
  walk(parsed)
  return found
}

function label(action: ImpactAction): string {
  const name = (action.effectName ?? '').trim()
  return name !== '' ? name : `${action.eventCode}#${action.seq}`
}

function isBlankJson(text: string | null | undefined): boolean {
  return text == null || text.trim() === '' || text.trim() === '{}'
}
