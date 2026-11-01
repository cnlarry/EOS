import { useState } from 'react'
import { describeApiError } from '../../lib/errors'
import { apiClient } from '../../services/api'
import { newIdempotencyKey } from '../document-workbench/formEditorUtils'
import type {
  AssistantDocumentActionDraft,
  AssistantDocumentActionResultDraft,
  AssistantDocumentActionParam,
} from './useChatStream'

interface DocumentActionResponse {
  outcome: 'refreshed' | 'navigated' | 'message'
  message?: string | null
  targetModuleId?: number | null
  targetKey?: string[] | null
  warnings?: Array<{ code: string; message: string }> | null
  requiresConfirmation?: boolean
}

function recordLink(moduleId: number, keys: string[]): string {
  return `/workbench/${moduleId}/view/${keys.map(key => encodeURIComponent(key)).join('/')}`
}

function declaredFields(draft: AssistantDocumentActionDraft): AssistantDocumentActionParam[] {
  const fields = draft.params?.fields
  return Array.isArray(fields) ? fields.filter(field => field && typeof field.key === 'string' && field.key !== '') : []
}

/**
 * 自定义单据动作的确认卡。
 *
 * 服务端只在这个动作声明了 CONFIRM_TAG=1 时生成这张卡；真正执行由用户点击这里完成，
 * 前端直接调既有动作端点（`POST /document-workbench/{moduleId}/action/{actionKey}`），
 * 幂等键随这次点击生成——与统一表单里的按钮走同一条路。
 */
export function DocumentActionCard({ draft }: { draft: AssistantDocumentActionDraft }) {
  const fields = declaredFields(draft)
  const [values, setValues] = useState<Record<string, string>>({ ...(draft.values ?? {}) })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<DocumentActionResponse | null>(null)

  const run = async () => {
    const missing = fields.filter(field => field.required && (values[field.key] ?? '').trim() === '')
    if (missing.length > 0) {
      setError(`请填写：${missing.map(field => field.label).join('、')}`)
      return
    }

    setBusy(true)
    setError(null)
    try {
      const response = await apiClient.post<DocumentActionResponse>(
        `/document-workbench/${draft.moduleId}/action/${encodeURIComponent(draft.actionKey)}`,
        {
          key: draft.keys,
          params: Object.keys(values).length > 0 ? values : null,
          confirm: true,
        },
        { headers: { 'X-Idempotency-Key': newIdempotencyKey() } },
      )
      setDone(response)
    } catch (cause) {
      setError(describeApiError(cause, `${draft.actionLabel}执行失败。`))
    } finally {
      setBusy(false)
    }
  }

  if (done) {
    return (
      <DocumentActionResultCard
        result={{
          kind: 'document-action-result',
          moduleId: draft.moduleId,
          moduleTitle: draft.moduleTitle,
          actionKey: draft.actionKey,
          actionLabel: draft.actionLabel,
          message: done.message,
          warnings: done.warnings,
          targetModuleId: done.targetModuleId,
          targetKey: done.targetKey,
        }}
      />
    )
  }

  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">⚙️ {draft.actionLabel}（{draft.moduleTitle || `模块 #${draft.moduleId}`}）</div>
      <div className="text-secondary small mb-2">
        单据主键：{draft.keys.join('/') || '（缺失）'}
      </div>
      {draft.previewMessage && <div className="mb-2">{draft.previewMessage}</div>}
      {fields.length > 0 && (
        <div className="d-flex flex-column gap-2 mb-2">
          {fields.map(field => (
            <div key={field.key}>
              <label className="form-label small mb-1" htmlFor={`assistant-doc-action-${field.key}`}>
                {field.label}{field.required ? '（必填）' : ''}
              </label>
              <input
                id={`assistant-doc-action-${field.key}`}
                className="form-control form-control-sm"
                type={field.type === 'number' ? 'number' : field.type === 'date' ? 'date' : 'text'}
                value={values[field.key] ?? ''}
                maxLength={field.type === 'string' ? field.maxLength ?? undefined : undefined}
                onChange={event => setValues(current => ({ ...current, [field.key]: event.target.value }))}
              />
            </div>
          ))}
        </div>
      )}
      {draft.confirmTag
        ? <div className="text-secondary small mb-2">该动作需要确认；点下面的按钮后才会写入，失败不会留下半成品。</div>
        : null}
      {error && <div className="erp-assistant-error small mb-2" role="alert">{error}</div>}
      <button className="btn btn-sm btn-primary" type="button" disabled={busy} onClick={() => void run()}>
        {busy ? '执行中…' : `确认执行：${draft.actionLabel}`}
      </button>
    </div>
  )
}

/** 自定义单据动作的执行结果。 */
export function DocumentActionResultCard({ result }: { result: AssistantDocumentActionResultDraft }) {
  const warnings = result.warnings ?? []
  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">
        ✅ {result.actionLabel}结果（{result.moduleTitle || `模块 #${result.moduleId}`}）
      </div>
      <div className="mb-1">{result.message ?? '动作已完成。'}</div>
      {warnings.map((warning, index) => (
        <div key={index} className="text-warning small">{warning.message}</div>
      ))}
      {result.targetModuleId && result.targetKey && result.targetKey.length > 0 && (
        <a className="erp-assistant-md-link small" href={recordLink(result.targetModuleId, result.targetKey)}>
          打开生成的单据
        </a>
      )}
    </div>
  )
}
