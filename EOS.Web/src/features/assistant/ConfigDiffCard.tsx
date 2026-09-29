import { useRef, useState } from 'react'
import { newIdempotencyKey } from '../document-workbench/formEditorUtils'
import { applyConfigChange, previewConfigChange } from './api'
import type {
  AssistantConfigApplyResult,
  AssistantConfigDiff,
  ConfigDiffItem,
} from './types'

/** 卡内一项：服务端算出的对照 + 勾选态（默认勾选：用户要的就是"照 A 配 B"）。 */
interface CardItem extends ConfigDiffItem {
  selected: boolean
}

const SURFACE_LABELS: Record<string, string> = {
  fields: '字段配置',
  datasource: '数据来源',
  buttons: '自定义按钮',
  effect: '效果键',
}

function problemMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return '操作失败。'
}

function toCardItem(item: ConfigDiffItem): CardItem {
  return { ...item, selected: true }
}

/**
 * 配置改动的应用结果：逐项回读"写进去了没有、没写进去是为什么"。
 * 与对照卡的结论同源（服务端返回的形状），两条入口看起来一样。
 */
export function ConfigApplyCard({ result }: { result: AssistantConfigApplyResult }) {
  const applied = result.items.filter(item => item.applied).length
  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">
        🧩 配置改动结果（{SURFACE_LABELS[result.surface] ?? result.surface}）：{result.targetLabel}，
        成功 {applied} 项、失败 {result.items.length - applied} 项
      </div>
      {result.blockedMessage && (
        <div className="erp-assistant-error" role="alert">{result.blockedMessage}</div>
      )}
      {result.items.map(item => (
        <div key={item.id} className="small">
          <span className="fw-bold">{item.target}</span>{' '}
          {item.applied
            ? <span className="text-success">已写入{item.message ? `（${item.message}）` : ''}</span>
            : <span className="text-warning">未写入：{item.message ?? item.code}</span>}
        </div>
      ))}
      {result.notes.map((note, index) => (
        <div key={index} className="erp-assistant-config-note">{note}</div>
      ))}
    </div>
  )
}

/**
 * 配置改动对照卡：**旧值 / 新值 / 影响面**逐项列出，逐项可取消勾选。
 *
 * <para>
 * 不可预演的项在这里**单独标注**：预演只覆盖批核生效与解批，保存阶段的效果链、字段与数据来源的
 * 元数据改动都没有可跑的链路。把它们混进"已校验"会让用户以为"点过预演就没事了"——那比不预演更危险。
 * </para>
 *
 * <para>
 * 应用时只回传"要应用哪些项"，写入值由服务端重新规划得出：卡片不携带任何可写值，也不自建写路径。
 * </para>
 */
