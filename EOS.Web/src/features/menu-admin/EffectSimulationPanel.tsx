import { useState } from 'react'
import { IconPlayerPlay, IconSearch } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { describeApiError } from '../../lib/errors'
import type { BusinessAction } from './BusinessActionsPanel'
import {
  formatCondition,
  formatOpSentence,
  makeLabelLookup,
  withTargetTable,
  type BusinessNameLookup,
} from './businessActionText'
import {
  SIMULATION_EVENTS,
  SIMULATION_EVENT_LABELS,
  simulateEffectChain,
  type EffectSimulationOp,
  type EffectSimulationReport,
  type EffectSimulationStep,
  type SimulationEvent,
} from './effectSimulation'

/**
 * 效果链预演：选一张真实单据，在事务内跑一遍真实的生效链（含状态翻转）再回滚，
 * 把"会发生什么"逐步报告出来。
 *
 * 顶部那句声明不是装饰：预演跑的是真实 Handler，用户最需要确认的就是"我没有真的批了这张单"。
 */
export function EffectSimulationPanel({
  moduleId,
  moduleTitle,
  actions,
  names,
  reverseKindLabels,
  onClose,
}: {
  moduleId: number
  moduleTitle: string
  actions: BusinessAction[]
  names: BusinessNameLookup
  reverseKindLabels?: Record<string, string> | null
  onClose: () => void
}) {
  const [event, setEvent] = useState<SimulationEvent>('APPROVE_EFFECT')
  const [recordKey, setRecordKey] = useState<string[] | null>(null)
  const [recordText, setRecordText] = useState('')
  const [chooserOpen, setChooserOpen] = useState(false)
  const [keyColumns, setKeyColumns] = useState<string[]>([])
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [report, setReport] = useState<EffectSimulationReport | null>(null)

  const lookup = makeLabelLookup(reverseKindLabels)

  const run = async () => {
    if (!recordKey || recordKey.length === 0) return
    setRunning(true)
    setError(null)
    setReport(null)
    try {
      setReport(await simulateEffectChain(moduleId, event, recordKey))
    } catch (cause) {
      setError(describeApiError(cause, '预演失败。'))
    } finally {
      setRunning(false)
    }
  }

  const reset = () => {
    setReport(null)
    setError(null)
  }

  return (
    <Modal
      title={`效果链预演 — ${moduleTitle}`}
      onClose={onClose}
      size="xl"
      scrollable
      footer={(
        <div className="d-flex justify-content-between w-100 align-items-center flex-wrap gap-2">
          <div className="small text-secondary">预演已在事务内回滚，库内数据零变化。</div>
          <div className="d-flex gap-2">
            <Button size="sm" variant="ghost" onClick={reset} disabled={!report && !error}>清空结果</Button>
            <Button size="sm" onClick={onClose}>关闭</Button>
          </div>
        </div>
      )}
    >
      <div className="alert alert-secondary py-2 px-3" role="status">
        <strong>预演不改数据。</strong>
        {' '}它在同一事务内跑完真实的「前置守卫 → 校验闸 → 状态翻转 → 效果链」，随后无条件回滚；
        报告里的每一行都是"将会发生什么"，不是"已经发生了什么"。
      </div>

      <div className="row g-2 align-items-end mb-3">
        <div className="col-3">
          <label className="form-label small mb-1" htmlFor="effect-simulation-event">事件</label>
          <select
            id="effect-simulation-event"
            className="form-select form-select-sm"
            value={event}
            onChange={(next) => {
              setEvent(next.target.value as SimulationEvent)
              reset()
            }}
          >
            {SIMULATION_EVENTS.map((code) => (
              <option key={code} value={code}>{SIMULATION_EVENT_LABELS[code] ?? code}</option>
            ))}
          </select>
        </div>
        <div className="col-6">
          <label className="form-label small mb-1" htmlFor="effect-simulation-record">单据</label>
          <div className="d-flex gap-1">
            <input
              id="effect-simulation-record"
              className="form-control form-control-sm"
              value={recordText}
              readOnly
              placeholder="点右侧「选择单据」挑一张真实单据…"
            />
            <Button size="sm" icon={<IconSearch size={16} />} onClick={() => setChooserOpen(true)}>选择单据</Button>
          </div>
        </div>
        <div className="col-3">
          <Button
            size="sm"
            icon={<IconPlayerPlay size={16} />}
            onClick={() => void run()}
            disabled={running || recordKey == null || recordKey.length === 0}
          >
            {running ? '预演中…' : '预演（不改数据）'}
          </Button>
        </div>
      </div>

      <div className="text-secondary small mb-3">
        保存后效果（SAVE）暂不支持预演：它发生在主子表落库之后，预演它等于先伪造一次完整保存。
        本批次覆盖批核生效 / 解批。
      </div>

      {error ? (
        <div className="alert alert-danger py-2 px-3" role="alert">{error}</div>
      ) : null}

      {report ? <ReportView report={report} actions={actions} names={names} lookup={lookup} /> : null}

      {chooserOpen ? (
        <UnifiedChooser
          open
          title="选择一张单据"
          source={{ kind: 'sourceKey', key: 'menu-admin.records', args: { moduleId: String(moduleId) } }}
          mode="single"
          onColumnsLoaded={(columns, defaultKeys) =>
            setKeyColumns((defaultKeys ?? []).length > 0 ? defaultKeys! : columns.map((column) => column.key))
          }
          onPick={(rows) => {
            const row = rows[0]
            if (!row) return
            const columns = keyColumns.length > 0 ? keyColumns : Object.keys(row)
            const values = columns.map((column) => String(row[column] ?? ''))
            setRecordKey(values)
            setRecordText(values.join(' / '))
            setChooserOpen(false)
            reset()
          }}
          onClose={() => setChooserOpen(false)}
          searchPlaceholder="搜索单号…"
          emptyText="该模块主表没有可选择的单据。"
        />
      ) : null}
    </Modal>
  )
}

