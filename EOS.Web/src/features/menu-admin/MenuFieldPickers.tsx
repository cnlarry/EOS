import { IconArrowDown, IconArrowUp, IconPlus, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { useEffect, useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { FILTER_OPS, parseFilter, quoteValue, rowIssues, type FilterRow } from './menuFilter'

export interface MenuFieldOption {
  F_ID: string
  F_DESC: string
  F_TYPE: string
  IS_VISIBLE: boolean
  IS_VIRTUAL: boolean
  IS_QUERY: boolean
}

type SortDirection = 'asc' | 'desc'

interface SelectedField {
  field: string
  dir: SortDirection
}

function useTableFields(table: string | null, enabled: boolean) {
  return useQuery({
    queryKey: ['menu-admin', 'fields', table],
    queryFn: async () => {
      try {
        return await apiClient.get<MenuFieldOption[]>(`/admin/menus/fields?table=${encodeURIComponent(table ?? '')}`)
      } catch (error) {
        console.error(`[菜单管理] 字段列表加载失败（表 ${table ?? ''}），完整错误：`, error)
        throw error
      }
    },
    enabled: enabled && Boolean(table),
  })
}

function describeError(error: unknown, fallback: string): string {
  if (error instanceof ApiError) return `${error.body.message}（HTTP ${error.status}）`
  if (error && typeof error === 'object') {
    const candidate = error as { status?: unknown; body?: { message?: string } }
    if (typeof candidate.status === 'number' && candidate.body) {
      return `${candidate.body.message ?? '请求失败'}（HTTP ${candidate.status}）`
    }
  }
  if (error instanceof Error) return `${error.name}: ${error.message}`
  return fallback
}

function parseValue(value: string, mode: 'multi' | 'sort'): SelectedField[] {
  const bare = (token: string) => token.replace(/^\[|\]$/g, '').split('.').pop() ?? ''
  if (mode === 'sort') {
    return value
      .split(',')
      .map((part) => {
        const tokens = part.trim().split(/\s+/)
        const field = bare(tokens[0] ?? '')
        return field ? { field, dir: tokens[1]?.toLowerCase() === 'desc' ? 'desc' as const : 'asc' as const } : null
      })
      .filter((item): item is SelectedField => item !== null)
  }
  return value
    .split(';')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((field) => ({ field: bare(field), dir: 'asc' as const }))
}

/**
 * 字段选择器（统一电子表格风格，与统一表单选择器一致）：
 * - 统一选择器（menu-admin.fields 数据源）列出字段（字段名/描述/类型），勾选或点行选择；
 *   排序模式每行可切换升/降序；
 * - 下方「已选顺序」条展示并支持上移/下移/移除；
 * - mode='multi' 保存为分号分隔；mode='sort' 保存为 "FIELD ASC|DESC" 逗号分隔。
 */
export function MenuFieldPicker({
  open,
  title,
  table,
  mode,
  value,
  onSave,
  onClose,
}: {
  open: boolean
  title: string
  table: string | null
  mode: 'multi' | 'sort'
  value: string
  onSave: (value: string) => void
  onClose: () => void
}) {
  const fields = useTableFields(table, open)
  const [selected, setSelected] = useState<SelectedField[]>([])

  useEffect(() => {
    if (open) setSelected(parseValue(value, mode))
  }, [open, value, mode])

  const fieldOptions = useMemo(
    () => (fields.data ?? []).filter((field) => !field.IS_VIRTUAL),
    [fields.data],
  )
  const byId = useMemo(() => new Map(fieldOptions.map((field) => [field.F_ID, field])), [fieldOptions])
  const rowSelection = useMemo(
    () => Object.fromEntries(selected.map((item) => [item.field, true])),
    [selected],
  )

  const reconcileSelection = (next: Record<string, boolean>) => {
    setSelected((current) => {
      const currentKeys = new Set(current.map((item) => item.field))
      const nextKeys = new Set(Object.keys(next).filter((key) => next[key]))
      const removed = [...currentKeys].filter((key) => !nextKeys.has(key))
      const added = [...nextKeys].filter((key) => !currentKeys.has(key))
      if (removed.length === 0 && added.length === 0) return current
      let result = current.filter((item) => !removed.includes(item.field))
      for (const field of added) result = [...result, { field, dir: 'asc' as const }]
      return result
    })
  }

  const toggle = (fieldId: string) => {
    setSelected((current) => (current.some((item) => item.field === fieldId)
      ? current.filter((item) => item.field !== fieldId)
      : [...current, { field: fieldId, dir: 'asc' }]))
  }

  const toggleDir = (fieldId: string) => {
    setSelected((current) => current.map((item) => (item.field === fieldId ? { ...item, dir: item.dir === 'asc' ? 'desc' : 'asc' } : item)))
  }

  const move = (fieldId: string, delta: -1 | 1) => {
    setSelected((current) => {
      const index = current.findIndex((item) => item.field === fieldId)
      const target = index + delta
      if (index < 0 || target < 0 || target >= current.length) return current
      const next = [...current]
      ;[next[index], next[target]] = [next[target], next[index]]
      return next
    })
  }

  const handleSave = () => {
    if (mode === 'sort') {
      onSave(selected.map((item) => `${item.field} ${item.dir.toUpperCase()}`).join(','))
    } else {
      onSave(selected.map((item) => item.field).join(';'))
    }
    onClose()
  }

  const extraColumns: ColumnDef<UnifiedChooserRow, unknown>[] = mode === 'sort' ? [{
      id: 'dir',
      accessorKey: 'F_ID',
      enableSorting: false,
      header: '方向',
      meta: { maxWidth: 320 },
      cell: ({ row }) => {
        const fieldId = String(row.original.F_ID)
        const item = selected.find((s) => s.field === fieldId)
        return item ? (
          <button type="button" className="erp-field-dir" onClick={(event) => { event.stopPropagation(); toggleDir(fieldId) }}>
            {item.dir === 'asc' ? '升序' : '降序'}
          </button>
        ) : null
      },
    }] : []

  if (!open) return null

  return (
    <UnifiedChooser
      open={open}
      title={title}
      source={{ kind: 'sourceKey', key: 'menu-admin.fields', args: table ? { tableId: table } : undefined }}
      getRowId={(row) => String(row.F_ID)}
      mode="multi"
      dialogSize="md"
      selectedKeys={rowSelection}
      onSelectedKeysChange={reconcileSelection}
      onPick={() => handleSave()}
      onClose={onClose}
      emptyText="该表没有可用字段。"
      extraColumns={extraColumns}
      extra={
        <div className="mt-2">
          <label className="form-label">已选顺序</label>
          <div className="erp-field-picker-list">
            {selected.map((item, index) => {
              const field = byId.get(item.field)
              return (
                <div key={item.field} className="erp-field-picker-row">
                  <span className="erp-field-picker-order">{index + 1}</span>
                  <span className="erp-field-picker-desc">{field?.F_DESC ?? item.field}</span>
                  <span className="erp-menu-table-id">{item.field}</span>
                  <span className="ms-auto d-flex align-items-center gap-1">
                    {mode === 'sort' && (
                      <button type="button" className="erp-field-dir" onClick={() => toggleDir(item.field)}>
                        {item.dir === 'asc' ? '升序' : '降序'}
                      </button>
                    )}
                    <button type="button" className="erp-field-mini" aria-label="上移" disabled={index === 0} onClick={() => move(item.field, -1)}>
                      <IconArrowUp size={14} />
                    </button>
                    <button type="button" className="erp-field-mini" aria-label="下移" disabled={index === selected.length - 1} onClick={() => move(item.field, 1)}>
                      <IconArrowDown size={14} />
                    </button>
                    <button type="button" className="erp-field-mini" aria-label="移除" onClick={() => toggle(item.field)}>
                      <IconTrash size={14} />
                    </button>
                  </span>
                </div>
              )
            })}
            {selected.length === 0 && <div className="text-secondary small p-2">尚未选择字段。</div>}
          </div>
        </div>
      }
    />
  )
}

/**
 * 主表过滤条件构建器：字段 + 运算符 + 值 组成条件行，输出与 DataFilterParser
 * 兼容的谓词（如 `(PRO_TYPE=1) AND (PRO_NAME='a')`）。
 * 保存前对每行做基本校验（字段必选、值必填、类型提示），并防止清空
 * 构建器无法表达的既有复杂条件（如 ISNULL、getdate()、列运算）。
 */
export function MenuFilterBuilder({
  open,
  table,
  value,
  onSave,
  onClose,
}: {
  open: boolean
  table: string | null
  value: string
  onSave: (value: string) => void
  onClose: () => void
}) {
  const fields = useTableFields(table, open)
  const [rows, setRows] = useState<FilterRow[]>([])
  const [unparseableOriginal, setUnparseableOriginal] = useState(false)

  useEffect(() => {
    if (!open) return
    const parsed = parseFilter(value)
    setRows(parsed ?? [])
    setUnparseableOriginal(parsed == null && value.trim().length > 0)
  }, [open, value])

  const fieldOptions = useMemo(
    () => (fields.data ?? []).filter((field) => !field.IS_VIRTUAL),
    [fields.data],
  )
  const fieldById = useMemo(() => new Map(fieldOptions.map((field) => [field.F_ID, field])), [fieldOptions])
  const issuesByRow = useMemo(
    () => rows.map((row) => rowIssues(row, fieldById.get(row.field))),
    [rows, fieldById],
  )
  const errorCount = issuesByRow.reduce(
    (count, issues) => count + issues.filter((issue) => issue.kind === 'error').length,
    0,
  )
  const warningCount = issuesByRow.reduce(
    (count, issues) => count + issues.filter((issue) => issue.kind === 'warning').length,
    0,
  )

  const updateRow = (index: number, patch: Partial<FilterRow>) => {
    setRows((current) => current.map((row, i) => (i === index ? { ...row, ...patch } : row)))
  }

  const handleSave = () => {
    if (errorCount > 0) return
    if (unparseableOriginal && rows.length === 0) {
      // 既有条件无法在构建器中表达且用户未添加新条件：保留原值，避免静默清空
      onSave(value)
      onClose()
      return
    }
    const parts = rows.map((row, i) => `${i === 0 ? '' : `${row.logic} `}(${row.field} ${row.op} ${quoteValue(row.value)})`.trim())
    onSave(parts.join(' '))
    onClose()
  }

  if (!open) return null

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered erp-dialog-md">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">构建主表过滤条件</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {fields.isPending ? (
              <LoadingState label="正在加载字段…" />
            ) : fields.isError ? (
              <ErrorState
                message={describeError(fields.error, '加载字段失败')}
                onRetry={() => void fields.refetch()}
              />
            ) : (
              <div className="erp-filter-rows">
                {unparseableOriginal && (
                  <div className="alert alert-warning py-2 px-3 small mb-2">
                    现有过滤条件包含构建器不支持的写法（如 ISNULL、getdate()、列运算等），已保留原值。
                    添加新条件并保存后，将以新条件覆盖原值。
                  </div>
                )}
                <div className="text-secondary small mb-2">
                  每行 = 字段 + 运算符 + 比较值；多行以 AND/OR 连接，保存为括号谓词。
                </div>
                {rows.map((row, index) => (
                  <div key={index} className="erp-filter-row">
                    <div className="erp-filter-row-line">
                      {index > 0 && (
                        <select
                          className="form-select form-select-sm erp-filter-logic"
                          value={row.logic}
                          onChange={(event) => updateRow(index, { logic: event.target.value as 'AND' | 'OR' })}
                        >
                          <option value="AND">AND</option>
                          <option value="OR">OR</option>
                        </select>
                      )}
                      <select
                        className={`form-select form-select-sm${issuesByRow[index].some((issue) => issue.kind === 'error') ? ' is-invalid' : ''}`}
                        value={row.field}
                        onChange={(event) => updateRow(index, { field: event.target.value })}
                      >
                        <option value="">选择字段…</option>
                        {fieldOptions.map((field) => (
                          <option key={field.F_ID} value={field.F_ID}>{field.F_DESC}（{field.F_ID}）</option>
                        ))}
                      </select>
                      <select
                        className="form-select form-select-sm erp-filter-op"
                        value={row.op}
                        onChange={(event) => updateRow(index, { op: event.target.value })}
                      >
                        {FILTER_OPS.map((op) => <option key={op} value={op}>{op}</option>)}
                      </select>
                      <input
                        className={`form-control form-control-sm${issuesByRow[index].some((issue) => issue.kind === 'error') ? ' is-invalid' : ''}`}
                        value={row.value}
                        placeholder="值"
                        onChange={(event) => updateRow(index, { value: event.target.value })}
                      />
                      <button type="button" className="erp-field-mini" aria-label="删除条件" onClick={() => setRows((current) => current.filter((_, i) => i !== index))}>
                        <IconTrash size={14} />
                      </button>
                    </div>
                    {issuesByRow[index].length > 0 && (
                      <div className="erp-filter-row-issues">
                        {issuesByRow[index].map((issue, issueIndex) => (
                          <div key={issueIndex} className={issue.kind === 'error' ? 'text-danger' : 'text-warning'}>
                            {issue.message}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                ))}
                <Button size="sm" icon={<IconPlus size={14} />} onClick={() => setRows((current) => [...current, { field: '', op: '=', logic: 'AND', value: '' }])}>
                  添加条件
                </Button>
                {rows.length === 0 && <div className="text-secondary small mt-1">当前无条件（保存将清空过滤条件）。</div>}
                {errorCount > 0 && (
                  <div className="text-danger small mt-1">还有 {errorCount} 处条件不完整，修正后才能保存。</div>
                )}
                {errorCount === 0 && warningCount > 0 && (
                  <div className="text-warning small mt-1">有 {warningCount} 处提示（不影响保存）。</div>
                )}
              </div>
            )}
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            <Button variant="primary" disabled={errorCount > 0} onClick={handleSave}>确定</Button>
          </div>
        </div>
      </div>
    </div>
  )
}
