import { useEffect, useState } from 'react'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

export interface BatchCreateResult {
  created: number
  skipped: number
  skippedReasons: string[]
}

interface UnmanagedFieldsModalProps {
  open: boolean
  tableId: string
  tableDescription?: string
  onClose: () => void
  onSaved: () => void
}

/**
 * 未管理字段批量生成（2302）：物理列存在、FIELDS 里没有元数据的字段。
 *
 * 候选走统一选择器（数据源 `field-admin.unmanaged-fields`，按 `args.tableId` 收窄，权限门 2302）：
 * 判定与批量生成同源（`sys.columns − FIELDS`），勾选后服务端按「未受管理字段」规则生成
 * （默认显示 / 默认列 / 可查询 / 可复制，描述取列说明 MS_Description，无则用列名）。
 *
 * 确认后弹窗**不关闭**：结果就地显示在网格下方，方便接着处理别的批次。
 */
export function UnmanagedFieldsModal({ open, tableId, tableDescription, onClose, onSaved }: UnmanagedFieldsModalProps) {
  const [selectedKeys, setSelectedKeys] = useState<Record<string, boolean>>({})
  const [result, setResult] = useState<BatchCreateResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) {
      setSelectedKeys({})
      setResult(null)
      setError(null)
    }
  }, [open, tableId])

  const handleGenerate = async (rows: UnifiedChooserRow[]) => {
    const fieldIds = rows.map((row) => String(row.COLUMN_NAME ?? '')).filter((id) => id !== '')
    if (fieldIds.length === 0) return
    setSaving(true)
    setError(null)
    try {
      const created = await apiClient.post<BatchCreateResult>(`/admin/tables/${encodeURIComponent(tableId)}/fields/batch`, {
        tableId,
        fieldIds,
      })
      setResult(created)
      setSelectedKeys({})
      onSaved()
    } catch (saveError) {
      setError(describeApiError(saveError, '生成失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <UnifiedChooser
      open={open}
      title={`未管理字段批量生成（${tableDescription ?? tableId}）`}
      source={{ kind: 'sourceKey', key: 'field-admin.unmanaged-fields', args: { tableId } }}
      mode="multi"
      dialogSize="lg"
      storageKey="field-admin-unmanaged-fields"
      searchPlaceholder="搜索字段名 / 类型"
      emptyText="该表所有物理列均已有字段元数据。"
      getRowId={(row) => String(row.COLUMN_NAME ?? '')}
      selectedKeys={selectedKeys}
      onSelectedKeysChange={setSelectedKeys}
      columnRenderers={{
        COLUMN_NAME: (row) => <span className="font-monospace fw-semibold">{String(row.COLUMN_NAME ?? '')}</span>,
        DATA_TYPE: (row) => <span className="text-secondary">{String(row.DATA_TYPE ?? '')}</span>,
      }}
      extra={(
        <div className="d-flex flex-column gap-2 mt-2">
          <div className="text-secondary small">
            这些物理列尚未在 FIELDS 中登记元数据。勾选后按「未受管理字段」规则生成：
            默认显示 / 默认列 / 可查询 / 可复制，描述取列说明（MS_Description，无则用列名）。
          </div>
          {saving ? <div className="text-secondary small">正在生成…</div> : null}
          {error ? <div className="alert alert-danger mb-0">{error}</div> : null}
          {result ? (
            <div className={`alert ${result.created > 0 ? 'alert-success' : 'alert-warning'} mb-0`}>
              已生成 {result.created} 个字段元数据，跳过 {result.skipped} 个。
              {result.skippedReasons.length > 0 ? (
                <ul className="mb-0 mt-1 small">
                  {result.skippedReasons.map((reason, index) => <li key={index}>{reason}</li>)}
                </ul>
              ) : null}
            </div>
          ) : null}
        </div>
      )}
      onPick={(rows) => void handleGenerate(rows)}
      onClose={onClose}
    />
  )
}
