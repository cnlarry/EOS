import { IconAdjustmentsHorizontal } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import type { ColumnDef } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'
import { whereUsedBom, type BomWhereUsedRow } from './bomApi'

/**
 * 反查用途（Where-used）：给定料号，向上找出所有用到它的父件。
 *
 * 这是改料前必查的一步——"这个料被用在哪"，答案必须带路径：用户不只想知道
 * "被 B 用了"，还想知道"B 又在 A 的第 2 层"。
 */
export function BomWhereUsedPage() {
  const params = useParams<{ proNo?: string }>()
  const navigate = useNavigate()
  const [proNo, setProNo] = useState(params.proNo ?? '')
  const [depth, setDepth] = useState(5)
  const [paramsOpen, setParamsOpen] = useState(false)

  const query = useQuery({
    queryKey: ['bom', 'where-used', proNo, depth],
    queryFn: ({ signal }) => whereUsedBom(proNo, undefined, depth, signal),
    enabled: proNo.length > 0,
    retry: false,
  })

  const columns = useMemo<ColumnDef<BomWhereUsedRow, unknown>[]>(() => [
    {
      id: 'level',
      header: '层级',
      accessorFn: row => row.level,
      meta: { className: 'text-end', minWidth: 70 },
      // 0 = 直接父件，1 = 父件的父件……显示成 1 起更符合直觉
      cell: ({ row }) => String(row.original.level + 1),
    },
    { id: 'parentNo', header: '父件料号', accessorFn: row => row.parentNo, meta: { minWidth: 180 } },
    { id: 'parentName', header: '品名', accessorFn: row => row.parentName, meta: { minWidth: 140 } },
    { id: 'parentSpec', header: '规格', accessorFn: row => row.parentSpec, meta: { minWidth: 140 } },
    { id: 'parentEdition', header: '版次', accessorFn: row => row.parentEdition, meta: { minWidth: 90 } },
    {
      id: 'serialNo',
      header: '项次',
      accessorFn: row => row.serialNo,
      meta: { className: 'text-end', minWidth: 80 },
    },
    {
      id: 'qtyPer',
      header: '用量/父件',
      accessorFn: row => row.qtyPer,
      meta: { className: 'text-end', minWidth: 100 },
      cell: ({ row }) => (Number.isFinite(row.original.qtyPer) ? String(Number(row.original.qtyPer.toFixed(6))) : ''),
    },
    { id: 'path', header: '所在路径', accessorFn: row => row.path, meta: { minWidth: 260 } },
  ], [])

  const commands = useMemo<ErpCommandItem[]>(() => [
    { action: 'bom-params', icon: <IconAdjustmentsHorizontal size={16} />, label: '参数', title: '向上查找的层数', onClick: () => setParamsOpen(true) },
    { action: 'refresh', label: '刷新', onClick: () => { void query.refetch() } },
    { action: 'back', label: '返回列表', onClick: () => navigate('/bom/product') },
  ], [navigate, query])

  const rows = query.data?.rows ?? []

  return (
    <>
      <ErpListCard
        search={
          <ErpSearchBox
            value={proNo}
            onChange={setProNo}
            placeholder="输入要反查的料号后回车"
            ariaLabel="料号"
          />
        }
        actions={<ErpCommandBar items={commands} ariaLabel="反查用途操作" />}
        header={
          proNo ? (
            <div className="card-header erp-card-toolbar">
              <span>{proNo} 被 {rows.length} 处使用 · 向上 {depth} 层</span>
            </div>
          ) : undefined
        }
      >
        {!proNo ? (
          <EmptyState description="输入料号后回车：结果按父件逐层列出，路径显示它挂在哪一层。" />
        ) : query.isPending ? (
          <LoadingState />
        ) : query.isError ? (
          <ErrorState message={describeApiError(query.error, '操作失败。')} onRetry={() => { void query.refetch() }} />
        ) : rows.length === 0 ? (
          <EmptyState description="没有找到使用该料号的 BOM —— 它可能只作为成品存在，或已停用。" />
        ) : (
          // resizable + storageKey：列宽拖拽后写 localStorage（persistResize 默认开）
          <ErpTable columns={columns} data={rows} resizable copyable keyboardNavigation storageKey="bom-where-used" />
        )}
      </ErpListCard>

      {paramsOpen ? (
        <Modal
          title="反查参数"
          size="sm"
          onClose={() => setParamsOpen(false)}
          footer={<Button variant="primary" onClick={() => setParamsOpen(false)}>确定</Button>}
        >
          <label className="form-label" htmlFor="bom-where-depth">向上查找层数</label>
          <input
            id="bom-where-depth"
            className="form-control"
            type="number"
            min={1}
            max={20}
            value={depth}
            onChange={event => setDepth(Math.min(20, Math.max(1, Number(event.target.value) || 1)))}
          />
        </Modal>
      ) : null}
    </>
  )
}
