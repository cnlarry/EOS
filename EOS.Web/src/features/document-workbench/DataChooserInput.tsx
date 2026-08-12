import { useEffect, useMemo, useRef, useState } from 'react'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { Button } from '../../components/ui/Button'
import { ErpTable } from '../../components/common/ErpTable'
import { apiClient } from '../../services/api'
import { formatFieldValue } from './fieldFormat'
import type { FormFieldDefinition } from './formDefinition'

export interface ChooserRow { [key: string]: unknown }
export interface ChooserColumn { key: string; label: string; dataType: string; format: string | null }
export interface ChooserData { columns: ChooserColumn[]; rows: ChooserRow[]; total: number }

interface DataChooserInputProps {
  moduleId: string
  field: FormFieldDefinition
  /** 主表当前值：用于 CHOOSE_FILTER 的 {m.FIELD} 模板替换（服务端参数化） */
  masterValues?: Record<string, string>
  /** 明细当前行值：用于 CHOOSE_FILTER 的 {d.FIELD} 模板替换（服务端参数化） */
  detailValues?: Record<string, string>
  /** 完成选择：单选传单元素数组；多选（明细添加）传勾选的多行 */
  onPick: (rows: ChooserRow[]) => void
  onClose: () => void
}

/**
 * 通用选择器（对齐旧系统 Chooser.aspx + document-workbench 电子表格体验）：
 * - 首列复选框：单选勾选即完成；多选支持表头全选 + 逐行勾选后「确定」回填；
 * - 统一电子表格（ErpTable）：列宽拖拽持久化（按模块+字段隔离）、列头菜单服务端排序、
 *   复制、行点击/键盘选择等能力与工作台一致。
 * 多选仅用于统一编辑器添加明细（旧系统 TR 传参逐行填值），主表选择一律单选。
 */
