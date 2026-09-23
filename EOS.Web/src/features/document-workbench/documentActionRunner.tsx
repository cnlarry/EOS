import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { useToast } from '../../components/ui/toastContext'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { newIdempotencyKey } from './formEditorUtils'

/**
 * 单据操作（自定义按钮）的前端执行器。
 *
 * 契约（ADR-018 §3.3）：`POST /document-workbench/{moduleId}/action/{actionKey}`，
 * 幂等键走 `X-Idempotency-Key` 请求头；CONFIRM_TAG 的操作先用 `confirm=false` 探路，
 * 服务端在同一事务里试跑后回滚、把"将会发生什么"回传（探路不占幂等键），用户确认后再 `confirm=true` 真执行。
 *
 * 按钮只在**浏览态**出现，且界面有未保存改动时禁用——操作作用于已落库的单据状态，
 * 混着未保存的界面改动一起执行会让"服务端看到的单据"和用户以为的不一致。
 */
export interface DocumentActionParamField {
  key: string
  label: string
  type: string
  required: boolean
  maxLength: number | null
}

export interface DocumentActionMeta {
  key: string
  label: string
  confirmTag: boolean
  failMode: string
  placement: string
  params?: { fields?: DocumentActionParamField[] } | null
}

export interface DocumentActionWarning {
  code: string
  message: string
}

export interface DocumentActionResponse {
  outcome: 'refreshed' | 'navigated' | 'message'
  message?: string | null
  targetModuleId?: number | null
  targetKey?: string[] | null
  warnings?: DocumentActionWarning[] | null
  requiresConfirmation?: boolean
}

interface RunnerOptions {
  moduleId: string
  /** 已落库单据的主键值；为空表示当前没有可执行的单据（新增态）。 */
  keyValues: string[] | null
  /** 未保存改动：此时按钮禁用，点击给出"请先保存"的提示。 */
  dirty: boolean
  onRefreshed: () => void | Promise<void>
  onNavigate: (moduleId: number, key: string[]) => void
}

type Stage =
  | { kind: 'closed' }
  | { kind: 'params'; action: DocumentActionMeta; values: Record<string, string> }
  | { kind: 'confirm'; action: DocumentActionMeta; values: Record<string, string>; preview: string | null }

export function declaredParams(action: DocumentActionMeta): DocumentActionParamField[] {
  const fields = action.params?.fields
  return Array.isArray(fields) ? fields.filter((field) => field && typeof field.key === 'string' && field.key !== '') : []
}

