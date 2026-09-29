import { useRef, useState } from 'react'
import { newIdempotencyKey } from '../document-workbench/formEditorUtils'
import { confirmApprovalRequest, executeApprovalAction } from './api'
import type { ApprovalRequestAction, ApprovalRequestPayload, AssistantApprovalRequestPreview } from './types'

/**
 * 「操作请求卡」：**不可代劳**的处置（批核 / 解批 / 结案 / 取消结案）由用户在这里点确认。
 *
 * <para>
 * 三条边界写在卡片上，因为它们是这个组件的全部设计：
 * ① **逐行可见**：每行列出主键与当前状态，并逐行标注"可执行 / 不可执行 + 原因"——
 *    没有"批 N 张"的单一按钮；② **逐行可取消勾选**：不勾的行不会被提交；
 * ③ **执行主体是这次点击**：确认后由本组件直接调既有批核族端点，助手侧不持有那条通路。
 * </para>
 *
 * <para>
 * 卡上的判定只是服务端给出的预告，**不代替服务端放行**：真正执行时服务端照旧独立重新授权。
 * </para>
 */
export function ApprovalRequestCard({ draft }: { draft: AssistantApprovalRequestPreview }) {
  const [rows, setRows] = useState<CardRow[]>(() => draft.rows.map(toCardRow))
  const [busy, setBusy] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  // 同一张卡是同一次用户意图：确认键在这里生成一次，逐行派生出稳定的幂等键。
  const confirmKeyRef = useRef(newIdempotencyKey())

  const label = ACTION_LABELS[draft.action]
  const selected = rows.filter(row => row.selected && row.allowed)
  const denied = rows.filter(row => !row.allowed).length

  const toggleRow = (rowIndex: number) => {
    setRows(current => current.map((row, index) =>
      index === rowIndex ? { ...row, selected: !row.selected } : row))
  }

  const handleConfirm = async () => {
    if (busy || confirmed || selected.length === 0) return
    setBusy(true)
    const payload = toPayload(rows, draft, true)
    try {
      // 先留一条"用户点了确认"的痕：它是 best-effort 的，写不进去不该拦住用户已经点下的这次执行。
      await confirmApprovalRequest(payload)
    } catch {
      // 忽略：见上句
    }
    const outcomes = new Map<number, RowOutcome>()
    for (let index = 0; index < rows.length; index += 1) {
      const row = rows[index]
      if (!row.selected || !row.allowed) continue
      try {
        await executeApprovalAction(draft.moduleId, draft.action, row.keys, `${confirmKeyRef.current}:${index}`)
        outcomes.set(index, { succeeded: true, message: '' })
      } catch (cause) {
        outcomes.set(index, { succeeded: false, message: problemMessage(cause) })
      }
    }
    setRows(current => current.map((row, index) => {
      const outcome = outcomes.get(index)
      return outcome ? { ...row, outcome } : row
    }))
    setConfirmed(true)
    setBusy(false)
  }

  if (draft.blocked) {
    return (
      <div className="erp-assistant-draft-card">
        <div className="fw-bold mb-1">🔏 {label}：{draft.moduleTitle || `模块 #${draft.moduleId}`}</div>
        {/* 权限问题必须明说：只给"操作失败"会把人推向绕过系统 */}
        <div className="erp-assistant-error" role="alert">
          {draft.moduleDenialMessage ?? draft.moduleDenialCode}
        </div>
      </div>
    )
  }

  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">
        🔏 操作请求卡（{label}）：{draft.moduleTitle || `模块 #${draft.moduleId}`}（{rows.length} 行，
        不可执行 {denied} 行）
      </div>

      {rows.map((row, index) => (
        <div
          key={`${rowLabel(row.keys)}-${index}`}
          className={`erp-assistant-action-row${row.allowed ? '' : ' is-denied'}`}
        >
          <div className="erp-assistant-action-head">
            <input
              type="checkbox"
              className="form-check-input"
              checked={row.selected}
              disabled={!row.allowed || busy || confirmed}
              aria-label={`选择${label} ${rowLabel(row.keys)}`}
              onChange={() => toggleRow(index)}
            />
            <span className="fw-bold">{rowLabel(row.keys)}</span>
            {row.status && <span className="text-secondary small">{row.status}</span>}
            <span className="text-secondary small">
              {row.allowed ? '可执行' : `不可执行：${row.denialMessage ?? row.denialCode}`}
            </span>
          </div>

          {row.outcome && (
            <div className={row.outcome.succeeded ? 'text-success small' : 'text-warning small'}>
              {row.outcome.succeeded ? `已提交${label}` : `未提交：${row.outcome.message}`}
            </div>
          )}
        </div>
      ))}

      {draft.notes.map(note => (
        <div key={note} className="text-secondary small">{note}</div>
      ))}

      <div className="erp-assistant-action-actions">
        <button
          className="btn btn-sm btn-primary"
          type="button"
          disabled={busy || confirmed || selected.length === 0}
          onClick={() => void handleConfirm()}
        >
          {busy ? '提交中…' : confirmed ? '已提交' : `确认${label}所选（${selected.length} 行）`}
        </button>
      </div>
    </div>
  )
}

interface RowOutcome {
  succeeded: boolean
  message: string
}

/** 卡内一行：主键 + 状态 + 服务端结论 + 勾选态（默认勾选可执行的行）+ 提交结果。 */
interface CardRow {
  keys: string[]
  status: string
  allowed: boolean
  denialCode: string | null
  denialMessage: string | null
  selected: boolean
  outcome: RowOutcome | null
}

const ACTION_LABELS: Record<ApprovalRequestAction, string> = {
  approve: '批核',
  deapprove: '解批',
  endcase: '结案',
  unendcase: '取消结案',
}

function problemMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return '操作失败。'
}

function toCardRow(row: AssistantApprovalRequestPreview['rows'][number]): CardRow {
  return {
    keys: [...row.keys],
    status: row.status,
    allowed: row.allowed,
    denialCode: row.denialCode,
    denialMessage: row.denialMessage,
    // 默认勾选可执行的行：用户的意图就是"把这些处置了"，取消勾选才是例外。
    selected: row.allowed,
    outcome: null,
  }
}

function rowLabel(keys: string[]): string {
  return keys.length > 0 ? keys.join('/') : '（缺主键）'
}

/** 只提交被勾选且可执行的行：不勾的行不会被送出去。 */
function toPayload(
  rows: CardRow[],
  draft: AssistantApprovalRequestPreview,
  onlySelected: boolean,
): ApprovalRequestPayload {
  return {
    module_id: draft.moduleId,
    action: draft.action,
    rows: rows
      .filter(row => !onlySelected || (row.selected && row.allowed))
      .map(row => ({ keys: row.keys })),
  }
}
