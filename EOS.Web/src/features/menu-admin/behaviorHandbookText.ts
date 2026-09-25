import { MANUAL_EVENT } from './documentActionConfig'
import type { BusinessAction, BusinessConfigCatalog, LabelLookups, ValidationRule } from './BusinessActionsPanel'
import {
  eventLabel,
  formatCondition,
  formatMatch,
  formatOpSentence,
  reverseTextOf,
  summarizeAction,
  type BusinessNameLookup,
} from './businessActionText'

/**
 * 行为说明书的 Markdown 产物（纯函数，便于单独断言）：把一个模块的「校验规则 + 效果链
 * （含反向人话）」按事件汇总成可交接的文本——能贴进评审记录，也能给顾问读。
 */
export function handbookMarkdown({
  moduleTitle,
  masterTable,
  detailTable,
  actions,
  rules,
  catalog,
  labels,
  names,
  reverseLookup,
}: {
  moduleTitle: string
  masterTable?: string | null
  detailTable?: string | null
  actions: BusinessAction[]
  rules: ValidationRule[]
  catalog: BusinessConfigCatalog
  labels: LabelLookups
  names: BusinessNameLookup
  /** 反向 kind → 中文名（说明书里"解批会发生什么"那一段）。 */
  reverseLookup: (code: string | null | undefined) => string
}): string {
  const lines: string[] = []
  lines.push(`# 行为说明书：${moduleTitle}`)
  const tables = [masterTable, detailTable].filter((table) => table != null && table !== '').join(' / ')
  if (tables !== '') lines.push('', `操作主表：${tables}`)

  const effectActions = actions
    .filter((action) => action.eventCode !== MANUAL_EVENT)
    .sort((left, right) => left.seq - right.seq)

  if (effectActions.length > 0) {
    lines.push('', '## 效果链', '')
    lines.push('| 事件 | 顺序 | 效果 | 启用 | 失败模式 | 做什么 | 条件 | 解批反向 |')
    lines.push('|---|---|---|---|---|---|---|---|')
    for (const action of effectActions) {
      lines.push(`| ${[
        eventLabel(action.eventCode, catalog, labels),
        String(action.seq),
        labels.effectKeys(action.effectKey),
        action.enabled ? '是' : '否',
        labels.failModes(action.failMode),
        summarizeAction(action, names).replace(/\|/g, '\\|'),
        formatCondition(action.condition, names) ?? '—',
        reverseTextOf(action.reverse, reverseLookup),
      ].join(' | ')} |`)
    }
    for (const action of effectActions) {
      if ((action.ops ?? []).length === 0) continue
      lines.push(
        '',
        `### ${eventLabel(action.eventCode, catalog, labels)} #${action.seq} ${labels.effectKeys(action.effectKey)}`,
        '',
      )
      for (const op of [...(action.ops ?? [])].sort((left, right) => left.opSeq - right.opSeq)) {
        const match = formatMatch(op.match, names)
        lines.push(`- ${formatOpSentence(op, names)}${match ? ` @${match}` : ''}`)
      }
    }
  }

  if (rules.length > 0) {
    lines.push('', '## 校验规则', '')
    lines.push('| 阶段 | 顺序 | 校验模板 | 启用 | 失败文案 |')
    lines.push('|---|---|---|---|---|')
    for (const rule of [...rules].sort((left, right) => left.seq - right.seq)) {
      lines.push(`| ${[
        labels.validationStages(rule.stage),
        String(rule.seq),
        labels.validationKeys(rule.validationKey),
        rule.enabled ? '是' : '否',
        (rule.message ?? '').replace(/\|/g, '\\|') || '—',
      ].join(' | ')} |`)
    }
  }

  const manual = actions.filter((action) => action.eventCode === MANUAL_EVENT)
  if (manual.length > 0) {
    lines.push('', '## 自定义按钮', '')
    for (const action of [...manual].sort((left, right) => left.seq - right.seq)) {
      lines.push(`- #${action.seq} ${action.label ?? action.effectKey}（${action.effectKey}）`)
    }
  }

  return lines.join('\n')
}
