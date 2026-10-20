import { IconAdjustmentsHorizontal, IconColumns } from '@tabler/icons-react'
import type { ColumnDef, RowSelectionState, SortingState } from '../../lib/tanstackTable'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { formatFieldValue } from '../../features/document-workbench/fieldFormat'
import { Button } from '../ui/Button'
import { Modal } from '../ui/Modal'
import { ErpColumnSelector } from './ErpColumnSelector'
import { ErpListGrid } from './ErpListGrid'
import { ErpQueryBuilder } from './ErpQueryBuilder'
import { emptyQueryCondition, type QueryCondition } from './queryCondition'
import { loadChooserMemory, loadChooserSource, saveChooserMemory, type UnifiedChooserColumn, type UnifiedChooserData, type UnifiedChooserQuery, type UnifiedChooserRow, type UnifiedChooserSource } from './chooserSource'

export type { UnifiedChooserColumn, UnifiedChooserData, UnifiedChooserQuery, UnifiedChooserRow, UnifiedChooserSource } from './chooserSource'

export interface UnifiedChooserProps<T extends UnifiedChooserRow = UnifiedChooserRow> {
  open: boolean
  title: string
  source: UnifiedChooserSource
  mode: 'single' | 'multi'
  onPick: (rows: T[]) => void
  onClose: () => void
  /** formField 数据源模板值（{m.FIELD}），服务端参数化 */
  masterValues?: Record<string, string>
  /** formField 数据源模板值（{d.FIELD}），服务端参数化 */
  detailValues?: Record<string, string>
  /** 行键（默认按行下标；受控选中场景须传稳定键，如字段选择器用 F_ID） */
  getRowId?: (row: T, index: number) => string
  /** 查询条（字段下拉 + 关键字） */
  searchable?: boolean
  searchPlaceholder?: string
  /** 服务端分页 + 滚动加载（默认服务端数据源开启） */
  serverPaging?: boolean
  /** 客户端本地排序（loader 本地数据场景） */
  clientSorting?: boolean
  resizable?: boolean
  storageKey?: string
  dialogSize?: 'md' | 'lg'
  /** 数据列最大宽度（自动列宽上限，默认 320px） */
  maxColumnWidth?: number
  emptyText?: string
  /** 网格下方附加内容（如已选顺序条） */
  extra?: ReactNode
  /** 追加到服务端列之后的调用方列（如排序字段选择器的升/降序按钮列） */
  extraColumns?: ColumnDef<T, unknown>[]
  /** 服务端列的自定义单元格渲染（如类型列显示“主表/副表”） */
  columnRenderers?: Record<string, (row: T) => ReactNode>
  /** 受控勾选（用于保持选择顺序/附加状态的场景）；不传则由组件内部维护 */
  selectedKeys?: Record<string, boolean>
  onSelectedKeysChange?: (selection: Record<string, boolean>) => void
  /**
   * 服务端下发的列与建议默认列（如单据源的主键列）：调用方需要据此知道"哪一列是键"，
   * 而不是靠猜列顺序。
   */
  onColumnsLoaded?: (columns: UnifiedChooserColumn[], defaultKeys: string[] | null) => void
  /**
   * 选择器记忆的目标表名（如单据的主表/子表）：传入后按「当前用户 + 该表」记住
   * 上次的筛选字段与查询值，下次打开自动预填。不传则不记忆。
   */
  memoryTable?: string
}

const PAGE_SIZE = 50

/**
 * 统一选择器（全系统数据选择输入的唯一入口）：
 * - 工具行按「确认、搜索、高级查询、选择列」排列；首列单选模式为 Radio、多选模式为 CheckBox；
 * - 勾选/点行只改变选中状态；「确认」统一触发 onPick，无选中项时禁用；
 * - 弹窗固定高度，内容多时表格区滚动；「高级查询」为多条件组合（字段白名单 + 参数化），
 *   「选择列」为显示列配置；
 * - 数据列自动列宽 + 不换行 + 单列最大宽度上限；页脚仅保留「取消」。
 */