function ReportView({
  report,
  actions,
  names,
  lookup,
}: {
  report: EffectSimulationReport
  actions: BusinessAction[]
  names: BusinessNameLookup
  lookup: (code: string | null | undefined) => string
}) {
  const blocked = !report.precondition.passed || !report.validation.passed
  return (
    <div>
      <div className="d-flex align-items-center gap-2 flex-wrap mb-2">
        <span className={`badge ${report.rolledBack ? 'bg-green' : 'bg-red'}`}>
          {report.rolledBack ? '已回滚' : '未回滚（框架缺陷）'}
        </span>
        <span className="badge bg-azure">{SIMULATION_EVENT_LABELS[report.event] ?? report.event}</span>
        <span className="text-secondary small">
          单据 {report.recordKey.join(' / ')}
          {report.definitionVersion ? ` · 定义 ${report.definitionVersion}` : ''}
          {' · '}
          {report.durationMs} ms · 共 {report.counts.total} 步（执行 {report.counts.ran} / 跳过 {report.counts.skipped} / 失败 {report.counts.failed}）
        </span>
      </div>

      {!report.precondition.passed ? (
        <div className="alert alert-warning py-2 px-3" role="status">
          前置守卫未通过（{report.precondition.code}）：{report.precondition.message}
          <div className="small">这与真实点击批核/解批时的口径一致——真点也会被同一道闸拦下。</div>
        </div>
      ) : null}
      {!report.validation.passed ? (
        <div className="alert alert-warning py-2 px-3" role="status">
          校验闸拦截：{report.validation.message}
          <div className="small">被拦时效果链根本不会跑，所以下面没有步骤（与真实语义一致）。</div>
        </div>
      ) : null}

      {report.warnings.length > 0 ? (
        <div className="alert alert-secondary py-2 px-3" role="status">
          {report.warnings.map((warning) => <div className="small" key={warning}>{warning}</div>)}
        </div>
      ) : null}

      {!blocked && report.effects.length === 0 ? (
        <div className="text-secondary small">该事件在当前配置下没有可执行的动作。</div>
      ) : null}

      {report.effects.map((step) => (
        <StepCard key={step.seq} step={step} action={actions.find((item) => item.seq === step.seq)} names={names} lookup={lookup} />
      ))}
    </div>
  )
}

