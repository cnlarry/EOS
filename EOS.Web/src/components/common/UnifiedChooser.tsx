import { IconPlus, IconTrash } from '@tabler/icons-react'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { formatFieldValue } from '../../features/document-workbench/fieldFormat'
import { Button } from '../ui/Button'
import { ErrorState, LoadingState } from './AsyncState'
import { ErpColumnSelector } from './ErpColumnSelector'
import { ErpTable } from './ErpTable'
import { emptyQueryCondition, queryOperators, type QueryCondition } from './queryCondition'
import { apiClient } from '../../services/api'

export interface UnifiedChooserColumn {
  key: string
  label: string
  dataType: string
  format?: string | null
  /** 兼容 document-workbench form-chooser 旧协议的显示格式字段 */
  displayFormat?: string | null
}

export interface UnifiedChooserRow { [key: string]: unknown }
export interface UnifiedChooserData { columns: UnifiedChooserColumn[]; rows: UnifiedChooserRow[]; total: number }

export interface UnifiedChooserQuery {
  keyword?: string
  filterField?: string
  /** 高级查询结构化条件（服务端按数据源白名单 + 参数化执行） */
  conditions?: QueryCondition[]
  master?: Record<string, string>
  detail?: Record<string, string>
  sortField?: string | null
  sortDirection?: 'asc' | 'desc'
  page: number
  pageSize: number
}

/**
 * 统一选择器数据源（服务端描述）：
 * - formField：复用现有 CHOOSE_* 表单字段元数据（GET form-chooser 端点）；
 * - sourceKey：服务端注册数据源（POST /api/chooser/query，表/列/权限由服务端白名单解析）；
 * - loader：调用方自定义加载函数（过渡期，闭包内仍走各自授权 API）。
 */
