import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState, SortingState } from '@tanstack/react-table'
import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { useBlocker, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser, type UnifiedChooserRow } from '../../components/common/UnifiedChooser'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { FormFieldRenderer } from './FormFieldRenderer'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'
import { inputKind } from './formFieldKind'
import { buildFormCells, buildFormRows } from './formLayout'
import { validateDetailRows, validateMasterFields, type FieldErrors } from './formValidation'

interface SaveRecordRequest {
  values: Record<string, string>
  details: Record<string, string>[]
  original?: Record<string, string>
}

interface RecordBundle { master: Record<string, unknown>; details: Record<string, unknown>[] }

/** 明细网格行：__id 为表格行键，__index 映射 detailRows 原始行号，__filler 为占位空行 */
interface DetailGridRow {
  __id: string
  __index: number
  __filler: boolean
  [key: string]: unknown
}

function buildKey(form: FormDefinition, values: Record<string, string>): string[] {
  return form.masterPkOrder.map(column => values[column] ?? '')
}

function emptyValue(field: FormFieldDefinition): string {
  if (field.defaultValue != null) return field.defaultValue
  if (field.dataType.toLowerCase().includes('bit')) return '0'
  return ''
}

/**
 * 明细编辑控件的可用最小列宽。DISPLAY_LENGTH 是只读列表展示宽度（往往只有几十像素），
 * 编辑态直接套用会把输入控件压到无法操作；此处按控件类型给列宽下限，
 * 并配合 `minWidthFloor` 让拖拽/历史宽度也不能低于该下限。
 */
function detailControlMinWidth(field: FormFieldDefinition): number {
  const kind = inputKind(field)
  if (kind === 'checkbox') return 56
  if (kind === 'select') return 104
  if (kind === 'date') return 132
  const hasChooser = field.choosers.some(source => source.active && source.table)
  return hasChooser ? 168 : 110
}

function writableFields(fields: FormFieldDefinition[]): FormFieldDefinition[] {
  // 只读可见字段随保存提交（与服务端规则一致）：ONLY_CHOOSE 带选择器字段（CURR_ID/TAX_ID）
  // 与只读必填联动字段（如 CURR_RATE，由币别带出）必须提交，否则服务端必填校验失败；
  // displayOnly/serverFilled/虚拟字段仍不提交（服务端维护）。
  return fields.filter(field => field.isVisible && !field.serverFilled && !field.isVirtual && !field.displayOnly
    && (!field.isReadonly || field.isRequired || field.choosers.some(source => source.active && source.table)))
}

/** 统一选择器标题：字段标签 + 数据源描述（多选后缀由 UnifiedChooser 内部追加） */
function chooserTitle(field: FormFieldDefinition): string {
  const source = field.choosers.find(item => item.active && item.table)
  return `${field.label}${source?.description ? `（${source.description}）` : ''}`
}

function describeError(error: unknown): string {
  if (error instanceof ApiError && error.status === 404) return '该模块未启用统一表单编辑（含存盘后业务逻辑的模块暂不开放，或不在白名单内）。'
  if (error instanceof ApiError) return error.body.message
  return '无法加载表单定义。'
}

function summarizeFieldErrors(master: FieldErrors, details: FieldErrors[]): string {
  const messages = [...Object.values(master), ...details.flatMap(row => Object.values(row))]
  return messages.slice(0, 3).join('；')
}

