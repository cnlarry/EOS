import { useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { useEffect, useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

export interface GhostField {
  fieldId: string
  description: string
  dataType: string
}

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
 * 清单由服务端按下述口径给出——虚拟字段结构上就没有物理列，不是幽灵字段，不在此列表；
 * 服务端在真正删除前还会逐条复核，前端列表过期也不会误删。
 */
export function GhostFieldsModal({ open, tableId, tableDescription, onClose, onSaved }: GhostFieldsModalProps) {
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [result, setResult] = useState<CleanupGhostFieldsResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const ghosts = useQuery({
    queryKey: ['field-admin', 'ghosts', tableId],
    queryFn: () => apiClient.get<GhostField[]>(`/admin/tables/${encodeURIComponent(tableId)}/fields/ghosts`),
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
    if (!open || !ghosts.data) return
    // 字段列表刷新后，仅保留仍存在的选择
    const available = new Set(ghosts.data.map((item) => item.fieldId))
    setRowSelection((current) => Object.fromEntries(Object.entries(current).filter(([id]) => available.has(id))))
  }, [open, ghosts.data])

  const selectedCount = Object.values(rowSelection).filter(Boolean).length

  const columns = useMemo<ColumnDef<GhostField, unknown>[]>(() => [
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
    { accessorKey: 'description', header: '标题' },
    { accessorKey: 'dataType', header: '类型', cell: (info) => <span className="text-secondary">{String(info.getValue())}</span> },
  ], [])

  if (!open) return null

  const handleCleanup = async () => {
    const fieldIds = Object.keys(rowSelection).filter((id) => rowSelection[id])
    if (fieldIds.length === 0) return
    setSaving(true)
    setError(null)
    try {
      const cleaned = await apiClient.post<CleanupGhostFieldsResult>(`/admin/tables/${encodeURIComponent(tableId)}/fields/ghosts/cleanup`, {
        tableId,
        fieldIds,
      })
      setResult(cleaned)
      setRowSelection({})
      onSaved()
    } catch (saveError) {
      setError(describeApiError(saveError, '清理失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  const errorMessage = describeApiError(ghosts.error, '发生未知错误，请稍后重试。')

  return (
    <Modal
      title={`清理幽灵字段（${tableDescription ?? tableId}）`}
      onClose={onClose}
      dialogClassName="erp-dialog-lg"
      footer={<>
        <Button variant="secondary" className="me-2" onClick={onClose}>关闭</Button>
        <Button variant="danger" loading={saving} disabled={selectedCount === 0 || saving} onClick={() => void handleCleanup()}>清理</Button>
      </>}
    >
      <div className="alert alert-warning">
        下列字段元数据已找不到对应物理列（物理列被改名或删除）。清理只删除字段元数据及其历史列配置，
        <strong>不动物理表</strong>；虚拟字段没有物理列，不属幽灵字段，不在本清单内。
      </div>
      {result && (
        <div className={`alert ${result.removed > 0 ? 'alert-success' : 'alert-warning'}`}>
          已清理 {result.removed} 个幽灵字段，跳过 {result.skipped} 个。
          {result.skippedReasons.length > 0 && (
            <ul className="mb-0 mt-1 small">
              {result.skippedReasons.map((reason, index) => <li key={index}>{reason}</li>)}
            </ul>
          )}
        </div>
      )}
      {error && <div className="alert alert-danger">{error}</div>}
      {ghosts.isPending ? (
        <LoadingState label="正在加载幽灵字段…" />
      ) : ghosts.isError ? (
        <ErrorState message={errorMessage} onRetry={() => void ghosts.refetch()} />
      ) : (ghosts.data ?? []).length === 0 ? (
        <div className="text-center text-secondary py-5">该表没有幽灵字段：所有字段元数据都能对应到物理列。</div>
      ) : (
        <>
          <div className="d-flex align-items-center gap-3 mb-2">
            <span className="text-secondary small">共 {ghosts.data?.length ?? 0} 个幽灵字段，已选 {selectedCount} 个</span>
          </div>
          <div className="erp-ghost-table">
            <ErpTable
              columns={columns}
              data={ghosts.data ?? []}
              getRowId={(row) => row.fieldId}
              rowSelection={rowSelection}
              onRowSelectionChange={setRowSelection}
              dense
              empty={<div className="text-center text-secondary py-5">该表没有幽灵字段。</div>}
            />
          </div>
        </>
      )}
    </Modal>
  )
}