export type UnifiedChooserSource =
  | { kind: 'formField'; moduleId: string; fieldKey: string }
  | { kind: 'sourceKey'; key: string; args?: Record<string, string> }
  | { kind: 'loader'; load: (query: UnifiedChooserQuery) => Promise<UnifiedChooserData> }

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

  const requestSeq = useRef(0)
  const wasOpen = useRef(false)

  const load = async (query: UnifiedChooserQuery): Promise<UnifiedChooserData> => {
    if (source.kind === 'loader') return source.load(query)
    if (source.kind === 'sourceKey') {
      return apiClient.post<UnifiedChooserData>('/chooser/query', {
        sourceKey: source.key,
        args: source.args,
        keyword: query.keyword,
        filterField: query.filterField,
        conditions: query.conditions?.length ? query.conditions : undefined,
        sortField: query.sortField,
        sortDirection: query.sortDirection,
        page: query.page,
        pageSize: query.pageSize,
      })
    }
    const result = await apiClient.get<UnifiedChooserData>(`/document-workbench/${source.moduleId}/form-chooser/${encodeURIComponent(source.fieldKey)}`, {
      query: {
        keyword: query.keyword,
        filterField: query.filterField,
        conditions: query.conditions?.length ? JSON.stringify(query.conditions) : undefined,
        master: query.master ? JSON.stringify(query.master) : undefined,
        detail: query.detail ? JSON.stringify(query.detail) : undefined,
        sortField: query.sortField ?? undefined,
        sortDirection: query.sortDirection,
        page: String(query.page),
        pageSize: String(query.pageSize),
      },
    })
    return {
      columns: (result.columns ?? []).map(column => ({
        key: column.key,
        label: column.label,
        dataType: column.dataType,
        format: column.format ?? column.displayFormat ?? null,
      })),
      rows: result.rows ?? [],
      total: result.total ?? 0,
    }
  }

  const buildQuery = (
    targetPage: number,
    sort: SortingState = sorting,
    keywordValue = keyword,
    filterFieldValue = filterField,
    conditionsValue = conditions,
  ): UnifiedChooserQuery => ({
    keyword: keywordValue.trim() || undefined,
    filterField: filterFieldValue || undefined,
    conditions: conditionsValue.length > 0 ? conditionsValue : undefined,
    master: masterValues,
    detail: detailValues,
    sortField: sort[0]?.id ?? null,
    sortDirection: sort[0]?.desc ? 'desc' : 'asc',
    page: targetPage,
    pageSize: PAGE_SIZE,
  })

  const fetchPage = async (
    targetPage: number,
    sort: SortingState = sorting,
    keywordValue = keyword,
    filterFieldValue = filterField,
    conditionsValue = conditions,
  ) => {
    const seq = ++requestSeq.current
    setLoading(true)
    setError(null)
    try {
      const result = await load(buildQuery(targetPage, sort, keywordValue, filterFieldValue, conditionsValue))
      if (seq !== requestSeq.current) return
      setRows(result.rows as T[])
      setFieldColumns(result.columns)
      setTotal(result.total)
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
      setKeyword('')
      setFilterField('')
      setConditions([])
      setVisibleColumnKeys(null)
      setAdvancedOpen(false)
      setColumnsOpen(false)
      void fetchPage(1, [], '', '', [])
    } else if (!open) {
      wasOpen.current = false
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const handleSortingChange = (next: SortingState) => {
    setSorting(next)
    if (serverPaging) void fetchPage(1, next)
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

  const dataColumns: ColumnDef<T, unknown>[] = fieldColumns
    .filter(column => visibleColumnKeys === null || visibleColumnKeys.includes(column.key))
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
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className={`modal-dialog modal-dialog-centered erp-chooser-dialog ${dialogSize === 'lg' ? 'erp-dialog-lg' : 'erp-dialog-md'}`}>
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title}{mode === 'multi' ? '（可多选）' : ''}</h2>
            <button className="btn-close ms-auto" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            <div className="erp-chooser-toolbar">
              <Button variant="primary" disabled={selectedCount === 0} onClick={confirm}>确认</Button>
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
                    onKeyDown={event => { if (event.key === 'Enter') void fetchPage(1) }}
                  />
                  <Button variant="primary" loading={loading} onClick={() => void fetchPage(1)}>查询</Button>
                </div>
              ) : null}
              <Button onClick={() => setAdvancedOpen(true)} disabled={fieldColumns.length === 0}>
                高级查询{conditions.length > 0 ? `（${conditions.length}）` : ''}
              </Button>
              <Button onClick={() => setColumnsOpen(true)} disabled={fieldColumns.length === 0}>选择列</Button>
            </div>
            {error ? <ErrorState message={error} onRetry={() => void fetchPage(page)} /> : null}
            {!error && loading && rows.length === 0 ? <LoadingState label="正在加载…" /> : null}
            {!error && !loading && rows.length === 0 ? (
              <div className="text-secondary py-4 text-center">{emptyText}</div>
            ) : null}
            {!error && rows.length > 0 ? (
              <>
                <div className="erp-chooser-body-table">
                  <ErpTable
                    columns={columns}
                    data={rows}
                    getRowId={getRowId}
                    sorting={sorting}
                    onSortingChange={handleSortingChange}
                    clientSideSorting={clientSorting}
                    rowSelection={selected}
                    onRowSelectionChange={commitSelection}
                    onRowClick={row => toggleRowSelection(getRowId(row, rows.indexOf(row)))}
                    resizable={resizable}
                    storageKey={storageKey}
                    responsive={false}
                    keyboardNavigation={false}
                    empty={null}
                    onEndReached={serverPaging ? () => void loadMore() : undefined}
                    hasMore={serverPaging && rows.length < total}
                    loadingMore={loadingMore}
                  />
                </div>
                <div className="d-flex align-items-center justify-content-between mt-2">
                  <span className="text-secondary small">
                    共 {total} 条{rows.length < total ? `，已加载 ${rows.length} 条` : ''}，已选 {selectedCount} 项
                  </span>
                </div>
              </>
            ) : null}
            <div className="erp-chooser-extra">{extra}</div>
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
          </div>
        </div>
      </div>
      {advancedOpen ? (
        <ChooserAdvancedQuery
          open
          fields={fieldColumns}
          initial={conditions}
          onApply={applyAdvanced}
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
            const keys = selection['columns'] ?? []
            setVisibleColumnKeys(keys.length === fieldColumns.length ? null : keys)
            setColumnsOpen(false)
          }}
        />
      ) : null}
    </div>
  )
}

