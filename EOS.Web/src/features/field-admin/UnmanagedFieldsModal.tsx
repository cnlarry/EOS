import { useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { useEffect, useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

export interface UnmanagedField {
  fieldId: string
  dataType: string
}

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

export function UnmanagedFieldsModal({ open, tableId, tableDescription, onClose, onSaved }: UnmanagedFieldsModalProps) {
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [result, setResult] = useState<BatchCreateResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const fields = useQuery({
    queryKey: ['field-admin', 'unmanaged', tableId],
    queryFn: () => apiClient.get<UnmanagedField[]>(`/admin/tables/${encodeURIComponent(tableId)}/fields/unmanaged`),
    enabled: open,
  })

  useEffect(() => {
    if (!open) {
      setRowSelection({})
      setResult(null)
      setError(null)
      return
    }
    setResult(null)
    setError(null)
  }, [open, tableId])

  useEffect(() => {
    if (!open || !fields.data) return
    // 字段列表刷新后，仅保留仍存在的选择
    const available = new Set(fields.data.map((item) => item.fieldId))
    setRowSelection((current) => Object.fromEntries(Object.entries(current).filter(([id]) => available.has(id))))
  }, [open, fields.data])

  const selectedCount = Object.values(rowSelection).filter(Boolean).length

  const columns = useMemo<ColumnDef<UnmanagedField, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', resizable: false, frozenLeft: true, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="全选"
          checked={table.getIsAllPageRowsSelected()}
          ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={(event) => { row.toggleSelected(event.target.checked) }}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    { accessorKey: 'fieldId', header: '字段名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'dataType', header: '类型', cell: (info) => <span className="text-secondary">{String(info.getValue())}</span> },
  ], [])

  if (!open) return null

  const handleGenerate = async () => {
    const fieldIds = Object.keys(rowSelection).filter((id) => rowSelection[id])
    if (fieldIds.length === 0) return
    setSaving(true)
    setError(null)
    try {
      const created = await apiClient.post<BatchCreateResult>(`/admin/tables/${encodeURIComponent(tableId)}/fields/batch`, {
        tableId,
        fieldIds,
      })
      setResult(created)
      setRowSelection({})
      onSaved()
    } catch (saveError) {
      setError(describeApiError(saveError, '生成失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  const errorMessage = describeApiError(fields.error, '发生未知错误，请稍后重试。')

  return (
    <Modal
      title={`未管理字段批量生成（${tableDescription ?? tableId}）`}
      onClose={onClose}
      dialogClassName="erp-dialog-lg"
      footer={<>
        <Button variant="secondary" className="me-2" onClick={onClose}>关闭</Button>
        <Button variant="primary" loading={saving} disabled={selectedCount === 0 || saving} onClick={() => void handleGenerate()}>生成</Button>
      </>}
    >
      <div className="alert alert-info">
              下列物理列尚未在 FIELDS 中登记元数据。勾选后按「未受管理字段」规则生成：
              默认显示/默认列/可查询/可复制，描述取列说明（MS_Description，无则用列名）。
            </div>
            {result && (
              <div className={`alert ${result.created > 0 ? 'alert-success' : 'alert-warning'}`}>
                已生成 {result.created} 个字段元数据，跳过 {result.skipped} 个。
                {result.skippedReasons.length > 0 && (
                  <ul className="mb-0 mt-1 small">
                    {result.skippedReasons.map((reason, index) => <li key={index}>{reason}</li>)}
                  </ul>
                )}
              </div>
            )}
            {error && <div className="alert alert-danger">{error}</div>}
            {fields.isPending ? (
              <LoadingState label="正在加载未管理字段…" />
            ) : fields.isError ? (
              <ErrorState message={errorMessage} onRetry={() => void fields.refetch()} />
            ) : (fields.data ?? []).length === 0 ? (
              <div className="text-center text-secondary py-5">该表所有物理列均已有字段元数据。</div>
            ) : (
              <>
                <div className="d-flex align-items-center gap-3 mb-2">
                  <span className="text-secondary small">共 {fields.data?.length ?? 0} 个未管理字段，已选 {selectedCount} 个</span>
                </div>
                <div className="erp-unmanaged-table">
                  <ErpTable
                    columns={columns}
                    data={fields.data ?? []}
                    getRowId={(row) => row.fieldId}
                    rowSelection={rowSelection}
                    onRowSelectionChange={setRowSelection}
                    dense
                    empty={<div className="text-center text-secondary py-5">该表所有物理列均已有字段元数据。</div>}
                  />
                </div>
              </>
            )}
    </Modal>
  )
}
