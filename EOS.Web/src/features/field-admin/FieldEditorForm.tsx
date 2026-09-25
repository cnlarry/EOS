import { useMutation, useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { IconPencil, IconPlus, IconTrash } from '@tabler/icons-react'
import { LoadingState } from '../../components/common/AsyncState'
import { TabbedPanel, type TabbedPanelTab } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { DataSourceEditorModal } from './DataSourceEditorModal'
import {
  parseFilterRows,
  parseReturnRows,
  serializeFilterRows,
  serializeReturnRows,
  type FilterRowDraft,
  type ReturnRowDraft,
} from './chooserDraft'

export interface ChooserSource {
  active: boolean
  table: string | null
  description: string | null
  moduleId: number | null
  filter: string | null
  returnMapping: string | null
  serialNo: number | null
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
  /** 单据生命周期系统列：结构锁定，仅名称/显示/备注类可改。 */
  isSystemColumn?: boolean
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

export type FieldSection = 'basic' | 'sources' | 'permissions' | 'layout' | 'advanced' | 'history'

const SECTION_TABS: TabbedPanelTab<FieldSection>[] = [
  { key: 'basic', label: '基本信息' },
  { key: 'sources', label: '数据来源' },
  { key: 'permissions', label: '权限与行为' },
  { key: 'layout', label: '表单布局' },
  { key: 'advanced', label: '高级设置' },
]

/** 字段变更历史事件（AUDIT_EVENT，RESOURCE_TYPE=FIELD_ADMIN）。 */
export interface FieldHistoryChange {
  name: string
  oldValue: string | null
  newValue: string | null
}

export interface FieldHistoryEvent {
  occurredAt: string
  actorUserId: string
  /** 操作人姓名（后端对照 SYSDN.Emp_Name 解析，缺省回退 user id）。 */
  actorName?: string
  action: string
  summary: string | null
  changes: FieldHistoryChange[]
}

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

interface FieldEditorFormProps {
  mode: 'new' | 'edit'
  tableId: string
  fieldKey?: string
  endpoints: FieldEditorEndpoints
  onCancel: () => void
  onSaved: () => void
  /** 全尺寸页面模式：追加「变更历史」选项卡（AUDIT_EVENT 字段级明细）。 */
  historyTab?: boolean
  /** 保存动作句柄（页面工具栏按钮触发 Form 内部保存；React 19 ref 作为普通 prop）。 */
  actionRef?: React.MutableRefObject<{ save: () => void; saveAsync: () => Promise<void> } | null>
  /** 保存状态回调（页面工具栏按钮禁用/loading 联动；dirty 供页面做未保存离开确认）。 */
  onStateChange?: (state: { canSave: boolean; saving: boolean; dirty: boolean }) => void
  /** 底部操作区渲染（弹窗用）；页面模式由页面工具栏承担，不传。 */
  renderActions?: (action: { canSave: boolean; saving: boolean; onSave: () => void; onCancel: () => void }) => React.ReactNode
  /** 进入时的模块上下文（?moduleId=）：用于从字段维护跳到该模块的表单设计。 */
  contextModuleId?: number | null
}

function emptyChoosers(): ChooserSource[] {
  return []
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

function moveItem<T>(items: T[], index: number, delta: -1 | 1): T[] {
  const target = index + delta
  if (target < 0 || target >= items.length) return items
  const next = [...items]
  const [item] = next.splice(index, 1)
  next.splice(target, 0, item)
  return next
}

/** 数据源构建器草稿统一按列表位置（i{index}）索引：初始化/弹窗读回/保存序列化三处同键。 */
function chooserUiKey(index: number): string {
  return `i${index}`
}

/** 把构建器行按列表位置合并回 choosers（保存序列化与 dirty 对比共用同一口径）。 */
function mergeChooserUi(
  value: FieldMeta,
  ui: Record<string, { filterRows: FilterRowDraft[]; returnRows: ReturnRowDraft[] }>,
): FieldMeta {
  return {
    ...value,
    choosers: value.choosers.map((source, index) => {
      const entry = ui[chooserUiKey(index)]
      return entry
        ? { ...source, filter: serializeFilterRows(entry.filterRows), returnMapping: serializeReturnRows(entry.returnRows) }
        : source
    }),
  }
}

export function FieldEditorForm({ mode, tableId, fieldKey, endpoints, onCancel, onSaved, historyTab = false, actionRef, onStateChange, renderActions, contextModuleId = null }: FieldEditorFormProps) {
  const navigate = useNavigate()
  const [draft, setDraft] = useState<FieldMeta | null>(null)
  const [original, setOriginal] = useState<FieldMeta | null>(null)
  const [section, setSection] = useState<FieldSection>('basic')
  const [exprStatus, setExprStatus] = useState<Record<ExpressionKind, { message: string; tone: 'ok' | 'error' | 'info'; rows?: Record<string, unknown>[] }>>({
    virtual_exp: { message: '', tone: 'info' },
    convert_function: { message: '', tone: 'info' },
    datasource_sql: { message: '', tone: 'info' },
  })
  const [exprBusy, setExprBusy] = useState<Record<ExpressionKind, boolean>>({ virtual_exp: false, convert_function: false, datasource_sql: false })
  const [exprOriginal, setExprOriginal] = useState<Record<ExpressionKind, string | null>>({ virtual_exp: null, convert_function: null, datasource_sql: null })
  // P2/P4 构建器状态：过滤行/回填行（按列表位置 i{index} 键）、来源表选择、列/目标字段选择
  const [chooserUi, setChooserUi] = useState<Record<string, { filterRows: FilterRowDraft[]; returnRows: ReturnRowDraft[] }>>({})
  const [dataSourceEditor, setDataSourceEditor] = useState<{ index: number } | null>(null)
  // dirty 基线：取「构建器行回写后」的草稿快照，解析/序列化往返不产生假 dirty
  const baselineRef = useRef<string | null>(null)
  // 已初始化的字段标识：同字段后台重取（refetch/窗口聚焦）不覆盖编辑中的草稿
  const loadedKeyRef = useRef<string | null>(null)
  const [historyLimit, setHistoryLimit] = useState(20)

  useEffect(() => {
    if (mode !== 'new') return
    const empty = emptyDraft(tableId)
    setDraft(empty)
    setOriginal(null)
    setChooserUi({})
    baselineRef.current = JSON.stringify(empty)
    loadedKeyRef.current = `empty:${tableId}`
    setSection('basic')
  }, [mode, tableId])

  const loadQuery = useQuery({
    queryKey: ['field-editor', 'load', tableId, fieldKey ?? '', mode],
    queryFn: endpoints.load,
    enabled: Boolean(fieldKey) && (mode === 'edit' || mode === 'new'),
  })
  const historyQuery = useQuery({
    queryKey: ['field-admin', 'history', tableId, fieldKey ?? ''],
    queryFn: async () => fieldKey
      ? apiClient.get<FieldHistoryEvent[]>(`/admin/fields/${encodeURIComponent(tableId)}/${encodeURIComponent(fieldKey)}/history`)
      : [],
    enabled: historyTab && mode === 'edit' && Boolean(fieldKey),
  })
  useEffect(() => {
    const data = loadQuery.data
    if (!data) return
    const loadedKey = `${mode}:${tableId}:${fieldKey ?? ''}`
    if (loadedKeyRef.current === loadedKey) return
    loadedKeyRef.current = loadedKey
    const nextDraft = mode === 'edit' ? data : { ...data, key: '', tableId }
    const ui = Object.fromEntries(
      (data.choosers ?? []).map((source, index) => [
        chooserUiKey(index),
        { filterRows: parseFilterRows(source.filter), returnRows: parseReturnRows(source.returnMapping) },
      ]),
    )
    setDraft(nextDraft)
    setOriginal(mode === 'edit' ? data : null)
    setExprOriginal({
      virtual_exp: data.virtualExpression ?? null,
      convert_function: data.convertFunction ?? null,
      datasource_sql: data.dataSourceSql ?? null,
    })
    setChooserUi(ui)
    baselineRef.current = JSON.stringify(mergeChooserUi(nextDraft, ui))
  }, [mode, loadQuery.data, tableId, fieldKey])

  const tablesQuery = useQuery({
    queryKey: ['field-editor', 'tables'],
    queryFn: endpoints.tables ?? (async () => [] as SetupLookup[]),
    enabled: Boolean(endpoints.tables),
  })
  const modulesQuery = useQuery({
    queryKey: ['field-editor', 'modules'],
    queryFn: endpoints.modules ?? (async () => [] as SetupLookup[]),
    enabled: Boolean(endpoints.modules),
  })

  const lastPayloadRef = useRef<string | null>(null)
  const save = useMutation({
    mutationFn: async (value: FieldMeta) => {
      // P2/P4：把构建器行序列化回 FILTER_STRUCT / RETURN_ITEMS JSON
      const payload = mergeChooserUi(value, chooserUi)
      lastPayloadRef.current = JSON.stringify(payload)
      return endpoints.save(extractInput(payload), payload.tableId, payload.key, original ? extractInput(original) : null)
    },
    onSuccess: () => {
      // 保存成功后以提交内容为新基线，并同步归零 dirty（onSaved 触发的返回导航不得被离开确认拦截）
      if (lastPayloadRef.current != null) baselineRef.current = lastPayloadRef.current
      onStateChange?.({ canSave, saving: false, dirty: false })
      onSaved()
    },
  })

  const updateChooser = (index: number, patch: Partial<ChooserSource>) => {
    setDraft(prev => prev ? { ...prev, choosers: prev.choosers.map((source, i) => i === index ? { ...source, ...patch } : source) } : prev)
  }

  const moveChooser = (index: number, delta: -1 | 1) => {
    if (!draft) return
    const keys = draft.choosers.map((_, i) => chooserUiKey(i))
    const nextKeys = moveItem(keys, index, delta)
    if (nextKeys === keys) return
    setDraft(prev => prev ? { ...prev, choosers: moveItem(prev.choosers, index, delta) } : prev)
    setChooserUi(prev => {
      // 构建器草稿跟随数据项一起搬移，保持「位置键 ↔ 数据」对齐
      const next: typeof prev = {}
      nextKeys.forEach((key, position) => { if (prev[key]) next[chooserUiKey(position)] = prev[key] })
      return next
    })
  }

  const removeChooser = (index: number) => {
    if (!draft) return
    const source = draft.choosers[index]
    if (!source) return
    if (!window.confirm(`确定要删除数据源「${source.description || source.table || '未命名数据源'}」吗？删除在保存字段后生效。`)) return
    setDraft(prev => prev ? { ...prev, choosers: prev.choosers.filter((_, i) => i !== index) } : prev)
    setChooserUi(prev => {
      const next: typeof prev = {}
      draft.choosers.forEach((_, i) => {
        if (i === index) return
        const entry = prev[chooserUiKey(i)]
        if (entry) next[chooserUiKey(i < index ? i : i - 1)] = entry
      })
      return next
    })
  }

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
  /** 系统列结构锁：新建态不涉及，编辑态锁定类型/校验/数据源/权限与分组。 */
  const locked = !isNew && draft?.isSystemColumn === true
  const tabs: TabbedPanelTab<FieldSection>[] = historyTab
    ? [...SECTION_TABS, { key: 'history', label: '变更历史' }]
    : SECTION_TABS
  const canSave = Boolean(draft && draft.label.trim() && draft.width >= 40 && draft.width <= 300
    && regexIssue(draft.regex) === null && (!isNew || (draft.key.trim() && draft.tableId.trim())))
  const dirty = draft != null && baselineRef.current != null
    && JSON.stringify(mergeChooserUi(draft, chooserUi)) !== baselineRef.current
  const lastStateRef = useRef<{ canSave: boolean; saving: boolean; dirty: boolean } | null>(null)

  useEffect(() => {
    if (actionRef) {
      actionRef.current = {
        save: () => { if (draft) save.mutate(draft) },
        saveAsync: () => (draft ? save.mutateAsync(draft).then(() => undefined) : Promise.resolve()),
      }
    }
    const next = { canSave, saving: save.isPending, dirty }
    const last = lastStateRef.current
    if (!last || last.canSave !== next.canSave || last.saving !== next.saving || last.dirty !== next.dirty) {
      lastStateRef.current = next
      onStateChange?.(next)
    }
  })

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

  return (
    <div>
      <datalist id="field-setup-tables">{tablesQuery.data?.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}</datalist>
            <div>
              {mode === 'edit' && loadQuery.isPending ? (
                <LoadingState label="正在加载字段元数据…" />
              ) : mode === 'edit' && loadQuery.isError ? (
                <div className="alert alert-danger">无法加载该字段的元数据，请确认当前账号具有字段设置权限。</div>
              ) : draft ? (
                <div onPointerDown={event => event.stopPropagation()}>
                  {!isNew && draft.isSystemColumn === true && (
                    <div className="alert alert-info py-2 px-3 small mb-3">
                      系统列（单据生命周期列由管线持有）：类型、校验、数据源、权限与分组结构锁定，仅名称、显示与备注类可改；不可删除。
                    </div>
                  )}
                  <TabbedPanel tabs={tabs} activeKey={section} onActiveKeyChange={setSection} label="字段设置分区">
                    <div className="row g-3">
                    {section === 'basic' && <>
                      <div className="col-md-4">
                        <label className="form-label">数据表</label>
                        {isNew ? (
                          <input className="form-control" list="field-setup-tables" value={draft.tableId} onChange={event => setDraft({ ...draft, tableId: event.target.value })} />
                        ) : (
                          <input className="form-control" value={draft.tableId} disabled />
                        )}
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">字段名称</label>
                        {isNew ? (
                          <input className="form-control" value={draft.key} onChange={event => setDraft({ ...draft, key: event.target.value })} />
                        ) : (
                          <input className="form-control" value={draft.key} disabled />
                        )}
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">字段标题</label>
                        <input className="form-control" value={draft.label} onChange={event => setDraft({ ...draft, label: event.target.value })} />
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">数据库类型</label>
                        <select className="form-select" value={draft.dataType} disabled={locked} onChange={event => setDraft({ ...draft, dataType: event.target.value })}>
                          {ALLOWED_TYPES.map((type) => <option key={type} value={type}>{type}</option>)}
                        </select>
                        {!isNew && (
                          <div className="form-hint">
                            类型决定录入控件与显示格式（date 只取日期、datetime 含时间）；改类型只更新元数据，不变更物理列
                          </div>
                        )}
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
                      <div className="col-md-4">
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
                    <div className="col-12">
                      <hr className="my-2" />
                    </div>
                      <div className="col-md-4">
                        <label className="form-label">默认值</label>
                        <input className="form-control" value={draft.defaultValue ?? ''} disabled={locked} onChange={event => setDraft({ ...draft, defaultValue: event.target.value })} />
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">检验顺序</label>
                        <input type="number" className="form-control" value={draft.verifyIndex ?? ''} onChange={event => setDraft({ ...draft, verifyIndex: event.target.value === '' ? null : Number(event.target.value) })} />
                      </div>
                      <div className="col-12">
                        <label className="form-label">正则表达式</label>
                        <input className={`form-control${regexIssue(draft.regex) ? ' is-invalid' : ''}`} value={draft.regex ?? ''} disabled={locked} placeholder="如 ^[A-Z0-9]{8}$" onChange={event => setDraft({ ...draft, regex: event.target.value })} />
                        {regexIssue(draft.regex) && <div className="invalid-feedback">{regexIssue(draft.regex)}</div>}
                      </div>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isRequired} disabled={locked} onChange={() => setDraft({ ...draft, isRequired: !draft.isRequired })} />
                          <span className="form-check-label">不能为空</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isReadonly} disabled={locked} onChange={() => setDraft({ ...draft, isReadonly: !draft.isReadonly })} />
                          <span className="form-check-label">只读</span>
                        </label>
                      </div>
                    </>}
                    {section === 'permissions' && <>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isCost} disabled={locked} onChange={() => setDraft({ ...draft, isCost: !draft.isCost })} />
                          <span className="form-check-label">成本字段</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.isSecrecy} disabled={locked} onChange={() => setDraft({ ...draft, isSecrecy: !draft.isSecrecy })} />
                          <span className="form-check-label">保密字段</span>
                        </label>
                      </div>
                      <div className="col-md-8">
                        <label className="form-label">查看详情 URL</label>
                        <input className="form-control" placeholder="仅允许站内相对路径" value={draft.browseUrl ?? ''} disabled={locked} onChange={event => setDraft({ ...draft, browseUrl: event.target.value })} />
                      </div>
                      <div className="col-md-4">
                        <label className="form-label">浏览权限模块 ID</label>
                        <select className="form-select" value={draft.browseModuleId ?? ''} disabled={locked} onChange={event => setDraft({ ...draft, browseModuleId: event.target.value === '' ? null : Number(event.target.value) })}>
                          <option value="">不限制</option>{modulesQuery.data?.map(item => <option key={item.value} value={item.value}>{item.label} ({item.value})</option>)}</select>
                      </div>
                      <div className="col-12 d-flex gap-4">
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.onlyChoose} disabled={locked} onChange={() => setDraft({ ...draft, onlyChoose: !draft.onlyChoose })} />
                          <span className="form-check-label">数据仅可选入</span>
                        </label>
                        <label className="form-check">
                          <input className="form-check-input" type="checkbox" checked={draft.chooseMultiple} disabled={locked} onChange={() => setDraft({ ...draft, chooseMultiple: !draft.chooseMultiple })} />
                          <span className="form-check-label">支持多笔选入</span>
                        </label>
                      </div>
                      <div className="col-12">
                        <label className="form-label">自定义数据选择页面</label>
                        <input className="form-control" value={draft.choosePage ?? ''} disabled={locked} onChange={event => setDraft({ ...draft, choosePage: event.target.value })} />
                      </div>
                    </>}
                    {section === 'sources' && <>
                      <div className="col-12 d-flex justify-content-end mb-2">
                        <Button size="sm" variant="primary" icon={<IconPlus size={16} />} disabled={locked} onClick={() => setDataSourceEditor({ index: draft.choosers.length })}>新增数据源</Button>
                      </div>
                      {draft.choosers.length === 0 && (
                        <div className="col-12 text-secondary">暂无数据源。点击右上角「新增数据源」配置取数通道（过滤条件/回填映射在弹窗内构建）。</div>
                      )}
                      {draft.choosers.map((source, index) => (
                        <div className="col-12" key={chooserUiKey(index)}>
                          <div className="border rounded p-2 d-flex align-items-center gap-2">
                            <input className="form-check-input" type="checkbox" checked={source.active} disabled={locked} onChange={() => updateChooser(index, { active: !source.active })} title="启用/停用" />
                            <div className="flex-grow-1">
                              <div className="fw-semibold">{source.description || source.table || '未命名数据源'}</div>
                              <div className="text-secondary small font-monospace">
                                {source.table ?? ''}{source.moduleId != null ? ` · ${modulesQuery.data?.find(item => item.value === String(source.moduleId))?.label ?? `模块 ${source.moduleId}`}` : ''}
                              </div>
                            </div>
                            <Button size="sm" variant="secondary" disabled={locked || index === 0} title="上移" onClick={() => moveChooser(index, -1)}>↑</Button>
                            <Button size="sm" variant="secondary" disabled={locked || index === draft.choosers.length - 1} title="下移" onClick={() => moveChooser(index, 1)}>↓</Button>
                            <Button size="sm" variant="ghost" icon={<IconPencil size={16} />} disabled={locked} onClick={() => setDataSourceEditor({ index })}>编辑</Button>
                            <Button size="sm" variant="danger" icon={<IconTrash size={16} />} disabled={locked} onClick={() => removeChooser(index)}>删除</Button>
                          </div>
                        </div>
                      ))}
                    </>}
                    {section === 'layout' && <>
                      <div className="col-12">
                        <div className="alert alert-info">
                          表单的排布（顺序 / 占位 / 复合格 / 分节 / 页签 / 表单内隐藏）已归**模块级版式**：
                          同一张表被多个模块共用时，各模块可以有自己的形态，不再互相牵动。
                          字段级的那套排布设置（顺序 / 占位 / 复合格 / 页签 / 强制换行）已<strong>退役</strong>——请到表单设计里调整。
                          <div className="mt-2">
                            <Button
                              size="sm"
                              variant="primary"
                              disabled={contextModuleId == null}
                              onClick={() => {
                                if (contextModuleId == null) return
                                navigate(`/workbench/${contextModuleId}/view?design=1`)
                              }}
                            >
                              打开表单设计{contextModuleId == null ? '（需从工作台进入以带模块上下文）' : ''}
                            </Button>
                          </div>
                        </div>
                      </div>
                      <div className="col-12">
                        <label className="form-label">下拉选项（FORM_OPTIONS）</label>
                        <input className="form-control" value={draft.options ?? ''} disabled={locked} placeholder="如 O=外含税;I=内含税;N=不含税；有值即渲染下拉框；标签末尾加 ! 表示可见但不可选（未实现的档位）" onChange={event => setDraft({ ...draft, options: event.target.value || null })} />
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
                          <input className="form-check-input" type="checkbox" checked={draft.canCopy} disabled={locked} onChange={() => setDraft({ ...draft, canCopy: !draft.canCopy })} />
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
                    {section === 'history' && (
                      <div className="col-12">
                        {historyQuery.isPending && <LoadingState label="正在加载变更历史…" />}
                        {historyQuery.isError && <div className="alert alert-danger">变更历史加载失败。</div>}
                        {historyQuery.data && historyQuery.data.length === 0 && (
                          <div className="text-secondary">暂无字段变更记录。</div>
                        )}
                        <div className="d-flex flex-column gap-2">
                          {historyQuery.data?.slice(0, historyLimit).map(event => (
                            <div key={`${event.occurredAt}-${event.action}-${event.actorUserId}`} className="border rounded p-2">
                              <div className="d-flex justify-content-between align-items-center">
                                <strong>{event.action === 'CREATE' ? '新增' : event.action === 'UPDATE' ? '修改' : event.action === 'DELETE' ? '删除' : event.action}</strong>
                                <span className="text-secondary small">{event.occurredAt} · {event.actorName || event.actorUserId}</span>
                              </div>
                              {event.summary && <div className="small text-secondary mt-1">{event.summary}</div>}
                              {event.changes.length > 0 && (
                                <table className="table table-sm table-bordered mt-1 mb-0">
                                  <thead><tr><th>字段</th><th>原值</th><th>新值</th></tr></thead>
                                  <tbody>
                                    {event.changes.slice(0, 30).map(change => (
                                      <tr key={change.name}>
                                        <td className="text-nowrap">{change.name}</td>
                                        <td className="text-secondary text-break">{change.oldValue ?? ''}</td>
                                        <td className="text-break">{change.newValue ?? ''}</td>
                                      </tr>
                                    ))}
                                  </tbody>
                                </table>
                              )}
                              {event.changes.length > 30 && (
                                <div className="small text-secondary mt-1">字段级明细共 {event.changes.length} 条，其余 {event.changes.length - 30} 条省略。</div>
                              )}
                            </div>
                          ))}
                        </div>
                        {historyQuery.data && historyQuery.data.length > historyLimit && (
                          <Button size="sm" variant="secondary" className="mt-2" onClick={() => setHistoryLimit(limit => limit + 20)}>
                            加载更多（还有 {historyQuery.data.length - historyLimit} 条）
                          </Button>
                        )}
                      </div>
                    )}
                    </div>
                  </TabbedPanel>
                  {dataSourceEditor != null && (
                    <DataSourceEditorModal
                      open
                      initial={dataSourceEditor.index < draft.choosers.length
                        ? {
                            source: draft.choosers[dataSourceEditor.index],
                            filterRows: chooserUi[chooserUiKey(dataSourceEditor.index)]?.filterRows ?? [],
                            returnRows: chooserUi[chooserUiKey(dataSourceEditor.index)]?.returnRows ?? [],
                          }
                        : null}
                      currentTable={tableId}
                      endpoints={endpoints}
                      onClose={() => setDataSourceEditor(null)}
                      onSave={draftData => {
                        const index = dataSourceEditor.index
                        setDraft(prev => prev ? { ...prev, choosers: index < prev.choosers.length
                          ? prev.choosers.map((item, i) => i === index ? draftData.source : item)
                          : [...prev.choosers, draftData.source] } : prev)
                        setChooserUi(prev => ({ ...prev, [chooserUiKey(index)]: { filterRows: draftData.filterRows, returnRows: draftData.returnRows } }))
                      }}
                    />
                  )}
                  {save.isError && <div className="alert alert-danger mt-3 mb-0">{String((save.error as Error)?.message ?? '保存失败')}</div>}
                  {renderActions?.({
                    canSave,
                    saving: save.isPending,
                    onSave: () => draft && save.mutate(draft),
                    onCancel,
                  })}
                </div>
              ) : (
                <div className="text-secondary text-center py-5">未找到该字段的元数据。</div>
              )}
            </div>
          </div>
  )
}
