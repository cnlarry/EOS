import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState, VisibilityState } from '@tanstack/react-table'
import { IconColumns, IconDownload, IconEdit, IconEye, IconPlus, IconRefresh, IconSend, IconTrash } from '@tabler/icons-react'
import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../../components/common/AsyncState'
import { ErpListCard } from '../../../components/common/ErpListCard'
import { ErpPagination } from '../../../components/common/ErpPagination'
import { ErpSearchBox } from '../../../components/common/ErpSearchBox'
import { ErpTable } from '../../../components/common/ErpTable'
import { StatusBadge } from '../../../components/common/StatusBadge'
import { Button } from '../../../components/ui/Button'
import { ApiError } from '../../../types/api'
import { useAuth } from '../../auth/authContext'
import { purchaseOrdersQueryOptions } from '../api/purchaseOrders'
import type { PurchaseOrderSortField, PurchaseOrderStatus, PurchaseOrderSummary, SortDirection } from '../types/purchaseOrder'

const pageSizes = [2, 5, 10, 20]
const hideableColumns = [
  { id: 'buyerName', label: '采购员' }, { id: 'purchaseDate', label: '采购日期' }, { id: 'totalAmount', label: '含税金额' }, { id: 'status', label: '状态' },
]
function formatMoney(value: string) { return new Intl.NumberFormat('zh-CN', { style: 'currency', currency: 'CNY' }).format(Number(value)) }
function positiveInt(value: string | null, fallback: number) { const parsed = Number(value); return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback }

