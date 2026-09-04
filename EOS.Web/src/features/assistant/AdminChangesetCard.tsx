import { useState } from 'react'
import { applyChangeset } from './api'
import type { AssistantAdminDraft, AssistantApplyResult } from './types'

function problemMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return '执行失败。'
}

/** 元数据变更集确认卡：试算 diff 预览 + 确认执行（结构化确认，自然语言无效）。 */
export function AdminChangesetCard({ draft }: { draft: AssistantAdminDraft }) {
  const [applying, setApplying] = useState(false)
  const [result, setResult] = useState<AssistantApplyResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  const handleApply = async (changeset: unknown) => {
    if (applying || result) return
    setApplying(true)
    setError(null)
    try {
      setResult(await applyChangeset(changeset))
    } catch (err) {
      setError(problemMessage(err))
    } finally {
      setApplying(false)
    }
  }

  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">🧩 元数据变更集：{draft.goal || '（未命名）'}</div>
      {draft.blocked && (
        <div className="erp-assistant-error" role="alert">试算未通过，已拦截，不会执行。</div>
      )}
      {draft.errors.map((message, index) => (
        <div key={index} className="text-warning small">{message}</div>
      ))}
      {draft.tables.map(table => (
        <div key={`${table.table}-${table.action}`} className="mb-1">
          <div>
            <strong>{table.table}</strong>
            <span className="text-secondary small"> [{table.action}] {table.status}</span>
          </div>
          {table.wouldCreate.map(line => (
            <div key={line} className="text-success small">新建：{line}</div>
          ))}
          {table.wouldSkip.map(line => (
            <div key={line} className="text-secondary small">跳过：{line}</div>
          ))}
          {table.errors.map(line => (
            <div key={line} className="text-warning small">错误：{line}</div>
          ))}
        </div>
      ))}
      {!draft.blocked && !result && (
        <button className="btn btn-sm btn-danger" type="button" disabled={applying}
          onClick={() => void handleApply(draft.changeset)}>
          {applying ? '执行中…' : '确认执行（写元数据）'}
        </button>
      )}
      {error && <div className="erp-assistant-error" role="alert">{error}</div>}
      {result && (
        <div className="text-success small">
          执行完成：登记表 {result.tablesRegistered} 个，新增字段 {result.fieldsCreated} 个，
          跳过 {result.fieldsSkipped} 个。
          {result.notes.map(note => (
            <div key={note}>{note}</div>
          ))}
        </div>
      )}
    </div>
  )
}
