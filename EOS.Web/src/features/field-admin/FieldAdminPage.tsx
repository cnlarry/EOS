import { IconCopy, IconEdit, IconListDetails, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useCallback, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpPagination } from '../../components/common/ErpPagination'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { radioSelectColumn } from '../../components/common/erpRadioSelectColumn'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import type { FieldAdminTable } from './TableAdminPage'
import { UnmanagedFieldsModal } from './UnmanagedFieldsModal'

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
  isPrimaryKey: boolean
  physicalExists: boolean
}

interface FieldAdminPageResult {
  items: FieldAdminFieldSummary[]
  total: number
  page: number
  pageSize: number
}

const pageSize = 16

/** 只读布尔列：复选框表达（勾选=True，未勾选=False），仅展示不可操作。 */
function booleanCell(label: string, value: boolean) {
  return <input type="checkbox" className="form-check-input" aria-label={label} checked={value} disabled />
}

export function FieldAdminPage() {
  const { tableId = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [unmanagedOpen, setUnmanagedOpen] = useState(false)
  const [selectedField, setSelectedField] = useState<string | null>(null)

  const tablesQuery = useQuery({
    queryKey: ['field-admin', 'tables'],
    queryFn: () => apiClient.get<FieldAdminTable[]>('/admin/tables'),
  })
  const table = tablesQuery.data?.find((item) => item.tableId.toLowerCase() === tableId.toLowerCase())

  const fields = useQuery({
    queryKey: ['field-admin', 'fields', tableId, keyword, page],
    queryFn: () => apiClient.get<FieldAdminPageResult>(`/admin/tables/${encodeURIComponent(tableId)}/fields`, { query: { keyword, page, pageSize } }),
  })

  const remove = useMutation({
    mutationFn: (fieldId: string) => apiClient.delete(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(fieldId)}`),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: ['field-admin', 'fields'] }) },
  })

  const items = fields.data?.items ?? []
  const errorMessage = fields.error instanceof ApiError ? fields.error.body.message : '发生未知错误，请稍后重试。'

  const confirmDelete = useCallback((fieldId: string, description: string) => {
    if (window.confirm(`确定要删除字段“${fieldId} (${description})”吗？删除后该字段将不再显示与查询，历史配置引用会被一并清理。`)) {
      remove.mutate(fieldId)
    }
  }, [remove])

  const columns = useMemo<ColumnDef<FieldAdminFieldSummary, unknown>[]>(() => [
    radioSelectColumn<FieldAdminFieldSummary>(`field-admin-fields-${tableId}`, selectedField, setSelectedField),
    { accessorKey: 'fieldId', header: '字段名', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span> },
    { accessorKey: 'description', header: '标题' },
    { accessorKey: 'dataType', header: '类型', cell: (info) => <span className="text-secondary">{String(info.getValue())}</span> },
    { accessorKey: 'isPrimaryKey', header: '主键', cell: (info) => booleanCell('主键', Boolean(info.getValue())) },
    {
      accessorKey: 'physicalExists',
      header: '物理列',
      cell: (info) => (info.getValue()
        ? <span className="badge bg-green-lt">存在</span>
        : <span className="badge bg-danger-lt">不存在</span>),
    },
    { accessorKey: 'isVirtual', header: '虚拟', cell: (info) => booleanCell('虚拟', Boolean(info.getValue())) },
    { accessorKey: 'isVisible', header: '可见', cell: (info) => booleanCell('可见', Boolean(info.getValue())) },
    { accessorKey: 'isDefault', header: '默认', cell: (info) => booleanCell('默认', Boolean(info.getValue())) },
    { accessorKey: 'isQueryable', header: '查询', cell: (info) => booleanCell('查询', Boolean(info.getValue())) },
    { accessorKey: 'isReadonly', header: '只读', cell: (info) => booleanCell('只读', Boolean(info.getValue())) },
    { accessorKey: 'isCost', header: '成本', cell: (info) => booleanCell('成本', Boolean(info.getValue())) },
    { accessorKey: 'isSecrecy', header: '保密', cell: (info) => booleanCell('保密', Boolean(info.getValue())) },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      // 操作列固定宽度：容纳三个文字按钮，不随内容/拖拽变化
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, minWidth: 220, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <div className="d-flex gap-1 justify-content-end">
          <Button size="sm" variant="ghost" icon={<IconCopy size={14} />} title="复制" onClick={() => navigate(`/admin/fields/${encodeURIComponent(tableId)}/new?copyFrom=${encodeURIComponent(row.original.fieldId)}`)}>复制</Button>
          <Button size="sm" variant="ghost" icon={<IconEdit size={14} />} title="编辑" onClick={() => navigate(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(row.original.fieldId)}`)}>编辑</Button>
          <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} title="删除" loading={remove.isPending && remove.variables === row.original.fieldId} disabled={remove.isPending} onClick={() => confirmDelete(row.original.fieldId, row.original.description)}>删除</Button>
        </div>
      ),
    },
  ], [confirmDelete, remove.isPending, remove.variables, selectedField, tableId, navigate])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="字段维护查询与操作"
        search={<ErpSearchBox value={keyword} onChange={(value) => { setKeyword(value); setPage(1) }} debounceMs={300} placeholder="搜索字段名或标题" ariaLabel="搜索字段" />}
        actions={<>
          <Button size="sm" icon={<IconPlus size={16} />} onClick={() => navigate(`/admin/fields/${encodeURIComponent(tableId)}/new`)}>新增</Button>
          <Button size="sm" icon={<IconListDetails size={16} />} onClick={() => setUnmanagedOpen(true)}>未管理字段</Button>
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void fields.refetch()}>刷新</Button>
          <Button size="sm" onClick={() => navigate('/admin/tables')}>返回</Button>
        </>}
        header={<div className="card-header py-2 d-flex align-items-center gap-2">
          <h2 className="card-title mb-0">{table?.description ?? tableId}</h2>
          <span className="badge bg-blue-lt font-monospace">{tableId}</span>
          <span className="ms-auto d-flex align-items-center gap-2">
            {(table?.unmanagedCount ?? 0) > 0 && (
              <button type="button" className="badge bg-warning-lt border-0" title="点击查看未管理字段" onClick={() => setUnmanagedOpen(true)}>
                未管理 {table?.unmanagedCount ?? 0}
              </button>
            )}
            <span className="text-secondary small">共 {fields.data?.total ?? 0} 个字段</span>
          </span>
        </div>}
        footer={!fields.isPending && !fields.isError ? <ErpPagination total={fields.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={setPage} /> : undefined}
      >
        {fields.isPending ? <LoadingState label="正在加载字段列表…" /> : fields.isError ? <ErrorState message={errorMessage} onRetry={() => void fields.refetch()} /> : (
          <ErpTable
            columns={columns}
            data={items}
            getRowId={(row) => `${row.tableId}.${row.fieldId}`}
            onRowClick={(row) => setSelectedField(`${row.tableId}.${row.fieldId}`)}
            resizable
            storageKey={`field-admin-fields-${tableId}`}
            empty={<EmptyState title="没有找到字段" description="请调整搜索条件，或点击“新增”创建字段。" />}
          />
        )}
      </ErpListCard>
      {remove.isError && <div className="alert alert-danger">{remove.error instanceof ApiError ? remove.error.body.message : '删除失败。'}</div>}
      {unmanagedOpen && (
        <UnmanagedFieldsModal
          open
          tableId={tableId}
          tableDescription={table?.description}
          onClose={() => setUnmanagedOpen(false)}
          onSaved={() => {
            void queryClient.invalidateQueries({ queryKey: ['field-admin', 'fields'] })
            void queryClient.invalidateQueries({ queryKey: ['field-admin', 'unmanaged'] })
            void queryClient.invalidateQueries({ queryKey: ['field-admin', 'tables'] })
          }}
        />
      )}
    </div>
  )
}
