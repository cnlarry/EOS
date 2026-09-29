import { useEffect, useMemo, useRef, useState } from 'react'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import type { UnifiedChooserRow, UnifiedChooserSource } from '../../components/common/chooserSource'
import { emptyValue, newIdempotencyKey, parseReturnItems, writableFields } from '../document-workbench/formEditorUtils'
import type { FormDefinition, FormFieldDefinition } from '../document-workbench/formDefinition'
import { applyRecordAction, getFormDefinition, previewRecordAction } from './api'
import type {
  AssistantActionImpact,
  AssistantActionRowPreview,
  AssistantRecordActionPayload,
  AssistantRecordActionPreview,
  AssistantRecordActionResult,
} from './types'

/**
 * 执行结果的回读：模型自己执行与用户在卡上确认执行，落到的是同一份形状，
 * 因此"助手说它改了什么"和"卡上确认后改了什么"看起来一模一样。
 */
export function ActionResultCard({ result }: { result: AssistantRecordActionResult }) {
  const succeeded = result.rows.filter(row => row.succeeded).length
  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">
        ✅ {ACTION_LABELS[result.action]}结果：{result.moduleTitle || `模块 #${result.moduleId}`}
        （成功 {succeeded} 行、失败 {result.rows.length - succeeded} 行）
      </div>
      {result.moduleDenialMessage && (
        <div className="erp-assistant-error" role="alert">{result.moduleDenialMessage}</div>
      )}
      {result.rows.map((row, index) => (
        <div key={`${rowLabel(row.keys)}-${index}`} className="small">
          <span className="fw-bold">{rowLabel(row.resultKeys ?? row.keys)}</span>{' '}
          {row.succeeded
            ? <span className="text-success">已保存</span>
            : <span className="text-warning">未保存：{row.message ?? row.code}</span>}
        </div>
      ))}
    </div>
  )
}

/** 卡内一行：主键 + 可编辑的值 + 服务端结论 + 勾选态（删除用，默认不勾）。 */
interface CardRow {
  keys: string[]
  values: Record<string, string>
  allowed: boolean
  denialCode: string | null
  denialMessage: string | null
  impacts: AssistantActionImpact[]
  selected: boolean
}

const ACTION_LABELS: Record<AssistantRecordActionPayload['action'], string> = {
  insert: '新增',
  update: '修改',
  delete: '删除',
}

function problemMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return '操作失败。'
}

function toCardRow(row: AssistantActionRowPreview, previous?: CardRow): CardRow {
  const values: Record<string, string> = {}
  for (const [key, value] of Object.entries(row.values ?? {})) {
    values[key] = value ?? ''
  }
  return {
    keys: [...row.keys],
    values,
    allowed: row.allowed,
    denialCode: row.denialCode,
    denialMessage: row.denialMessage,
    impacts: row.impacts ?? [],
    selected: previous?.selected ?? false,
  }
}

function rowLabel(keys: string[]): string {
  return keys.length > 0 ? keys.join('/') : '（新单）'
}

function hasChooser(field: FormFieldDefinition): boolean {
  return field.choosers.some(source => source.active && (source.table || source.sourceKey))
}

/**
 * 就地操作卡：助手的动作**在卡片里做完**，不再"生成草稿 → 跳走"。
 *
 * <para>
 * 可编辑字段由服务端元数据驱动（`form-chooser` / `form-definition`），字段清单不写死在这里；
 * 引用数据一律走统一选择器（`formField` 或 `sourceKey`），卡片不自建选择弹窗。
 * </para>
 *
 * <para>
 * "能不能做、为什么不能"来自服务端：预演在真实事务内跑完整条路径后回滚，执行前再独立重新授权。
 * 卡上的判定只是预告，**不代替服务端放行**，也不把模型的话当指令——参数只来自这些结构化字段。
 * </para>
 */
