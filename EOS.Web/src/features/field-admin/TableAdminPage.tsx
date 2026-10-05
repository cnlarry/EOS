import { IconEdit, IconListDetails, IconPlus, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, SortingState } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { radioSelectColumn } from '../../components/common/erpRadioSelectColumn'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { TableEditorModal, type TableEditorEndpoints, type TableDetail } from './TableEditorModal'
import { ExpressionAuditModal } from './ExpressionAuditModal'
import { PhysicalTablePickerModal } from './PhysicalTablePickerModal'
import { describeApiError } from '../../lib/errors'

export interface FieldAdminTable {
  tableId: string
  description: string
  kind: string | null
  type: string | null
  fieldCount: number
  unmanagedCount: number
  orphanCount: number
}

const KIND_FILTERS = [
  { value: '', label: '全部' },
  { value: 'P', label: '主表' },
  { value: 'S', label: '明细' },
  { value: 'O', label: '其它' },
  { value: 'V', label: '视图' },
]

export function TableAdminPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [kind, setKind] = useState('')
  // 新增走「选取物理表/视图」弹窗；编辑弹窗只维护已登记的表信息
  const [pickerOpen, setPickerOpen] = useState(false)
  const [editor, setEditor] = useState<{ tableId: string } | null>(null)
  const [exprAuditOpen, setExprAuditOpen] = useState(false)
  const [selectedTable, setSelectedTable] = useState<string | null>(null)
  const [sorting, setSorting] = useState<SortingState>([])
  // 表元数据一次性加载：本地筛选即可，不需要分页与手动刷新
  const tables = useQuery({
    queryKey: ['field-admin', 'tables', kind],
    queryFn: () => apiClient.get<FieldAdminTable[]>('/admin/tables', { query: { kind: kind || undefined } }),
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  })

  const filtered = (tables.data ?? []).filter((item) => {
    if (kind && item.kind !== kind) return false
    return !keyword || item.tableId.toLowerCase().includes(keyword.toLowerCase()) || item.description.toLowerCase().includes(keyword.toLowerCase())
  })

  const errorMessage = describeApiError(tables.error, '发生未知错误，请稍后重试。')

  const remove = useMutation({
    mutationFn: (tableId: string) => apiClient.delete(`/admin/tables/${encodeURIComponent(tableId)}`),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['field-admin', 'tables'] }) },
  })

  const endpoints = useMemo<TableEditorEndpoints>(() => ({
    load: async () => {
      if (!editor?.tableId) return null
      return apiClient.get<TableDetail>(`/admin/tables/${encodeURIComponent(editor.tableId)}`)
    },
    save: async (input, tableId, original) => {
      await apiClient.put(`/admin/tables/${encodeURIComponent(tableId)}`, { tableId, table: input, original })
    },
  }), [editor])

  const confirmDelete = useCallback((tableId: string, description: string) => {
    if (window.confirm(`确定要删除数据表元数据“${tableId}（${description}）”吗？仅删除 TABLES 元数据，不影响物理表；存在字段/模块/配置引用时将被拒绝。`)) {
      remove.mutate(tableId)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<FieldAdminTable, unknown>[]>(() => [
    radioSelectColumn<FieldAdminTable>('field-admin-tables', selectedTable, setSelectedTable),
    { accessorKey: 'tableId', header: '表名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'description', header: '描述' },
    { accessorKey: 'fieldCount', header: '字段数', cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span> },
    {
      accessorKey: 'unmanagedCount',
      header: '未管理',
      cell: (info) => {
        const count = Number(info.getValue() ?? 0)
        const hint = count > 0
          ? `${count} 个物理列还没有字段元数据`
          : '该表所有物理列都已有字段元数据'
        return count > 0
          ? <span className="badge bg-danger-lt text-danger" title={hint}>{count}</span>
          : <span className="badge bg-green-lt text-success" title={hint}>0</span>
      },
    },
    {
      accessorKey: 'orphanCount',
      header: '幽灵',
      cell: (info) => {
        const count = Number(info.getValue() ?? 0)
        const hint = count > 0
          ? `${count} 个字段元数据已找不到对应物理列`
          : '没有幽灵字段'
        return count > 0
          ? <span className="badge bg-danger-lt text-danger" title={hint}>{count}</span>
          : <span className="badge bg-green-lt text-success" title={hint}>0</span>
      },
    },
    { accessorKey: 'type', header: '类型', cell: (info) => <span className="text-secondary">{String(info.getValue() ?? '—')}</span> },
    {
      accessorKey: 'kind',
      header: '性质',
      cell: (info) => {
        const kindValue = String(info.getValue() ?? '')
        const label = KIND_FILTERS.find((item) => item.value === kindValue)?.label ?? kindValue
        return <span className="text-secondary">{label}</span>
      },
    },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      // 操作列对齐 2305/2306/字段维护：ghost + 14px 图标 + 文字，右对齐冻结，不随内容/拖拽变化
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 220, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <div className="d-flex gap-1 justify-content-end">
          <Button size="sm" variant="ghost" icon={<IconListDetails size={14} />} title="管理字段" onClick={(event) => { event.stopPropagation(); navigate(`/admin/tables/${encodeURIComponent(row.original.tableId)}/fields`) }}>管理字段</Button>
          <Button size="sm" variant="ghost" icon={<IconEdit size={14} />} title="编辑" onClick={(event) => { event.stopPropagation(); setEditor({ tableId: row.original.tableId }) }}>编辑</Button>
          <Button
            size="sm"
            variant="ghost"
            icon={<IconTrash size={14} />}
            title="删除"
            loading={remove.isPending && remove.variables === row.original.tableId}
            disabled={remove.isPending}
            onClick={(event) => { event.stopPropagation(); confirmDelete(row.original.tableId, row.original.description) }}
          >删除</Button>
        </div>
      ),
    },
  ], [confirmDelete, navigate, remove.isPending, remove.variables, selectedTable])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="数据表维护查询"
        search={(
          <div className="d-flex gap-2 align-items-center">
            <select
              className="form-select form-select-sm erp-kind-filter"
              aria-label="按性质筛选"
              value={kind}
              onChange={(event) => setKind(event.target.value)}
            >
              {KIND_FILTERS.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
            </select>
            <ErpSearchBox value={keyword} onChange={setKeyword} debounceMs={300} placeholder="搜索表名或描述" ariaLabel="搜索数据表" />
          </div>
        )}
        actions={<>
          <Button size="sm" onClick={() => setExprAuditOpen(true)}>表达式审计</Button>
          <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setPickerOpen(true)}>新增</Button>
        </>}
      >
        {tables.isPending ? <LoadingState label="正在加载数据表…" /> : tables.isError ? <ErrorState message={errorMessage} onRetry={() => void tables.refetch()} /> : (
          <ErpTable
            columns={columns}
            data={filtered}
            getRowId={(row) => row.tableId}
            onRowClick={(row) => setSelectedTable(row.tableId)}
            clientSideSorting
            sorting={sorting}
            onSortingChange={setSorting}
            resizable
            storageKey="field-admin-tables"
            empty={<EmptyState title="没有找到数据表" description="请调整搜索条件后重试。" />}
          />
        )}
      </ErpListCard>
      {remove.isError && <div className="alert alert-danger">{describeApiError(remove.error, '删除失败。')}</div>}
      {editor && (
        <TableEditorModal
          open
          tableId={editor.tableId}
          endpoints={endpoints}
          onClose={() => setEditor(null)}
          onSaved={() => {
            void queryClient.invalidateQueries({ queryKey: ['field-admin', 'tables'] })
            setEditor(null)
          }}
        />
      )}
      {pickerOpen && (
        <PhysicalTablePickerModal
          open
          onClose={() => setPickerOpen(false)}
          onRegistered={() => { void queryClient.invalidateQueries({ queryKey: ['field-admin', 'tables'] }) }}
        />
      )}
      <ExpressionAuditModal open={exprAuditOpen} onClose={() => setExprAuditOpen(false)} />
    </div>
  )
}
