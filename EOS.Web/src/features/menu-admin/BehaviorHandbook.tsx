import { useMemo, useState } from 'react'
import { IconCopy } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { MANUAL_EVENT } from './documentActionConfig'
import type { BusinessAction, BusinessConfigCatalog, LabelLookups, ValidationRule } from './BusinessActionsPanel'
import {
  eventLabel,
  formatMatch,
  formatOpSentence,
  makeLabelLookup,
  reverseTextOf,
  summarizeAction,
  type BusinessNameLookup,
} from './businessActionText'
import { handbookMarkdown } from './behaviorHandbookText'

/**
 * 行为说明书：把一个模块的「校验规则 + 效果链（含反向）」按事件分组渲染成人话。
 *
 * 它回答的是"这张单会发生什么"，而答案当前被拆在三个行为页签里；产物可交接——
 * 能贴进评审记录，也能作为别人（或 AI）改配置前的阅读界面。
 * 数据源就是配置面已有的目录与当前草稿，不新增后端端点。
 */
export function BehaviorHandbook({
  moduleTitle,
  masterTable,
  detailTable,
  actions,
  rules,
  catalog,
  labels,
  names,
  reverseKindLabels,
  onClose,
}: {
  moduleTitle: string
  masterTable?: string | null
  detailTable?: string | null
  actions: BusinessAction[]
  rules: ValidationRule[]
  catalog: BusinessConfigCatalog
  labels: LabelLookups
  names: BusinessNameLookup
  reverseKindLabels?: Record<string, string> | null
  onClose: () => void
}) {
  const [copied, setCopied] = useState(false)
  const reverseLookup = useMemo(() => makeLabelLookup(reverseKindLabels), [reverseKindLabels])
  const markdown = useMemo(
    () => handbookMarkdown({ moduleTitle, masterTable, detailTable, actions, rules, catalog, labels, names, reverseLookup }),
    [moduleTitle, masterTable, detailTable, actions, rules, catalog, labels, names, reverseLookup],
  )

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(markdown)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      setCopied(false)
    }
  }

  const effectActions = useMemo(
    () => actions.filter((action) => action.eventCode !== MANUAL_EVENT).sort((left, right) => left.seq - right.seq),
    [actions],
  )
  const eventCodes = useMemo(
    () => [...new Set(effectActions.map((action) => action.eventCode))].sort(),
    [effectActions],
  )

  return (
    <Modal
      title={`行为说明书 — ${moduleTitle}`}
      onClose={onClose}
      size="xl"
      scrollable
      footer={(
        <div className="d-flex justify-content-between w-100 align-items-center">
          <span className="small text-secondary">只读汇总，覆盖三个行为页签的全部内容。</span>
          <div className="d-flex gap-2">
            <Button size="sm" icon={<IconCopy size={16} />} onClick={() => void copy()}>
              {copied ? '已复制' : '复制为 Markdown'}
            </Button>
            <Button size="sm" onClick={onClose}>关闭</Button>
          </div>
        </div>
      )}
    >
      {eventCodes.length === 0 && rules.length === 0 ? (
        <div className="text-secondary">该模块还没有配置行为（效果链 / 校验规则）。</div>
      ) : null}

      {eventCodes.map((code) => {
        const group = effectActions.filter((action) => action.eventCode === code)
        return (
          <section key={code} className="mb-3">
            <h6 className="mb-1">{eventLabel(code, catalog, labels)}（{group.length}）</h6>
            {group.map((action) => (
              <div key={action.seq} className="border-bottom py-2">
                <div className="d-flex gap-2 flex-wrap align-items-center">
                  <span className="text-secondary small">#{action.seq}</span>
                  <strong>{labels.effectKeys(action.effectKey)}</strong>
                  <span className="text-secondary small">{action.effectKey}</span>
                  {action.enabled ? null : <span className="badge bg-secondary">已停用</span>}
                  <span className="badge bg-azure">{labels.failModes(action.failMode)}</span>
                </div>
                <div className="small mt-1">{summarizeAction(action, names)}</div>
                <div className="small text-secondary mt-1">
                  解批反向：{reverseTextOf(action.reverse, reverseLookup)}
                </div>
                {(action.ops ?? []).length > 0 ? (
                  <ul className="small mb-0 mt-1">
                    {[...(action.ops ?? [])].sort((left, right) => left.opSeq - right.opSeq).map((op) => {
                      const match = formatMatch(op.match, names)
                      return (
                        <li key={op.opSeq}>
                          {formatOpSentence(op, names)}
                          {match ? ` @${match}` : ''}
                        </li>
                      )
                    })}
                  </ul>
                ) : null}
              </div>
            ))}
          </section>
        )
      })}

      {rules.length > 0 ? (
        <section>
          <h6 className="mb-1">校验规则（{rules.length}）</h6>
          <ul className="small mb-0">
            {[...rules].sort((left, right) => left.seq - right.seq).map((rule) => (
              <li key={`${rule.stage}|${rule.seq}`}>
                {labels.validationStages(rule.stage)} #{rule.seq} · {labels.validationKeys(rule.validationKey)}
                {rule.enabled ? '' : '（已停用）'}
                {rule.message ? ` · 失败提示：${rule.message}` : ''}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {actions.some((action) => action.eventCode === MANUAL_EVENT) ? (
        <section className="mt-3">
          <h6 className="mb-1">自定义按钮</h6>
          <ul className="small mb-0">
            {actions
              .filter((action) => action.eventCode === MANUAL_EVENT)
              .sort((left, right) => left.seq - right.seq)
              .map((action) => (
                <li key={action.seq}>#{action.seq} {action.label ?? action.effectKey}（{action.effectKey}）</li>
              ))}
          </ul>
        </section>
      ) : null}
    </Modal>
  )
}