export function ActionCard({ draft }: { draft: AssistantRecordActionPreview }) {
  const [definition, setDefinition] = useState<FormDefinition | null>(null)
  const [fieldsError, setFieldsError] = useState<string | null>(null)
  const [rows, setRows] = useState<CardRow[]>(() => draft.rows.map(row => toCardRow(row)))
  const [moduleDenialMessage, setModuleDenialMessage] = useState<string | null>(
    draft.blocked ? draft.moduleDenialMessage ?? draft.moduleDenialCode : null)
  const [notes, setNotes] = useState<string[]>(draft.notes)
  const [busy, setBusy] = useState<'preview' | 'apply' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<AssistantRecordActionResult | null>(null)
  const [chooser, setChooser] = useState<{ rowIndex: number; field: FormFieldDefinition } | null>(null)
  // 同一张卡是同一次用户意图：确认键在这里生成一次，重复点确认不会写两次。
  const confirmKeyRef = useRef(newIdempotencyKey())
  const isDelete = draft.action === 'delete'

  useEffect(() => {
    if (draft.blocked) return
    let cancelled = false
    setFieldsError(null)
    void getFormDefinition(draft.moduleId, draft.action === 'insert' ? 'new' : 'edit')
      .then(data => {
        if (cancelled) return
        // 响应异常时按"字段元数据不可用"处理：卡片照常能看结论与执行，不让抽屉崩掉
        if (!data || !Array.isArray(data.masterFields)) {
          setFieldsError('字段元数据不可用，无法就地编辑。')
          return
        }
        setDefinition(data)
      })
      .catch((cause: unknown) => { if (!cancelled) setFieldsError(problemMessage(cause)) })
    return () => { cancelled = true }
  }, [draft.moduleId, draft.action, draft.blocked])

  /** 可提交字段：与保存路径同一口径（服务端维护/虚拟/隐藏字段不在这里）。 */
  const submitFields = useMemo(
    () => (definition ? writableFields(definition.masterFields) : []),
    [definition])

  /** 就地可改的字段：主键与自增列不作为输入（修改/删除时主键是"定位"而不是"要改的值"）。 */
  const inputFields = useMemo(() => submitFields.filter(field =>
    !field.isAutoIncrement && (draft.action === 'insert' || !field.isPrimaryKey)), [submitFields, draft.action])

  const setRowValue = (rowIndex: number, key: string, value: string) => {
    setRows(current => current.map((row, index) =>
      index === rowIndex ? { ...row, values: { ...row.values, [key]: value } } : row))
  }

  const toggleRow = (rowIndex: number) => {
    setRows(current => current.map((row, index) =>
      index === rowIndex ? { ...row, selected: !row.selected } : row))
  }

  const payload = (onlySelected: boolean): AssistantRecordActionPayload => ({
    module_id: draft.moduleId,
    action: draft.action,
    rows: rows
      .filter(row => !onlySelected || row.selected)
      .map(row => ({ keys: row.keys, values: row.values })),
  })

  const handlePreview = async () => {
    if (busy) return
    setBusy('preview')
    setError(null)
    try {
      const data = await previewRecordAction(payload(false))
      setRows(current => data.rows.map((row, index) => toCardRow(row, current[index])))
      setNotes(data.notes)
      setModuleDenialMessage(data.blocked ? data.moduleDenialMessage ?? data.moduleDenialCode : null)
      setResult(null)
    } catch (cause) {
      setError(problemMessage(cause))
    } finally {
      setBusy(null)
    }
  }

  const handleApply = async () => {
    if (busy) return
    setBusy('apply')
    setError(null)
    try {
      setResult(await applyRecordAction(payload(isDelete), confirmKeyRef.current))
    } catch (cause) {
      setError(problemMessage(cause))
    } finally {
      setBusy(null)
    }
  }

  const applyChooser = (rowIndex: number, field: FormFieldDefinition, picked: UnifiedChooserRow) => {
    const source = field.choosers.find(item => item.active && (item.table || item.sourceKey))
    const mapping = parseReturnItems(source?.returnMapping)
    setRows(current => current.map((row, index) => {
      if (index !== rowIndex) return row
      const next = { ...row.values }
      for (const item of mapping) {
        if (picked[item.column] === undefined) continue
        next[item.target] = String(picked[item.column] ?? '')
      }
      return { ...row, values: next }
    }))
    setChooser(null)
  }

  const chooserSource = (field: FormFieldDefinition): UnifiedChooserSource => {
    const source = field.choosers.find(item => item.active && (item.table || item.sourceKey))
    if (source?.sourceKey) return { kind: 'sourceKey', key: source.sourceKey }
    return { kind: 'formField', moduleId: String(draft.moduleId), fieldKey: field.key, serialNo: source?.serialNo ?? null }
  }

  const executed = result !== null && result.rows.some(row => row.succeeded)
  const selectedCount = rows.filter(row => row.selected).length
  const canApply = !moduleDenialMessage && !fieldsError && !executed && !busy
    && (isDelete ? selectedCount > 0 : rows.length > 0)

  if (draft.blocked) {
    return (
      <div className="erp-assistant-draft-card">
        <div className="fw-bold mb-1">🗂 {ACTION_LABELS[draft.action]}：{draft.moduleTitle || `模块 #${draft.moduleId}`}</div>
        {/* 权限问题必须明说：只给"操作失败"会把人推向绕过系统 */}
        <div className="erp-assistant-error" role="alert">{moduleDenialMessage}</div>
      </div>
    )
  }

  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">
        🗂 {ACTION_LABELS[draft.action]}：{draft.moduleTitle || `模块 #${draft.moduleId}`}（{rows.length} 行）
      </div>

      {moduleDenialMessage && <div className="erp-assistant-error" role="alert">{moduleDenialMessage}</div>}
      {fieldsError && <div className="text-warning small">{fieldsError}</div>}

      {rows.map((row, rowIndex) => {
        const outcome = result?.rows[rowIndex]
        return (
          <div
            key={`${rowLabel(row.keys)}-${rowIndex}`}
            className={`erp-assistant-action-row${row.allowed ? '' : ' is-denied'}`}
          >
            <div className="erp-assistant-action-head">
              {isDelete && (
                <input
                  type="checkbox"
                  className="form-check-input"
                  checked={row.selected}
                  disabled={!row.allowed || executed}
                  aria-label={`选择删除 ${rowLabel(row.keys)}`}
                  onChange={() => toggleRow(rowIndex)}
                />
              )}
              <span className="fw-bold">{rowLabel(row.keys)}</span>
              <span className="text-secondary small">
                {row.allowed ? '可执行' : `不可执行：${row.denialMessage ?? row.denialCode}`}
              </span>
            </div>

            {inputFields.length > 0 && (
              <div className="erp-assistant-action-fields">
                {inputFields.map(field => (
                  <label key={field.key} className="erp-assistant-action-field">
                    <span className="text-secondary small">{field.label}{field.isRequired ? ' *' : ''}</span>
                    {hasChooser(field) ? (
                      <span className="d-flex gap-1">
                        <input
                          className="form-control form-control-sm"
                          readOnly
                          value={row.values[field.key] ?? ''}
                          aria-label={`${field.label}（第 ${rowIndex + 1} 行）`}
                        />
                        <button
                          className="btn btn-sm btn-ghost-secondary"
                          type="button"
                          disabled={executed}
                          aria-label={`选择 ${field.label}（第 ${rowIndex + 1} 行）`}
                          onClick={() => setChooser({ rowIndex, field })}
                        >
                          选择
                        </button>
                      </span>
                    ) : field.isReadonly ? (
                      <span className="text-secondary small">{row.values[field.key] || '—'}</span>
                    ) : (
                      <input
                        className="form-control form-control-sm"
                        value={row.values[field.key] ?? ''}
                        placeholder={emptyValue(field) || undefined}
                        disabled={executed || !row.allowed}
                        aria-label={`${field.label}（第 ${rowIndex + 1} 行）`}
                        onChange={event => setRowValue(rowIndex, field.key, event.target.value)}
                      />
                    )}
                  </label>
                ))}
              </div>
            )}

            {row.impacts.length > 0 && (
              <div className="erp-assistant-action-impact">
                {row.impacts.map((impact, impactIndex) => (
                  <div key={impactIndex} className="small text-secondary">
                    影响面：{impact.effectKey} {impact.eventCode}
                    {impact.targetTable ? ` → ${impact.targetTable}.${impact.targetField} [${impact.opCode}]` : ''}
                  </div>
                ))}
              </div>
            )}

            {outcome && (
              <div className={outcome.succeeded ? 'text-success small' : 'text-warning small'}>
                {outcome.succeeded
                  ? `已保存：${rowLabel(outcome.resultKeys ?? outcome.keys)}`
                  : `未保存：${outcome.message ?? outcome.code}`}
              </div>
            )}
          </div>
        )
      })}

      {notes.map(note => (
        <div key={note} className="text-secondary small">{note}</div>
      ))}

      <div className="erp-assistant-action-actions">
        <button className="btn btn-sm btn-ghost-secondary" type="button"
          disabled={busy !== null || executed || isDelete && rows.length === 0}
          onClick={() => void handlePreview()}>
          {busy === 'preview' ? '预演中…' : '重新预演'}
        </button>
        <button
          className={`btn btn-sm ${isDelete ? 'btn-danger' : 'btn-primary'}`}
          type="button"
          disabled={!canApply}
          onClick={() => void handleApply()}
        >
          {busy === 'apply'
            ? '执行中…'
            : isDelete
              ? `确认删除所选${selectedCount > 0 ? `（${selectedCount} 行）` : ''}`
              : '确认执行'}
        </button>
      </div>

      {error && <div className="erp-assistant-error" role="alert">{error}</div>}

      {chooser && (
        <UnifiedChooser
          open
          title={`${chooser.field.label}（第 ${chooser.rowIndex + 1} 行）`}
          source={chooserSource(chooser.field)}
          mode="single"
          onPick={rows => {
            const picked = rows[0]
            if (picked) applyChooser(chooser.rowIndex, chooser.field, picked)
            else setChooser(null)
          }}
          onClose={() => setChooser(null)}
        />
      )}
    </div>
  )
}
