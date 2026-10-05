import { useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useMemo, useState } from 'react'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { radioSelectColumn } from '../../components/common/erpRadioSelectColumn'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

export interface PhysicalTableCandidate {
  tableId: string
  objectType: string
  description: string
  columnCount: number
}

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
 * 选取后服务端按物理结构登记：表说明取 MS_Description（无则用表名），字段的类型与说明来自物理列
 * （列说明无则用列名），主键/自增/计算列标志一并带入。不创建物理对象，登记后可自行编辑。
 */
export function PhysicalTablePickerModal({ open, onClose, onRegistered }: PhysicalTablePickerModalProps) {
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [selectedTable, setSelectedTable] = useState<string | null>(null)
  const [result, setResult] = useState<RegisterPhysicalTableResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const candidates = useQuery({
    queryKey: ['field-admin', 'physical-tables'],
    queryFn: () => apiClient.get<PhysicalTableCandidate[]>('/admin/lookups/physical-tables'),
    enabled: open,
  })

  const filtered = (candidates.data ?? []).filter((item) => {
    if (!keyword) return true
    const text = keyword.toLowerCase()
    return item.tableId.toLowerCase().includes(text) || item.description.toLowerCase().includes(text)
  })

  const columns = useMemo<ColumnDef<PhysicalTableCandidate, unknown>[]>(() => [
    radioSelectColumn<PhysicalTableCandidate>('field-admin-physical-tables', selectedTable, setSelectedTable),
    { accessorKey: 'tableId', header: '表名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'description', header: '说明', cell: (info) => <span className={(info.getValue() as string) === info.row.original.tableId ? 'text-secondary' : ''}>{String(info.getValue())}</span> },
    {
      accessorKey: 'objectType',
      header: '类型',
      cell: (info) => <span className="text-secondary">{String(info.getValue()) === 'V' ? '视图' : '表'}</span>,
    },
    { accessorKey: 'columnCount', header: '列数', cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span> },
  ], [selectedTable])

  if (!open) return null

  const handleRegister = async () => {
    if (!selectedTable) return
    setSaving(true)
    setError(null)
    try {
      const registered = await apiClient.post<RegisterPhysicalTableResult>('/admin/tables/from-physical', { tableId: selectedTable })
      setResult(registered)
      setSelectedTable(null)
      await queryClient.invalidateQueries({ queryKey: ['field-admin', 'physical-tables'] })
      onRegistered()
    } catch (saveError) {
      setError(describeApiError(saveError, '登记失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  const errorMessage = describeApiError(candidates.error, '发生未知错误，请稍后重试。')

  return (
    <Modal
      title="新增数据表元数据（选取物理表或视图）"
      onClose={onClose}
      dialogClassName="erp-dialog-lg"
      footer={<>
        <Button variant="secondary" className="me-2" onClick={onClose}>关闭</Button>
        <Button variant="primary" loading={saving} disabled={!selectedTable || saving} onClick={() => void handleRegister()}>登记并生成字段</Button>
      </>}
    >
      <div className="alert alert-info">
        下列物理表/视图尚未登记表元数据。登记按物理结构自动生成：
        表描述取表说明（无则用表名），字段的类型与说明来自物理列（列说明无则用列名），
        并带入主键、自增、计算列标志；<strong>不创建物理表</strong>，登记后可按需编辑。
      </div>
      {result && (
        <div className={`alert ${result.fieldSkipped > 0 ? 'alert-warning' : 'alert-success'}`}>
          已登记 {result.tableId}（{result.type === 'VIEW' ? '视图' : '表'}）：描述「{result.description}」，
          生成 {result.fieldCreated} 个字段元数据，跳过 {result.fieldSkipped} 个。
          {result.skippedReasons.length > 0 && (
            <ul className="mb-0 mt-1 small">
              {result.skippedReasons.map((reason, index) => <li key={index}>{reason}</li>)}
            </ul>
          )}
        </div>
      )}
      {error && <div className="alert alert-danger">{error}</div>}
      <div className="mb-2">
        <ErpSearchBox value={keyword} onChange={setKeyword} placeholder="搜索表名或说明" ariaLabel="搜索物理表" />
      </div>
      {candidates.isPending ? <LoadingState label="正在加载未登记的物理表…" /> : candidates.isError ? (
        <ErrorState message={errorMessage} onRetry={() => void candidates.refetch()} />
      ) : (
        <>
          <div className="text-secondary small mb-2">共 {filtered.length} 个未登记的物理表/视图</div>
          <div className="erp-physical-table-picker">
            <ErpTable
              columns={columns}
              data={filtered}
              getRowId={(row) => row.tableId}
              onRowClick={(row) => setSelectedTable(row.tableId)}
              resizable
              storageKey="field-admin-physical-tables"
              empty={<EmptyState title="没有可登记的物理表" description="dbo 下的表与视图都已登记表元数据。" />}
            />
          </div>
        </>
      )}
    </Modal>
  )
}
