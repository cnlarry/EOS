import { IconPlus, IconX } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { FILTER_OPERATORS, isTypeCompatible, type FilterRowDraft, type ReturnRowDraft } from './chooserDraft'
import type { ChooserSource, FieldEditorEndpoints } from './FieldEditorForm'

export interface DataSourceDraft {
  source: ChooserSource
  filterRows: FilterRowDraft[]
  returnRows: ReturnRowDraft[]
}

interface TableColumn {
  name: string
  dataType: string
  description: string
}

interface FieldSummaryItem {
  fieldId: string
  description: string
  dataType: string
}

interface FieldPageResult {
  items: FieldSummaryItem[]
}

interface DataSourceEditorModalProps {
  open: boolean
  /** null 表示新增数据源；否则为待编辑草稿（含过滤/回填行）。 */
  initial: DataSourceDraft | null
  /** 当前编辑字段所属表（回填目标字段候选）。 */
  currentTable: string
  endpoints: FieldEditorEndpoints
  onClose: () => void
  onSave: (draft: DataSourceDraft) => void
}

function emptySource(): ChooserSource {
  return { active: true, table: null, description: null, moduleId: null, filter: null, returnMapping: null, serialNo: null }
}

interface FieldPickerOption {
  value: string
  label: string
  dataType?: string
}

