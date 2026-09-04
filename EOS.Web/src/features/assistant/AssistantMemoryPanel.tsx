import { useCallback, useEffect, useState } from 'react'
import { deleteMemory, forgetAllMemory, listMemory, listPendingMemory, resolvePendingMemory, saveMemory } from './api'
import type { PendingMemory } from './api'
import type { AssistantMemory } from './types'

/** 显式记忆管理（本人可见）：查看/新增/删除。打开时延迟加载，不影响抽屉主流程。 */
export function AssistantMemoryPanel({ onAskDigest, onClose }: {
  onAskDigest: () => void
  onClose: () => void
}) {
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [preferences, setPreferences] = useState<string | null>(null)
  const [memories, setMemories] = useState<AssistantMemory[]>([])
  const [pendings, setPendings] = useState<PendingMemory[]>([])
  const [forgetArmed, setForgetArmed] = useState(false)
  const [memoryType, setMemoryType] = useState('fact')
  const [memoryKey, setMemoryKey] = useState('')
  const [memoryValue, setMemoryValue] = useState('')
  const [saving, setSaving] = useState(false)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [data, pending] = await Promise.all([listMemory(), listPendingMemory()])
      setPreferences(data.preferences)
      setMemories(data.memories)
      setPendings(pending.memories)
    } catch {
      setError('记忆加载失败，请重试。')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const handleSave = useCallback(async () => {
    if (!memoryKey.trim() || !memoryValue.trim() || saving) return
    setSaving(true)
    setError(null)
    try {
      await saveMemory({ memoryType, memoryKey: memoryKey.trim(), memoryValue: memoryValue.trim() })
      setMemoryKey('')
      setMemoryValue('')
      await refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : '保存记忆失败。')
    } finally {
      setSaving(false)
    }
  }, [memoryKey, memoryValue, memoryType, refresh, saving])

  const handleDelete = useCallback(async (id: string) => {
    setError(null)
    try {
      await deleteMemory(id)
      setMemories(prev => prev.filter(item => item.id !== id))
    } catch {
      setError('删除记忆失败。')
    }
  }, [])

  const handleResolve = useCallback(async (id: string, confirm: boolean) => {
    setError(null)
    try {
      await resolvePendingMemory(id, confirm)
      setPendings(prev => prev.filter(item => item.id !== id))
      if (confirm) await refresh()
    } catch {
      setError('确认记忆失败。')
    }
  }, [refresh])

  const handleForget = useCallback(async () => {
    if (!forgetArmed) {
      setForgetArmed(true)
      return
    }
    setError(null)
    try {
      await forgetAllMemory()
      setForgetArmed(false)
      await refresh()
    } catch {
      setError('清空记忆失败。')
    }
  }, [forgetArmed, refresh])

  return (
    <div className="erp-assistant-memory" aria-label="我的记忆">
      <div className="d-flex align-items-center justify-content-between mb-2">
        <strong>我的记忆（仅本人可见）</strong>
        <div className="d-flex gap-1">
          <button className="btn btn-sm btn-ghost-secondary" type="button" title="请助手总结偏好与记忆" onClick={onAskDigest}>
            今日摘要
          </button>
          <button className="btn btn-sm btn-ghost-secondary" type="button" aria-label="关闭记忆面板" onClick={onClose}>
            ✕
          </button>
        </div>
      </div>
      {preferences && (
        <div className="text-secondary small mb-2">偏好：{preferences}</div>
      )}
      {loading && <div className="text-secondary small">加载中…</div>}
      {error && <div className="erp-assistant-error" role="alert">{error}</div>}
      {pendings.length > 0 && (
        <div className="mb-2" aria-label="待确认记忆">
          <div className="text-secondary small mb-1">AI 想记住这些，请确认（确认前不会生效）：</div>
          {pendings.map(item => (
            <div key={item.id} className="erp-assistant-memory-item">
              <span className="badge bg-warning me-1">待确认{item.confidence ?? ''}</span>
              <strong>{item.key}</strong>
              <div className="text-secondary small">{item.value}</div>
              <div className="d-flex gap-1">
                <button className="btn btn-sm btn-primary" type="button"
                  aria-label={`确认记住 ${item.key}`}
                  onClick={() => void handleResolve(item.id, true)}>
                  确认
                </button>
                <button className="btn btn-sm btn-ghost-secondary" type="button"
                  aria-label={`拒绝记住 ${item.key}`}
                  onClick={() => void handleResolve(item.id, false)}>
                  拒绝
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
      <div className="mb-2">
        <button className="btn btn-sm btn-ghost-danger" type="button"
          onClick={() => void handleForget()}>
          {forgetArmed ? '再次点击确认清空全部记忆' : '忘记我（清空全部记忆）'}
        </button>
      </div>
      {!loading && memories.length === 0 && (
        <div className="text-secondary small mb-2">暂无记忆，可把常用查询与偏好记下来。</div>
      )}
      {memories.map(item => (
        <div key={item.id} className="erp-assistant-memory-item">
          <span className="badge bg-secondary me-1">{item.type}</span>
          <strong>{item.key}</strong>
          <div className="text-secondary small">{item.value}</div>
          <button className="btn btn-sm btn-ghost-danger" type="button"
            aria-label={`删除记忆 ${item.key}`}
            onClick={() => void handleDelete(item.id)}>
            删除
          </button>
        </div>
      ))}
      <div className="erp-assistant-memory-form">
        <select className="form-select form-select-sm" value={memoryType}
          onChange={(event) => setMemoryType(event.target.value)} aria-label="记忆类型">
          <option value="fact">事项</option>
          <option value="preference">偏好</option>
          <option value="favorite">常用</option>
        </select>
        <input className="form-control form-control-sm" placeholder="标题，如：常用模块"
          value={memoryKey} onChange={(event) => setMemoryKey(event.target.value)} aria-label="记忆标题" />
        <textarea className="form-control form-control-sm" rows={2} placeholder="内容，如：先看送货单"
          value={memoryValue} onChange={(event) => setMemoryValue(event.target.value)} aria-label="记忆内容" />
        <button className="btn btn-sm btn-primary" type="button" disabled={saving || !memoryKey.trim() || !memoryValue.trim()}
          onClick={() => void handleSave()}>
          记住这条
        </button>
      </div>
    </div>
  )
}