export function PurchaseOrdersPage() {
  const { hasPermission } = useAuth()
  const [searchParams, setSearchParams] = useSearchParams()
  const activeKeyword = searchParams.get('keyword') ?? ''
  const activeStatus = (searchParams.get('status') ?? '') as PurchaseOrderStatus | ''
  const activeDateFrom = searchParams.get('dateFrom') ?? ''
  const activeDateTo = searchParams.get('dateTo') ?? ''
  const page = positiveInt(searchParams.get('page'), 1)
  const pageSize = positiveInt(searchParams.get('pageSize'), 5)
  const sortBy = (searchParams.get('sortBy') ?? 'purchaseDate') as PurchaseOrderSortField
  const sortDirection: SortDirection = searchParams.get('sortDirection') === 'asc' ? 'asc' : 'desc'
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [activeOrderId, setActiveOrderId] = useState<string>()
  const [columnVisibility, setColumnVisibility] = useState<VisibilityState>({})
  const [columnsOpen, setColumnsOpen] = useState(false)
  const query = { keyword: activeKeyword || undefined, status: activeStatus || undefined, dateFrom: activeDateFrom || undefined, dateTo: activeDateTo || undefined, sortBy, sortDirection, page, pageSize }
  const ordersQuery = useQuery({ ...purchaseOrdersQueryOptions(query), placeholderData: keepPreviousData })

  const columns = useMemo<ColumnDef<PurchaseOrderSummary, unknown>[]>(() => [
    { id: 'select', size: 33, enableSorting: false, enableHiding: false, meta: { className: 'erp-select-column', truncate: false }, header: ({ table }) => <input className="form-check-input" type="checkbox" aria-label="选择当前页全部订单" checked={table.getIsAllPageRowsSelected()} ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }} onChange={table.getToggleAllPageRowsSelectedHandler()} />, cell: ({ row }) => <input className="form-check-input" type="checkbox" aria-label={`选择订单 ${row.original.number}`} checked={row.getIsSelected()} onChange={row.getToggleSelectedHandler()} /> },
    { accessorKey: 'number', header: '订单编号', cell: ({ row }) => <Link className="fw-semibold" to={`/procurement/purchase-orders/${row.original.id}`}>{row.original.number}</Link> },
    { accessorKey: 'supplierName', header: '供应商' },
    { accessorKey: 'buyerName', header: '采购员', enableSorting: false },
    { accessorKey: 'purchaseDate', header: '采购日期', meta: { className: 'text-secondary text-nowrap' } },
    { accessorKey: 'totalAmount', header: '含税金额', cell: ({ row }) => formatMoney(row.original.totalAmount), meta: { className: 'text-end text-nowrap fw-medium', title: ({ value }) => formatMoney(String(value)) } },
    { accessorKey: 'status', header: '状态', cell: ({ row }) => <StatusBadge status={row.original.status} />, meta: { truncate: false } },
  ], [])

  function updateParams(values: Record<string, string | undefined>, resetPage = false) {
    const next = new URLSearchParams(searchParams)
    Object.entries(values).forEach(([key, value]) => value ? next.set(key, value) : next.delete(key))
    if (resetPage) next.delete('page')
    setSearchParams(next)
    setRowSelection({})
  }
  function changeSorting(next: SortingState) { const first = next[0]; updateParams({ sortBy: first?.id, sortDirection: first ? (first.desc ? 'desc' : 'asc') : undefined }, true) }
  const sorting: SortingState = [{ id: sortBy, desc: sortDirection === 'desc' }]
  const selectedCount = Object.keys(rowSelection).length
  const activeOrder = ordersQuery.data?.items.find((order) => order.id === activeOrderId) ?? ordersQuery.data?.items[0]
  const errorMessage = ordersQuery.error instanceof ApiError ? ordersQuery.error.body.message : '发生未知错误，请稍后重试。'

  return (
    <div className="d-grid gap-2 erp-purchase-page">
      <ErpListCard
        ariaLabel="采购订单查询与操作"
        search={<ErpSearchBox value={activeKeyword} onChange={(value) => updateParams({ keyword: value }, true)} debounceMs={400} placeholder="搜索单据、供应商或商品" ariaLabel="搜索单据、供应商或商品" />}
        actions={<>
          {hasPermission('purchase-order.create') && <Link className="btn btn-primary btn-sm" to="/procurement/purchase-orders/new"><IconPlus size={16} /> 新建</Link>}
          <Button size="sm" icon={<IconEye size={16} />} disabled={!activeOrder}>查看</Button>
          {hasPermission('purchase-order.update') && <Button size="sm" icon={<IconEdit size={16} />} disabled={!activeOrder}>编辑</Button>}
          {hasPermission('purchase-order.submit') && <Button size="sm" icon={<IconSend size={16} />} disabled={!activeOrder || activeOrder.status !== 'draft'}>提交审核</Button>}
          {hasPermission('purchase-order.export') && <Button size="sm" icon={<IconDownload size={16} />}>{selectedCount ? `导出所选 (${selectedCount})` : '导出'}</Button>}
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void ordersQuery.refetch()}>刷新</Button>
          {hasPermission('purchase-order.delete') && <Button size="sm" variant="danger" icon={<IconTrash size={16} />} disabled={selectedCount === 0}>删除</Button>}
          <div className="position-relative">
            <Button size="sm" icon={<IconColumns size={16} />} onClick={() => setColumnsOpen((open) => !open)}>选择列</Button>
            {columnsOpen && <div className="erp-column-menu card"><div className="card-body py-2">{hideableColumns.map((column) => <label className="form-check" key={column.id}><input className="form-check-input" type="checkbox" checked={columnVisibility[column.id] !== false} onChange={(event) => setColumnVisibility((current) => ({ ...current, [column.id]: event.target.checked }))} /><span className="form-check-label">{column.label}</span></label>)}</div></div>}
          </div>
        </>}
        footer={!ordersQuery.isPending && !ordersQuery.isError ? (
          <ErpPagination total={ordersQuery.data?.total ?? 0} page={page} pageSize={pageSize} onPageChange={(next) => updateParams({ page: String(next) })} pageSizes={pageSizes} onPageSizeChange={(size) => updateParams({ pageSize: String(size) }, true)} />
        ) : undefined}
      >
        {ordersQuery.isPending ? <LoadingState label="正在加载采购订单…" /> : ordersQuery.isError ? <ErrorState message={errorMessage} onRetry={() => void ordersQuery.refetch()} /> : (
          <ErpTable columns={columns} data={ordersQuery.data.items} getRowId={(order) => order.id} sorting={sorting} onSortingChange={changeSorting} rowSelection={rowSelection} onRowSelectionChange={setRowSelection} columnVisibility={columnVisibility} onColumnVisibilityChange={setColumnVisibility} onRowClick={(order) => setActiveOrderId(order.id)} activeRowId={activeOrder?.id} resizable storageKey="purchase-orders" empty={<EmptyState title="没有找到采购订单" description="请调整关键词、状态或日期条件后重新查询。" />} />
        )}
      </ErpListCard>
      {activeOrder && (
        <section className="card erp-detail-card">
          <div className="card-header py-2">
            <h2 className="card-title">订单明细</h2>
            <span className="text-secondary small ms-2">{activeOrder.number} · {activeOrder.supplierName}</span>
            <span className="badge bg-blue-lt ms-auto">{activeOrder.lines?.length ?? 0} 行</span>
          </div>
          <div className="table-responsive">
            <table className="table table-sm table-vcenter card-table mb-0">
              <thead><tr><th>#</th><th>物料编码</th><th>物料名称</th><th>规格型号</th><th>单位</th><th className="text-end">数量</th><th className="text-end">含税单价</th><th className="text-end">税率</th><th className="text-end">价税合计</th></tr></thead>
              <tbody>{activeOrder.lines?.length ? activeOrder.lines.map((line, index) => <tr key={line.id}><td className="text-secondary">{index + 1}</td><td className="font-monospace">{line.productCode}</td><td>{line.productName}</td><td className="text-secondary">{line.specification}</td><td>{line.unit}</td><td className="text-end">{line.quantity}</td><td className="text-end">{formatMoney(line.unitPrice)}</td><td className="text-end">{line.taxRate}%</td><td className="text-end fw-medium">{formatMoney(line.lineAmount)}</td></tr>) : <tr><td colSpan={9} className="text-center text-secondary py-4">当前测试订单暂无明细数据</td></tr>}</tbody>
              <tfoot><tr><td colSpan={8} className="text-end fw-semibold">订单合计</td><td className="text-end fw-bold text-primary">{formatMoney(activeOrder.totalAmount)}</td></tr></tfoot>
            </table>
          </div>
        </section>
      )}
      <p className="text-secondary small mb-0">单击主表行可联动查看下方明细；输入关键词 “error” 可验证错误状态。</p>
    </div>
  )
}