/** 轻量字段下拉：主文本（中文(字段名)）+ 类型小号灰色，原生 select 不支持富文本故自定义。 */
function FieldPickerSelect({ options, value, onChange, placeholder, ariaLabel }: {
  options: FieldPickerOption[]
  value: string
  onChange: (value: string) => void
  placeholder: string
  ariaLabel: string
}) {
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement | null>(null)
  const selected = options.find(option => option.value === value)
  const close = () => setOpen(false)

  // 打开时聚焦当前选中项（无选中聚焦第一项），支持方向键在选项间移动
  useEffect(() => {
    if (!open) return
    const options = containerRef.current?.querySelectorAll<HTMLButtonElement>('[role="option"]')
    if (!options || options.length === 0) return
    const currentIndex = Array.from(options).findIndex(option => option.classList.contains('bg-primary-lt'))
    options[Math.max(currentIndex, 0)]?.focus()
  }, [open])

  const moveOptionFocus = (event: React.KeyboardEvent) => {
    const options = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="option"]'))
    const current = options.indexOf(document.activeElement as HTMLButtonElement)
    const next = event.key === 'ArrowDown' ? Math.min(current + 1, options.length - 1) : Math.max(current - 1, 0)
    options[next]?.focus()
  }

  return (
    <div
      className="position-relative flex-grow-1"
      ref={containerRef}
      onBlur={event => {
        const next = event.relatedTarget as Node | null
        if (!next || !event.currentTarget.contains(next)) close()
      }}
    >
      <button
        type="button"
        className="form-select form-select-sm text-start"
        aria-label={ariaLabel}
        aria-expanded={open}
        aria-haspopup="listbox"
        onClick={() => setOpen(current => !current)}
        onKeyDown={event => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            setOpen(true)
          } else if (event.key === 'Escape') {
            close()
          }
        }}
      >
        {selected
          ? <><span className="text-truncate d-inline-block align-middle" style={{ maxWidth: 'calc(100% - 70px)' }}>{selected.label}</span><span className="text-secondary small ms-1">{selected.dataType}</span></>
          : <span className="text-secondary">{placeholder}</span>}
      </button>
      {open && (
        <div
          className="position-absolute top-100 start-0 w-100 border rounded bg-white shadow-sm z-3"
          role="listbox"
          aria-label={ariaLabel}
          style={{ maxHeight: 240, overflowY: 'auto' }}
          onKeyDown={event => {
            if (event.key === 'Escape') {
              event.stopPropagation()
              close()
              containerRef.current?.querySelector<HTMLButtonElement>('button.form-select')?.focus()
            } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
              event.preventDefault()
              moveOptionFocus(event)
            }
          }}
        >
          <button type="button" role="option" className="d-block w-100 text-start px-2 py-1 border-0 bg-transparent text-secondary" onClick={() => { onChange(''); close() }}>{placeholder}</button>
          {options.map(option => (
            <button
              key={option.value}
              type="button"
              role="option"
              className={`erp-picker-option d-flex w-100 align-items-center justify-content-between px-2 py-1 border-0 bg-transparent ${option.value === value ? 'bg-primary-lt' : ''}`}
              onClick={() => { onChange(option.value); close() }}
            >
              <span className="text-truncate">{option.label}</span>
              {option.dataType && <span className="text-secondary small ms-2">{option.dataType}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

/** 来源表物理列（下拉选项）。 */
function useSourceColumns(table: string | null) {
  return useQuery({
    queryKey: ['field-admin', 'columns', table ?? ''],
    queryFn: async () => table
      ? apiClient.get<TableColumn[]>(`/admin/tables/${encodeURIComponent(table)}/columns`)
      : [],
    enabled: Boolean(table),
  })
}

/** 当前表 FIELDS 注册字段（回填目标候选，含类型用于同类型过滤）。 */
function useTargetFields(currentTable: string) {
  return useQuery({
    queryKey: ['field-admin', 'fields', currentTable, 'all'],
    queryFn: async () => {
      const result = await apiClient.get<FieldPageResult>(`/admin/tables/${encodeURIComponent(currentTable)}/fields`, { query: { page: '1', pageSize: '500' } })
      return result.items
    },
    enabled: Boolean(currentTable),
  })
}

/**
 * 数据源编辑弹窗（ADR-008 全页化第 4 点）：来源表/说明/模块 + 过滤构建器 + 回填下拉构建器。
 * 回填来源列与目标字段均用下拉（表内字段数量可控），目标字段按来源列同类型过滤。
 */
export function DataSourceEditorModal({ open, initial, currentTable, endpoints, onClose, onSave }: DataSourceEditorModalProps) {
  const [source, setSource] = useState<ChooserSource>(initial?.source ?? emptySource())
  const [filterRows, setFilterRows] = useState<FilterRowDraft[]>(initial?.filterRows ?? [])
  const [returnRows, setReturnRows] = useState<ReturnRowDraft[]>(initial?.returnRows ?? [])
  const [tablePickerOpen, setTablePickerOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const columnsQuery = useSourceColumns(source.table)
  const targetFieldsQuery = useTargetFields(currentTable)
  const modulesQuery = useQuery({
    queryKey: ['field-editor', 'modules', currentTable],
    queryFn: endpoints.modules ?? (async () => []),
    enabled: Boolean(endpoints.modules),
  })

  // 弹窗每次打开时以 initial 重建草稿
  const [lastOpen, setLastOpen] = useState<boolean | null>(null)
  if (open !== lastOpen) {
    setLastOpen(open)
    if (open) {
      setSource(initial?.source ?? emptySource())
      setFilterRows(initial?.filterRows ?? [])
      setReturnRows(initial?.returnRows ?? [])
      setError(null)
    }
  }

  const sourceColumns = columnsQuery.data ?? []
  const targetFields = targetFieldsQuery.data ?? []
  const sourceColumnOptions: FieldPickerOption[] = sourceColumns.map(column => ({
    value: column.name,
    label: `${column.description || column.name}(${column.name})`,
    dataType: column.dataType,
  }))

  const updateFilterRows = (rows: FilterRowDraft[]) => setFilterRows(rows)
  const updateReturnRows = (rows: ReturnRowDraft[]) => setReturnRows(rows)

  const handleSave = () => {
    // 确定即校验：坏 JSON / 未完成行若静默交给序列化会被丢弃，必须在弹窗内给出反馈
    if (filterRows.some(row => {
      if (!row.raw) return false
      try {
        JSON.parse(row.raw)
        return false
      } catch {
        return true
      }
    })) {
      setError('高级条件行 JSON 无法解析，请修正或删除该行后再确定。')
      return
    }
    if (filterRows.some(row => !row.raw && !row.field.trim())) {
      setError('存在未选择来源列的过滤条件行，请补全或删除。')
      return
    }
    if (returnRows.some(row => !row.column.trim() || !row.target.trim())) {
      setError('存在未配对的回填映射行（来源列与目标字段都需选择），请补全或删除。')
      return
    }
    onSave({
      source,
      filterRows,
      returnRows,
    })
    onClose()
  }

  return open ? (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-xl modal-dialog-centered erp-field-settings-dialog">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{initial ? `编辑数据源：${source.description || source.table || '未命名数据源'}` : '新增数据源'}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            <div className="row g-3">
              <div className="col-md-4">
                <label className="form-label">来源表</label>
                <div className="d-flex gap-1">
                  <input className="form-control" readOnly value={source.table ?? ''} placeholder="点击选择" />
                  <Button size="sm" variant="secondary" onClick={() => setTablePickerOpen(true)}>选择</Button>
                </div>
              </div>
              <div className="col-md-4">
                <label className="form-label">来源说明（选择器菜单显示名）</label>
                <input className="form-control" value={source.description ?? ''} onChange={event => setSource({ ...source, description: event.target.value })} />
              </div>
              <div className="col-md-4">
                <label className="form-label">来源模块（权限/数据范围）</label>
                <select className="form-select" value={source.moduleId ?? ''} onChange={event => setSource({ ...source, moduleId: event.target.value === '' ? null : Number(event.target.value) })}>
                  <option value="">继承当前模块</option>
                  {modulesQuery.data?.map(item => <option key={item.value} value={item.value}>{item.label} ({item.value})</option>)}
                </select>
              </div>
              <div className="col-12">
                <label className="form-label">过滤条件</label>
                <div className="border rounded p-2 d-flex flex-column gap-1">
                  {columnsQuery.isError && <div className="alert alert-danger py-1 px-2 mb-0 small">来源列加载失败，请确认 API 已重启（/admin/tables/{source.table}/columns）。</div>}
                  {filterRows.length === 0 && <div className="text-secondary small">无过滤条件（空=显式无过滤；迁移待重建来源请先重建再保存）。</div>}
                  {filterRows.map(row => row.raw ? (
                    <div key={row.key} className="d-flex gap-1 align-items-start">
                      <div className="flex-grow-1">
                        <div className="text-secondary small">高级条件（表达式/子查询，JSON 只读兜底）</div>
                        <pre className="small mb-0 text-break" style={{ whiteSpace: 'pre-wrap' }}>{row.raw}</pre>
                      </div>
                      <Button size="sm" variant="danger" onClick={() => updateFilterRows(filterRows.filter(r => r.key !== row.key))}>删除</Button>
                    </div>
                  ) : (
                    <div key={row.key} className="d-flex gap-1 align-items-center">
                      <select className="form-select form-select-sm w-auto" value={row.logic ?? 'AND'} disabled={row.logic == null} onChange={event => updateFilterRows(filterRows.map(r => r.key === row.key ? { ...r, logic: event.target.value as 'AND' | 'OR' } : r))}>
                        <option value="AND">且</option>
                        <option value="OR">或</option>
                      </select>
                      <FieldPickerSelect
                        options={sourceColumnOptions.map(option => ({ ...option, value: `${source.table}.${option.value}` }))}
                        value={row.field}
                        onChange={field => updateFilterRows(filterRows.map(r => r.key === row.key ? { ...r, field } : r))}
                        placeholder="选择来源列"
                        ariaLabel="过滤条件字段"
                      />
                      <select className="form-select form-select-sm w-auto" value={row.operator} onChange={event => updateFilterRows(filterRows.map(r => r.key === row.key ? { ...r, operator: event.target.value } : r))}>
                        {FILTER_OPERATORS.map(op => <option key={op} value={op}>{op}</option>)}
                      </select>
                      <input className="form-control form-control-sm" value={row.value} placeholder="值（支持 {m.X}/{d.X}/{module}）" onChange={event => updateFilterRows(filterRows.map(r => r.key === row.key ? { ...r, value: event.target.value } : r))} />
                      <Button size="sm" variant="danger" onClick={() => updateFilterRows(filterRows.filter(r => r.key !== row.key))}>删除</Button>
                    </div>
                  ))}
                  <div>
                    <Button size="sm" variant="secondary" onClick={() => updateFilterRows([...filterRows, { key: `r${Date.now()}`, field: '', operator: 'EQ', value: '', logic: filterRows.length === 0 ? null : (filterRows.find(r => r.logic)?.logic ?? 'AND') }])}>+ 条件</Button>
                  </div>
                </div>
              </div>
              <div className="col-12">
                <label className="form-label">回填映射（来源列 → 目标字段，目标仅显示同类型字段）</label>
                <div className="border rounded p-2 d-flex flex-column gap-1">
                  {columnsQuery.isError && <div className="alert alert-danger py-1 px-2 mb-0 small">来源列加载失败，请确认 API 已重启（/admin/tables/{source.table}/columns）。</div>}
                  {returnRows.length === 0 && <div className="text-secondary small">无回填映射（选择数据后不自动回填）。</div>}
                  {returnRows.map(row => {
                    const sourceColumn = sourceColumns.find(column => column.name.toLowerCase() === row.column.toLowerCase())
                    const compatibleTargets = sourceColumn
                      ? targetFields.filter(field => isTypeCompatible(sourceColumn.dataType, field.dataType))
                      : targetFields
                    return (
                      <div key={row.key} className="d-flex gap-1 align-items-center">
                        <FieldPickerSelect
                          options={sourceColumnOptions}
                          value={row.column}
                          onChange={columnName => {
                            const column = sourceColumns.find(c => c.name === columnName)
                            const compatible = column ? targetFields.filter(field => isTypeCompatible(column.dataType, field.dataType)) : []
                            const target = compatible.some(field => field.fieldId === row.target) ? row.target : (compatible[0]?.fieldId ?? '')
                            updateReturnRows(returnRows.map(r => r.key === row.key ? { ...r, column: columnName, target } : r))
                          }}
                          placeholder="选择来源列"
                          ariaLabel="回填来源列"
                        />
                        <span className="text-secondary">→</span>
                        <FieldPickerSelect
                          options={compatibleTargets.map(field => ({
                            value: field.fieldId,
                            label: `${field.description || field.fieldId}(${field.fieldId})`,
                            dataType: field.dataType,
                          }))}
                          value={row.target}
                          onChange={target => updateReturnRows(returnRows.map(r => r.key === row.key ? { ...r, target } : r))}
                          placeholder="选择目标字段"
                          ariaLabel="回填目标字段"
                        />
                        <Button size="sm" variant="danger" onClick={() => updateReturnRows(returnRows.filter(r => r.key !== row.key))}>删除</Button>
                      </div>
                    )
                  })}
                  <div>
                    <Button size="sm" variant="secondary" icon={<IconPlus size={14} />} onClick={() => updateReturnRows([...returnRows, { key: `m${Date.now()}`, column: '', target: '' }])}>+ 回填项</Button>
                  </div>
                </div>
              </div>
              {error && <div className="alert alert-danger py-2 px-3 small mb-0" role="alert">{error}</div>}
            </div>
          </div>
          <div className="modal-footer">
            <Button variant="secondary" icon={<IconX size={16} />} onClick={onClose}>取消</Button>
            <Button variant="primary" onClick={handleSave}>确定</Button>
          </div>
        </div>
      </div>
      {tablePickerOpen && (
        <UnifiedChooser
          open
          title="选择数据来源表"
          source={{ kind: 'sourceKey', key: 'field-admin.tables' }}
          mode="single"
          onPick={rows => {
            const row = rows[0]
            setSource(prev => ({
              ...prev,
              table: String(row.T_ID ?? ''),
              description: prev.description || (row.T_DESC ? String(row.T_DESC) : null) || null,
            }))
            setTablePickerOpen(false)
          }}
          onClose={() => setTablePickerOpen(false)}
        />
      )}
    </div>
  ) : null
}