export function FormEditorPage() {
  const { moduleId = '' } = useParams()
  const location = useLocation()
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const isEdit = location.pathname.endsWith('/edit')
  const isView = location.pathname.endsWith('/view')
  const isCopy = location.pathname.endsWith('/copy')
  const keyParam = searchParams.get('key')
  const copyFrom = searchParams.get('copyFrom')
  const originalRef = useRef<Record<string, string>>({})
  const [masterValues, setMasterValues] = useState<Record<string, string>>({})
  const [detailRows, setDetailRows] = useState<Record<string, string>[]>([])
  const [chooserField, setChooserField] = useState<FormFieldDefinition | null>(null)
  const [dirty, setDirty] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [detailErrors, setDetailErrors] = useState<FieldErrors[]>([])
  const [detailChooser, setDetailChooser] = useState<{ index: number; field: FormFieldDefinition } | null>(null)
  const [selectedDetailRows, setSelectedDetailRows] = useState<Set<number>>(new Set())
  const [detailSort, setDetailSort] = useState<{ key: string; dir: 1 | -1 } | null>(null)
  const [activeTab, setActiveTab] = useState(1)

  const formQuery = useQuery({
    queryKey: ['workbench', moduleId, 'form-definition', isEdit ? 'edit' : isView ? 'view' : 'new'],
    queryFn: () => apiClient.get<FormDefinition>(`/document-workbench/${moduleId}/form-definition?mode=${isEdit ? 'edit' : isView ? 'view' : 'new'}`),
  })
  const recordQuery = useQuery({
    queryKey: ['workbench', moduleId, 'record', keyParam ?? copyFrom],
    queryFn: () => apiClient.get<RecordBundle>(`/document-workbench/${moduleId}/record`, { query: { key: keyParam ?? copyFrom ?? '' } }),
    enabled: (isEdit || isView || isCopy) && Boolean(keyParam ?? copyFrom) && formQuery.isSuccess,
  })

  useEffect(() => {
    if (!formQuery.data || isEdit || isView || isCopy) return
    const initial: Record<string, string> = {}
    const defaults = formQuery.data.defaultValues ?? {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible) initial[field.key] = defaults[field.key] ?? emptyValue(field)
    }
    setMasterValues(initial)
  }, [formQuery.data, isEdit, isView, isCopy])

  useEffect(() => {
    if (formQuery.data) setActiveTab(formQuery.data.tabs[0]?.no ?? 1)
  }, [formQuery.data])

  useEffect(() => {
    if (!formQuery.data || !recordQuery.data) return
    const master: Record<string, string> = {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible) master[field.key] = recordQuery.data.master[field.key] == null ? '' : String(recordQuery.data.master[field.key])
    }
    if (isCopy) {
      // 复制（IF_COPY）：主键/自动单号由服务端重新生成，CAN_COPY=0 字段不带出；
      // 应用新增默认值（单别/单号/日期），保存走新增管线
      for (const field of formQuery.data.masterFields) {
        if (!field.isVisible) continue
        if (formQuery.data.masterPkOrder.some(pk => pk.toLowerCase() === field.key.toLowerCase()) || !field.canCopy) master[field.key] = ''
      }
      const defaults = formQuery.data.defaultValues ?? {}
      for (const field of formQuery.data.masterFields) {
        if (field.isVisible && defaults[field.key] != null) master[field.key] = defaults[field.key]
      }
    }
    originalRef.current = {}
    for (const field of writableFields(formQuery.data.masterFields)) originalRef.current[field.key] = master[field.key] ?? ''
    setMasterValues(master)
    setDetailRows(recordQuery.data.details.map(detail => {
      const row: Record<string, string> = {}
      for (const field of formQuery.data?.detailFields ?? []) {
        if (!field.isVisible) continue
        // 复制时：主表主键关联列（服务端从主表带入）与 CAN_COPY=0 字段不带出
        if (isCopy && (formQuery.data.masterPkOrder.some(pk => pk.toLowerCase() === field.key.toLowerCase()) || !field.canCopy)) {
          row[field.key] = ''
          continue
        }
        row[field.key] = detail[field.key] == null ? '' : String(detail[field.key])
      }
      return row
    }))
  }, [formQuery.data, recordQuery.data, isCopy])

  useEffect(() => {
    if (!dirty) return
    const handler = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  const blocker = useBlocker(dirty)
  useEffect(() => {
    if (blocker.state !== 'blocked') return
    if (window.confirm('有未保存的修改，确定离开吗？')) blocker.proceed()
    else blocker.reset()
  }, [blocker])

  const save = useMutation({
    mutationFn: async () => {
      if (!formQuery.data) throw new Error('表单定义未加载。')
      const values: Record<string, string> = {}
      for (const field of writableFields(formQuery.data.masterFields)) values[field.key] = masterValues[field.key] ?? ''
      const details = detailRows.map(row => {
        const detail: Record<string, string> = {}
        for (const field of writableFields(formQuery.data?.detailFields ?? [])) detail[field.key] = row[field.key] ?? ''
        return detail
      })
      const body: SaveRecordRequest = { values, details }
      if (isEdit) {
        body.original = originalRef.current
        const key = buildKey(formQuery.data, masterValues)
        return apiClient.put<{ key: string[] }>(`/document-workbench/${moduleId}/record?key=${encodeURIComponent(JSON.stringify(key))}`, body)
      }
      return apiClient.post<{ key: string[] }>(`/document-workbench/${moduleId}/record`, body)
    },
    onSuccess: async () => {
      setDirty(false)
      await queryClient.invalidateQueries({ queryKey: ['workbench', moduleId] })
      navigate(`/document-workbench/${moduleId}`)
    },
    onError: cause => {
      if (cause instanceof ApiError && cause.status === 400 && Array.isArray(cause.body.fieldErrors) && cause.body.fieldErrors.length > 0) {
        const masterKeys = new Set((formQuery.data?.masterFields ?? []).map(field => field.key))
        const master: FieldErrors = {}
        const detail: FieldErrors = {}
        for (const error of cause.body.fieldErrors) {
          if (!error.field) continue
          if (masterKeys.has(error.field)) master[error.field] = error.message ?? '校验失败。'
          else detail[error.field] = error.message ?? '校验失败。'
        }
        setFieldErrors(master)
        setDetailErrors(Object.keys(detail).length > 0 ? [detail] : [])
        setSaveError(`数据校验未通过：${summarizeFieldErrors(master, [detail])}`)
        return
      }
      setSaveError(cause instanceof Error ? cause.message : '保存失败。')
    },
  })

  const workflow = useMutation({
    mutationFn: async (action: 'approve' | 'deapprove') => {
      if (!formQuery.data) throw new Error('表单定义未加载。')
      const key = buildKey(formQuery.data, masterValues)
      return apiClient.post<{ key: string[] }>(`/document-workbench/${moduleId}/${action}`, { key })
    },
    onSuccess: async (_, action) => {
      window.alert(action === 'approve' ? '批核成功。' : '解批成功。')
      await recordQuery.refetch()
    },
    onError: cause => {
      const message = cause instanceof ApiError ? cause.body.message : '操作失败，请稍后重试。'
      window.alert(message)
    },
  })

  const validateClient = (): boolean => {
    if (!formQuery.data) return false
    const master = validateMasterFields(formQuery.data.masterFields, masterValues)
    const details = validateDetailRows(formQuery.data.detailFields, detailRows, formQuery.data.detailDfVerify)
    setFieldErrors(master)
    setDetailErrors(details)
    const hasErrors = Object.keys(master).length > 0 || details.some(row => Object.keys(row).length > 0)
    if (hasErrors) setSaveError(`数据校验未通过：${summarizeFieldErrors(master, details)}`)
    else setSaveError(null)
    return !hasErrors
  }

  const back = () => {
    navigate(`/document-workbench/${moduleId}`)
  }

  const openPrint = () => {
    if (!formQuery.data) return
    const key = buildKey(formQuery.data, masterValues)
    window.open(`/print/${moduleId}?key=${encodeURIComponent(JSON.stringify(key))}`, '_blank')
  }

  const applyChooser = (field: FormFieldDefinition, row: UnifiedChooserRow) => {
    const source = field.choosers.find(item => item.active && item.table)
    const mapping = source?.returnMapping
    if (mapping) {
      setMasterValues(current => {
        const next = { ...current }
        for (const pair of mapping.split(/[;,]/)) {
          const [target, column] = pair.split('=')
          if (!target || !column || row[column] === undefined) continue
          next[target.replace(/^(txt|cho|dro|chk|lab|hidd)_/, '')] = String(row[column] ?? '')
        }
        return next
      })
      setDirty(true)
    }
    setChooserField(null)
  }

  const updateDetail = (index: number, key: string, value: string) => {
    setDetailRows(current => current.map((row, i) => i === index ? { ...row, [key]: value } : row))
    setDetailErrors(current => current.map((rowErrors, i) => {
      if (i !== index) return rowErrors
      const next = { ...rowErrors }
      delete next[key]
      return next
    }))
    setDirty(true)
  }

  const buildEmptyDetailRow = (): Record<string, string> => {
    if (!formQuery.data) return {}
    const row: Record<string, string> = {}
    for (const field of formQuery.data.detailFields) {
      // 主表同名值自动带入新明细行（对齐旧系统 setTRKeyValue 随主表联动带值）
      if (field.isVisible) row[field.key] = masterValues[field.key] ?? emptyValue(field)
    }
    return row
  }

  const addDetailRow = () => {
    if (!formQuery.data) return
    const missing = formQuery.data.detailNoFields
      .split(';')
      .map(field => field.trim())
      .filter(field => field && !(masterValues[field] ?? '').trim())
    if (missing.length > 0) {
      setSaveError(`请先填写主表字段：${missing.join('、')}，再新增明细。`)
      return
    }
    setDetailRows(current => [...current, buildEmptyDetailRow()])
    setDetailErrors(current => [...current, {}])
    setDirty(true)
  }

  const applyDetailChooser = (index: number, field: FormFieldDefinition, rows: UnifiedChooserRow[]) => {
    const source = field.choosers.find(item => item.active && item.table)
    const mapping = source?.returnMapping
    const applyMapping = (target: Record<string, string>, row: UnifiedChooserRow) => {
      if (!mapping) return target
      for (const pair of mapping.split(/[;,]/)) {
        const [t, column] = pair.split('=')
        if (!t || !column || row[column] === undefined) continue
        target[t.replace(/^(txt|cho|dro|chk|lab|hidd)_/, '')] = String(row[column] ?? '')
      }
      return target
    }
    // 明细多选：逐条追加明细行（对齐旧系统 ReturnMultiValue 的 addTR 语义）
    if (field.chooseMultiple && rows.length > 1) {
      const newRows = rows.map(row => applyMapping(buildEmptyDetailRow(), row))
      setDetailRows(current => [...current, ...newRows])
      setDetailErrors(current => [...current, ...newRows.map(() => ({}))])
      setDirty(true)
      setDetailChooser(null)
      return
    }
    const row = rows[0]
    if (mapping) {
      setDetailRows(current => current.map((currentRow, i) => {
        if (i !== index) return currentRow
        return applyMapping({ ...currentRow }, row)
      }))
      setDirty(true)
    }
    setDetailChooser(null)
  }

  const removeDetailRow = (index: number) => {
    setDetailRows(current => current.filter((_, i) => i !== index))
    setDetailErrors(current => current.filter((_, i) => i !== index))
    setSelectedDetailRows(current => new Set([...current].filter(i => i !== index).map(i => i > index ? i - 1 : i)))
    setDirty(true)
  }

  const removeSelectedDetailRows = () => {
    setDetailRows(current => current.filter((_, index) => !selectedDetailRows.has(index)))
    setDetailErrors(current => current.filter((_, index) => !selectedDetailRows.has(index)))
    setSelectedDetailRows(new Set())
    setDirty(true)
  }

  const handleDetailSortingChange = (next: SortingState) => {
    const sort = next[0]
    setDetailSort(sort ? { key: sort.id, dir: sort.desc ? -1 : 1 } : null)
  }

  const handleDetailRowSelectionChange = (next: RowSelectionState) => {
    setSelectedDetailRows(new Set(
      Object.keys(next)
        .filter(id => next[id] && id.startsWith('r'))
        .map(id => Number(id.slice(1))),
    ))
  }

  if (formQuery.isPending || (isEdit && recordQuery.isPending)) return <LoadingState label="正在加载表单…" />
  if (formQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(formQuery.error)}</div></section>
  if (isEdit && recordQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(recordQuery.error)}</div></section>

  const form = formQuery.data
  if (!form) return null
  const visibleMaster = form.masterFields.filter(field => field.isVisible)
  const visibleDetail = form.detailFields.filter(field => field.isVisible)
  const detailFillerCount = Math.max(0, 5 - detailRows.length)
  const hasTabs = form.tabs.length > 0
  const activeTabNo = hasTabs ? activeTab : 1
  const masterCells = buildFormCells(visibleMaster).filter(cell => !hasTabs || cell[0].tabNo === activeTabNo)
  const masterRows = buildFormRows(masterCells, form.columns)
  const orderedDetailIndices = (() => {
    if (!detailSort) return detailRows.map((_, index) => index)
    const { key, dir } = detailSort
    const indices = detailRows.map((_, index) => index)
    indices.sort((a, b) => {
      const va = detailRows[a][key] ?? ''
      const vb = detailRows[b][key] ?? ''
      const na = Number(va)
      const nb = Number(vb)
      const numeric = va !== '' && vb !== '' && !Number.isNaN(na) && !Number.isNaN(nb)
      const cmp = numeric ? na - nb : String(va).localeCompare(String(vb), 'zh-CN', { numeric: true })
      return cmp * dir
    })
    return indices
  })()
  const detailRowSelection = Object.fromEntries([...selectedDetailRows].map(index => [`r${index}`, true])) as RowSelectionState
  const detailGridRows: DetailGridRow[] = [
    ...orderedDetailIndices.map(index => ({ __id: `r${index}`, __index: index, __filler: false, ...detailRows[index] })),
    ...Array.from({ length: detailFillerCount }, (_, fillerIndex) => ({ __id: `f${fillerIndex}`, __index: -1, __filler: true })),
  ]
  const detailColumns: ColumnDef<DetailGridRow, unknown>[] = [
    {
      id: '__check',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-check text-center', resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="全选"
          checked={table.getIsAllPageRowsSelected()}
          ref={input => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => row.original.__filler ? null : (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label={`选择第${row.index + 1}行`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={event => event.stopPropagation()}
        />
      ),
    },
    {
      id: '__rowNo',
      header: '序号',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-row-no text-center', resizable: false, truncate: false },
      cell: ({ row }) => row.original.__filler ? null : <span className="text-secondary">{row.index + 1}</span>,
    },
    ...visibleDetail.map((field): ColumnDef<DetailGridRow, unknown> => ({
      id: field.key,
      accessorKey: field.key,
      header: field.label,
      enableSorting: true,
      meta: { minWidth: Math.max(field.displayLength, detailControlMinWidth(field)), dataType: field.dataType, minWidthFloor: true, truncate: false },
      cell: ({ row }) => {
        if (row.original.__filler) return null
        const index = row.original.__index
        return (
          <FormFieldRenderer
            field={field}
            value={String(row.original[field.key] ?? '')}
            error={detailErrors[index]?.[field.key]}
            onChange={value => updateDetail(index, field.key, value)}
            onChoose={fieldToChoose => setDetailChooser({ index, field: fieldToChoose })}
            bare
          />
        )
      },
    })),
    {
      id: '__actions',
      header: '操作',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-detail-actions text-center', resizable: false, truncate: false },
      cell: ({ row }) => row.original.__filler ? null : (
        <Button size="sm" variant="danger" onClick={() => removeDetailRow(row.original.__index)}>删除</Button>
      ),
    },
  ]
  const renderField = (field: FormFieldDefinition, bare = false) => (
    <FormFieldRenderer
      key={field.key}
      field={field}
      value={masterValues[field.key] ?? ''}
      error={fieldErrors[field.key]}
      onChange={value => {
        setMasterValues(current => ({ ...current, [field.key]: value }))
        setFieldErrors(current => { const next = { ...current }; delete next[field.key]; return next })
        setDirty(true)
      }}
      onChoose={fieldToChoose => setChooserField(fieldToChoose)}
      bare={bare}
    />
  )
  const renderCell = (cell: FormFieldDefinition[]) => {
    const [main, ...companions] = cell
    if (companions.length === 0) return renderField(main)
    const isBoolean = main.dataType.toLowerCase().includes('bit')
    return (
      <div key={main.key} className="erp-form-cell">
        <label className="erp-form-label">{main.label}{!isBoolean && !main.isReadonly && !main.serverFilled && main.isRequired ? ' *' : ''}</label>
        <div className="erp-form-cell-controls">
          {renderField(main, true)}
          {companions.map(companion => renderField(companion, true))}
        </div>
      </div>
    )
  }

  return (
    <div className="d-flex flex-column gap-2 erp-form-page">
      {saveError ? <div className="alert alert-danger mb-0">{saveError}</div> : null}
      <section className="card erp-form-card">
        <div className="card-body">
          <div className="erp-form-toolbar">
            {!isView ? (
              <>
                {isCopy && <span className="small text-secondary align-self-center">复制模式：以选中记录为模板，保存后生成新单据</span>}
                <Button size="sm" variant="primary" loading={save.isPending} onClick={() => { if (validateClient()) save.mutate() }}>保存</Button>
                <Button size="sm" onClick={back}>取消</Button>
              </>
            ) : (
              <>
                <Button size="sm" onClick={back}>返回</Button>
                {form.buttons && form.buttons.length > 0 ? (
                  form.buttons.map((button, index) => {
                    if (button.action === 'approve' && form.hasWorkflow && keyParam && recordQuery.data && recordQuery.data.master.CONFIRM_TAG !== true) {
                      return <Button key={index} size="sm" variant="primary" loading={workflow.isPending} onClick={() => workflow.mutate('approve')}>批核</Button>
                    }
                    if (button.action === 'deapprove' && form.hasWorkflow && keyParam && recordQuery.data && recordQuery.data.master.CONFIRM_TAG === true) {
                      return <Button key={index} size="sm" variant="danger" loading={workflow.isPending} onClick={() => workflow.mutate('deapprove')}>解批</Button>
                    }
                    if (button.action === 'print' && keyParam) {
                      return <Button key={index} size="sm" onClick={openPrint}>打印</Button>
                    }
                    return null
                  })
                ) : form.hasWorkflow && keyParam && recordQuery.isSuccess && recordQuery.data ? (
                  <>
                    {recordQuery.data.master.CONFIRM_TAG !== true && (
                      <Button size="sm" variant="primary" loading={workflow.isPending} onClick={() => workflow.mutate('approve')}>批核</Button>
                    )}
                    {recordQuery.data.master.CONFIRM_TAG === true && (
                      <Button size="sm" variant="danger" loading={workflow.isPending} onClick={() => workflow.mutate('deapprove')}>解批</Button>
                    )}
                    <Button size="sm" onClick={openPrint}>打印</Button>
                  </>
                ) : null}
              </>
            )}
          </div>
          {hasTabs ? (
            <ul className="nav nav-tabs erp-form-tabs">
              {form.tabs.map(tab => (
                <li className="nav-item" key={tab.no}>
                  <button type="button" className={`nav-link${activeTabNo === tab.no ? ' active' : ''}`} onClick={() => setActiveTab(tab.no)}>{tab.title}</button>
                </li>
              ))}
            </ul>
          ) : null}
          <div className="erp-form-grid">
            {masterRows.map((row, rowIndex) => (
              <div className="erp-form-row" key={rowIndex} style={{ '--erp-form-cols': Math.max(1, form.columns) } as CSSProperties}>
                {row.map(cell => renderCell(cell))}
              </div>
            ))}
          </div>
        </div>
      </section>
      {form.detailFields.length > 0 ? (
        <section className="card erp-detail-card">
          <div className="card-header erp-detail-toolbar">
            <div className="d-flex gap-2">
              <Button size="sm" onClick={addDetailRow}>新增一行</Button>
              <Button size="sm" variant="danger" disabled={selectedDetailRows.size === 0} onClick={removeSelectedDetailRows}>删除所选{selectedDetailRows.size > 0 ? ` (${selectedDetailRows.size})` : ''}</Button>
              {/* 子表专用工具栏扩展位：生成请购单等后续加入 */}
            </div>
          </div>
          <div className="table-responsive">
            <ErpTable
              columns={detailColumns}
              data={detailGridRows}
              getRowId={row => row.__id}
              sorting={detailSort ? [{ id: detailSort.key, desc: detailSort.dir === -1 }] : []}
              onSortingChange={handleDetailSortingChange}
              rowSelection={detailRowSelection}
              onRowSelectionChange={handleDetailRowSelectionChange}
              resizable
              storageKey={`form-detail-${moduleId}`}
              className="erp-detail-grid"
              responsive={false}
              copyable={false}
              keyboardNavigation={false}
              rowClassName={row => row.__filler ? 'erp-detail-filler' : undefined}
              empty={null}
            />
          </div>
        </section>
      ) : null}
      {chooserField ? (
        <UnifiedChooser
          open
          title={chooserTitle(chooserField)}
          source={{ kind: 'formField', moduleId, fieldKey: chooserField.key }}
          mode={chooserField.chooseMultiple ? 'multi' : 'single'}
          masterValues={masterValues}
          onPick={rows => applyChooser(chooserField, rows[0])}
          onClose={() => setChooserField(null)}
          resizable
          storageKey={`chooser-${moduleId}-${chooserField.key}`}
          emptyText="没有可选数据。"
        />
      ) : null}
      {detailChooser ? (
        <UnifiedChooser
          open
          title={chooserTitle(detailChooser.field)}
          source={{ kind: 'formField', moduleId, fieldKey: detailChooser.field.key }}
          mode={detailChooser.field.chooseMultiple ? 'multi' : 'single'}
          masterValues={masterValues}
          detailValues={detailRows[detailChooser.index] ?? undefined}
          onPick={rows => applyDetailChooser(detailChooser.index, detailChooser.field, rows)}
          onClose={() => setDetailChooser(null)}
          resizable
          storageKey={`chooser-${moduleId}-${detailChooser.field.key}`}
          emptyText="没有可选数据。"
        />
      ) : null}
    </div>
  )
}
