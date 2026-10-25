import type { CSSProperties } from 'react'
import type { FormDefinition } from './formDefinition'
import { formatFieldValue } from './fieldFormat'
import { buildLifecycleGroups, type LifecycleFieldLike, type LifecycleGroup } from './lifecycleGroups'

/**
 * 单据生命周期信息在浏览态的两种呈现：
 *
 * - `LifecycleStatusBar`：工具栏右侧的状态徽标（批核 / 结案），始终可见；
 * - `LifecycleInfoBlock`：主表单卡底部的固定信息区，按 建立 / 修改 / 批核 / 结案 等分排列。
 *
 * 两者都只消费 `FormDefinition.lifecycleFields`（浏览态才有值），不读版式行；
 * 没有结案能力的模块（`hasFinishCapability = false`）只出批核状态、底部三组。
 */
export interface LifecycleDisplayProps {
  form: FormDefinition
  master?: Record<string, unknown> | null
  flowInProgress?: boolean
}

/** 能力判定与 `formToolbar` 的结案按钮显隐同源；`hasFinishCapability` 缺省时向字段/记录值回退。 */
export function lifecycleCapabilities(
  form: FormDefinition,
  master?: Record<string, unknown> | null,
): { hasApprove: boolean; hasFinish: boolean } {
  const fields = form.lifecycleFields ?? []
  const hasApprove = form.hasApproveCapability === false
    ? false
    : (form.hasApproveCapability ?? (form.hasWorkflow || form.hasStatelessApprove))
      || fields.some(field => field.key.toUpperCase().startsWith('CONFIRM_'))
  const hasFinish = form.hasFinishCapability === false
    ? false
    : form.hasFinishCapability
      ?? (fields.some(field => field.key.toUpperCase().startsWith('FINISHED_'))
        || master?.FINISHED_TAG !== undefined)
  return { hasApprove, hasFinish }
}

function textOf(value: unknown): string {
  return value === null || value === undefined ? '' : String(value)
}

function formatLifecycleDate(
  field: LifecycleFieldLike,
  value: unknown,
): { text: string; full: string; raw: string } {
  const full = formatFieldValue(value, field.dataType, field.displayFormat ?? null)
  // 固定区显示到分钟（秒只在 title 里保留）：生命周期时间是追溯信息，秒级噪声没有阅读价值。
  const text = full.replace(/(\d{1,2}:\d{2}):\d{2}$/, '$1')
  return { text, full, raw: typeof value === 'string' ? value : '' }
}

function personOf(group: LifecycleGroup<LifecycleFieldLike> | undefined, master: Record<string, unknown>): string {
  return group?.person ? textOf(master[group.person.key]) : ''
}

function dateOf(group: LifecycleGroup<LifecycleFieldLike> | undefined, master: Record<string, unknown>): string {
  return group?.date ? formatLifecycleDate(group.date, master[group.date.key]).text : ''
}

function statusTitle(text: string, person: string, date: string): string {
  return [text, person, date].filter(value => value.length > 0).join(' · ')
}

/**
 * 工具栏右侧状态：批核 / 结案各一枚徽标。
 * 无结案能力时只渲染批核；记录尚未到达时不猜状态（避免先显示"未批核"再翻成"已批核"）。
 */
export function LifecycleStatusBar({ form, master, flowInProgress = false }: LifecycleDisplayProps) {
  if (!master) return null
  const { hasApprove, hasFinish } = lifecycleCapabilities(form, master)
  const showApprove = hasApprove && master.CONFIRM_TAG !== undefined
  const showFinish = hasFinish && master.FINISHED_TAG !== undefined
  if (!showApprove && !showFinish) return null

  const groups = buildLifecycleGroups(form.lifecycleFields ?? [])
  const approveGroup = groups.find(group => group.key === 'approve')
  const closeGroup = groups.find(group => group.key === 'close')
  const confirmed = master.CONFIRM_TAG === true
  const finished = master.FINISHED_TAG === true

  const approveText = flowInProgress ? '审批中' : confirmed ? '已批核' : '未批核'
  const approveClass = flowInProgress
    ? 'bg-warning-lt text-warning'
    : confirmed ? 'bg-success-lt text-success' : 'bg-secondary-lt text-secondary'
  const approveTitle = statusTitle(
    approveText,
    personOf(approveGroup, master),
    dateOf(approveGroup, master),
  )

  const finishText = finished ? '已结案' : '未结案'
  const finishClass = finished ? 'bg-azure-lt text-azure' : 'bg-secondary-lt text-secondary'
  const finishTitle = statusTitle(
    finishText,
    personOf(closeGroup, master),
    dateOf(closeGroup, master),
  )

  return (
    <div className="erp-doc-status" role="status" aria-label="单据状态">
      {showApprove ? (
        <span className={`badge erp-doc-status-badge ${approveClass}`} title={approveTitle}>
          <span className="erp-doc-status-dot" aria-hidden="true" />
          {approveText}
        </span>
      ) : null}
      {showFinish ? (
        <span className={`badge erp-doc-status-badge ${finishClass}`} title={finishTitle}>
          <span className="erp-doc-status-dot" aria-hidden="true" />
          {finishText}
        </span>
      ) : null}
    </div>
  )
}

/**
 * 主表单卡底部的固定信息区：等分组宽（组数由“有没有结案能力”决定 3 或 4），
 * 无可见标题（不要把“单据信息”当标题写进界面），语义由 aria-label 承担。
 */
export function LifecycleInfoBlock({ form, master }: LifecycleDisplayProps) {
  if (!master) return null
  const { hasApprove, hasFinish } = lifecycleCapabilities(form, master)
  const groups = buildLifecycleGroups(form.lifecycleFields ?? [], {
    includeApprove: hasApprove,
    includeClose: hasFinish,
  })
  if (groups.length === 0) return null

  return (
    <section
      className="erp-form-lifecycle"
      aria-label="单据与经办信息"
      style={{ '--erp-lifecycle-groups': groups.length } as CSSProperties}
    >
      {groups.map(group => {
        const person = group.person ? textOf(master[group.person.key]) : ''
        const date = group.date ? formatLifecycleDate(group.date, master[group.date.key]) : null
        return (
          <div className="erp-form-lifecycle-group" key={group.key}>
            <span className="erp-form-lifecycle-label">{group.label}</span>
            <span className="erp-form-lifecycle-person" title={person || undefined}>
              {person || '—'}
            </span>
            {group.date ? (
              <time
                className="erp-form-lifecycle-date"
                dateTime={date?.raw || undefined}
                title={date?.full || undefined}
              >
                {date?.text || '—'}
              </time>
            ) : (
              <span className="erp-form-lifecycle-date">—</span>
            )}
          </div>
        )
      })}
    </section>
  )
}