export function DataChooserInput({ moduleId, field, masterValues, detailValues, onPick, onClose }: DataChooserInputProps) {
  const source = field.choosers.find(item => item.active && item.table)
  const multi = field.chooseMultiple
  const [filterField, setFilterField] = useState('')
  const [keyword, setKeyword] = useState('')
  const [rows, setRows] = useState<ChooserRow[]>([])
  const [fieldColumns, setFieldColumns] = useState<ChooserColumn[]>([])
  const [total, setTotal] = useState(0)
  const [sorting, setSorting] = useState<SortingState>([])
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<RowSelectionState>({})
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const sortField = sorting[0]?.id ?? null
  const sortDirection: 'asc' | 'desc' = sorting[0]?.desc ? 'desc' : 'asc'

  const buildQuery = (targetPage: number, sortFieldValue = sortField, sortDirectionValue = sortDirection): Record<string, string> => {
    const query: Record<string, string> = {}
    if (keyword.trim()) query.keyword = keyword.trim()
    if (filterField) query.filterField = filterField
    if (masterValues && Object.keys(masterValues).length > 0) query.master = JSON.stringify(masterValues)
    if (detailValues && Object.keys(detailValues).length > 0) query.detail = JSON.stringify(detailValues)
    if (sortFieldValue) query.sortField = sortFieldValue
    if (sortDirectionValue === 'desc') query.sortDirection = 'desc'
    query.page = String(targetPage)
    query.pageSize = '50'
    return query
  }

  const search = async (nextSortField?: string | null, nextSortDirection?: 'asc' | 'desc', nextPage = 1) => {
    setLoading(true)
    setError(null)
    try {
      const result = await apiClient.get<ChooserData>(`/document-workbench/${moduleId}/form-chooser/${encodeURIComponent(field.key)}`, {
        query: buildQuery(nextPage, nextSortField ?? sortField, nextSortDirection ?? sortDirection),
      })
      setRows(result.rows)
      setFieldColumns(result.columns)
      setTotal(result.total)
      setSelected({})
      setPage(nextPage)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : '选择器加载失败。')
    } finally {
      setLoading(false)
    }
  }

  const loadMore = async () => {
    if (loading || loadingMore || rows.length >= total) return
    setLoadingMore(true)
    setError(null)
    try {
      const result = await apiClient.get<ChooserData>(`/document-workbench/${moduleId}/form-chooser/${encodeURIComponent(field.key)}`, {
        query: buildQuery(page + 1),
      })
      setRows(current => [...current, ...result.rows])
      setTotal(result.total)
      setPage(current => current + 1)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : '选择器加载失败。')
    } finally {
      setLoadingMore(false)
    }
  }

  const loaded = useRef(false)
  useEffect(() => {
    if (loaded.current) return
    loaded.current = true
    void search()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const handleSortingChange = (next: SortingState) => {
    setSorting(next)
    const sort = next[0]
    void search(sort?.id ?? null, sort?.desc ? 'desc' : 'asc', 1)
  }

  const toggleRowSelection = (id: string) => {
    setSelected(current => {
      const next = { ...current }
      if (next[id]) delete next[id]
      else next[id] = true
      return next
    })
  }

  const confirmMulti = () => {
    const pickedRows = Object.keys(selected)
      .map(id => rows[Number(id)])
      .filter((row): row is ChooserRow => Boolean(row))
    if (pickedRows.length > 0) onPick(pickedRows)
  }

  const indexByRow = useMemo(() => new Map(rows.map((row, index) => [row, index])), [rows])
  const columns: ColumnDef<ChooserRow, unknown>[] = [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', resizable: false, frozenLeft: true, truncate: false },
      header: multi ? ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="全选"
          checked={table.getIsAllPageRowsSelected()}
          ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ) : () => null,
      cell: ({ row }) => multi ? (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event => event.stopPropagation()}
        />
      ) : (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择"
          checked={false}
          onChange={() => onPick([row.original])}
          onClick={event => event.stopPropagation()}
        />
      ),
    },
    ...fieldColumns.map((column): ColumnDef<ChooserRow, unknown> => ({
      id: column.key,
      accessorKey: column.key,
      header: column.label,
      enableSorting: true,
      meta: {
        dataType: column.dataType,
        title: ({ value }) => formatFieldValue(value, column.dataType, column.format) || undefined,
      },
      cell: ({ getValue }) => {
        const text = formatFieldValue(getValue(), column.dataType, column.format)
        return text || '—'
      },
    })),
  ]

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered modal-lg">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{field.label}{source?.description ? `（${source.description}）` : ''}{multi ? '（可多选）' : ''}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {/* 查询区：字段下拉 + 条件值 + 查询 */}
            <div className="input-group mb-2 erp-chooser-query">
              <select className="form-select erp-chooser-field" value={filterField} onChange={event => setFilterField(event.target.value)}>
                <option value="">全部</option>
                {fieldColumns.map(column => <option key={column.key} value={column.key}>{column.label}</option>)}
              </select>
              <input className="form-control" value={keyword} placeholder="输入查询条件，回车查询" onChange={event => setKeyword(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void search() }} />
              <Button variant="primary" loading={loading} onClick={() => void search()}>查询</Button>
            </div>
            {error ? <div className="alert alert-danger">{error}</div> : null}
            {!error && !loading && rows.length === 0
              ? <div className="text-secondary py-4 text-center">没有可选数据。</div>
              : null}
            {!error && rows.length > 0 ? (
              <div className="table-responsive" style={{ maxHeight: 360, overflow: 'auto' }}>
                <ErpTable
                  columns={columns}
                  data={rows}
                  getRowId={(_row, index) => String(index)}
                  sorting={sorting}
                  onSortingChange={handleSortingChange}
                  rowSelection={selected}
                  onRowSelectionChange={setSelected}
                  onRowClick={multi
                    ? row => toggleRowSelection(String(indexByRow.get(row) ?? -1))
                    : row => onPick([row])}
                  resizable
                  storageKey={`chooser-${moduleId}-${field.key}`}
                  responsive={false}
                  keyboardNavigation={false}
                  empty={null}
                  onEndReached={() => void loadMore()}
                  hasMore={rows.length < total}
                  loadingMore={loadingMore}
                />
                <div className="d-flex align-items-center justify-content-between mt-2">
                  <span className="text-secondary small">
                    共 {total} 条{rows.length < total ? `，已加载 ${rows.length} 条` : ''}
                  </span>
                  {loadingMore ? <span className="text-secondary small">正在加载更多…</span> : null}
                </div>
              </div>
            ) : null}
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            {multi
              ? <Button variant="primary" disabled={Object.keys(selected).length === 0} onClick={confirmMulti}>确定（已选 {Object.keys(selected).length} 项）</Button>
              : null}
          </div>
        </div>
      </div>
    </div>
  )
}
