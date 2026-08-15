import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'

export interface ChooserSource {
  active: boolean
  table: string | null
  description: string | null
  moduleId: number | null
  filter: string | null
  returnMapping: string | null
}

export interface FieldInput {
  label: string
  dataType: string
  width: number
  align: string | null
  headerAlign: string
  format: string | null
  isVisible: boolean
  isDefault: boolean
  isQueryable: boolean
  isReadonly: boolean
  isRequired: boolean
  isCost: boolean
  isSecrecy: boolean
  defaultValue: string | null
  verifyIndex: number | null
  regex: string | null
  remark: string | null
  browseUrl: string | null
  browseModuleId: number | null
  onlyChoose: boolean
  chooseMultiple: boolean
  choosePage: string | null
  choosers: ChooserSource[]
  canCopy: boolean
  tabNo: number
  formOrder: number | null
  span: number
  newLine: boolean
  cellGroup: string | null
  cellRole: number
  options: string | null
}

export interface FieldMeta extends FieldInput {
  key: string
  tableId: string
  isVirtual: boolean
  virtualExpression: string | null
  isAutoIncrement: boolean
  convertFunction: string | null
  dataSourceSql: string | null
  lastUpdatedBy: string | null
  lastUpdatedAt: string | null
  isPrimaryKey?: boolean
  physicalExists?: boolean
  physicalType?: string | null
  typeMatches?: boolean | null
}

export interface SetupLookup {
  value: string
  label: string
}

/** 与服务端 FieldAdminRepository.AllowedTypes 保持一致；伪类型保留兼容旧数据。 */
const ALLOWED_TYPES = [
  'nvarchar', 'varchar', 'nchar', 'char',
  'int', 'bigint', 'smallint', 'tinyint',
  'decimal', 'numeric', 'float', 'real', 'money', 'smallmoney',
  'date', 'datetime', 'datetime2', 'smalldatetime', 'time',
  'bit', 'uniqueidentifier', 'text', 'ntext', 'image', 'varbinary', 'binary',
  'xml', 'timestamp', 'sql_variant', 'geometry', 'geography', 'hierarchyid',
  'IDCard', 'URL', 'Email', 'PhoneNo', 'ZipCode', 'String', 'Integer',
]

export type FieldSection = 'display' | 'validation' | 'security' | 'layout' | 'advanced'

export type ExpressionKind = 'virtual_exp' | 'convert_function' | 'datasource_sql'

export interface ExpressionValidation {
  ok: boolean
  errors: string[]
  hints: string[]
  whiteListVersion: number
}

export interface ExpressionPreview {
  ok: boolean
  errors: string[]
  rows: Record<string, unknown>[]
}

export interface FieldEditorEndpoints {
  load: () => Promise<FieldMeta | null>
  save: (input: FieldInput, tableId: string, fieldId: string, original: FieldInput | null) => Promise<void>
  tables?: () => Promise<SetupLookup[]>
  modules?: () => Promise<SetupLookup[]>
  validateExpression?: (kind: ExpressionKind, tableId: string, fieldId: string, expression: string | null) => Promise<ExpressionValidation>
  previewExpression?: (kind: ExpressionKind, tableId: string, fieldId: string, expression: string | null) => Promise<ExpressionPreview>
  publishExpression?: (kind: ExpressionKind, tableId: string, fieldId: string, expression: string | null, original: string | null) => Promise<void>
}

interface FieldEditorModalProps {
  open: boolean
  mode: 'new' | 'edit'
  tableId: string
  fieldKey?: string
  title?: string
  endpoints: FieldEditorEndpoints
  onClose: () => void
  onSaved: () => void
}

function emptyChoosers(): ChooserSource[] {
  return Array.from({ length: 4 }, () => ({ active: false, table: null, description: null, moduleId: null, filter: null, returnMapping: null }))
}

function emptyDraft(tableId: string): FieldMeta {
  return {
    key: '', tableId,
    label: '', dataType: 'nvarchar', width: 100, align: '', headerAlign: 'center', format: null,
    isVisible: true, isDefault: true, isQueryable: true, isReadonly: false, isRequired: false,
    isCost: false, isSecrecy: false, defaultValue: null, verifyIndex: null, regex: null, remark: null,
    browseUrl: null, browseModuleId: null, onlyChoose: false, chooseMultiple: false, choosePage: null,
    choosers: emptyChoosers(),
    tabNo: 1, formOrder: null, span: 1, newLine: false, cellGroup: null, cellRole: 0, options: null,
    isVirtual: false, virtualExpression: null, canCopy: true, isAutoIncrement: false, convertFunction: null,
    dataSourceSql: null, lastUpdatedBy: null, lastUpdatedAt: null,
  }
}

