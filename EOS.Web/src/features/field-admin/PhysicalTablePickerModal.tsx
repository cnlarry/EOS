import { useEffect, useState } from 'react'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

export interface RegisterPhysicalTableResult {
  tableId: string
  description: string
  kind: string
  type: string
  fieldCreated: number
  fieldSkipped: number
  skippedReasons: string[]
}

interface PhysicalTablePickerModalProps {
  open: boolean
  onClose: () => void
  onRegistered: () => void
}

/**
 * 新增数据表元数据（2302）：从**未登记的物理表/视图**里选取，而不是手敲表名。
 *
 * 候选走统一选择器（数据源 `field-admin.unregistered-tables`，权限门 2302）：它的源是
 * `sys.objects` 而不是已登记的 TABLES 行——"还没登记"正是这个数据源的全部意义。
 * 登记后服务端按物理结构生成元数据：表说明取 MS_Description（无则用表名），字段的类型与说明来自
 * 物理列（列说明无则用列名），并带入主键、自增、计算列标志；**不创建物理对象**。
 *
 * 确认后弹窗**不关闭**：登记是可以连着做多张表的动作，结果就地显示在网格下方。
 */
export function PhysicalTablePickerModal({ open, onClose, onRegistered }: PhysicalTablePickerModalProps) {
  const [selectedKeys, setSelectedKeys] = useState<Record<string, boolean>>({})
  const [result, setResult] = useState<RegisterPhysicalTableResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) {
      setSelectedKeys({})
      setResult(null)
      setError(null)
    }
  }, [open])

  const handleRegister = async (rows: UnifiedChooserRow[]) => {
    const tableId = String(rows[0]?.T_ID ?? '')
    if (tableId === '') return
    setSaving(true)
    setError(null)
    try {
      const registered = await apiClient.post<RegisterPhysicalTableResult>('/admin/tables/from-physical', { tableId })
      setResult(registered)
      setSelectedKeys({})
      onRegistered()
    } catch (saveError) {
      setError(describeApiError(saveError, '登记失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <UnifiedChooser
      open={open}
      title="新增数据表元数据（选取物理表或视图）"
      source={{ kind: 'sourceKey', key: 'field-admin.unregistered-tables' }}
      mode="single"
      dialogSize="lg"
      storageKey="field-admin-unregistered-tables"
      searchPlaceholder="搜索表名 / 说明"
      emptyText="dbo 下的表与视图都已登记表元数据。"
      getRowId={(row) => String(row.T_ID ?? '')}
      selectedKeys={selectedKeys}
      onSelectedKeysChange={setSelectedKeys}
      columnRenderers={{
        T_ID: (row) => <span className="font-monospace fw-semibold">{String(row.T_ID ?? '')}</span>,
        OBJECT_TYPE: (row) => <span className="text-secondary">{String(row.OBJECT_TYPE ?? '') === 'V' ? '视图' : '表'}</span>,
        DESCRIPTION: (row) => <span className="text-secondary">{String(row.DESCRIPTION ?? '')}</span>,
        COLUMN_COUNT: (row) => <span className="text-secondary">{String(row.COLUMN_COUNT ?? '')}</span>,
      }}
      extra={(
        <div className="d-flex flex-column gap-2 mt-2">
          <div className="text-secondary small">
            登记按物理结构自动生成：表描述取表说明（无则用表名），字段的类型与说明来自物理列
            （列说明无则用列名），并带入主键、自增、计算列标志；<strong>不创建物理表</strong>，登记后可按需编辑。
          </div>
          {saving ? <div className="text-secondary small">正在登记…</div> : null}
          {error ? <div className="alert alert-danger mb-0">{error}</div> : null}
          {result ? (
            <div className={`alert ${result.fieldSkipped > 0 ? 'alert-warning' : 'alert-success'} mb-0`}>
              已登记 {result.tableId}（{result.type === 'VIEW' ? '视图' : '表'}）：描述「{result.description}」，
              生成 {result.fieldCreated} 个字段元数据，跳过 {result.fieldSkipped} 个。
              {result.skippedReasons.length > 0 ? (
                <ul className="mb-0 mt-1 small">
                  {result.skippedReasons.map((reason, index) => <li key={index}>{reason}</li>)}
                </ul>
              ) : null}
            </div>
          ) : null}
        </div>
      )}
      onPick={(rows) => void handleRegister(rows)}
      onClose={onClose}
    />
  )
}
