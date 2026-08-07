import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useBlocker, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { DataChooserInput, type ChooserRow } from './DataChooserInput'
import { FormFieldRenderer } from './FormFieldRenderer'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'
import { validateDetailRows, validateMasterFields, type FieldErrors } from './formValidation'

interface SaveRecordRequest {
  values: Record<string, string>
  details: Record<string, string>[]
  original?: Record<string, string>
}

interface RecordBundle { master: Record<string, unknown>; details: Record<string, unknown>[] }

function buildKey(form: FormDefinition, values: Record<string, string>): string[] {
  return form.masterPkOrder.map(column => values[column] ?? '')
}

function emptyValue(field: FormFieldDefinition): string {
  if (field.defaultValue != null) return field.defaultValue
  if (field.dataType.toLowerCase().includes('bit')) return '0'
  return ''
}

function writableFields(fields: FormFieldDefinition[]): FormFieldDefinition[] {
  return fields.filter(field => field.isVisible && !field.isReadonly && !field.serverFilled && !field.isVirtual)
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
  const keyParam = searchParams.get('key')
  const originalRef = useRef<Record<string, string>>({})
  const [masterValues, setMasterValues] = useState<Record<string, string>>({})
  const [detailRows, setDetailRows] = useState<Record<string, string>[]>([])
  const [chooserField, setChooserField] = useState<FormFieldDefinition | null>(null)
  const [dirty, setDirty] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [detailErrors, setDetailErrors] = useState<FieldErrors[]>([])
  const [detailChooser, setDetailChooser] = useState<{ index: number; field: FormFieldDefinition } | null>(null)

  const formQuery = useQuery({
    queryKey: ['workbench', moduleId, 'form-definition', isEdit ? 'edit' : 'new'],
    queryFn: () => apiClient.get<FormDefinition>(`/document-workbench/${moduleId}/form-definition?mode=${isEdit ? 'edit' : 'new'}`),
  })
  const recordQuery = useQuery({
    queryKey: ['workbench', moduleId, 'record', keyParam],
    queryFn: () => apiClient.get<RecordBundle>(`/document-workbench/${moduleId}/record`, { query: { key: keyParam ?? '' } }),
    enabled: isEdit && Boolean(keyParam) && formQuery.isSuccess,
  })

  useEffect(() => {
    if (!formQuery.data || isEdit) return
    const initial: Record<string, string> = {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible) initial[field.key] = emptyValue(field)
    }
    setMasterValues(initial)
  }, [formQuery.data, isEdit])

  useEffect(() => {
    if (!formQuery.data || !recordQuery.data) return
    const master: Record<string, string> = {}
    for (const field of formQuery.data.masterFields) {
      if (field.isVisible) master[field.key] = recordQuery.data.master[field.key] == null ? '' : String(recordQuery.data.master[field.key])
    }
    originalRef.current = {}
    for (const field of writableFields(formQuery.data.masterFields)) originalRef.current[field.key] = master[field.key] ?? ''
    setMasterValues(master)
    setDetailRows(recordQuery.data.details.map(detail => {
      const row: Record<string, string> = {}
      for (const field of formQuery.data?.detailFields ?? []) {
        if (field.isVisible) row[field.key] = detail[field.key] == null ? '' : String(detail[field.key])
      }
      return row
    }))
  }, [formQuery.data, recordQuery.data])

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

  const applyChooser = (field: FormFieldDefinition, row: ChooserRow) => {
    const source = field.choosers.find(item => item.active && item.table)
    const mapping = source?.returnMapping
    if (mapping) {
      setMasterValues(current => {
        const next = { ...current }
        for (const pair of mapping.split(';')) {
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
    const row: Record<string, string> = {}
    for (const field of formQuery.data.detailFields) {
      if (field.isVisible) row[field.key] = emptyValue(field)
    }
    setDetailRows(current => [...current, row])
    setDetailErrors(current => [...current, {}])
    setDirty(true)
  }

  const applyDetailChooser = (index: number, field: FormFieldDefinition, row: ChooserRow) => {
    const source = field.choosers.find(item => item.active && item.table)
    const mapping = source?.returnMapping
    if (mapping) {
      setDetailRows(current => current.map((currentRow, i) => {
        if (i !== index) return currentRow
        const next = { ...currentRow }
        for (const pair of mapping.split(';')) {
          const [target, column] = pair.split('=')
          if (!target || !column || row[column] === undefined) continue
          next[target.replace(/^(txt|cho|dro|chk|lab|hidd)_/, '')] = String(row[column] ?? '')
        }
        return next
      }))
      setDirty(true)
    }
    setDetailChooser(null)
  }

  const removeDetailRow = (index: number) => {
    setDetailRows(current => current.filter((_, i) => i !== index))
    setDetailErrors(current => current.filter((_, i) => i !== index))
    setDirty(true)
  }

  if (formQuery.isPending || (isEdit && recordQuery.isPending)) return <LoadingState label="正在加载表单…" />
  if (formQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(formQuery.error)}</div></section>
  if (isEdit && recordQuery.isError) return <section className="card"><div className="card-body text-center py-5">{describeError(recordQuery.error)}</div></section>

  const form = formQuery.data
  if (!form) return null
  const visibleMaster = form.masterFields.filter(field => field.isVisible)
  const visibleDetail = form.detailFields.filter(field => field.isVisible)

  return (
    <div className="d-grid gap-2">
      <div className="d-flex justify-content-between align-items-center">
        <h1 className="h3 mb-0">{isEdit ? '编辑' : '新建'}{form.title}</h1>
        <div className="d-flex gap-2">
          <Button onClick={back}>返回</Button>
          <Button variant="primary" loading={save.isPending} onClick={() => { if (validateClient()) save.mutate() }}>保存</Button>
        </div>
      </div>
      {saveError ? <div className="alert alert-danger mb-0">{saveError}</div> : null}
      <section className="card">
        <div className="card-body">
          <div className="row g-3">
            {visibleMaster.map(field => (
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
              />
            ))}
          </div>
        </div>
      </section>
      {form.detailFields.length > 0 ? (
        <section className="card">
          <div className="card-header d-flex justify-content-between align-items-center">
            <h2 className="h5 mb-0">明细</h2>
            <Button size="sm" onClick={addDetailRow}>新增一行</Button>
          </div>
          <div className="table-responsive">
            <table className="table table-sm mb-0">
              <thead><tr>{visibleDetail.map(field => <th key={field.key}>{field.label}</th>)}<th /></tr></thead>
              <tbody>
                {detailRows.map((row, index) => (
                  <tr key={index}>
                    {visibleDetail.map(field => (
                      <td key={field.key}>
                        <div className="d-flex gap-1">
                          <input
                            className={`form-control form-control-sm${detailErrors[index]?.[field.key] ? ' is-invalid' : ''}`}
                            value={row[field.key] ?? ''}
                            disabled={field.isReadonly || field.serverFilled}
                            onChange={event => updateDetail(index, field.key, event.target.value)}
                          />
                          {field.choosers.some(source => source.active && source.table) && !field.isReadonly && !field.serverFilled ? (
                            <Button size="sm" onClick={() => setDetailChooser({ index, field })}>选择</Button>
                          ) : null}
                        </div>
                        {detailErrors[index]?.[field.key] ? <div className="invalid-feedback d-block">{detailErrors[index][field.key]}</div> : null}
                        {field.isPrimaryKey && field.isReadonly ? <div className="form-hint text-secondary">保存时自动编号</div> : null}
                      </td>
                    ))}
                    <td><Button size="sm" variant="danger" onClick={() => removeDetailRow(index)}>删除</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}
      {chooserField ? (
        <DataChooserInput moduleId={moduleId} field={chooserField} onPick={row => applyChooser(chooserField, row)} onClose={() => setChooserField(null)} />
      ) : null}
      {detailChooser ? (
        <DataChooserInput moduleId={moduleId} field={detailChooser.field} onPick={row => applyDetailChooser(detailChooser.index, detailChooser.field, row)} onClose={() => setDetailChooser(null)} />
      ) : null}
    </div>
  )
}