function extractInput(meta: FieldMeta): FieldInput {
  const { key: _key, tableId: _tableId, isVirtual: _virtual, virtualExpression: _exp, isAutoIncrement: _auto, convertFunction: _convert, dataSourceSql: _sql, lastUpdatedBy: _by, lastUpdatedAt: _at, ...input } = meta
  return input
}

function regexIssue(regex: string | null): string | null {
  if (!regex) return null
  try {
    new RegExp(regex)
    return null
  } catch {
    return '正则表达式无法编译，请检查语法。'
  }
}

export function FieldEditorModal({ open, mode, tableId, fieldKey, title, endpoints, onClose, onSaved }: FieldEditorModalProps) {
  const [draft, setDraft] = useState<FieldMeta | null>(null)
  const [original, setOriginal] = useState<FieldMeta | null>(null)
  const [section, setSection] = useState<FieldSection>('display')
  const [exprStatus, setExprStatus] = useState<Record<ExpressionKind, { message: string; tone: 'ok' | 'error' | 'info'; rows?: Record<string, unknown>[] }>>({
    virtual_exp: { message: '', tone: 'info' },
    convert_function: { message: '', tone: 'info' },
    datasource_sql: { message: '', tone: 'info' },
  })
  const [exprBusy, setExprBusy] = useState<Record<ExpressionKind, boolean>>({ virtual_exp: false, convert_function: false, datasource_sql: false })
  const [exprOriginal, setExprOriginal] = useState<Record<ExpressionKind, string | null>>({ virtual_exp: null, convert_function: null, datasource_sql: null })

  useEffect(() => {
    if (!open) {
      setDraft(null)
      setOriginal(null)
      setSection('display')
      return
    }
    if (mode === 'new') {
      setDraft(emptyDraft(tableId))
      setOriginal(null)
      setSection('display')
    }
  }, [open, mode, tableId])

  const loadQuery = useQuery({
    queryKey: ['field-editor', 'load', tableId, fieldKey ?? '', mode],
    queryFn: endpoints.load,
    enabled: open && Boolean(fieldKey) && (mode === 'edit' || mode === 'new'),
  })
  useEffect(() => {
    if (!open || !loadQuery.data) return
    if (mode === 'edit') {
      setDraft(loadQuery.data)
      setOriginal(loadQuery.data)
      setExprOriginal({
        virtual_exp: loadQuery.data.virtualExpression ?? null,
        convert_function: loadQuery.data.convertFunction ?? null,
        datasource_sql: loadQuery.data.dataSourceSql ?? null,
      })
    } else if (mode === 'new') {
      setDraft({ ...loadQuery.data, key: '', tableId })
      setOriginal(null)
    }
  }, [open, mode, loadQuery.data, tableId])

  const tablesQuery = useQuery({
    queryKey: ['field-editor', 'tables'],
    queryFn: endpoints.tables ?? (async () => [] as SetupLookup[]),
    enabled: open && Boolean(endpoints.tables),
  })
  const modulesQuery = useQuery({
    queryKey: ['field-editor', 'modules'],
    queryFn: endpoints.modules ?? (async () => [] as SetupLookup[]),
    enabled: open && Boolean(endpoints.modules),
  })

  const save = useMutation({
    mutationFn: async (value: FieldMeta) => endpoints.save(extractInput(value), value.tableId, value.key, original ? extractInput(original) : null),
    onSuccess: () => onSaved(),
  })

  const expressionValue = (kind: ExpressionKind): string | null => {
    if (!draft) return null
    return kind === 'virtual_exp' ? draft.virtualExpression ?? null
      : kind === 'convert_function' ? draft.convertFunction ?? null
        : draft.dataSourceSql ?? null
  }
  const setExpressionValue = (kind: ExpressionKind, value: string | null) => {
    if (!draft) return
    if (kind === 'virtual_exp') setDraft({ ...draft, virtualExpression: value || null })
    else if (kind === 'convert_function') setDraft({ ...draft, convertFunction: value || null })
    else setDraft({ ...draft, dataSourceSql: value || null })
    setExprStatus((prev) => ({ ...prev, [kind]: { message: '', tone: 'info' } }))
  }
  const runValidate = async (kind: ExpressionKind) => {
    if (!draft || !endpoints.validateExpression) return
    setExprBusy((prev) => ({ ...prev, [kind]: true }))
    try {
      const result = await endpoints.validateExpression(kind, draft.tableId, draft.key, expressionValue(kind))
      setExprStatus((prev) => ({ ...prev, [kind]: {
        message: result.ok
          ? `校验通过（白名单 v${result.whiteListVersion}）${result.hints.length ? '：' + result.hints.join('；') : ''}`
          : result.errors.join('；'),
        tone: result.ok ? 'ok' : 'error',
      } }))
    } catch (error) {
      setExprStatus((prev) => ({ ...prev, [kind]: { message: `校验失败：${error instanceof Error ? error.message : String(error)}`, tone: 'error' } }))
    } finally {
      setExprBusy((prev) => ({ ...prev, [kind]: false }))
    }
  }
  const runPreview = async (kind: ExpressionKind) => {
    if (!draft || !endpoints.previewExpression) return
    setExprBusy((prev) => ({ ...prev, [kind]: true }))
    try {
      const result = await endpoints.previewExpression(kind, draft.tableId, draft.key, expressionValue(kind))
      setExprStatus((prev) => ({ ...prev, [kind]: {
        message: result.ok ? `预览成功（最多 20 行）` : result.errors.join('；'),
        tone: result.ok ? 'ok' : 'error',
        rows: result.ok ? result.rows : undefined,
      } }))
    } catch (error) {
      setExprStatus((prev) => ({ ...prev, [kind]: { message: `预览失败：${error instanceof Error ? error.message : String(error)}`, tone: 'error' } }))
    } finally {
      setExprBusy((prev) => ({ ...prev, [kind]: false }))
    }
  }
  const runPublish = async (kind: ExpressionKind) => {
    if (!draft || !endpoints.publishExpression) return
    const value = expressionValue(kind)
    if (value === exprOriginal[kind]) {
      setExprStatus((prev) => ({ ...prev, [kind]: { message: '值与当前一致，无需发布。', tone: 'info' } }))
      return
    }
    setExprBusy((prev) => ({ ...prev, [kind]: true }))
    try {
      await endpoints.publishExpression(kind, draft.tableId, draft.key, value, exprOriginal[kind])
      setExprOriginal((prev) => ({ ...prev, [kind]: value }))
      setExprStatus((prev) => ({ ...prev, [kind]: { message: '已发布（SYSDF 审计已留痕）。', tone: 'ok' } }))
    } catch (error) {
      setExprStatus((prev) => ({ ...prev, [kind]: { message: `发布失败：${error instanceof Error ? error.message : String(error)}`, tone: 'error' } }))
    } finally {
      setExprBusy((prev) => ({ ...prev, [kind]: false }))
    }
  }

  const isNew = mode === 'new'
  const dialogTitle = title ?? (isNew ? '新增字段' : '字段管理')

  const renderExpressionRow = (kind: ExpressionKind, label: string, multiline: boolean, placeholder: string) => {
    const status = exprStatus[kind]
    const value = expressionValue(kind) ?? ''
    const previewColumns = status.rows && status.rows.length > 0 ? Object.keys(status.rows[0]).slice(0, 4) : []
    return (
      <div className="col-12">
        <label className="form-label">{label}</label>
        {multiline
          ? <textarea className="form-control font-monospace" rows={3} value={value} placeholder={placeholder} onChange={(event) => setExpressionValue(kind, event.target.value)} />
          : <input className="form-control" value={value} placeholder={placeholder} onChange={(event) => setExpressionValue(kind, event.target.value)} />}
        <div className="d-flex gap-2 mt-1 mb-1">
          <Button size="sm" loading={exprBusy[kind]} onClick={() => void runValidate(kind)} disabled={!endpoints.validateExpression}>校验</Button>
          <Button size="sm" loading={exprBusy[kind]} onClick={() => void runPreview(kind)} disabled={!endpoints.previewExpression}>预览</Button>
          <Button size="sm" variant="danger" loading={exprBusy[kind]} onClick={() => void runPublish(kind)} disabled={!endpoints.publishExpression || value.trim() === (exprOriginal[kind] ?? '')}>发布</Button>
        </div>
        {status.message && <div className={`small mb-1 ${status.tone === 'ok' ? 'text-success' : status.tone === 'error' ? 'text-danger' : 'text-secondary'}`}>{status.message}</div>}
        {status.rows && status.rows.length > 0 && (
          <table className="table table-sm table-bordered mt-1">
            <thead><tr>{previewColumns.map((column) => <th key={column}>{column}</th>)}</tr></thead>
            <tbody>{status.rows.slice(0, 5).map((row, index) => (
              <tr key={index}>{previewColumns.map((column) => <td key={column}>{String(row[column] ?? '')}</td>)}</tr>
            ))}</tbody>
          </table>
        )}
      </div>
    )
  }

  return open ? (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-lg modal-dialog-centered erp-field-settings-dialog">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{dialogTitle}</h2>
            <button className="btn-close" aria-label="关闭" onClick={() => { setDraft(null); onClose() }} />
          </div>
          <div className="modal-body">
            <datalist id="field-setup-tables">{tablesQuery.data?.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}</datalist>
            <div className="alert alert-warning">{isNew ? '新增字段将写入全局字段元数据（FIELDS），直接影响该表后续的显示、查询、权限与录入行为。' : '字段设置会直接影响业务数据的显示、查询、权限与录入行为。当前窗口只允许维护选中的单个字段。'}</div>
            <div>
              {mode === 'edit' && loadQuery.isPending ? (
                <LoadingState label="正在加载字段元数据…" />
              ) : mode === 'edit' && loadQuery.isError ? (
                <div className="alert alert-danger">无法加载该字段的元数据，请确认当前账号具有字段设置权限。</div>
              ) : draft ? (
                <div className="card">
                  <div className="card-header p-0 position-relative" style={{ zIndex: 2 }}>
                    <div className="nav nav-tabs px-3 erp-field-settings-tabs" role="tablist" onPointerDown={event => event.stopPropagation()}>
                      <button type="button" className={`nav-link ${section === 'display' ? 'active' : ''}`} onClick={event => { event.preventDefault(); event.stopPropagation(); setSection('display') }}>显示与查询</button>
                      <button type="button" className={`nav-link ${section === 'validation' ? 'active' : ''}`} onClick={event => { event.preventDefault(); event.stopPropagation(); setSection('validation') }}>录入与校验</button>
                      <button type="button" className={`nav-link ${section === 'security' ? 'active' : ''}`} onClick={event => { event.preventDefault(); event.stopPropagation(); setSection('security') }}>权限与备注</button>
                      <button type="button" className={`nav-link ${section === 'layout' ? 'active' : ''}`} onClick={event => { event.preventDefault(); event.stopPropagation(); setSection('layout') }}>表单布局</button>
                      <button type="button" className={`nav-link ${section === 'advanced' ? 'active' : ''}`} onClick={event => { event.preventDefault(); event.stopPropagation(); setSection('advanced') }}>高级设置</button>
                    </div>
                  </div>
                  <div className="card-body row g-3">
                    {section === 'display' && <>
                      <div className="col-md-6">
                        <label className="form-label">数据表</label>
                        {isNew ? (
                          <input className="form-control" list="field-setup-tables" value={draft.tableId} onChange={event => setDraft({ ...draft, tableId: event.target.value })} />
                        ) : (
                          <input className="form-control" value={draft.tableId} disabled />
                        )}
                      </div>
                      <div className="col-md-6">
                        <label className="form-label">字段名称</label>
                        {isNew ? (
                          <input className="form-control" value={draft.key} onChange={event => setDraft({ ...draft, key: event.target.value })} />
                        ) : (
                          <input className="form-control" value={draft.key} disabled />
                        )}
                      </div>
                      <div className="col-md-6">
                        <label className="form-label">字段标题</label>
                        <input className="form-control" value={draft.label} onChange={event => setDraft({ ...draft, label: event.target.value })} />
                      </div>
                      <div className="col-md-6">
                        <label className="form-label">数据库类型</label>
                        <select className="form-select" value={draft.dataType} disabled={!isNew} onChange={event => setDraft({ ...draft, dataType: event.target.value })}>
                          {ALLOWED_TYPES.map((type) => <option key={type} value={type}>{type}</option>)}
                        </select>
                      </div>
                      <div className="col-md-3">
                        <label className="form-label">列宽</label>
                        <input type="number" min="40" max="300" className="form-control" value={draft.width} onChange={event => setDraft({ ...draft, width: Number(event.target.value) })} />
                      </div>
                      <div className="col-md-3">
                        <label className="form-label">对齐</label>
                        <select className="form-select" value={draft.align ?? ''} onChange={event => setDraft({ ...draft, align: event.target.value })}>
                          <option value="">默认（数字右对齐 / 其它左对齐）</option>
                          <option value="left">左对齐</option>
                          <option value="center">居中</option>
                          <option value="right">右对齐</option>
                        </select>
                      </div>
                      <div className="col-md-6">
                        <label className="form-label">标题对齐</label>
                        <select className="form-select" value={draft.headerAlign} onChange={event => setDraft({ ...draft, headerAlign: event.target.value })}>
                          <option value="left">左对齐</option>
                          <option value="center">居中</option>
                          <option value="right">右对齐</option>
                        </select>
                      </div>
                      <div className="col-12">
                        <label className="form-label">显示格式</label>
                        <input className="form-control" value={draft.format ?? ''} onChange={event => setDraft({ ...draft, format: event.target.value })} />
                      </div>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isVisible} onChange={() => setDraft({ ...draft, isVisible: !draft.isVisible })} />
                          <span className="form-check-label">可见</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isDefault} onChange={() => setDraft({ ...draft, isDefault: !draft.isDefault })} />
                          <span className="form-check-label">默认字段</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isQueryable} onChange={() => setDraft({ ...draft, isQueryable: !draft.isQueryable })} />
                          <span className="form-check-label">允许查询</span>
                        </label>
                      </div>
                      {!isNew && (
                        <div className="col-12 d-flex gap-2 flex-wrap align-items-center">
                          <span className={`badge ${draft.isPrimaryKey ? 'bg-blue-lt' : 'bg-secondary-lt'}`}>主键{draft.isPrimaryKey ? '：是' : '：否'}</span>
                          <span className={`badge ${draft.physicalExists === false ? 'bg-danger-lt' : 'bg-green-lt'}`}>
                            物理列：{draft.physicalExists === false ? '不存在' : draft.physicalType ?? '未知'}
                          </span>
                          {draft.physicalExists && draft.typeMatches === false && (
                            <span className="badge bg-warning-lt">类型不一致（元数据 {draft.dataType} / 物理 {draft.physicalType ?? '—'}）</span>
                          )}
                        </div>
                      )}
                      {!isNew && draft.physicalExists === false && !draft.isVirtual && (
                        <div className="col-12">
                          <div className="alert alert-warning py-2 px-3 small mb-0">
                            该字段元数据引用的物理列不存在（幽灵字段），列表已默认隐藏。编辑仅影响元数据，不影响查询与录入。
                          </div>
                        </div>
                      )}
                    </>}
                    {section === 'validation' && <>
                      <div className="col-md-6">
                        <label className="form-label">默认值</label>
                        <input className="form-control" value={draft.defaultValue ?? ''} onChange={event => setDraft({ ...draft, defaultValue: event.target.value })} />
                      </div>
                      <div className="col-md-6">
                        <label className="form-label">检验顺序</label>
                        <input type="number" className="form-control" value={draft.verifyIndex ?? ''} onChange={event => setDraft({ ...draft, verifyIndex: event.target.value === '' ? null : Number(event.target.value) })} />
                      </div>
                      <div className="col-12">
                        <label className="form-label">正则表达式</label>
                        <input className={`form-control${regexIssue(draft.regex) ? ' is-invalid' : ''}`} value={draft.regex ?? ''} placeholder="如 ^[A-Z0-9]{8}$" onChange={event => setDraft({ ...draft, regex: event.target.value })} />
                        {regexIssue(draft.regex) && <div className="invalid-feedback">{regexIssue(draft.regex)}</div>}
                      </div>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isRequired} onChange={() => setDraft({ ...draft, isRequired: !draft.isRequired })} />
                          <span className="form-check-label">不能为空</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isReadonly} onChange={() => setDraft({ ...draft, isReadonly: !draft.isReadonly })} />
                          <span className="form-check-label">只读</span>
                        </label>
                      </div>
                    </>}
                    {section === 'security' && <>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isCost} onChange={() => setDraft({ ...draft, isCost: !draft.isCost })} />
                          <span className="form-check-label">成本字段</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isSecrecy} onChange={() => setDraft({ ...draft, isSecrecy: !draft.isSecrecy })} />
                          <span className="form-check-label">保密字段</span>
                        </label>
                      </div>
                      <div className="col-md-8">
                        <label className="form-label">查看详情 URL</label>
                        <input className="form-control" placeholder="仅允许站内相对路径" value={draft.browseUrl ?? ''} onChange={event => setDraft({ ...draft, browseUrl: event.target.value })} />
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">浏览权限模块 ID</label>
                        <select className="form-select" value={draft.browseModuleId ?? ''} onChange={event => setDraft({ ...draft, browseModuleId: event.target.value === '' ? null : Number(event.target.value) })}>
                          <option value="">不限制</option>{modulesQuery.data?.map(item => <option key={item.value} value={item.value}>{item.label} ({item.value})</option>)}</select>
                        <input type="hidden" value={draft.browseModuleId ?? ''} onChange={event => setDraft({ ...draft, browseModuleId: event.target.value === '' ? null : Number(event.target.value) })} />
                      </div>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.onlyChoose} onChange={() => setDraft({ ...draft, onlyChoose: !draft.onlyChoose })} />
                          <span className="form-check-label">数据仅可选入</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.chooseMultiple} onChange={() => setDraft({ ...draft, chooseMultiple: !draft.chooseMultiple })} />
                          <span className="form-check-label">支持多笔选入</span>
                        </label>
                      </div>
                      <div className="col-12">
                        <label className="form-label">自定义数据选择页面</label>
                        <input className="form-control" value={draft.choosePage ?? ''} onChange={event => setDraft({ ...draft, choosePage: event.target.value })} />
                      </div>
                      {draft.choosers.map((source, index) => (
                        <div className="col-12" key={index}>
                          <div className="card card-sm">
                            <div className="card-header">
                              <label className="form-check m-0">
                                <input className="form-check-input" type="checkbox" checked={source.active} onChange={() => setDraft({ ...draft, choosers: draft.choosers.map((item, i) => i === index ? { ...item, active: !item.active } : item) })} />
                                <span className="form-check-label">数据来源 {index + 1}</span>
                              </label>
                            </div>
                            <div className="card-body row g-2">
                              <div className="col-md-6">
                                <label className="form-label">来源表</label>
                                <input className="form-control" list="field-setup-tables" value={source.table ?? ''} onChange={event => setDraft({ ...draft, choosers: draft.choosers.map((item, i) => i === index ? { ...item, table: event.target.value } : item) })} />
                              </div>
                              <div className="col-md-6">
                                <label className="form-label">来源说明</label>
                                <input className="form-control" value={source.description ?? ''} onChange={event => setDraft({ ...draft, choosers: draft.choosers.map((item, i) => i === index ? { ...item, description: event.target.value } : item) })} />
                              </div>
                              <div className="col-12">
                                <label className="form-label">过滤条件</label>
                                <textarea className="form-control" rows={2} value={source.filter ?? ''} onChange={event => setDraft({ ...draft, choosers: draft.choosers.map((item, i) => i === index ? { ...item, filter: event.target.value } : item) })} />
                                {source.filter?.includes('{') && (
                                  <div className="text-warning small mt-1">含运行时占位符（&#123;...&#125;），受控解析完成前不执行，保存时原样保留。</div>
                                )}
                              </div>
                              <div className="col-12">
                                <label className="form-label">返回值映射</label>
                                <textarea className="form-control" rows={2} value={source.returnMapping ?? ''} onChange={event => setDraft({ ...draft, choosers: draft.choosers.map((item, i) => i === index ? { ...item, returnMapping: event.target.value } : item) })} />
                              </div>
                            </div>
                          </div>
                        </div>
                      ))}
                    </>}
                    {section === 'layout' && <>
                      <div className="col-12">
                        <div className="alert alert-warning">表单布局仅作用于统一表单编辑页（新增/编辑/查看）。页签归属与顺序需配合模块级页签定义（菜单管理中的 FORM_TABS）。</div>
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">页签序号（FORM_TAB_NO）</label>
                        <input type="number" min="1" className="form-control" value={draft.tabNo} onChange={event => setDraft({ ...draft, tabNo: Math.max(1, Number(event.target.value) || 1) })} />
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">表单顺序（FORM_ORDER）</label>
                        <input type="number" min="1" className="form-control" value={draft.formOrder ?? ''} placeholder="留空按默认列序" onChange={event => setDraft({ ...draft, formOrder: event.target.value === '' ? null : Math.max(1, Number(event.target.value) || 1) })} />
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">跨列宽度（FORM_SPAN）</label>
                        <select className="form-select" value={draft.span} onChange={event => setDraft({ ...draft, span: Number(event.target.value) })}>
                          <option value={1}>半行</option>
                          <option value={2}>整行独占</option>
                        </select>
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">复合格角色（FORM_CELL_ROLE）</label>
                        <select className="form-select" value={draft.cellRole} onChange={event => setDraft({ ...draft, cellRole: Number(event.target.value) })}>
                          <option value={0}>普通字段</option>
                          <option value={1}>主字段（带标签 + 选择器）</option>
                          <option value={2}>从字段（同格联动显示）</option>
                        </select>
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">复合格组（FORM_CELL_GROUP）</label>
                        <input className="form-control" value={draft.cellGroup ?? ''} placeholder="如 CLIENT，同组字段同一格" onChange={event => setDraft({ ...draft, cellGroup: event.target.value || null })} />
                      </div>
                      <div className="col-md-4 d-flex align-items-end pb-2">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.newLine} onChange={() => setDraft({ ...draft, newLine: !draft.newLine })} />
                          <span className="form-check-label">强制换行（FORM_NEW_LINE）</span>
                        </label>
                      </div>
                      <div className="col-12">
                        <label className="form-label">下拉选项（FORM_OPTIONS）</label>
                        <input className="form-control" value={draft.options ?? ''} placeholder="如 O=外含税;I=内含税;N=不含税；有值即渲染下拉框" onChange={event => setDraft({ ...draft, options: event.target.value || null })} />
                      </div>
                    </>}
                    {section === 'advanced' && <>
                      <div className="col-12">
                        <div className="alert alert-warning">高级表达式会影响数据读取和单据处理，请按「校验 → 预览 → 发布」顺序操作；发布走受控解析 + SYSDF 审计，未通过校验的表达式不会进入运行时。转换函数与数据源 SQL 为注册表/受限语法白名单。</div>
                      </div>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isVirtual} disabled />
                          <span className="form-check-label">虚拟字段</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.canCopy} onChange={() => setDraft({ ...draft, canCopy: !draft.canCopy })} />
                          <span className="form-check-label">数据可复制</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isAutoIncrement} disabled />
                          <span className="form-check-label">自动增长</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isPrimaryKey ?? false} disabled />
                          <span className="form-check-label">主键标记（IS_PK）</span>
                        </label>
                      </div>
                      {renderExpressionRow('virtual_exp', '虚拟表达式（表.列）', true, '如 CLIENT.CLIENT_NAME（须在 QUERY_RELATION 白名单内）')}
                      {renderExpressionRow('convert_function', '转换函数', false, '如 f_get_emp_name_by_id（受控注册表）')}
                      {renderExpressionRow('datasource_sql', '数据源 SQL（受限 SELECT）', true, '如 SELECT G_IDX,G_DESC FROM SYSDG')}
                      <div className="col-md-6">
                        <label className="form-label">最后修改人</label>
                        <input className="form-control" value={draft.lastUpdatedBy ?? ''} disabled />
                      </div>
                      <div className="col-md-6">
                        <label className="form-label">最后修改时间</label>
                        <input className="form-control" value={draft.lastUpdatedAt ? new Date(draft.lastUpdatedAt).toLocaleString('zh-CN') : ''} disabled />
                      </div>
                      <div className="col-12">
                        <label className="form-label">字段备注</label>
                        <textarea className="form-control" rows={3} value={draft.remark ?? ''} onChange={event => setDraft({ ...draft, remark: event.target.value })} />
                      </div>
                    </>}
                  </div>
                  {save.isError && <div className="alert alert-danger m-3 mb-0">{String((save.error as Error)?.message ?? '保存失败')}</div>}
                  <div className="card-footer text-end">
                    <Button variant="primary" loading={save.isPending} disabled={!draft.label.trim() || draft.width < 40 || draft.width > 300 || regexIssue(draft.regex) !== null || (isNew && (!draft.key.trim() || !draft.tableId.trim()))} onClick={() => save.mutate(draft)}>{isNew ? '新增字段' : '保存字段设置'}</Button>
                  </div>
                </div>
              ) : (
                <div className="text-secondary text-center py-5">未找到该字段的元数据。</div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  ) : null
}