export function UnifiedChooser<T extends UnifiedChooserRow = UnifiedChooserRow>({
  open,
  title,
  source,
  mode,
  onPick,
  onClose,
  masterValues,
  detailValues,
  getRowId = (_row, index) => String(index),
  searchable = true,
  searchPlaceholder = '输入查询条件，回车查询',
  serverPaging = source.kind !== 'loader',
  clientSorting = false,
  resizable = false,
  storageKey,
  dialogSize = 'lg',
  maxColumnWidth = 320,
  emptyText = '没有匹配的数据。',
  extra,
  extraColumns,
  columnRenderers,
  selectedKeys,
  onSelectedKeysChange,
  onColumnsLoaded,
  memoryTable,
}: UnifiedChooserProps<T>) {
  const controlledSelection = Boolean(onSelectedKeysChange)
  const [internalSelected, setInternalSelected] = useState<RowSelectionState>({})
  const selected = selectedKeys ?? internalSelected
  const commitSelection = (next: RowSelectionState) => {
    if (onSelectedKeysChange) onSelectedKeysChange(next)
    else setInternalSelected(next)
  }
  const selectedCount = Object.keys(selected).length

  const [keyword, setKeyword] = useState('')
  const [filterField, setFilterField] = useState('')
  const [conditions, setConditions] = useState<QueryCondition[]>([])
  /** 高级查询草稿：打开时装入已应用条件，取消不写回、应用才生效 */
  const [advancedDraft, setAdvancedDraft] = useState<QueryCondition[]>([])
  const [visibleColumnKeys, setVisibleColumnKeys] = useState<string[] | null>(null)
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [columnsOpen, setColumnsOpen] = useState(false)
  const [rows, setRows] = useState<T[]>([])
  const [fieldColumns, setFieldColumns] = useState<UnifiedChooserColumn[]>([])
  const [total, setTotal] = useState(0)
  const [sorting, setSorting] = useState<SortingState>([])
  const [page, setPage] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  /** 服务端解析出的目标表（formField 数据源），用于选择器记忆 */
  const [resolvedTable, setResolvedTable] = useState<string | null>(null)

  const requestSeq = useRef(0)
  const wasOpen = useRef(false)
  const appliedDefaultsRef = useRef(false)
  const memorySeq = useRef(0)

  const load = (query: UnifiedChooserQuery): Promise<UnifiedChooserData> => loadChooserSource(source, query)

  const buildQuery = (
    targetPage: number,
    sort: SortingState = sorting,
    keywordValue = keyword,
    filterFieldValue = filterField,
    conditionsValue = conditions,
    applyMemory = false,
  ): UnifiedChooserQuery => ({
    keyword: keywordValue.trim() || undefined,
    filterField: filterFieldValue || undefined,
    conditions: conditionsValue.length > 0 ? conditionsValue : undefined,
    master: masterValues,
    detail: detailValues,
    sortField: sort[0]?.id ?? null,
    sortDirection: sort[0]?.desc ? 'desc' : 'asc',
    applyMemory: applyMemory || undefined,
    page: targetPage,
    pageSize: PAGE_SIZE,
  })

  const fetchPage = async (
    targetPage: number,
    sort: SortingState = sorting,
    keywordValue = keyword,
    filterFieldValue = filterField,
    conditionsValue = conditions,
    applyMemory = false,
  ) => {
    const seq = ++requestSeq.current
    setLoading(true)
    setError(null)
    try {
      const result = await load(buildQuery(targetPage, sort, keywordValue, filterFieldValue, conditionsValue, applyMemory))
      if (seq !== requestSeq.current) return
      setRows(result.rows as T[])
      setFieldColumns(result.columns)
      setTotal(result.total)
      if (!appliedDefaultsRef.current) {
        appliedDefaultsRef.current = true
        setVisibleColumnKeys(result.defaultKeys?.length ? result.defaultKeys : null)
      }
      if (result.targetTable) setResolvedTable(result.targetTable)
      // 服务端已在本次查询里套用了记忆：回填查询条即可，不再按同一条件重查一遍
      if (result.appliedMemory) {
        const memoryField = result.appliedMemory.filterField ?? ''
        // 记忆里的筛选列可能已不可见：只回填仍在下拉里的列
        setFilterField(result.columns.some(column => column.key === memoryField) ? memoryField : '')
        setKeyword(result.appliedMemory.keyword ?? '')
      }
      onColumnsLoaded?.(result.columns, result.defaultKeys ?? null)
      if (!controlledSelection) setInternalSelected({})
      setPage(targetPage)
    } catch (cause) {
      if (seq !== requestSeq.current) return
      setError(cause instanceof Error ? cause.message : '选择器加载失败。')
    } finally {
      if (seq === requestSeq.current) setLoading(false)
    }
  }

  const loadMore = async () => {
    if (!serverPaging || loading || loadingMore || rows.length >= total) return
    const seq = ++requestSeq.current
    setLoadingMore(true)
    setError(null)
    try {
      const result = await load(buildQuery(page + 1))
      if (seq !== requestSeq.current) return
      setRows(current => [...current, ...result.rows as T[]])
      setTotal(result.total)
      setPage(current => current + 1)
    } catch (cause) {
      if (seq !== requestSeq.current) return
      setError(cause instanceof Error ? cause.message : '选择器加载失败。')
    } finally {
      if (seq === requestSeq.current) setLoadingMore(false)
    }
  }

  useEffect(() => {
    if (open && !wasOpen.current) {
      wasOpen.current = true
      setConditions([])
      setVisibleColumnKeys(null)
      appliedDefaultsRef.current = false
      setAdvancedOpen(false)
      setColumnsOpen(false)
      setKeyword('')
      setFilterField('')
      setResolvedTable(null)

      if (!memoryTable) {
        // 目标表由服务端解析：首查请服务端一并套用记忆，避免"空条件查一次 → 读记忆 → 再重查"
        void fetchPage(1, [], '', '', [], true)
      } else {
        // 记忆是录入加速：先按上次的筛选条件打开，读不到就按空条件打开。
        const seq = ++memorySeq.current
        void loadChooserMemory(memoryTable).then(memory => {
          if (seq !== memorySeq.current || !wasOpen.current) return
          const nextField = memory?.filterField ?? ''
          const nextKeyword = memory?.keyword ?? ''
          setFilterField(nextField)
          setKeyword(nextKeyword)
          void fetchPage(1, [], nextKeyword, nextField, [])
        })
      }
    } else if (!open) {
      wasOpen.current = false
      memorySeq.current += 1
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  // 用户主动查询时落地记忆（分页与排序不算"这次怎么搜的"）
  const runSearch = () => {
    const table = memoryTable ?? resolvedTable
    if (table) void saveChooserMemory(table, filterField, keyword.trim())
    void fetchPage(1)
  }

  const handleSortingChange = (next: SortingState) => {
    setSorting(next)
    if (serverPaging) void fetchPage(1, next)
  }

  // 首查失败时查询条还没被记忆回填，重试仍请服务端套用记忆（仅首页且没有显式条件时）
  const retry = () => {
    void fetchPage(page, sorting, keyword, filterField, conditions, page === 1 && !keyword.trim() && !filterField)
  }

  const toggleRowSelection = (id: string) => {
    commitSelection(mode === 'single' ? { [id]: true } : { ...selected, [id]: !selected[id] })
  }

  const rowById = useMemo(
    () => new Map(rows.map((row, index) => [getRowId(row, index), row])),
    [rows, getRowId],
  )

  const confirm = () => {
    const picked = Object.keys(selected)
      .map(id => rowById.get(id))
      .filter((row): row is T => Boolean(row))
    if (picked.length > 0) onPick(picked)
  }

  const applyAdvanced = (next: QueryCondition[]) => {
    setConditions(next)
    void fetchPage(1, sorting, keyword, filterField, next)
  }

  const columnGroups = useMemo(() => [{
    id: 'columns',
    label: '选择器列',
    fields: fieldColumns.map(column => ({ key: column.key, label: column.label })),
    visibleKeys: visibleColumnKeys ?? fieldColumns.map(column => column.key),
    defaultKeys: fieldColumns.map(column => column.key),
  }], [fieldColumns, visibleColumnKeys])

  const selectColumn: ColumnDef<T, unknown> = {
    id: 'select',
    enableSorting: false,
    enableHiding: false,
    meta: { className: 'erp-select-column', resizable: false, frozenLeft: true, truncate: false },
    header: mode === 'multi' ? ({ table }) => (
      <input
        className="form-check-input"
        type="checkbox"
        aria-label="全选"
        checked={table.getIsAllPageRowsSelected()}
        ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
        onChange={table.getToggleAllPageRowsSelectedHandler()}
      />
    ) : () => null,
    cell: ({ row }) => (
      <input
        className="form-check-input"
        type={mode === 'multi' ? 'checkbox' : 'radio'}
        name={mode === 'multi' ? undefined : 'unified-chooser'}
        aria-label="选择此行"
        checked={row.getIsSelected()}
        onChange={event => {
          // 单选组点击时浏览器可能对“失去选中”的 radio 也触发 change，只认勾选事件
          if (mode === 'multi' || event.target.checked) toggleRowSelection(row.id)
        }}
        onClick={event => event.stopPropagation()}
      />
    ),
  }

  // 显示列以「选择列」保存的顺序为准：按 visibleColumnKeys 取序（未配置时用服务端字段顺序），
  // 不能按服务端字段顺序过滤——否则用户在「选择列」里调整的顺序不会生效。
  const visibleColumns: UnifiedChooserColumn[] = visibleColumnKeys === null
    ? fieldColumns
    : visibleColumnKeys
      .map(key => fieldColumns.find(column => column.key === key))
      .filter((column): column is UnifiedChooserColumn => Boolean(column))

  const dataColumns: ColumnDef<T, unknown>[] = visibleColumns
    .map(column => ({
      id: column.key,
      accessorKey: column.key,
      header: column.label,
      enableSorting: serverPaging || clientSorting,
      meta: {
        dataType: column.dataType,
        maxWidth: maxColumnWidth,
        title: ({ value }) => formatFieldValue(value, column.dataType, column.format ?? null) || undefined,
      },
      cell: ({ row, getValue }) => {
        const custom = columnRenderers?.[column.key]
        if (custom) return custom(row.original as T)
        const text = formatFieldValue(getValue(), column.dataType, column.format ?? null)
        return text || '—'
      },
    }))

  const columns: ColumnDef<T, unknown>[] = [selectColumn, ...dataColumns, ...(extraColumns ?? [])]

  if (!open) return null

  return (
    <>
      <Modal
        title={`${title}${mode === 'multi' ? '（可多选）' : ''}`}
        onClose={onClose}
        dialogClassName={`erp-chooser-dialog ${dialogSize === 'lg' ? 'erp-dialog-lg' : 'erp-dialog-md'}`}
        footer={<div className="d-flex gap-2 ms-auto">
          <Button onClick={onClose}>取消</Button>
          <Button variant="primary" disabled={selectedCount === 0} onClick={confirm}>确认</Button>
        </div>}
      >
        {/* 工具行在列表卡之外：卡片只包表格与统计 */}
        <div className="erp-chooser-toolbar">
          {searchable ? (
            <div className="input-group erp-chooser-query">
              <select className="form-select erp-chooser-field" value={filterField} onChange={event => setFilterField(event.target.value)}>
                <option value="">全部</option>
                {fieldColumns.map(column => <option key={column.key} value={column.key}>{column.label}</option>)}
              </select>
              <input
                className="form-control"
                value={keyword}
                placeholder={searchPlaceholder}
                onChange={event => setKeyword(event.target.value)}
                onKeyDown={event => { if (event.key === 'Enter') runSearch() }}
              />
              <Button variant="primary" loading={loading} onClick={runSearch}>查询</Button>
            </div>
          ) : null}
          <Button
            className="erp-chooser-icon-btn"
            icon={<IconAdjustmentsHorizontal size={16} />}
            title={conditions.length > 0 ? `高级查询（${conditions.length}）` : '高级查询'}
            aria-label="高级查询"
            disabled={fieldColumns.length === 0}
            onClick={() => {
              setAdvancedDraft(conditions.length > 0 ? conditions : [emptyQueryCondition()])
              setAdvancedOpen(true)
            }}
          />
          <Button
            className="erp-chooser-icon-btn"
            icon={<IconColumns size={16} />}
            title="选择列"
            aria-label="选择列"
            disabled={fieldColumns.length === 0}
            onClick={() => setColumnsOpen(true)}
          />
        </div>
        <ErpListGrid
          listId={storageKey ?? 'unified-chooser'}
          columns={columns}
          rows={rows}
          rowKey={getRowId}
          loading={loading && rows.length === 0}
          loadingLabel="正在加载…"
          error={error}
          onRetry={retry}
          empty={<div className="text-secondary py-4 text-center">{emptyText}</div>}
          selection={{ value: selected, onChange: commitSelection }}
          onRowClick={row => toggleRowSelection(getRowId(row, rows.indexOf(row)))}
          regionClassName="erp-chooser-body-table"
          footer={(
            <span className="text-secondary small">
              共 {total} 条{rows.length < total ? `，已加载 ${rows.length} 条` : ''}，已选 {selectedCount} 项
            </span>
          )}
          tableProps={{
            resizable,
            responsive: false,
            keyboardNavigation: false,
            // 点行选择只走下面的 onRowClick，避免表格内部再改一次选择态
            rowClickSingleSelect: false,
            clientSideSorting: clientSorting,
            sorting,
            onSortingChange: handleSortingChange,
            onEndReached: serverPaging ? () => void loadMore() : undefined,
            hasMore: serverPaging && rows.length < total,
            loadingMore,
          }}
        >
          <div className="erp-chooser-extra">{extra}</div>
        </ErpListGrid>
      </Modal>
      {advancedOpen ? (
        <ErpQueryBuilder
          open
          title="高级查询"
          fields={fieldColumns.map(column => ({ key: column.key, label: column.label }))}
          conditions={advancedDraft}
          onChange={setAdvancedDraft}
          onApply={() => {
            const applied = advancedDraft.filter(item => item.field)
            setAdvancedOpen(false)
            applyAdvanced(applied)
          }}
          onClear={() => {
            setAdvancedDraft([emptyQueryCondition()])
            setAdvancedOpen(false)
            applyAdvanced([])
          }}
          onClose={() => setAdvancedOpen(false)}
        />
      ) : null}
      {columnsOpen ? (
        <ErpColumnSelector
          open
          title="选择列"
          groups={columnGroups}
          onClose={() => setColumnsOpen(false)}
          onSave={async (selection) => {
            // 保存的列配置始终带顺序：即使选满全部列，也要保留用户排定的顺序
            // （把"选满"折叠成 null 会让顺序回落到服务端字段顺序，用户的重排静默失效）
            setVisibleColumnKeys(selection['columns'] ?? [])
            setColumnsOpen(false)
          }}
        />
      ) : null}
    </>
  )
}
