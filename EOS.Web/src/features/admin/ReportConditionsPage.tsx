import { IconEdit, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { ErrorState, EmptyState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

interface ModuleOption { moduleId: number; description: string }
interface ConditionRow {
  serialNo: number; type: number; field: string | null; expression: string | null;
  description: string | null; defaultValue: string | null; parameterName: string | null; remark: string | null
}

/** 条件类型（旧 RptList2 语义）；类型 5 现代报表查看器暂不支持渲染，维护数据时明确提示。 */
const CONDITION_TYPES = [
  { value: 1, label: '范围' },
  { value: 2, label: '固定单选' },
  { value: 3, label: '从数据表单选' },
  { value: 4, label: '固定多选' },
  { value: 5, label: '从数据表多选（查看器暂不支持）' },
]

const typeLabel = (type: number) => CONDITION_TYPES.find((item) => item.value === type)?.label ?? String(type)

interface OptionPair { label: string; value: string }

type ConditionEditorState = { mode: 'new' } | { mode: 'edit'; row: ConditionRow }

function selectColumn<T>(): ColumnDef<T, unknown> {
  return {
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
        ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
        onChange={table.getToggleAllPageRowsSelectedHandler()}
      />
    ),
    cell: ({ row }) => (
      <input
        className="form-check-input"
        type="checkbox"
        aria-label="选择此行"
        checked={row.getIsSelected()}
        onChange={row.getToggleSelectedHandler()}
        onClick={(event) => event.stopPropagation()}
      />
    ),
  }
}

/** 把「标签:值;标签:值」DSL 宽松解析为可编辑行（缺冒号的残缺段丢弃，保存时按严格规则重建）。 */
function parseOptionPairs(expression: string | null): OptionPair[] {
  if (!expression) return []
  return expression
    .split(';')
    .map((segment) => {
      const separator = segment.indexOf(':')
      if (separator <= 0) return null
      return { label: segment.slice(0, separator).trim(), value: segment.slice(separator + 1).trim() }
    })
    .filter((item): item is OptionPair => item !== null && item.label.length > 0)
}

interface ConditionEditorModalProps {
  open: boolean
  mode: 'new' | 'edit'
  row?: ConditionRow
  moduleId: number
  nextSerial: number
  onClose: () => void
  onSaved: () => void
}

/** 过滤条件行（SYSQR_DEFAULT）新增/编辑弹窗：按条件类型给出类型化编辑器。 */
function ConditionEditorModal({ open, mode, row, moduleId, nextSerial, onClose, onSaved }: ConditionEditorModalProps) {
  const [serialNo, setSerialNo] = useState('')
  const [type, setType] = useState(1)
  const [field, setField] = useState('')
  const [description, setDescription] = useState('')
  const [optionPairs, setOptionPairs] = useState<OptionPair[]>([])
  const [expressionText, setExpressionText] = useState('')
  const [defaultValue, setDefaultValue] = useState('')
  const [parameterName, setParameterName] = useState('')
  const [remark, setRemark] = useState('')
  const [fieldChooserOpen, setFieldChooserOpen] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    const nextType = row?.type ?? 1
    setSerialNo(mode === 'edit' ? String(row?.serialNo ?? '') : String(nextSerial))
    setType(nextType)
    setField(row?.field ?? '')
    setDescription(row?.description ?? '')
    setOptionPairs(parseOptionPairs(row?.expression ?? null))
    setExpressionText(nextType === 3 || nextType === 5 ? row?.expression ?? '' : '')
    setDefaultValue(row?.defaultValue ?? '')
    setParameterName(row?.parameterName ?? '')
    setRemark(row?.remark ?? '')
    setError(null)
    setFieldChooserOpen(false)
  }, [open, mode, row, nextSerial])

  if (!open) return null

  const buildExpression = (): string => {
    if (type === 1) return ''
    if (type === 2 || type === 4) {
      return optionPairs
        .filter((pair) => pair.label.trim().length > 0)
        .map((pair) => `${pair.label.trim()}:${pair.value.trim()}`)
        .join(';')
    }
    return expressionText
  }

  const submit = async () => {
    const serial = Number(serialNo)
    if (!Number.isInteger(serial) || serial < 1) { setError('条件序号无效（正整数）。'); return }
    if (!field.trim()) { setError('请选择查询字段。'); return }
    if ((type === 2 || type === 4) && !optionPairs.some((pair) => pair.label.trim().length > 0)) {
      setError('固定单选/多选至少需要一个选项（标签:值）。'); return
    }
    if ((type === 3 || type === 5) && !expressionText.trim()) {
      setError('从数据表单选/多选需要提供数据源语句。'); return
    }
    setSaving(true)
    setError(null)
    try {
      const body = {
        serialNo: serial, type, field: field.trim(), expression: buildExpression(),
        description: String(description ?? ''), defaultValue: String(defaultValue ?? ''),
        parameterName: String(parameterName ?? ''), remark: String(remark ?? ''),
      }
      if (mode === 'edit') {
        await apiClient.put(`/report-conditions/conditions/${row?.serialNo}?moduleId=${moduleId}`, body)
      } else {
        await apiClient.post(`/report-conditions/conditions?moduleId=${moduleId}`, body)
      }
      onSaved()
    } catch (reason) {
      setError(reason instanceof ApiError ? reason.body.message : '保存失败，请稍后重试。')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered modal-lg">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{mode === 'new' ? `新增过滤条件：模块 ${moduleId}` : `编辑过滤条件：模块 ${moduleId}`}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {error && <div className="alert alert-danger py-2 mb-3" role="alert">{error}</div>}
            <div className="row g-3">
              <div className="col-md-3">
                <label className="form-label" htmlFor="condition-serial">序号</label>
                <input id="condition-serial" className="form-control" type="number" value={serialNo} disabled={mode === 'edit'} onChange={(event) => setSerialNo(event.target.value)} />
                {mode === 'edit' && <div className="form-hint">序号不可修改</div>}
              </div>
              <div className="col-md-3">
                <label className="form-label" htmlFor="condition-type">条件类型</label>
                <select id="condition-type" className="form-select" value={type} onChange={(event) => setType(Number(event.target.value))}>
                  {CONDITION_TYPES.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
                </select>
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="condition-field">查询字段（主表.列）</label>
                <div className="d-flex gap-1">
                  <input id="condition-field" className="form-control font-monospace" readOnly value={field} />
                  <Button size="sm" onClick={() => setFieldChooserOpen(true)}>选择字段…</Button>
                </div>
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="condition-desc">条件描述（报表用户所见标签）</label>
                <input id="condition-desc" className="form-control" value={description} onChange={(event) => setDescription(event.target.value)} />
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="condition-default">默认查询值{type === 1 ? '（范围条件仅起值）' : ''}</label>
                <input id="condition-default" className="form-control" value={defaultValue} onChange={(event) => setDefaultValue(event.target.value)} />
              </div>
              <div className="col-md-6">
                <label className="form-label" htmlFor="condition-para">参数名称</label>
                <input id="condition-para" className="form-control" value={parameterName} onChange={(event) => setParameterName(event.target.value)} />
              </div>
              {type === 1 && (
                <div className="col-md-12">
                  <div className="form-hint">范围条件不使用条件语句（运行时按「起值☆止值」渲染两个输入框）。</div>
                </div>
              )}
              {(type === 2 || type === 4) && (
                <div className="col-md-12">
                  <label className="form-label">选项（标签:值，保存为分号分隔 DSL）</label>
                  {optionPairs.map((pair, index) => (
                    <div className="d-flex gap-1 mb-1" key={`option-${index}`}>
                      <input
                        className="form-control"
                        placeholder="标签"
                        aria-label={`选项${index + 1}标签`}
                        value={pair.label}
                        onChange={(event) => setOptionPairs((current) => current.map((item, i) => (i === index ? { ...item, label: event.target.value } : item)))}
                      />
                      <input
                        className="form-control"
                        placeholder="值"
                        aria-label={`选项${index + 1}值`}
                        value={pair.value}
                        onChange={(event) => setOptionPairs((current) => current.map((item, i) => (i === index ? { ...item, value: event.target.value } : item)))}
                      />
                      <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} onClick={() => setOptionPairs((current) => current.filter((_, i) => i !== index))}>移除</Button>
                    </div>
                  ))}
                  <Button size="sm" onClick={() => setOptionPairs((current) => [...current, { label: '', value: '' }])}>添加选项</Button>
                </div>
              )}
              {(type === 3 || type === 5) && (
                <div className="col-md-12">
                  <label className="form-label" htmlFor="condition-expression">数据源语句</label>
                  <input id="condition-expression" className="form-control font-monospace" placeholder="SELECT 列 C_ID, 列 C_VALUE FROM 表" value={expressionText} onChange={(event) => setExpressionText(event.target.value)} />
                  <div className="form-hint">格式固定：SELECT 列 C_ID, 列 C_VALUE FROM 表；保存时校验表/列物理存在。</div>
                </div>
              )}
              <div className="col-md-12">
                <label className="form-label" htmlFor="condition-remark">备注</label>
                <input id="condition-remark" className="form-control" value={remark} onChange={(event) => setRemark(event.target.value)} />
              </div>
            </div>
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            <Button variant="primary" onClick={() => void submit()} loading={saving}>{mode === 'edit' ? '保存修改' : '新增条件'}</Button>
          </div>
        </div>
      </div>
      {fieldChooserOpen && (
        <UnifiedChooser
          open
          title="选择查询字段"
          source={{ kind: 'sourceKey', key: 'report-conditions.fields', args: { moduleId: String(moduleId) } }}
          mode="single"
          getRowId={(chooserRow) => `${String(chooserRow.T_ID)}.${String(chooserRow.F_ID)}`}
          onPick={(rows) => {
            const picked = rows[0] as { T_ID?: unknown; F_ID?: unknown } | undefined
            if (picked) setField(`${String(picked.T_ID)}.${String(picked.F_ID)}`)
            setFieldChooserOpen(false)
          }}
          onClose={() => setFieldChooserOpen(false)}
          dialogSize="md"
          emptyText="该模块主表没有可用字段。"
        />
      )}
    </div>
  )
}