function StepCard({
  step,
  action,
  names,
  lookup,
}: {
  step: EffectSimulationStep
  action?: BusinessAction
  names: BusinessNameLookup
  lookup: (code: string | null | undefined) => string
}) {
  const outcomeText = step.outcome === 'ran' ? '执行' : step.outcome === 'skipped' ? '跳过' : '失败'
  const outcomeClass = step.outcome === 'ran' ? 'bg-green' : step.outcome === 'skipped' ? 'bg-yellow' : 'bg-red'
  const condition = step.condition ? formatCondition(step.condition, withTargetTable(names, step.ops[0]?.targetTable)) : null
  return (
    <div className="card mb-2">
      <div className="card-body py-2">
        <div className="d-flex align-items-center gap-2 flex-wrap">
          <span className="text-secondary small">#{step.seq}</span>
          <span className={`badge ${outcomeClass}`}>{outcomeText}</span>
          <strong>{step.effectName ?? step.effectKey}</strong>
          <span className="text-secondary small">{step.effectKey}</span>
          <span className="text-secondary small">影响 {step.rowsAffected} 行</span>
          {!step.enabled ? <span className="badge bg-secondary">已停用</span> : null}
        </div>
        {step.skipReason ? (
          <div className="small mt-1">
            {step.skipReason}
            {condition ? `（${condition}）` : null}
          </div>
        ) : null}
        {step.message ? <div className="small text-danger mt-1">{step.message}</div> : null}
        {step.ops.map((op) => (
          <OpRow key={op.opSeq} op={op} configured={action?.ops?.find((item) => item.opSeq === op.opSeq)} names={names} />
        ))}
        {action?.reverse ? (
          <div className="small text-secondary mt-1">
            解批反向：{lookup(readKind(action.reverse)) || '未配置'}
          </div>
        ) : null}
      </div>
    </div>
  )
}

function OpRow({
  op,
  configured,
  names,
}: {
  op: EffectSimulationOp
  configured?: { opSeq: number; targetTable?: string | null; targetField?: string | null; opCode?: string | null; sourceScope?: string | null; sourceTable?: string | null; sourceField?: string | null; sourceAgg?: string | null; sourceConstant?: string | null; sourceTerms?: string | null }
  names: BusinessNameLookup
}) {
  const sentence = configured
    ? formatOpSentence(configured, names)
    : `${names.table(op.targetTable)}.${names.field(op.targetTable, op.targetField)} ${op.opCode}`
  return (
    <div className="mt-2">
      <div className="small">{sentence} — 影响 {op.rowsAffected} 行</div>
      {op.changes.length > 0 ? (
        <table className="table table-sm table-borderless mb-0 mt-1">
          <thead>
            <tr className="small text-secondary">
              <th>目标行</th>
              <th>列</th>
              <th>旧值</th>
              <th>新值</th>
            </tr>
          </thead>
          <tbody>
            {op.changes.flatMap((change) =>
              change.columns.map((column) => (
                <tr key={`${change.identity}|${column.name}`}>
                  <td className="small">{change.identity}</td>
                  <td className="small">{names.field(op.targetTable, column.name)}</td>
                  <td className="small">{column.before ?? '（空）'}</td>
                  <td className="small">{column.after ?? '（空）'}</td>
                </tr>
              )),
            )}
          </tbody>
        </table>
      ) : null}
    </div>
  )
}

function readKind(json: string): string {
  try {
    const value = JSON.parse(json) as { kind?: unknown }
    return typeof value?.kind === 'string' ? value.kind : ''
  } catch {
    return ''
  }
}