export function useDocumentActionRunner({ moduleId, keyValues, dirty, onRefreshed, onNavigate }: RunnerOptions) {
  const { notify } = useToast()
  const [stage, setStage] = useState<Stage>({ kind: 'closed' })
  const [busyKey, setBusyKey] = useState<string | null>(null)
  const canRun = Boolean(keyValues && keyValues.length > 0) && !dirty && busyKey === null

  const post = useCallback(
    async (action: DocumentActionMeta, values: Record<string, string>, confirm: boolean) =>
      apiClient.post<DocumentActionResponse>(
        `/document-workbench/${moduleId}/action/${encodeURIComponent(action.key)}`,
        { key: keyValues, params: Object.keys(values).length > 0 ? values : null, confirm },
        { headers: { 'X-Idempotency-Key': newIdempotencyKey() } },
      ),
    [moduleId, keyValues],
  )

  const apply = useCallback(
    async (action: DocumentActionMeta, response: DocumentActionResponse) => {
      for (const warning of response.warnings ?? []) {
        notify({ message: `${action.label}：${warning.message}`, variant: 'warning' })
      }
      if (response.outcome === 'navigated' && response.targetModuleId && response.targetKey) {
        notify({ message: response.message ?? `${action.label}完成，已生成后续单据。`, variant: 'success' })
        onNavigate(response.targetModuleId, response.targetKey)
        return
      }
      if (response.outcome === 'refreshed') {
        await onRefreshed()
      }
      notify({
        message: response.message ?? `${action.label}完成。`,
        variant: response.warnings && response.warnings.length > 0 ? 'warning' : 'success',
      })
    },
    [notify, onNavigate, onRefreshed],
  )

  const execute = useCallback(
    async (action: DocumentActionMeta, values: Record<string, string>) => {
      setStage({ kind: 'closed' })
      setBusyKey(action.key)
      try {
        await apply(action, await post(action, values, true))
      } catch (cause) {
        notify({ message: describeApiError(cause, `${action.label}失败。`), variant: 'danger' })
      } finally {
        setBusyKey(null)
      }
    },
    [apply, notify, post],
  )

  const run = useCallback(
    async (action: DocumentActionMeta) => {
      if (!keyValues || keyValues.length === 0) {
        notify({ message: '先保存并打开一张单据，才能执行该操作。', variant: 'warning' })
        return
      }
      if (dirty) {
        notify({ message: '界面有未保存的改动，请先保存后再执行操作。', variant: 'warning' })
        return
      }
      if (busyKey !== null) return
      const fields = declaredParams(action)
      if (fields.length > 0) {
        const initial: Record<string, string> = {}
        for (const field of fields) initial[field.key] = ''
        setStage({ kind: 'params', action, values: initial })
        return
      }
      if (!action.confirmTag) {
        void execute(action, {})
        return
      }
      // 探路口径：不写库、不占幂等键，只把"将会发生什么"取回来给用户看。
      setBusyKey(action.key)
      try {
        const preview = await post(action, {}, false)
        setStage({ kind: 'confirm', action, values: {}, preview: preview.message ?? null })
      } catch (cause) {
        notify({ message: describeApiError(cause, `${action.label}预检失败。`), variant: 'danger' })
      } finally {
        setBusyKey(null)
      }
    },
    [busyKey, dirty, execute, keyValues, notify, post],
  )

  const confirmWithParams = useCallback(async () => {
    if (stage.kind !== 'params') return
    const { action, values } = stage
    const missing = declaredParams(action).filter((field) => field.required && (values[field.key] ?? '').trim() === '')
    if (missing.length > 0) {
      notify({ message: `请填写：${missing.map((field) => field.label).join('、')}`, variant: 'warning' })
      return
    }
    if (!action.confirmTag) {
      void execute(action, values)
      return
    }
    setBusyKey(action.key)
    try {
      const preview = await post(action, values, false)
      setStage({ kind: 'confirm', action, values, preview: preview.message ?? null })
    } catch (cause) {
      notify({ message: describeApiError(cause, `${action.label}预检失败。`), variant: 'danger' })
    } finally {
      setBusyKey(null)
    }
  }, [execute, notify, post, stage])

  const dialog: ReactNode = useMemo(() => {
    if (stage.kind === 'closed') return null
    const { action } = stage
    const fields = declaredParams(action)
    return (
      <Modal
        title={action.label}
        onClose={() => setStage({ kind: 'closed' })}
        size="sm"
        footer={
          <div className="d-flex gap-2 ms-auto">
            <Button onClick={() => setStage({ kind: 'closed' })}>取消</Button>
            <Button
              variant="primary"
              loading={busyKey === action.key}
              onClick={() => void (stage.kind === 'params' ? confirmWithParams() : execute(action, stage.values))}
            >
              确定
            </Button>
          </div>
        }
      >
        {stage.kind === 'params' ? (
          <div className="d-flex flex-column gap-2">
            {fields.map((field) => (
              <div key={field.key}>
                <label className="form-label small mb-1" htmlFor={`doc-action-param-${field.key}`}>
                  {field.label}{field.required ? '（必填）' : ''}
                </label>
                <input
                  id={`doc-action-param-${field.key}`}
                  className="form-control form-control-sm"
                  type={field.type === 'number' ? 'number' : field.type === 'date' ? 'date' : 'text'}
                  value={stage.values[field.key] ?? ''}
                  maxLength={field.type === 'string' ? field.maxLength ?? undefined : undefined}
                  onChange={(event) =>
                    setStage((current) =>
                      current.kind === 'params'
                        ? { ...current, values: { ...current.values, [field.key]: event.target.value } }
                        : current,
                    )
                  }
                />
              </div>
            ))}
          </div>
        ) : (
          <div>
            <div className="mb-2">{stage.preview ?? '将执行该操作，是否继续？'}</div>
            <div className="text-secondary small">操作只在已落库的单据状态上执行；执行失败不会留下半成品。</div>
          </div>
        )}
      </Modal>
    )
  }, [busyKey, confirmWithParams, execute, stage])

  return { run, canRun, busyKey, dialog }
}
