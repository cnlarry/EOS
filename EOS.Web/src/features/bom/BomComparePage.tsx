import { IconArrowBackUp, IconArrowsLeftRight } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import type { ColumnDef } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'
import { compareBom, type BomComparisonRow } from './bomApi'

const STATUS_LABEL: Record<BomComparisonRow['status'], string> = {
  added: '新增',
  removed: '删除',
  changed: '改动',
  same: '相同',
}

/** 行底色：新增绿、删除红、改动黄；相同保持默认。Bootstrap 语义类，与列表页一致。 */
const STATUS_CLASS: Record<BomComparisonRow['status'], string | undefined> = {
  added: 'table-success',
  removed: 'table-danger',
  changed: 'table-warning',
  same: undefined,
}

/**
 * 版本对比：两个版次按子件对齐。
 *
 * 只比 D4 定义的四个业务列（用量 / 底数 / 损耗率 / 备注）的**原值**——
 * 折算成"单件用量"再比会把"改了底数"显示成"改了用量"，用户看不出自己动了哪一列。
 *
 * 对齐键是子件料号而不是项次：同一子件在两版里项次通常会变，按项次对齐会得到
 * 一堆"删了又加"的假差异。
 */
export function BomComparePage() {
  const params = useParams<{ proNo: string; edition: string; otherEdition: string }>()
  const proNo = params.proNo ?? ''
  const editionA = params.edition ?? ''
  const editionB = params.otherEdition ?? ''
  const navigate = useNavigate()

  const query = useQuery({
    queryKey: ['bom', 'compare', proNo, editionA, editionB],
    queryFn: ({ signal }) => compareBom(proNo, editionA, editionB, signal),
    enabled: Boolean(proNo && editionA && editionB),
    retry: false,
  })

  const rows = query.data?.rows ?? []

  const summary = useMemo(() => {
    const counts = { added: 0, removed: 0, changed: 0, same: 0 }
    for (const row of rows) counts[row.status] += 1
    return counts
  }, [rows])

  const columns = useMemo<ColumnDef<BomComparisonRow, unknown>[]>(() => [
    {
      id: 'status',
      header: '状态',
      accessorFn: row => STATUS_LABEL[row.status],
      meta: { minWidth: 80, resizable: false },
      cell: ({ row }) => STATUS_LABEL[row.original.status],
    },
    { id: 'proNo', header: '子件料号', accessorFn: row => row.proNo, meta: { minWidth: 200 } },
    {
      // 子件版本是"这份 BOM 用它的哪一版"，改了它对下游是实质变化——
      // 不显示的话，只换了版本的两行会报 changed 但看不出改了什么
      id: 'elementEdition',
      header: '子件版本（A → B）',
      meta: { minWidth: 130 },
      cell: ({ row }) => {
        const left = row.original.elementEditionA ?? ''
        const right = row.original.elementEditionB ?? ''
        return left === right ? (left || '—') : `${left || '—'} → ${right || '—'}`
      },
    },
    {
      id: 'qty',
      header: '用量（A → B）',
      meta: { minWidth: 150 },
      cell: ({ row }) => `${formatQty(row.original.qtyA)} → ${formatQty(row.original.qtyB)}`,
    },
    {
      id: 'baseQty',
      header: '底数（A → B）',
      meta: { minWidth: 130 },
      cell: ({ row }) => `${formatQty(row.original.baseQtyA)} → ${formatQty(row.original.baseQtyB)}`,
    },
    {
      id: 'lostRate',
      header: '损耗率%（A → B）',
      meta: { minWidth: 140 },
      cell: ({ row }) => `${formatQty(row.original.lostRateA)} → ${formatQty(row.original.lostRateB)}`,
    },
    {
      id: 'remark',
      header: '备注（A → B）',
      meta: { minWidth: 220 },
      cell: ({ row }) => {
        const left = row.original.remarkA ?? ''
        const right = row.original.remarkB ?? ''
        return left === right ? left : `${left || '—'} → ${right || '—'}`
      },
    },
  ], [])

  const commands = useMemo<ErpCommandItem[]>(() => [
    {
      action: 'back',
      icon: <IconArrowBackUp size={16} />,
      label: '返回列表',
      onClick: () => navigate('/bom/product'),
    },
    { action: 'refresh', label: '刷新', onClick: () => { void query.refetch() } },
  ], [navigate, query])

  return (
    <ErpListCard
      search={undefined}
      actions={<ErpCommandBar items={commands} ariaLabel="版本对比操作" />}
      header={
        <div className="card-header erp-card-toolbar">
          <span>
            <IconArrowsLeftRight size={14} /> {proNo} · 版次 {editionA} → {editionB} · 新增 {summary.added} / 删除{' '}
            {summary.removed} / 改动 {summary.changed} / 相同 {summary.same}
          </span>
        </div>
      }
    >
      {!proNo || !editionA || !editionB ? (
        <EmptyState description="地址缺少料号或版次，无法对比。" />
      ) : query.isPending ? (
        <LoadingState />
      ) : query.isError ? (
        <ErrorState message={describeApiError(query.error, '对比失败。')} onRetry={() => { void query.refetch() }} />
      ) : rows.length === 0 ? (
        <EmptyState description="两个版次都没有明细，没有可对比的内容。" />
      ) : (
        <ErpTable
          columns={columns}
          data={rows}
          resizable
          copyable
          keyboardNavigation
          storageKey="bom-compare"
          rowClassName={row => STATUS_CLASS[row.status]}
        />
      )}
    </ErpListCard>
  )
}

/** 空值显示为「—」而不是 0：某版没有这个子件与"用量是 0"是两件事。 */
function formatQty(value: number | null): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return String(Number(value.toFixed(6)))
}
