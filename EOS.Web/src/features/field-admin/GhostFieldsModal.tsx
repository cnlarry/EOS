import { useEffect, useState } from 'react'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

export interface CleanupGhostFieldsResult {
  removed: number
  skipped: number
  skippedReasons: string[]
}

interface GhostFieldsModalProps {
  open: boolean
  tableId: string
  tableDescription?: string
  onClose: () => void
  onSaved: () => void
}

/**
 * 幽灵字段清理（2302）：元数据还在、物理列已被改名或删除的字段。
 *
 * 候选走统一选择器（数据源 `field-admin.ghost-fields`，按 `args.tableId` 收窄，权限门 2302）。
 * 判定口径与清理一致：虚拟字段结构上就没有物理列，不是幽灵字段，不在候选里；
 * 服务端在真正删除前还会逐条复核，前端列表过期也不会误删。
 */
export function GhostFieldsModal({ open, tableId, tableDescription, onClose, onSaved }: GhostFieldsModalProps) {
  const [selectedKeys, setSelectedKeys] = useState<Record<string, boolean>>({})
  const [result, setResult] = useState<CleanupGhostFieldsResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) {
      setSelectedKeys({})
      setResult(null)
      setError(null)
    }
  }, [open, tableId])

  const handleCleanup = async (rows: UnifiedChooserRow[]) => {
    const fieldIds = rows.map((row) => String(row.F_ID ?? '')).filter((id) => id !== '')
    if (fieldIds.length === 0) return
    setSaving(true)
    setError(null)
    try {
      const cleaned = await apiClient.post<CleanupGhostFieldsResult>(
        `/admin/tables/${encodeURIComponent(tableId)}/fields/ghosts/cleanup`,
        { tableId, fieldIds },
      )
      setResult(cleaned)
      setSelectedKeys({})
      onSaved()
    } catch (saveError) {
      setError(describeApiError(saveError, '清理失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <UnifiedChooser
      open={open}
      title={`清理幽灵字段（${tableDescription ?? tableId}）`}
      source={{ kind: 'sourceKey', key: 'field-admin.ghost-fields', args: { tableId } }}
      mode="multi"
      dialogSize="lg"
      storageKey="field-admin-ghost-fields"
      searchPlaceholder="搜索字段名 / 标题 / 类型"
      emptyText="该表没有幽灵字段：所有字段元数据都能对应到物理列。"
      getRowId={(row) => String(row.F_ID ?? '')}
      selectedKeys={selectedKeys}
      onSelectedKeysChange={setSelectedKeys}
      columnRenderers={{
        F_ID: (row) => <span className="font-monospace fw-semibold">{String(row.F_ID ?? '')}</span>,
        DATA_TYPE: (row) => <span className="text-secondary">{String(row.DATA_TYPE ?? '')}</span>,
      }}
      extra={(
        <div className="d-flex flex-column gap-2 mt-2">
          <div className="text-secondary small">
            这些字段元数据已找不到对应物理列（物理列被改名或删除）。清理只删除字段元数据及其历史列配置，
            <strong>不动物理表</strong>；虚拟字段没有物理列，不属幽灵字段，不在候选里。
          </div>
          {saving ? <div className="text-secondary small">正在清理…</div> : null}
          {error ? <div className="alert alert-danger mb-0">{error}</div> : null}
          {result ? (
            <div className={`alert ${result.removed > 0 ? 'alert-success' : 'alert-warning'} mb-0`}>
              已清理 {result.removed} 个幽灵字段，跳过 {result.skipped} 个。
              {result.skippedReasons.length > 0 ? (
                <ul className="mb-0 mt-1 small">
                  {result.skippedReasons.map((reason, index) => <li key={index}>{reason}</li>)}
                </ul>
              ) : null}
            </div>
          ) : null}
        </div>
      )}
      onPick={(rows) => void handleCleanup(rows)}
      onClose={onClose}
    />
  )
}