/**
 * 报表过滤条件设置（2205 定制页，2026-08-28 自统一表单白名单归类）：
 * SYSQR_DA + SYSQR_DEFAULT 主子表——上方模块列表、下方该模块的条件行；
 * 条件编辑按 F_TYPE 类型化（范围/固定单选/固定多选选项 DSL、数据表单选/多选数据源语句），
 * 与报表运行时解析规则镜像，坏配置保存时拦截。
 */
export function ReportConditionsPage() {
  const [keyword, setKeyword] = useState('')
  const [moduleSelection, setModuleSelection] = useState<RowSelectionState>({})
  const [conditionSelection, setConditionSelection] = useState<RowSelectionState>({})
  const [selectedModule, setSelectedModule] = useState(0)
  const [editor, setEditor] = useState<ConditionEditorState | null>(null)

  const modules = useQuery({ queryKey: ['report-conditions', 'modules'], queryFn: () => apiClient.get<ModuleOption[]>('/report-conditions/modules') })
  const conditions = useQuery({
    queryKey: ['report-conditions', 'conditions', selectedModule],
    queryFn: () => apiClient.get<ConditionRow[]>(`/report-conditions/conditions?moduleId=${selectedModule}`),
    enabled: selectedModule > 0,
  })

  const moduleRows = modules.data ?? []
  const conditionRows = conditions.data ?? []

  const filteredModules = useMemo(() => {
    const all = modules.data ?? []
    const text = keyword.trim().toLowerCase()
    if (!text) return all
    return all.filter((row) =>
      String(row.moduleId).includes(text)
      || row.description.toLowerCase().includes(text))
  }, [modules.data, keyword])

  // 数据加载后无有效选中时默认选中第一条（刷新/过滤变化时同样自动回落）
  useEffect(() => {
    if (!modules.data) return
    setModuleSelection((current) => {
      const valid = Object.keys(current).filter((key) => current[key] && modules.data!.some((item) => String(item.moduleId) === key))
      if (valid.length > 0) return current
      const first = modules.data![0]?.moduleId ?? 0
      setSelectedModule(first)
      setConditionSelection({})
      setEditor(null)
      return first ? { [first]: true } : {}
    })
  }, [modules.data])

  const deleteCondition = useCallback(async (row: ConditionRow) => {
    if (!window.confirm(`确定删除条件「${row.description ?? row.field ?? row.serialNo}」？保存过该模块条件值的用户记忆将一并清除。`)) return
    try {
      await apiClient.delete(`/report-conditions/conditions/${row.serialNo}?moduleId=${selectedModule}`)
      await conditions.refetch()
    } catch { /* 删除失败静默：保持列表现场，错误由下次刷新体现 */ }
  }, [selectedModule, conditions])

  const handleModuleSelectionChange = (next: RowSelectionState) => {
    setModuleSelection(next)
    const keys = Object.keys(next).filter((key) => next[key])
    const nextModule = Number(keys[0] ?? 0)
    if (nextModule !== selectedModule) {
      setSelectedModule(nextModule)
      setConditionSelection({})
      setEditor(null)
    }
  }

  const moduleColumns = useMemo<ColumnDef<ModuleOption, unknown>[]>(() => [
    selectColumn<ModuleOption>(),
    { accessorKey: 'moduleId', header: '模块编号', cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue() ?? '')}</span> },
    { accessorKey: 'description', header: '模块名称', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
  ], [])

  const conditionColumns = useMemo<ColumnDef<ConditionRow, unknown>[]>(() => [
    selectColumn<ConditionRow>(),
    { accessorKey: 'serialNo', header: '序号' },
    { accessorKey: 'description', header: '条件描述', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'field', header: '查询字段', cell: (info) => <span className="font-monospace">{info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())}</span> },
    { accessorKey: 'type', header: '条件类型', cell: (info) => typeLabel(Number(info.getValue())) },
    { accessorKey: 'expression', header: '条件语句/选项', cell: (info) => <span className="font-monospace">{info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())}</span> },
    { accessorKey: 'defaultValue', header: '默认值', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'parameterName', header: '参数名', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    { accessorKey: 'remark', header: '备注', cell: (info) => (info.getValue() == null || info.getValue() === '' ? '—' : String(info.getValue())) },
    {
      id: 'actions',
      header: '操作',
      enableSorting: false,
      meta: { className: 'text-end text-nowrap', frozenRight: true, truncate: false, minWidth: 132, minWidthFloor: true, resizable: false },
      cell: ({ row }) => (
        <div className="d-inline-flex gap-1">
          <Button size="sm" variant="ghost" icon={<IconEdit size={14} />} onClick={() => setEditor({ mode: 'edit', row: row.original })}>编辑</Button>
          <Button size="sm" variant="ghost" icon={<IconTrash size={14} />} onClick={() => void deleteCondition(row.original)}>删除</Button>
        </div>
      ),
    },
  ], [deleteCondition])

  if (modules.isPending) return <LoadingState label="正在加载模块…" />
  if (modules.isError) return <ErrorState message={modules.error instanceof ApiError ? modules.error.body.message : '加载失败。'} onRetry={() => void modules.refetch()} />

  const selectedDescription = moduleRows.find((item) => item.moduleId === selectedModule)?.description
  const conditionsError = conditions.error instanceof ApiError ? conditions.error.body.message : '加载失败。'
  const nextSerial = conditionRows.reduce((max, row) => Math.max(max, row.serialNo), 0) + 1

  return (
    <div className="erp-workbench-page">
      <ErpListCard
        ariaLabel="报表过滤条件模块查询"
        search={<ErpSearchBox value={keyword} onChange={setKeyword} debounceMs={300} placeholder="搜索模块编号/名称" ariaLabel="搜索模块" />}
        actions={<Button size="sm" className="erp-command-btn" icon={<IconRefresh size={16} />} onClick={() => void modules.refetch()}>刷新</Button>}
        header={modules.data ? <div className="erp-list-header text-secondary small px-3 pt-2">共 {moduleRows.length} 个可维护模块{keyword.trim() ? `，筛选后 ${filteredModules.length} 个` : ''}；点击行选中模块，下方维护其报表过滤条件。</div> : undefined}
      >
        <div className="erp-master-table-region">
          <ErpTable
            columns={moduleColumns}
            data={filteredModules}
            getRowId={(row: ModuleOption) => String(row.moduleId)}
            empty={<EmptyState title={keyword.trim() ? '未找到匹配模块' : '暂无可维护模块'} description={keyword.trim() ? '换一个关键词试试。' : '没有可维护报表过滤条件的模块。'} />}
            resizable
            storageKey="report-conditions-modules"
            clientSideSorting
            rowClickSingleSelect
            rowSelection={moduleSelection}
            onRowSelectionChange={handleModuleSelectionChange}
          />
        </div>
      </ErpListCard>
      <section className="card erp-detail-card">
        <div className="card-header erp-detail-toolbar d-flex align-items-center gap-2">
          <span className="small fw-semibold">{selectedModule ? `过滤条件（SYSQR_DEFAULT）——模块：${selectedModule}${selectedDescription ? ` ${selectedDescription}` : ''}` : '过滤条件（SYSQR_DEFAULT）'}</span>
          <div className="ms-auto">
            <Button size="sm" variant="primary" className="erp-command-btn" icon={<IconPlus size={16} />} disabled={!selectedModule} onClick={() => setEditor({ mode: 'new' })}>新增条件</Button>
          </div>
        </div>
        {selectedModule && conditions.isPending ? <LoadingState label="正在加载过滤条件…" /> : conditions.isError ? (
          <ErrorState message={conditionsError} onRetry={() => void conditions.refetch()} />
        ) : (
          <ErpTable
            columns={conditionColumns}
            data={conditionRows}
            getRowId={(row: ConditionRow) => String(row.serialNo)}
            empty={<EmptyState title={selectedModule ? '暂无过滤条件' : '未选择模块'} description={selectedModule ? '点击「新增条件」建立第一条过滤条件。' : '请先选择上方模块，再维护其报表过滤条件。'} />}
            resizable
            storageKey="report-conditions-rows"
            clientSideSorting
            rowClickSingleSelect
            rowSelection={conditionSelection}
            onRowSelectionChange={setConditionSelection}
          />
        )}
      </section>
      {editor && selectedModule > 0 && (
        <ConditionEditorModal
          open
          mode={editor.mode}
          row={editor.mode === 'edit' ? editor.row : undefined}
          moduleId={selectedModule}
          nextSerial={nextSerial}
          onClose={() => setEditor(null)}
          onSaved={() => { setEditor(null); void conditions.refetch() }}
        />
      )}
    </div>
  )
}
