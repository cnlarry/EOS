import { IconEdit, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { FieldEditorModal, type FieldInput, type FieldMeta, type SetupLookup } from './FieldEditorModal'
import type { FieldAdminTable } from './TableAdminPage'

interface FieldAdminFieldSummary {
  tableId: string
  fieldId: string
  description: string
  dataType: string
  isVirtual: boolean
  isVisible: boolean
  isDefault: boolean
  isQueryable: boolean
  isReadonly: boolean
  isCost: boolean
  isSecrecy: boolean
}

interface FieldAdminPageResult {
  items: FieldAdminFieldSummary[]
  total: number
  page: number
  pageSize: number
}

interface FieldAdminMetadata {
  tableId: string
  fieldId: string
  field: FieldInput
  isVirtual: boolean
  virtualExpression: string | null
  isAutoIncrement: boolean
  convertFunction: string | null
  dataSourceSql: string | null
  lastUpdatedBy: string | null
  lastUpdatedAt: string | null
}

function adminMetaToFieldMeta(meta: FieldAdminMetadata): FieldMeta {
  return {
    ...meta.field,
    key: meta.fieldId,
    tableId: meta.tableId,
    isVirtual: meta.isVirtual,
    virtualExpression: meta.virtualExpression,
    isAutoIncrement: meta.isAutoIncrement,
    convertFunction: meta.convertFunction,
    dataSourceSql: meta.dataSourceSql,
    lastUpdatedBy: meta.lastUpdatedBy,
    lastUpdatedAt: meta.lastUpdatedAt,
  }
}

const pageSize = 16

export function FieldAdminPage() {
  const { tableId = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [editor, setEditor] = useState<{ mode: 'new' | 'edit'; fieldKey?: string } | null>(null)

  const tablesQuery = useQuery({ queryKey: ['field-admin', 'tables'], queryFn: () => apiClient.get<FieldAdminTable[]>('/admin/tables') })
  const table = tablesQuery.data?.find((item) => item.tableId.toLowerCase() === tableId.toLowerCase())

  const fields = useQuery({
    queryKey: ['field-admin', 'fields', tableId, keyword, page],
    queryFn: () => apiClient.get<FieldAdminPageResult>(`/admin/tables/${encodeURIComponent(tableId)}/fields`, { query: { keyword, page, pageSize } }),
  })

  const remove = useMutation({
    mutationFn: (fieldId: string) => apiClient.delete(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(fieldId)}`),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['field-admin', 'fields'] }) },
  })

  const endpoints = {
    load: async () => {
      if (!editor?.fieldKey) return null
      const meta = await apiClient.get<FieldAdminMetadata>(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(editor.fieldKey)}`)
      return adminMetaToFieldMeta(meta)
    },
    save: async (input: FieldInput, targetTable: string, fieldId: string, original: FieldInput | null) => {
      if (editor?.mode === 'new') {
        await apiClient.post('/admin/fields', { tableId: targetTable, fieldId, field: input })
      } else {
        await apiClient.put(`/admin/fields/${encodeURIComponent(targetTable)}/${encodeURIComponent(fieldId)}`, { tableId: targetTable, fieldId, field: input, original })
      }
    },
    tables: async () => (await apiClient.get<FieldAdminTable[]>('/admin/tables'))
      .map((item) => ({ value: item.tableId, label: `${item.description} (${item.tableId})` }) as SetupLookup),
    modules: async () => (await apiClient.get<{ id: number; label: string }[]>('/admin/lookups/modules'))
      .map((item) => ({ value: String(item.id), label: item.label }) as SetupLookup),
  }

  const items = fields.data?.items ?? []
  const errorMessage = fields.error instanceof ApiError ? fields.error.body.message : '发生未知错误，请稍后重试。'

  const confirmDelete = useCallback((fieldId: string, description: string) => {
    if (window.confirm(`确定要删除字段“${fieldId} (${description})”吗？删除后该字段将不再显示与查询，历史配置引用会被一并清理。`)) {
      remove.mutate(fieldId)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<FieldAdminFieldSummary, unknown>[]>(() => [
    { accessorKey: 'fieldId', header: '字段名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'description', header: '标题' },
    { accessorKey: 'dataType', header: '类型', cell: (info) => <span className="text-secondary">{String(info.getValue())}</span> },
    { accessorKey: 'isVirtual', header: '虚拟', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'isVisible', header: '可见', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'isDefault', header: '默认', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'isQueryable', header: '查询', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'isReadonly', header: '只读', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'isCost', header: '成本', cell: (info) => (info.getValue() ? '是' : '否') },
    { accessorKey: 'isSecrecy', header: '保密', cell: (info) => (info.getValue() ? '是' : '否') },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-end text-nowrap', frozenRight: true },
      cell: ({ row }) => (
        <>
          <Button size="sm" className="erp-table-action" onClick={() => setEditor({ mode: 'new', fieldKey: row.original.fieldId })}>复制</Button>
          <Button size="sm" className="erp-table-action" icon={<IconEdit size={16} />} onClick={() => setEditor({ mode: 'edit', fieldKey: row.original.fieldId })}>编辑</Button>
          <Button size="sm" className="erp-table-action" variant="danger" icon={<IconTrash size={16} />} loading={remove.isPending && remove.variables === row.original.fieldId} disabled={remove.isPending} onClick={() => confirmDelete(row.original.fieldId, row.original.description)}>删除</Button>
        </>
      ),
    },
  ], [confirmDelete, remove.isPending, remove.variables])

  return (
    <div className="d-grid gap-2">
      <ErpListCard
        ariaLabel="字段维护查询与操作"
        search={<ErpSearchBox value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }} debounceMs={300} placeholder="搜索字段名或标题" ariaLabel="搜索字段" />}
        actions={<>
          <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setEditor({ mode: 'new' })}>新增</Button>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void fields.refetch()}>刷新</Button>
          <Button size="sm" onClick={() => navigate('/admin/tables')}>返回</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2">
          <h2 className="card-title mb-0">{table?.description ?? tableId}</h2>
          <span className="badge bg-blue-lt font-monospace">{tableId}</span>
          <span className="text-secondary small ms-auto">共 {fields.data?.total ?? 0} 个字段</span>
        </div>}
        footer={!fields.isPending && !fields.isError ? <ErpPagination total={fields.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} /> : undefined}
      >
        {fields.isPending ? <LoadingState label="正在加载字段列表…" /> : fields.isError ? <ErrorState message={errorMessage} onRetry={() => void fields.refetch()} /> : (
          <ErpTable columns={columns} data={items} resizable storageKey={`field-admin-fields-${tableId}`} empty={<EmptyState title="没有找到字段" description="请调整搜索条件，或点击“新增”创建字段。" />} />
        )}
      </ErpListCard>
      {remove.isError && <div className="alert alert-danger">{remove.error instanceof ApiError ? remove.error.body.message : '删除失败。'}</div>}
      {editor && (
        <FieldEditorModal
          open
          mode={editor.mode}
          tableId={tableId}
          fieldKey={editor.fieldKey}
          title={editor.mode === 'new' ? `新增字段（${tableId}）` : `字段管理（${editor.fieldKey}）`}
          endpoints={endpoints}
          onClose={() => setEditor(null)}
          onSaved={() => {
            void queryClient.invalidateQueries({ queryKey: ['field-admin', 'fields'] })
            setEditor(null)
          }}
        />
      )}
    </div>
  )
}
