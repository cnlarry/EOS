import { useEffect, useRef, useState } from 'react'
import { Button } from '../../components/ui/Button'
import { ResizableTable } from '../../components/common/ResizableTable'
import { apiClient } from '../../services/api'
import type { FormFieldDefinition } from './formDefinition'

export interface ChooserRow { [key: string]: unknown }
export interface ChooserColumn { key: string; label: string }
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
 * - 列宽拖拽持久化（ResizableTable，按模块+字段隔离）；
 * - 表头点击服务端排序（sortField/sortDirection）。
 * 多选仅用于统一编辑器添加明细（旧系统 TR 传参逐行填值），主表选择一律单选。
 */
export function DataChooserInput({ moduleId, field, masterValues, detailValues, onPick, onClose }: DataChooserInputProps) {
  const source = field.choosers.find(item => item.active && item.table)
  const multi = field.chooseMultiple
  const [filterField, setFilterField] = useState('')
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<ChooserData | null>(null)
  const [sortField, setSortField] = useState<string | null>(null)
  const [sortDirection, setSortDirection] = useState<'asc' | 'desc'>('asc')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const search = async (nextSortField?: string | null, nextSortDirection?: 'asc' | 'desc', nextPage = 1) => {
    setLoading(true)
    setError(null)
    try {
      const query: Record<string, string> = {}
      if (keyword.trim()) query.keyword = keyword.trim()
      if (filterField) query.filterField = filterField
      if (masterValues && Object.keys(masterValues).length > 0) query.master = JSON.stringify(masterValues)
      if (detailValues && Object.keys(detailValues).length > 0) query.detail = JSON.stringify(detailValues)
      if (nextSortField ?? sortField) query.sortField = (nextSortField ?? sortField)!
      if ((nextSortDirection ?? sortDirection) === 'desc') query.sortDirection = 'desc'
      query.page = String(nextPage)
      query.pageSize = '50'
      const result = await apiClient.get<ChooserData>(`/document-workbench/${moduleId}/form-chooser/${encodeURIComponent(field.key)}`, {
        query,
      })
      setData(result)
      setSelected(new Set())
      setPage(nextPage)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : '选择器加载失败。')
    } finally {
      setLoading(false)
    }
  }

  const loaded = useRef(false)
  useEffect(() => {
    if (loaded.current) return
    loaded.current = true
    void search()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const toggleSort = (columnKey: string) => {
    if (sortField === columnKey) {
      const next = sortDirection === 'asc' ? 'desc' : 'asc'
      setSortDirection(next)
      void search(columnKey, next, 1)
    } else {
      setSortField(columnKey)
      setSortDirection('asc')
      void search(columnKey, 'asc', 1)
    }
  }

  const allSelected = data != null && data.rows.length > 0 && selected.size === data.rows.length

  const toggleRow = (index: number) => {
    if (!multi) {
      // 单选：勾选即完成选择
      const row = data?.rows[index]
      if (row) onPick([row])
      return
    }
    setSelected(current => {
      const next = new Set(current)
      if (next.has(index)) next.delete(index)
      else next.add(index)
      return next
    })
  }

  const toggleAll = () => {
    if (!data) return
    setSelected(allSelected ? new Set() : new Set(data.rows.map((_, index) => index)))
  }

  const confirmMulti = () => {
    if (!data) return
    const rows = data.rows.filter((_, index) => selected.has(index))
    if (rows.length > 0) onPick(rows)
  }

  const total = data?.total ?? 0
  const totalPages = Math.max(1, Math.ceil(total / 50))

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
                {data?.columns.map(column => <option key={column.key} value={column.key}>{column.label}</option>)}
              </select>
              <input className="form-control" value={keyword} placeholder="输入查询条件，回车查询" onChange={event => setKeyword(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void search() }} />
              <Button variant="primary" loading={loading} onClick={() => void search()}>查询</Button>
            </div>
            {error ? <div className="alert alert-danger">{error}</div> : null}
            {data && !error ? (data.rows.length === 0
              ? <div className="text-secondary py-4 text-center">没有可选数据。</div>
              : (
              <div className="table-responsive" style={{ maxHeight: 360, overflow: 'auto' }}>
                <ResizableTable className="table table-sm table-hover mb-0" storageKey={`chooser-${moduleId}-${field.key}`}>
                  <thead>
                    <tr>
                      {multi ? (
                        <th style={{ width: 36 }}>
                          <input type="checkbox" className="form-check-input" checked={allSelected} onChange={toggleAll} aria-label="全选" />
                        </th>
                      ) : <th style={{ width: 36 }} />}
                      {data.columns.map(column => (
                        <th key={column.key} className="erp-chooser-sortable" onClick={() => toggleSort(column.key)} style={{ cursor: 'pointer', userSelect: 'none' }}>
                          {column.label}
                          {sortField === column.key ? (sortDirection === 'asc' ? ' ▲' : ' ▼') : ''}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {data.rows.map((row, index) => (
                      <tr
                        key={index}
                        className={`erp-clickable${selected.has(index) ? ' table-active' : ''}`}
                        onClick={() => toggleRow(index)}
                        onDoubleClick={() => { if (!multi) onPick([row]) }}
                      >
                        <td>
                          <input
                            type="checkbox"
                            className="form-check-input"
                            checked={selected.has(index)}
                            onChange={() => toggleRow(index)}
                            onClick={event => event.stopPropagation()}
                            aria-label={multi ? '选择此行' : '选择'}
                          />
                        </td>
                        {data.columns.map(column => <td key={column.key}>{String(row[column.key] ?? '')}</td>)}
                      </tr>
                    ))}
                  </tbody>
                </ResizableTable>
                <div className="d-flex align-items-center justify-content-between mt-2">
                  <span className="text-secondary small">共 {total} 条</span>
                  <div className="btn-group">
                    <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => void search(sortField, sortDirection, page - 1)}>上一页</Button>
                    <span className="align-self-center mx-2 small">第 {page} / {totalPages} 页</span>
                    <Button size="sm" variant="secondary" disabled={page >= totalPages} onClick={() => void search(sortField, sortDirection, page + 1)}>下一页</Button>
                  </div>
                </div>
              </div>
            )) : null}
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            {multi
              ? <Button variant="primary" disabled={selected.size === 0} onClick={confirmMulti}>确定（已选 {selected.size} 项）</Button>
              : null}
          </div>
        </div>
      </div>
    </div>
  )
}