/**
 * 高级查询：多条件组合（字段 + 运算符 + 值 + AND/OR），与工作台列头筛选同一套运算符语义。
 * 提交后由选择器按数据源白名单 + 参数化发送服务端执行。
 */
function ChooserAdvancedQuery({
  open,
  fields,
  initial,
  onApply,
  onClose,
}: {
  open: boolean
  fields: UnifiedChooserColumn[]
  initial: QueryCondition[]
  onApply: (conditions: QueryCondition[]) => void
  onClose: () => void
}) {
  const [rows, setRows] = useState<QueryCondition[]>([])

  useEffect(() => {
    if (open) setRows(initial.length > 0 ? initial : [emptyQueryCondition()])
  }, [open, initial])

  const updateRow = (index: number, patch: Partial<QueryCondition>) => {
    setRows(current => current.map((row, i) => i === index ? { ...row, ...patch } : row))
  }

  const apply = () => {
    onApply(rows.filter(row => row.field))
    onClose()
  }

  if (!open) return null

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered erp-dialog-md">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">高级查询</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            <div className="erp-filter-rows">
              {rows.map((row, index) => (
                <div key={index} className="erp-filter-row d-flex gap-2 align-items-center">
                  {index > 0 && (
                    <select
                      className="form-select form-select-sm erp-filter-logic"
                      value={row.logic}
                      onChange={event => updateRow(index, { logic: event.target.value })}
                      aria-label="条件逻辑"
                    >
                      <option value="and">AND</option>
                      <option value="or">OR</option>
                    </select>
                  )}
                  <select
                    className="form-select form-select-sm"
                    value={row.field}
                    onChange={event => updateRow(index, { field: event.target.value })}
                    aria-label="条件字段"
                  >
                    <option value="">选择字段…</option>
                    {fields.map(field => <option key={field.key} value={field.key}>{field.label}</option>)}
                  </select>
                  <select
                    className="form-select form-select-sm erp-filter-op"
                    value={row.operator}
                    onChange={event => updateRow(index, { operator: event.target.value })}
                    aria-label="条件运算符"
                  >
                    {queryOperators.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                  </select>
                  <input
                    className="form-control form-control-sm"
                    value={row.value}
                    placeholder="值"
                    disabled={row.operator === 'empty' || row.operator === 'notempty'}
                    onChange={event => updateRow(index, { value: event.target.value })}
                  />
                  {row.operator === 'between' && (
                    <input
                      className="form-control form-control-sm"
                      value={row.valueTo}
                      placeholder="至"
                      onChange={event => updateRow(index, { valueTo: event.target.value })}
                    />
                  )}
                  <button
                    type="button"
                    className="erp-field-mini"
                    aria-label="删除条件"
                    onClick={() => setRows(current => current.filter((_, i) => i !== index))}
                  >
                    <IconTrash size={14} />
                  </button>
                </div>
              ))}
            </div>
            <div className="mt-2 d-flex gap-2">
              <Button size="sm" icon={<IconPlus size={14} />} onClick={() => setRows(current => [...current, emptyQueryCondition()])}>
                添加条件
              </Button>
              <Button size="sm" variant="ghost" onClick={() => { onApply([]); onClose() }}>清除</Button>
            </div>
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            <Button variant="primary" onClick={apply}>应用</Button>
          </div>
        </div>
      </div>
    </div>
  )
}