export function ConfigDiffCard({ draft }: { draft: AssistantConfigDiff }) {
  const [items, setItems] = useState<CardItem[]>(() => draft.items.map(toCardItem))
  const [blockedMessage, setBlockedMessage] = useState<string | null>(
    draft.blocked ? draft.blockedMessage ?? draft.blockedCode : null)
  const [notes, setNotes] = useState<string[]>(draft.notes)
  const [busy, setBusy] = useState<'preview' | 'apply' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<AssistantConfigApplyResult | null>(null)
  // 同一张卡是同一次用户意图：确认键在这里生成一次，重复点确认不会写两次。
  const confirmKeyRef = useRef(newIdempotencyKey())

  const selectedIds = items.filter(item => item.selected).map(item => item.id)
  const notPreviewable = items.filter(item => !item.previewable).length
  const surfaceLabel = SURFACE_LABELS[draft.surface] ?? draft.surface

  function toggle(id: string) {
    setItems(previous => previous.map(item => (item.id === id ? { ...item, selected: !item.selected } : item)))
  }

  async function handlePreview() {
    setBusy('preview')
    setError(null)
    try {
      const next = await previewConfigChange(draft.request, selectedIds)
      setItems(next.items.map(item => {
        // 勾选态与卡片实例对齐：服务端不知道用户勾了谁，这一层状态留在前端。
        const previous = items.find(candidate => candidate.id === item.id)
        return { ...item, selected: previous?.selected ?? true }
      }))
      setNotes(next.notes)
      setBlockedMessage(next.blocked ? next.blockedMessage ?? next.blockedCode : null)
    } catch (cause) {
      setError(problemMessage(cause))
    } finally {
      setBusy(null)
    }
  }

  async function handleApply() {
    setBusy('apply')
    setError(null)
    try {
      const applied = await applyConfigChange(draft.request, selectedIds, confirmKeyRef.current)
      setResult(applied)
      setNotes(applied.notes)
    } catch (cause) {
      setError(problemMessage(cause))
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="erp-assistant-draft-card">
      <div className="fw-bold mb-1">
        🧩 配置改动对照（{surfaceLabel}）：照「{draft.sourceLabel}」配「{draft.targetLabel}」
      </div>
      {blockedMessage && <div className="erp-assistant-error" role="alert">{blockedMessage}</div>}
      {!blockedMessage && items.length === 0 && (
        <div className="small text-secondary">源与目标已经一致，没有需要改的地方。</div>
      )}

      {items.map(item => (
        <div
          key={item.id}
          className={`erp-assistant-config-item${item.previewable ? '' : ' is-unpreviewable'}`}
        >
          <div className="erp-assistant-action-head">
            <input
              type="checkbox"
              className="form-check-input"
              checked={item.selected}
              disabled={busy !== null || result !== null}
              aria-label={`应用 ${item.label}`}
              onChange={() => toggle(item.id)}
            />
            <span className="fw-bold">{item.label}</span>
            <span className="text-secondary small">{item.target}</span>
          </div>
          <table className="erp-assistant-draft-table">
            <thead>
              <tr>
                <th>项目</th>
                <th>旧值</th>
                <th>新值</th>
              </tr>
            </thead>
            <tbody>
              {item.changes.map(change => (
                <tr key={change.field}>
                  <td>{change.label}</td>
                  <td className="text-secondary">{change.oldValue ?? '（无）'}</td>
                  <td className="fw-bold">{change.newValue ?? '（清空）'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {item.impacts.length > 0 && (
            <div className="erp-assistant-config-note">影响面：{item.impacts.join('；')}</div>
          )}
          {item.previewNote && (
            <div className={`erp-assistant-config-note${item.previewable ? '' : ' is-warning'}`}>
              {item.previewNote}
            </div>
          )}
          {item.previewSummary && (
            <div className="erp-assistant-config-note is-previewed">预演结果：{item.previewSummary}</div>
          )}
        </div>
      ))}

      {items.length > 0 && (
        <div className="erp-assistant-config-summary">
          {notPreviewable > 0
            ? `本次 ${items.length} 项改动中有 ${notPreviewable} 项无法预演：它们没有被预演过，不能当成已校验。`
            : `本次 ${items.length} 项改动全部完成预演（预演跑完即回滚，库内无变化）。`}
        </div>
      )}
      {notes.map((note, index) => (
        <div key={index} className="erp-assistant-config-note">{note}</div>
      ))}

      {error && <div className="erp-assistant-error" role="alert">{error}</div>}

      {result && (
        <div className="erp-assistant-config-result">
          <div className="fw-bold">
            {result.blockedMessage ?? `已应用：成功 ${result.items.filter(item => item.applied).length} 项、失败 ${result.items.filter(item => !item.applied).length} 项`}
          </div>
          {result.items.map(item => (
            <div key={item.id} className="small">
              <span className="fw-bold">{item.target}</span>{' '}
              {item.applied
                ? <span className="text-success">已写入{item.message ? `（${item.message}）` : ''}</span>
                : <span className="text-warning">未写入：{item.message ?? item.code}</span>}
            </div>
          ))}
        </div>
      )}

      {!result && (
        <div className="erp-assistant-action-actions">
          <button
            className="btn btn-sm btn-outline-secondary"
            type="button"
            disabled={busy !== null || items.length === 0}
            onClick={() => void handlePreview()}
          >
            {busy === 'preview' ? '检查中…' : '重新检查（预演 / 自检）'}
          </button>
          <button
            className="btn btn-sm btn-primary"
            type="button"
            disabled={busy !== null || selectedIds.length === 0}
            onClick={() => void handleApply()}
          >
            {busy === 'apply' ? '应用中…' : `应用所选 ${selectedIds.length} 项`}
          </button>
        </div>
      )}
    </div>
  )
}
