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
}

export interface SetupLookup {
  value: string
  label: string
}

export type FieldSection = 'display' | 'validation' | 'security' | 'layout' | 'advanced'

export interface FieldEditorEndpoints {
  load: () => Promise<FieldMeta | null>
  save: (input: FieldInput, tableId: string, fieldId: string, original: FieldInput | null) => Promise<void>
  tables?: () => Promise<SetupLookup[]>
  modules?: () => Promise<SetupLookup[]>
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

export function FieldEditorModal({ open, mode, tableId, fieldKey, title, endpoints, onClose, onSaved }: FieldEditorModalProps) {
  const [draft, setDraft] = useState<FieldMeta | null>(null)
  const [original, setOriginal] = useState<FieldMeta | null>(null)
  const [section, setSection] = useState<FieldSection>('display')

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

  const isNew = mode === 'new'
  const dialogTitle = title ?? (isNew ? '新增字段' : '字段管理')

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
                        {isNew ? (
                          <input className="form-control" value={draft.dataType} onChange={event => setDraft({ ...draft, dataType: event.target.value })} />
                        ) : (
                          <input className="form-control" value={draft.dataType} disabled />
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
                        <input className="form-control" value={draft.regex ?? ''} onChange={event => setDraft({ ...draft, regex: event.target.value })} />
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
                        <div className="alert alert-warning">高级表达式会影响数据读取和单据处理，请仅在充分验证后修改。当前阶段只读展示虚拟表达式、转换函数和数据源 SQL，避免未经解析的表达式直接进入运行时。</div>
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
                      </div>
                      <div className="col-12">
                        <label className="form-label">虚拟表达式</label>
                        <textarea className="form-control" rows={3} value={draft.virtualExpression ?? ''} disabled />
                      </div>
                      <div className="col-12">
                        <label className="form-label">转换函数</label>
                        <input className="form-control" value={draft.convertFunction ?? ''} disabled />
                      </div>
                      <div className="col-12">
                        <label className="form-label">数据源 SQL</label>
                        <textarea className="form-control font-monospace" rows={3} value={draft.dataSourceSql ?? ''} disabled />
                      </div>
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
                    <Button variant="primary" loading={save.isPending} disabled={!draft.label.trim() || draft.width < 40 || draft.width > 300 || (isNew && (!draft.key.trim() || !draft.tableId.trim()))} onClick={() => save.mutate(draft)}>{isNew ? '新增字段' : '保存字段设置'}</Button>
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
