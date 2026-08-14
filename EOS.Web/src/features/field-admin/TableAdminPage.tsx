import { IconEdit, IconPlus, IconTrash } from '@tabler/icons-react'
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
import { ApiError } from '../../types/api'
import { TableEditorModal, type TableEditorEndpoints, type TableDetail } from './TableEditorModal'

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
  const [editor, setEditor] = useState<{ mode: 'new' | 'edit'; tableId?: string } | null>(null)
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

  const errorMessage = tables.error instanceof ApiError ? tables.error.body.message : '发生未知错误，请稍后重试。'

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
      if (editor?.mode === 'new') {
        await apiClient.post('/admin/tables', { tableId, table: input })
      } else {
        await apiClient.put(`/admin/tables/${encodeURIComponent(tableId)}`, { tableId, table: input, original })
      }
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
        return count > 0
          ? <span className="badge bg-danger-lt text-danger">{count}</span>
          : <span className="badge bg-green-lt text-success">0</span>
      },
    },
    {
      accessorKey: 'orphanCount',
      header: '幽灵',
      cell: (info) => {
        const count = Number(info.getValue() ?? 0)
        return count > 0
          ? <span className="badge bg-danger-lt text-danger">{count}</span>
          : <span className="badge bg-green-lt text-success">0</span>
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
      // 操作列固定宽度：容纳三个文字按钮，不随内容/拖拽变化
      meta: { className: 'text-end', frozenRight: true, truncate: false, minWidth: 184, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <div className="d-flex gap-1 justify-content-end">
          <Button size="sm" className="erp-table-action" onClick={(event) => { event.stopPropagation(); navigate(`/admin/tables/${encodeURIComponent(row.original.tableId)}/fields`) }}>管理字段</Button>
          <Button size="sm" className="erp-table-action" icon={<IconEdit size={16} />} onClick={(event) => { event.stopPropagation(); setEditor({ mode: 'edit', tableId: row.original.tableId }) }}>编辑</Button>
          <Button
            size="sm"
            className="erp-table-action"
            variant="danger"
            icon={<IconTrash size={16} />}
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
        actions={<Button size="sm" icon={<IconPlus size={16} />} onClick={() => setEditor({ mode: 'new' })}>新增</Button>}
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
      {remove.isError && <div className="alert alert-danger">{remove.error instanceof ApiError ? remove.error.body.message : '删除失败。'}</div>}
      {editor && (
        <TableEditorModal
          open
          mode={editor.mode}
          tableId={editor.tableId}
          endpoints={endpoints}
          onClose={() => setEditor(null)}
          onSaved={() => {
            void queryClient.invalidateQueries({ queryKey: ['field-admin', 'tables'] })
            setEditor(null)
          }}
        />
      )}
    </div>
  )
}
