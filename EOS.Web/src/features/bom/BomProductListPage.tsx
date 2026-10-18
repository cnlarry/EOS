import { IconCopy, IconEdit, IconPlus, IconRefresh, IconSearch, IconSitemap } from '@tabler/icons-react'
import { keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { useCallback, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar, type ErpCommandItem } from '../../components/common/ErpCommandBar'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { useToast } from '../../components/ui/toastContext'
import type { ColumnDef, RowSelectionState, SortingState } from '../../lib/tanstackTable'
import { describeApiError } from '../../lib/errors'
import { apiClient } from '../../services/api'
import { createBomVersion } from './bomApi'

const MODULE_ID = 1204
/** 回退主键序：元数据没取到时按 1204 的实际主键（迁移 409 起是 PRO_NO + EDITION）。 */
const FALLBACK_PK = ['PRO_NO', 'EDITION']
/**
 * 滚动加载的块大小（与统一工作台一致）：50 ≈ 两屏缓冲，
 * 既不一次拖全表，也不至于"加载更多"频繁闪烁。
 */
const PAGE_SIZE = 50

interface Definition {
  masterPkOrder: string[]
  /** 能不能新增：与「新增」按钮同一个判据（模块声明的可写能力 + 用户新增权）。 */
  hasAdd?: boolean
}

interface DataResponse {
  rows: Record<string, unknown>[]
  total: number
  /** 当前页号：滚动加载靠它判断还有没有下一页。 */
  page: number
}

/**
 * 产品 BOM 列表（模块 1204 的菜单入口）。
 *
 * 这是 1204 的**定制承载面**（ADR-039 D1）：列表自己画，单层维护也由定制页承担
 * （`BomProductEditorPage`）。记录数据仍走工作台的 `/records`：字段可见性、数据过滤、
 * 权限全部由服务端按同一套元数据裁决，这里只负责把行画出来。
 *
 * 外壳用 `.erp-full-list-page`：满屏高度、页面级不出滚动条、滚动只发生在表格内、
 * 表头吸顶，四周 10px 间隙由工作区外壳（`--erp-workspace-gap`）提供——不在这里写死内边距。
 */
export function BomProductListPage() {
  const navigate = useNavigate()
  const toast = useToast()
  const [keyword, setKeyword] = useState('')
  const [sorting, setSorting] = useState<SortingState>([])
  const [selected, setSelected] = useState<Record<string, true>>({})
  /** 当前活动行（单击/选中后高亮的那一行）；与 `selected` 分开是因为选中可多行、活动行只有一个。 */
  const [activeRow, setActiveRow] = useState<string | null>(null)
  const [creatingVersion, setCreatingVersion] = useState(false)

  const definition = useQuery({
    queryKey: ['workbench', MODULE_ID, 'definition'],
    queryFn: () => apiClient.get<Definition>(`/document-workbench/${MODULE_ID}/definition`),
  })
  // 滚动加载（与统一工作台同构）：滚到底自动取下一页，不做翻页控件。
  // BOM 主档是低频主数据，用户多在"找到那一条"而不是"翻到第 N 页"。
  const records = useInfiniteQuery({
    queryKey: ['bom', 'list', keyword, sorting],
    queryFn: ({ pageParam }) => {
      const sortFields = sorting.length > 0 ? sorting.map(item => item.id).join(',') : undefined
      const sortDirections = sorting.length > 0
        ? sorting.map(item => (item.desc ? 'desc' : 'asc')).join(',')
        : undefined
      return apiClient.get<DataResponse>(`/document-workbench/${MODULE_ID}/records`, {
        query: { page: pageParam, pageSize: PAGE_SIZE, keyword: keyword || undefined, sortFields, sortDirections },
      })
    },
    initialPageParam: 1,
    getNextPageParam: last => (last.page < Math.ceil(last.total / PAGE_SIZE) ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  })
  const rows = useMemo(() => records.data?.pages.flatMap(page => page.rows ?? []) ?? [], [records.data])

  const pkOrder = useMemo(
    () => (definition.data?.masterPkOrder?.length ? definition.data.masterPkOrder : FALLBACK_PK),
    [definition.data],
  )

  // nchar 主键存值带尾随空格：显示与拼 URL 前都修剪。
  const cellText = useCallback((row: Record<string, unknown>, key: string) => String(row[key] ?? '').trim(), [])

  /** 行身份 = 主键值拼接；主键全空时退回整行 JSON（与工作台同口径）。 */
  const rowKey = useCallback((row: Record<string, unknown>) => {
    const keys = pkOrder.map(column => cellText(row, column))
    return keys.some(key => key !== '') ? keys.join('|') : JSON.stringify(row)
  }, [cellText, pkOrder])

  const rowSelection = useMemo<RowSelectionState>(
    () => Object.fromEntries(Object.keys(selected).map(key => [key, true])),
    [selected],
  )
  const handleRowSelectionChange = useCallback((next: RowSelectionState) => {
    const checkedKeys = Object.keys(next).filter(key => next[key])
    setSelected(Object.fromEntries(checkedKeys.map(key => [key, true as const])))
    // 单选时同步活动行；清空或跨多行选择时不动它（活动行是"光标在哪"，不是"选了几行"）
    setActiveRow(checkedKeys.length === 1 ? checkedKeys[0] : null)
  }, [])

  /** 单击选中一行（与统一工作台 `handleRowClick` 同口径）：选中集只留这一行，活动行也指向它。 */
  const handleRowClick = useCallback((row: Record<string, unknown>) => {
    const key = rowKey(row)
    setSelected({ [key]: true })
    setActiveRow(key)
  }, [rowKey])

  // 双击进**结构树**（它是浏览态）：左边看整棵树，点节点看详情。
  // 浏览还有一层意思——"这条 BOM 的主表资料"，那也是树根节点的右栏内容，
  // 所以不再需要一个单独的主表浏览页。
  const openRecord = useCallback((row: Record<string, unknown>) => {
    navigate(`/bom/product/${encodeURIComponent(cellText(row, 'PRO_NO'))}/${encodeURIComponent(cellText(row, 'EDITION'))}/tree`)
  }, [cellText, navigate])

  const openEdit = useCallback((row: Record<string, unknown>) => {
    const keys = pkOrder.map(key => cellText(row, key))
    navigate(`/bom/product/${keys.map(encodeURIComponent).join('/')}/edit`)
  }, [cellText, navigate, pkOrder])

  const openTree = useCallback((row: Record<string, unknown>) => {
    navigate(`/bom/product/${encodeURIComponent(cellText(row, 'PRO_NO'))}/${encodeURIComponent(cellText(row, 'EDITION'))}/tree`)
  }, [cellText, navigate])

  const openWhereUsed = useCallback((row: Record<string, unknown>) => {
    navigate(`/bom/product/where-used/${encodeURIComponent(cellText(row, 'PRO_NO'))}`)
  }, [cellText, navigate])

  // 新建版本：版次由服务端算（MAX+1），前端只提交源键。
  // 是否显示与「新增」同一个判据（definition.hasAdd），不再读按钮清单。
  // 同步锁：setState 要等重渲染才生效，快速双击会在同一帧里读到两次 false。
  const creatingRef = useRef(false)
  const canNewVersion = definition.data?.hasAdd === true

  const createVersion = useCallback(async (row: Record<string, unknown>) => {
    if (creatingRef.current) return
    creatingRef.current = true
    setCreatingVersion(true)
    try {
      const result = await createBomVersion(cellText(row, 'PRO_NO'), cellText(row, 'EDITION'))
      toast.notify({ message: `已新建版次 ${result.edition}`, variant: 'success' })
      navigate(`/bom/product/${encodeURIComponent(result.proNo)}/${encodeURIComponent(result.edition)}`)
    } catch (error) {
      toast.notify({ message: describeApiError(error, '操作失败。'), variant: 'danger' })
    } finally {
      creatingRef.current = false
      setCreatingVersion(false)
    }
  }, [cellText, creatingRef, navigate, toast])

  /** 列宽写个人层（`SYSQL_FIELDS.F_WIDTH`）：与工作台同一端点、同一语义。失败不打断操作。 */
  const saveColumnWidth = useCallback(async (columnKey: string, width: number) => {
    try {
      await apiClient.put<void>(`/document-workbench/${MODULE_ID}/column-widths`, {
        master: { [columnKey]: Math.round(width) },
        detail: {},
      })
    } catch {
      /* 列宽是操作习惯，保存失败不该弹出错误打断正在做的事 */
    }
  }, [])

  const columns = useMemo<ColumnDef<Record<string, unknown>, unknown>[]>(() => [
    {
      // 选择列由**调用方**提供，`ErpTable` 不会自动生成（全库同此写法）。
      // 冻结在左侧、不可排序/隐藏/拖宽，与工作台列表一致。
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label={`选择 ${cellText(row.original, 'PRO_NO')} ${cellText(row.original, 'EDITION')}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event => event.stopPropagation()}
        />
      ),
    },
    {
      id: 'PRO_NO',
      header: '料号',
      accessorFn: row => cellText(row, 'PRO_NO'),
      meta: { minWidth: 180 },
      cell: ({ row }) => cellText(row.original, 'PRO_NO'),
    },
    {
      id: 'EDITION',
      header: '版次',
      accessorFn: row => cellText(row, 'EDITION'),
      meta: { minWidth: 80 },
      cell: ({ row }) => cellText(row.original, 'EDITION'),
    },
    {
      id: 'BATCH_QTY',
      header: '批量',
      accessorFn: row => cellText(row, 'BATCH_QTY'),
      meta: { className: 'text-end', minWidth: 90 },
    },
    { id: 'PLAN_PERSON', header: '计划员', accessorFn: row => cellText(row, 'PLAN_PERSON'), meta: { minWidth: 100 } },
    { id: 'ATTENTION', header: '注意事项', accessorFn: row => cellText(row, 'ATTENTION'), meta: { minWidth: 180 } },
    { id: 'REMARK', header: '备注', accessorFn: row => cellText(row, 'REMARK'), meta: { minWidth: 180 } },
    {
      id: 'LAST_UPDATE_DATE',
      header: '最后修改',
      accessorFn: row => cellText(row, 'LAST_UPDATE_DATE'),
      meta: { minWidth: 170 },
      cell: ({ row }) => formatDateTime(row.original.LAST_UPDATE_DATE),
    },
    {
      // 操作列：右对齐、右侧冻结（横向滚动时不动）、宽度按按钮组自适应。
      // 与 2305/2306/字段维护/待办 同一套写法。
      id: 'actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'text-nowrap text-end', frozenRight: true, truncate: false, autoWidth: true },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          {/* 与 /admin/groups 同一套：`Button size="sm" variant="ghost"` + 14px 图标 + title，
              不压缩垂直 padding——按钮看起来大一号，行高随之变高，这是该组页面的既有观感。 */}
          <Button
            size="sm"
            variant="ghost"
            icon={<IconEdit size={14} />}
            title="编辑这条 BOM"
            onClick={event => { event.stopPropagation(); openEdit(row.original) }}
          >
            编辑
          </Button>
          <Button
            size="sm"
            variant="ghost"
            icon={<IconSitemap size={14} />}
            title="展开多层结构"
            onClick={event => { event.stopPropagation(); openTree(row.original) }}
          >
            结构树
          </Button>
          <Button
            size="sm"
            variant="ghost"
            icon={<IconSearch size={14} />}
            title="反查这个料被用在哪里"
            onClick={event => { event.stopPropagation(); openWhereUsed(row.original) }}
          >
            反查
          </Button>
          {canNewVersion ? (
            <Button
              size="sm"
              variant="ghost"
              icon={<IconCopy size={14} />}
              title="复制结构到下一个版次"
              disabled={creatingVersion}
              onClick={event => { event.stopPropagation(); void createVersion(row.original) }}
            >
              新建版本
            </Button>
          ) : null}
        </div>
      ),
    },
  ], [canNewVersion, cellText, createVersion, creatingVersion, openEdit, openTree, openWhereUsed])

  const commands = useMemo<ErpCommandItem[]>(() => [
    {
      action: 'new',
      icon: <IconPlus size={16} />,
      label: '新增',
      variant: 'primary',
      onClick: () => navigate('/bom/product/new'),
    },
    {
      action: 'refresh',
      icon: <IconRefresh size={16} />,
      label: '刷新',
      onClick: () => { void records.refetch() },
    },
  ], [navigate, records])




  return (
    <div className="erp-full-list-page">
      <ErpListCard
        search={
          <ErpSearchBox
            value={keyword}
            onChange={setKeyword}
            placeholder="按料号 / 备注搜索"
            ariaLabel="搜索产品 BOM"
          />
        }
        actions={<ErpCommandBar items={commands} ariaLabel="产品 BOM 操作" />}
      >
        {records.isPending ? (
          <LoadingState />
        ) : records.isError ? (
          <ErrorState message={describeApiError(records.error, '操作失败。')} onRetry={() => { void records.refetch() }} />
        ) : rows.length === 0 ? (
          <EmptyState description="还没有 BOM。点「新增」选料号后录入明细。" />
        ) : (
          <ErpTable
            columns={columns}
            data={rows}
            getRowId={rowKey}
            sorting={sorting}
            onSortingChange={setSorting}
            rowSelection={rowSelection}
            onRowSelectionChange={handleRowSelectionChange}
            // 单击即选中（与统一工作台同口径）：checkbox 与键盘操作都走同一份 rowSelection。
            // 没有 activeRowId 时表格不会给当前行任何视觉反馈，看起来像"点了没反应"。
            onRowClick={handleRowClick}
            activeRowId={activeRow ?? undefined}
            copyable
            keyboardNavigation
            virtualize
            // 滚到底自动取下一页（统一工作台同款）；`loadingMore` 防重复触发
            onEndReached={() => { if (records.hasNextPage && !records.isFetchingNextPage) void records.fetchNextPage() }}
            hasMore={records.hasNextPage}
            loadingMore={records.isFetchingNextPage}
            resizable
            persistResize={false}
            onColumnResize={(columnKey, width) => { void saveColumnWidth(columnKey, width) }}
            onRowDoubleClick={openRecord}
            storageKey="bom-product-list"
          />
        )}
      </ErpListCard>
    </div>
  )
}

/**
 * 时间戳按 `yyyy-MM-dd HH:mm:ss` 显示。
 *
 * **不走 `new Date()`**：服务端下发的是库内原值（无时区），转一道 Date 会按浏览器时区
 * 偏移，"最后修改时间"就不再是库里那条时间。这里只做文本切割，缺秒时补 `:00`。
 */
export function formatDateTime(value: unknown): string {
  const text = String(value ?? '').trim()
  if (!text) return ''
  const matched = /^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2})(:\d{2})?/.exec(text)
  if (matched) return `${matched[1]} ${matched[2]}${matched[3] ?? ':00'}`
  return text
}
