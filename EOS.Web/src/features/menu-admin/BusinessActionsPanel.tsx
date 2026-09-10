import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import {
  IconArrowDown,
  IconArrowUp,
  IconDeviceFloppy,
  IconEdit,
  IconPlus,
  IconRefresh,
  IconTrash,
} from '@tabler/icons-react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import type { MenuAdminModule } from './MenuAdminPage'

interface BusinessActionOp {
  opSeq: number
  targetTable: string
  targetField: string
  opCode: string
  sourceScope: string
  sourceTable?: string | null
  sourceField?: string | null
  sourceAgg?: string | null
  sourceConstant?: string | null
  sourceTerms?: string | null
  match?: string | null
  condition?: string | null
  remark?: string | null
}

export interface BusinessAction {
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode: string
  condition?: string | null
  params?: string | null
  reverse?: string | null
  remark?: string | null
  sourceRef?: string | null
  ops?: BusinessActionOp[]
}

export interface ValidationRule {
  seq: number
  stage: string
  validationKey: string
  enabled: boolean
  params?: string | null
  message?: string | null
  remark?: string | null
  sourceRef?: string | null
}

interface ModuleBusinessConfig {
  moduleId: number
  actions: BusinessAction[]
  validationRules: ValidationRule[]
}

/** 面板当前编辑中的配置草稿：由主页面汇总后随模块一起保存。 */
export interface ModuleBusinessConfigDraft {
  moduleId: number
  actions: BusinessAction[]
  validationRules: ValidationRule[]
  /** 当前编辑内容与已落库配置不一致（用于「已修改未保存」状态判断）。 */
  dirty: boolean
}

interface BusinessConfigCatalog {
  events: string[]
  failModes: string[]
  effectKeys: string[]
  opCodes: string[]
  sourceScopes: string[]
  sourceAggregates: string[]
  validationStages: string[]
  validationKeys: string[]
}

interface EffectParamSchema {
  effectKey: string
  rootKeys: string[]
}

interface BusinessConfigSchemas {
  effects: EffectParamSchema[]
  reverseKinds: string[]
}

interface BusinessActionsPanelProps {
  module: MenuAdminModule
  /** 草稿上报：配置装载完成或编辑变化时回调；null 表示当前没有可提交的草稿（未装载/无表模块）。 */
  onDraftChange?: (draft: ModuleBusinessConfigDraft | null) => void
}

type EditorState =
  | { kind: 'action'; index: number | null; value: BusinessAction }
  | { kind: 'op'; actionIndex: number; index: number | null; value: BusinessActionOp }
  | { kind: 'rule'; index: number | null; value: ValidationRule }

const actionKey = (action: BusinessAction) => `${action.eventCode}|${action.seq}`

const cloneOp = (op: BusinessActionOp): BusinessActionOp => ({ ...op })
const cloneAction = (action: BusinessAction): BusinessAction => ({
  ...action,
  ops: (action.ops ?? []).map(cloneOp),
})
const cloneRule = (rule: ValidationRule): ValidationRule => ({ ...rule })

const emptyAction = (eventCode: string, seq: number): BusinessAction => ({
  seq,
  eventCode,
  effectKey: 'field-accumulate',
  effectName: '',
  enabled: true,
  failMode: 'BLOCK',
  condition: null,
  params: null,
  reverse: null,
  remark: null,
  sourceRef: null,
  ops: [],
})

const emptyOp = (): BusinessActionOp => ({
  opSeq: 1,
  targetTable: '',
  targetField: '',
  opCode: 'ASSIGN',
  sourceScope: 'MASTER',
  sourceTable: null,
  sourceField: null,
  sourceAgg: null,
  sourceConstant: null,
  sourceTerms: null,
  match: null,
  condition: null,
  remark: null,
})

const emptyRule = (stage: string): ValidationRule => ({
  seq: 1,
  stage,
  validationKey: 'qty-not-exceed',
  enabled: true,
  params: '{}',
  message: '',
  remark: null,
  sourceRef: null,
})

export function BusinessActionsPanel({ module, onDraftChange }: BusinessActionsPanelProps) {
  const moduleId = module.M_IDX
  const hasTables = module.MASTER_TABLE != null || module.DETAIL_TABLE != null

  const configQuery = useQuery({
    queryKey: ['module-business-config', moduleId],
    queryFn: () => apiClient.get<ModuleBusinessConfig>(`/admin/module-business-config/${moduleId}`),
    enabled: moduleId > 0 && hasTables,
  })
  const catalogQuery = useQuery({
    queryKey: ['module-business-config-meta'],
    queryFn: () => apiClient.get<BusinessConfigCatalog>(`/admin/module-business-config/meta`),
    enabled: moduleId > 0 && hasTables,
  })
  const schemasQuery = useQuery({
    queryKey: ['module-business-config-schemas'],
    queryFn: () => apiClient.get<BusinessConfigSchemas>(`/admin/module-business-config/schemas`),
    enabled: moduleId > 0 && hasTables,
  })

  const [actions, setActions] = useState<BusinessAction[]>([])
  const [rules, setRules] = useState<ValidationRule[]>([])
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [editor, setEditor] = useState<EditorState | null>(null)
  // 已从服务端装载完成的模块编号：只有装载完成才向上报草稿，避免切换模块的瞬间
  // 用空配置覆盖主页面持有的草稿。
  const [loadedModuleId, setLoadedModuleId] = useState<number | null>(null)

  useEffect(() => {
    setActions([])
    setRules([])
    setSelectedKey(null)
    setEditor(null)
    setLoadedModuleId(null)
  }, [moduleId])

  useEffect(() => {
    if (!configQuery.data) return
    setActions((configQuery.data.actions ?? []).map(cloneAction))
    setRules((configQuery.data.validationRules ?? []).map(cloneRule))
    setLoadedModuleId(configQuery.data.moduleId)
  }, [configQuery.data])

  const configDirty = useMemo(() => {
    if (!configQuery.data) return false
    return JSON.stringify(actions) !== JSON.stringify((configQuery.data.actions ?? []).map(cloneAction))
      || JSON.stringify(rules) !== JSON.stringify((configQuery.data.validationRules ?? []).map(cloneRule))
  }, [actions, rules, configQuery.data])

  useEffect(() => {
    if (!onDraftChange) return
    if (loadedModuleId !== moduleId) {
      onDraftChange(null)
      return
    }
    onDraftChange({ moduleId, actions, validationRules: rules, dirty: configDirty })
  }, [actions, rules, configDirty, loadedModuleId, moduleId, onDraftChange])

  const sortedActions = useMemo(
    () => [...actions].sort((a, b) => a.eventCode.localeCompare(b.eventCode) || a.seq - b.seq),
    [actions],
  )
  const sortedRules = useMemo(
    () => [...rules].sort((a, b) => a.stage.localeCompare(b.stage) || a.seq - b.seq),
    [rules],
  )
  const selectedIndex = actions.findIndex((action) => actionKey(action) === selectedKey)
  const selectedAction = selectedIndex >= 0 ? actions[selectedIndex] : undefined
  const selectedOps = useMemo(
    () => [...(selectedAction?.ops ?? [])].sort((a, b) => a.opSeq - b.opSeq),
    [selectedAction],
  )

  const replaceAction = (index: number, value: BusinessAction) =>
    setActions((prev) => prev.map((item, i) => (i === index ? cloneAction(value) : item)))
  const replaceRule = (index: number, value: ValidationRule) =>
    setRules((prev) => prev.map((item, i) => (i === index ? cloneRule(value) : item)))

  const confirmEditor = () => {
    if (!editor) return
    if (editor.kind === 'action') {
      if (editor.index == null) {
        const value = cloneAction(editor.value)
        setActions((prev) => [...prev, value])
        setSelectedKey(actionKey(value))
      } else {
        replaceAction(editor.index, editor.value)
      }
    } else if (editor.kind === 'rule') {
      if (editor.index == null) setRules((prev) => [...prev, cloneRule(editor.value)])
      else replaceRule(editor.index, editor.value)
    } else {
      const actionIndex = editor.actionIndex
      const op = cloneOp(editor.value)
      setActions((prev) =>
        prev.map((action, i) => {
          if (i !== actionIndex) return action
          const ops = [...(action.ops ?? [])]
          if (editor.index == null) ops.push(op)
          else ops[editor.index] = op
          return { ...action, ops }
        }),
      )
    }
    setEditor(null)
  }

  const openCreateAction = () => {
    const eventCode = catalogQuery.data?.events.includes('APPROVE_EFFECT') ? 'APPROVE_EFFECT' : (catalogQuery.data?.events[0] ?? 'SAVE')
    const seq = (actions.filter((item) => item.eventCode === eventCode).reduce((max, item) => Math.max(max, item.seq), 0)) + 1
    setEditor({ kind: 'action', index: null, value: emptyAction(eventCode, seq) })
  }
  const openCreateRule = () => {
    const stage = catalogQuery.data?.validationStages.includes('SAVE') ? 'SAVE' : (catalogQuery.data?.validationStages[0] ?? 'SAVE')
    const seq = (rules.filter((item) => item.stage === stage).reduce((max, item) => Math.max(max, item.seq), 0)) + 1
    setEditor({ kind: 'rule', index: null, value: { ...emptyRule(stage), seq } })
  }
  const openCreateOp = () => {
    if (selectedIndex < 0) return
    const ops = selectedAction?.ops ?? []
    const opSeq = ops.reduce((max, item) => Math.max(max, item.opSeq), 0) + 1
    setEditor({ kind: 'op', actionIndex: selectedIndex, index: null, value: { ...emptyOp(), opSeq } })
  }
  const openEditOp = (index: number) => {
    if (selectedIndex < 0 || !selectedAction?.ops) return
    setEditor({ kind: 'op', actionIndex: selectedIndex, index, value: cloneOp(selectedAction.ops[index]) })
  }
  const deleteSelectedAction = () => {
    if (selectedIndex < 0) return
    setActions((prev) => prev.filter((_, i) => i !== selectedIndex))
    setSelectedKey(null)
  }
  const editOp = (op: BusinessActionOp) => {
    const list = selectedAction?.ops ?? []
    const index = list.findIndex((item) => item.opSeq === op.opSeq)
    if (index >= 0) openEditOp(index)
  }
  const deleteOp = (op: BusinessActionOp) => {
    if (selectedIndex < 0) return
    setActions((prev) =>
      prev.map((action, i) =>
        i === selectedIndex ? { ...action, ops: (action.ops ?? []).filter((item) => item.opSeq !== op.opSeq) } : action,
      ),
    )
  }
  const editRule = (rule: ValidationRule) => {
    const index = rules.indexOf(rule)
    if (index >= 0) setEditor({ kind: 'rule', index, value: cloneRule(rule) })
  }
  const deleteRule = (rule: ValidationRule) =>
    setRules((prev) => prev.filter((item) => item !== rule))

  const moveSelectedAction = (delta: number) => {
    if (selectedIndex < 0) return
    const current = actions[selectedIndex]
    const peers = actions
      .map((item, index) => ({ item, index }))
      .filter((entry) => entry.item.eventCode === current.eventCode)
      .sort((a, b) => a.item.seq - b.item.seq)
    const position = peers.findIndex((entry) => entry.index === selectedIndex)
    const target = peers[position + delta]
    if (!target) return
    setActions((prev) =>
      prev.map((item, i) => {
        if (i === selectedIndex) return { ...item, seq: target.item.seq }
        if (i === target.index) return { ...item, seq: current.seq }
        return item
      }),
    )
  }

  if (!hasTables || moduleId <= 0) {
    return (
      <div className="p-3 text-secondary">
        两表皆空的模块不参与行为规则。请先在「基础 / 主表 / 子表」配置操作主表/副表并保存模块后再配置。
      </div>
    )
  }

  const ready = configQuery.isSuccess && catalogQuery.isSuccess && schemasQuery.isSuccess

  return (
    <div className="d-flex flex-column gap-3">
      <div className="d-flex align-items-center justify-content-between flex-wrap gap-2">
        <div>
          <strong>{tableTitle(module.MASTER_TABLE, module.MASTER_TABLE_DESC)} / {tableTitle(module.DETAIL_TABLE, module.DETAIL_TABLE_DESC)}</strong>
        </div>
        <div className="d-flex gap-2">
          <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void configQuery.refetch()}>
            重新加载
          </Button>
        </div>
      </div>

      {configQuery.isLoading || catalogQuery.isLoading || schemasQuery.isLoading ? <LoadingState /> : null}
      {configQuery.isError ? (
        <ErrorState
          message={`加载失败：${describeApiError(configQuery.error, '无法读取模块业务配置。')}`}
          onRetry={() => void configQuery.refetch()}
        />
      ) : null}
      {catalogQuery.isError ? (
        <ErrorState
          message={`目录加载失败：${describeApiError(catalogQuery.error, '无法读取配置目录。')}`}
          onRetry={() => void catalogQuery.refetch()}
        />
      ) : null}
      {schemasQuery.isError ? (
        <ErrorState
          message={`Schema 目录加载失败：${describeApiError(schemasQuery.error, '无法读取效果 Schema。')}`}
          onRetry={() => void schemasQuery.refetch()}
        />
      ) : null}

      {ready ? (
        <>
          <ActionSection
            catalog={catalogQuery.data!}
            actions={sortedActions}
            selectedKey={selectedKey}
            onSelect={setSelectedKey}
            onCreate={openCreateAction}
            onEdit={() => {
              if (selectedIndex >= 0) setEditor({ kind: 'action', index: selectedIndex, value: cloneAction(selectedAction!) })
            }}
            onDelete={deleteSelectedAction}
            onMoveUp={() => moveSelectedAction(-1)}
            onMoveDown={() => moveSelectedAction(1)}
            canEdit={selectedIndex >= 0}
          />
          <OpSection
            ops={selectedOps}
            onCreate={openCreateOp}
            onEdit={editOp}
            onDelete={deleteOp}
            canEdit={selectedIndex >= 0}
          />
          <RuleSection
            rules={sortedRules}
            onCreate={openCreateRule}
            onEdit={editRule}
            onDelete={deleteRule}
          />
          <div className="text-secondary small">
            结构化 JSON 字段（条件/参数/反向/定位键）当前以文本编辑，Schema 化表单随效果注册接入后替换；
            保存即服务端校验（目录值/顺序/JSON/物理表列），校验失败不会落库。
          </div>
        </>
      ) : null}

      {editor ? (
        <EditorModal
          editor={editor}
          catalog={catalogQuery.data!}
          schemas={schemasQuery.data!}
          onCancel={() => setEditor(null)}
          onConfirm={confirmEditor}
          onActionChange={(value) => setEditor((prev) => (prev?.kind === 'action' ? { ...prev, value } : prev))}
          onOpChange={(value) => setEditor((prev) => (prev?.kind === 'op' ? { ...prev, value } : prev))}
          onRuleChange={(value) => setEditor((prev) => (prev?.kind === 'rule' ? { ...prev, value } : prev))}
        />
      ) : null}
    </div>
  )
}

function ActionSection({
  catalog,
  actions,
  selectedKey,
  onSelect,
  onCreate,
  onEdit,
  onDelete,
  onMoveUp,
  onMoveDown,
  canEdit,
}: {
  catalog: BusinessConfigCatalog
  actions: BusinessAction[]
  selectedKey: string | null
  onSelect: (key: string | null) => void
  onCreate: () => void
  onEdit: () => void
  onDelete: () => void
  onMoveUp: () => void
  onMoveDown: () => void
  canEdit: boolean
}) {
  const columns = useMemo<ColumnDef<BusinessAction, unknown>[]>(() => [
    { accessorKey: 'eventCode', header: '事件', cell: (info) => eventLabel(String(info.getValue()), catalog) },
    { accessorKey: 'seq', header: '顺序', meta: { minWidth: 64 } },
    { accessorKey: 'effectKey', header: '效果键', cell: (info) => <code>{String(info.getValue())}</code> },
    { accessorKey: 'effectName', header: '名称', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    {
      accessorKey: 'enabled',
      header: '启用',
      cell: (info) => (info.getValue() ? '是' : '否'),
      meta: { minWidth: 60 },
    },
    { accessorKey: 'failMode', header: '失败模式', meta: { minWidth: 84 } },
    { accessorKey: 'remark', header: '说明', cell: (info) => emptyText(String(info.getValue() ?? '')) },
  ], [catalog])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1">
        <h6 className="mb-0">业务动作（{actions.length}）</h6>
        <div className="d-flex gap-1">
          <Button size="sm" icon={<IconPlus size={16} />} onClick={onCreate}>新增</Button>
          <Button size="sm" icon={<IconArrowUp size={16} />} onClick={onMoveUp} disabled={!canEdit} aria-label="上移">上移</Button>
          <Button size="sm" icon={<IconArrowDown size={16} />} onClick={onMoveDown} disabled={!canEdit} aria-label="下移">下移</Button>
          <Button size="sm" icon={<IconEdit size={16} />} onClick={onEdit} disabled={!canEdit}>编辑</Button>
          <Button size="sm" variant="danger" icon={<IconTrash size={16} />} onClick={onDelete} disabled={!canEdit}>删除</Button>
        </div>
      </div>
      <ErpTable
        columns={columns}
        data={actions}
        getRowId={(row) => actionKey(row)}
        activeRowId={selectedKey ?? undefined}
        onRowClick={(row) => onSelect(actionKey(row))}
        rowClickSingleSelect
        clientSideSorting
        copyable={false}
        empty={<div className="p-3 text-secondary">尚未配置业务动作，点「新增」开始。</div>}
      />
    </section>
  )
}

function OpSection({
  ops,
  onCreate,
  onEdit,
  onDelete,
  canEdit,
}: {
  ops: BusinessActionOp[]
  onCreate: () => void
  onEdit: (op: BusinessActionOp) => void
  onDelete: (op: BusinessActionOp) => void
  canEdit: boolean
}) {
  const columns = useMemo<ColumnDef<BusinessActionOp, unknown>[]>(() => [
    { accessorKey: 'opSeq', header: '序', meta: { minWidth: 48 } },
    { accessorKey: 'targetTable', header: '目标表' },
    { accessorKey: 'targetField', header: '目标字段' },
    { accessorKey: 'opCode', header: '运算' },
    { accessorKey: 'sourceScope', header: '源范围' },
    { accessorKey: 'sourceTable', header: '源表', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    { accessorKey: 'sourceField', header: '源字段', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    { accessorKey: 'sourceAgg', header: '聚合', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    { accessorKey: 'sourceConstant', header: '常量', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    {
      id: 'actions',
      header: '',
      meta: { minWidth: 110 },
      cell: ({ row }) => (
        <div className="d-flex gap-1 justify-content-end">
          <Button size="sm" variant="ghost" onClick={() => onEdit(row.original)}>编辑</Button>
          <Button size="sm" variant="ghost" onClick={() => onDelete(row.original)}>删除</Button>
        </div>
      ),
    },
  ], [onEdit, onDelete])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1">
        <h6 className="mb-0">选中动作的公式行（{ops.length}）</h6>
        <Button size="sm" icon={<IconPlus size={16} />} onClick={onCreate} disabled={!canEdit}>新增公式行</Button>
      </div>
      <ErpTable
        columns={columns}
        data={ops}
        getRowId={(row) => `${row.opSeq}`}
        clientSideSorting
        copyable={false}
        empty={<div className="p-3 text-secondary">先在左侧选择动作；服务型效果通常无公式行（参数在动作的 params 中）。</div>}
      />
    </section>
  )
}

function RuleSection({
  rules,
  onCreate,
  onEdit,
  onDelete,
}: {
  rules: ValidationRule[]
  onCreate: () => void
  onEdit: (rule: ValidationRule) => void
  onDelete: (rule: ValidationRule) => void
}) {
  const columns = useMemo<ColumnDef<ValidationRule, unknown>[]>(() => [
    { accessorKey: 'stage', header: '阶段' },
    { accessorKey: 'seq', header: '顺序', meta: { minWidth: 64 } },
    { accessorKey: 'validationKey', header: '模板键', cell: (info) => <code>{String(info.getValue())}</code> },
    {
      accessorKey: 'enabled',
      header: '启用',
      cell: (info) => (info.getValue() ? '是' : '否'),
      meta: { minWidth: 60 },
    },
    { accessorKey: 'message', header: '失败文案', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    { accessorKey: 'sourceRef', header: '溯源', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    {
      id: 'actions',
      header: '',
      meta: { minWidth: 110 },
      cell: ({ row }) => (
        <div className="d-flex gap-1 justify-content-end">
          <Button size="sm" variant="ghost" onClick={() => onEdit(row.original)}>编辑</Button>
          <Button size="sm" variant="ghost" onClick={() => onDelete(row.original)}>删除</Button>
        </div>
      ),
    },
  ], [onEdit, onDelete])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1">
        <h6 className="mb-0">校验规则（{rules.length}）</h6>
        <Button size="sm" icon={<IconPlus size={16} />} onClick={onCreate}>新增规则</Button>
      </div>
      <ErpTable
        columns={columns}
        data={rules}
        getRowId={(row) => `${row.stage}|${row.seq}`}
        clientSideSorting
        copyable={false}
        empty={<div className="p-3 text-secondary">尚未配置模块校验规则（核心默认校验为代码内建，不在此表）。</div>}
      />
    </section>
  )
}

function EditorModal({
  editor,
  catalog,
  schemas,
  onActionChange,
  onOpChange,
  onRuleChange,
  onCancel,
  onConfirm,
}: {
  editor: EditorState
  catalog: BusinessConfigCatalog
  schemas: BusinessConfigSchemas
  onActionChange: (value: BusinessAction) => void
  onOpChange: (value: BusinessActionOp) => void
  onRuleChange: (value: ValidationRule) => void
  onCancel: () => void
  onConfirm: () => void
}) {
  const title = editor.kind === 'action' ? (editor.index == null ? '新增业务动作' : '编辑业务动作')
    : editor.kind === 'op' ? (editor.index == null ? '新增公式行' : '编辑公式行')
    : (editor.index == null ? '新增校验规则' : '编辑校验规则')

  return (
    <Modal
      title={title}
      onClose={onCancel}
      size="lg"
      scrollable
      footer={
        <div className="d-flex gap-2 justify-content-end">
          <Button variant="secondary" onClick={onCancel}>取消</Button>
          <Button variant="primary" icon={<IconDeviceFloppy size={16} />} onClick={onConfirm}>保存</Button>
        </div>
      }
    >
      {editor.kind === 'action' ? (
        <ActionForm value={editor.value} catalog={catalog} schemas={schemas} onChange={onActionChange} />
      ) : editor.kind === 'op' ? (
        <OpForm value={editor.value} catalog={catalog} onChange={onOpChange} />
      ) : (
        <RuleForm value={editor.value} catalog={catalog} onChange={onRuleChange} />
      )}
    </Modal>
  )
}

function ActionForm({
  value,
  catalog,
  schemas,
  onChange,
}: {
  value: BusinessAction
  catalog: BusinessConfigCatalog
  schemas: BusinessConfigSchemas
  onChange: (value: BusinessAction) => void
}) {
  const set = <K extends keyof BusinessAction>(key: K, next: BusinessAction[K]) => onChange({ ...value, [key]: next })
  const effectSchema = (schemas.effects ?? []).find((item) => item.effectKey === value.effectKey)
  return (
    <div>
      <div className="row g-2">
        <Field label="事件" className="col-4">
          <select className="form-select form-select-sm" value={value.eventCode} onChange={(e) => set('eventCode', e.target.value)}>
            {catalog.events.map((item) => <option key={item} value={item}>{eventLabel(item, catalog)}</option>)}
          </select>
        </Field>
        <Field label="顺序（事件内）" className="col-2">
          <input type="number" min={1} className="form-control form-control-sm" value={value.seq}
            onChange={(e) => set('seq', Number(e.target.value))} />
        </Field>
        <Field label="效果键" className="col-6">
          <select className="form-select form-select-sm" value={value.effectKey} onChange={(e) => set('effectKey', e.target.value)}>
            {catalog.effectKeys.map((item) => <option key={item} value={item}>{item}</option>)}
          </select>
        </Field>
        <Field label="名称" className="col-8">
          <input className="form-control form-control-sm" value={value.effectName ?? ''}
            onChange={(e) => set('effectName', e.target.value || null)} />
        </Field>
        <Field label="失败模式" className="col-4">
          <select className="form-select form-select-sm" value={value.failMode} onChange={(e) => set('failMode', e.target.value)}>
            {catalog.failModes.map((item) => <option key={item} value={item}>{item}</option>)}
          </select>
        </Field>
        <Field label="启用" className="col-12">
          <div className="form-check">
            <input id="action-enabled" className="form-check-input" type="checkbox" checked={value.enabled}
              onChange={(e) => set('enabled', e.target.checked)} />
            <label className="form-check-label" htmlFor="action-enabled">启用该动作</label>
          </div>
        </Field>
        <Field label="说明" className="col-6">
          <input className="form-control form-control-sm" value={value.remark ?? ''}
            onChange={(e) => set('remark', e.target.value || null)} />
        </Field>
        <Field label="溯源（可选）" className="col-6">
          <input className="form-control form-control-sm" value={value.sourceRef ?? ''}
            onChange={(e) => set('sourceRef', e.target.value || null)} />
        </Field>
        <JsonField label="条件（结构化 JSON，可选）" value={value.condition ?? ''}
          onChange={(text) => set('condition', text || null)} />
        <Field label="参数（按效果 Schema 编辑）" className="col-12">
          <ParamsEditor
            effectKey={value.effectKey}
            schema={effectSchema}
            json={value.params ?? null}
            onChange={(json) => set('params', json || null)}
          />
        </Field>
        <Field label="反向（解批语义）" className="col-12">
          <ReverseEditor
            kinds={schemas.reverseKinds}
            json={value.reverse ?? null}
            onChange={(json) => set('reverse', json || null)}
          />
        </Field>
      </div>
    </div>
  )
}

function OpForm({
  value,
  catalog,
  onChange,
}: {
  value: BusinessActionOp
  catalog: BusinessConfigCatalog
  onChange: (value: BusinessActionOp) => void
}) {
  const set = <K extends keyof BusinessActionOp>(key: K, next: BusinessActionOp[K]) => onChange({ ...value, [key]: next })
  const isConstant = value.sourceScope === 'CONSTANT'
  const isTable = value.sourceScope === 'TABLE'
  return (
    <div className="row g-2">
      <Field label="顺序" className="col-2">
        <input type="number" min={1} className="form-control form-control-sm" value={value.opSeq}
          onChange={(e) => set('opSeq', Number(e.target.value))} />
      </Field>
      <Field label="目标表" className="col-5">
        <input className="form-control form-control-sm font-monospace" value={value.targetTable}
          onChange={(e) => set('targetTable', e.target.value.toUpperCase())} placeholder="如 PUR_PURCHASE_D" />
      </Field>
      <Field label="目标字段" className="col-5">
        <input className="form-control form-control-sm font-monospace" value={value.targetField}
          onChange={(e) => set('targetField', e.target.value.toUpperCase())} />
      </Field>
      <Field label="运算" className="col-4">
        <select className="form-select form-select-sm" value={value.opCode} onChange={(e) => set('opCode', e.target.value)}>
          {catalog.opCodes.map((item) => <option key={item} value={item}>{item}</option>)}
        </select>
      </Field>
      <Field label="源范围" className="col-4">
        <select className="form-select form-select-sm" value={value.sourceScope}
          onChange={(e) => set('sourceScope', e.target.value)}>
          {catalog.sourceScopes.map((item) => <option key={item} value={item}>{item}</option>)}
        </select>
      </Field>
      <Field label="源聚合（空=单值）" className="col-4">
        <select className="form-select form-select-sm" value={value.sourceAgg ?? ''}
          onChange={(e) => set('sourceAgg', e.target.value || null)}>
          <option value="">（单值）</option>
          {catalog.sourceAggregates.map((item) => <option key={item} value={item}>{item}</option>)}
        </select>
      </Field>
      {isTable ? (
        <Field label="源表（已登记上下文表）" className="col-6">
          <input className="form-control form-control-sm font-monospace" value={value.sourceTable ?? ''}
            onChange={(e) => set('sourceTable', e.target.value.toUpperCase() || null)} />
        </Field>
      ) : null}
      {isConstant ? (
        <Field label="常量值" className="col-6">
          <input className="form-control form-control-sm" value={value.sourceConstant ?? ''}
            onChange={(e) => set('sourceConstant', e.target.value)} />
        </Field>
      ) : (
        <>
          <Field label="源字段（与源加减项二选一）" className="col-6">
            <input className="form-control form-control-sm font-monospace" value={value.sourceField ?? ''}
              onChange={(e) => set('sourceField', e.target.value.toUpperCase() || null)} />
          </Field>
          <Field label="源加减项（结构化 JSON，可选）" className="col-12">
            <JsonArea value={value.sourceTerms ?? ''} onChange={(text) => set('sourceTerms', text || null)} />
          </Field>
        </>
      )}
      <JsonField label="定位键（结构化 JSON，可选）" value={value.match ?? ''}
        onChange={(text) => set('match', text || null)} />
      <JsonField label="条件（结构化 JSON，可选）" value={value.condition ?? ''}
        onChange={(text) => set('condition', text || null)} />
      <Field label="说明" className="col-12">
        <input className="form-control form-control-sm" value={value.remark ?? ''}
          onChange={(e) => set('remark', e.target.value || null)} />
      </Field>
    </div>
  )
}

function RuleForm({
  value,
  catalog,
  onChange,
}: {
  value: ValidationRule
  catalog: BusinessConfigCatalog
  onChange: (value: ValidationRule) => void
}) {
  const set = <K extends keyof ValidationRule>(key: K, next: ValidationRule[K]) => onChange({ ...value, [key]: next })
  return (
    <div className="row g-2">
      <Field label="阶段" className="col-4">
        <select className="form-select form-select-sm" value={value.stage} onChange={(e) => set('stage', e.target.value)}>
          {catalog.validationStages.map((item) => <option key={item} value={item}>{item}</option>)}
        </select>
      </Field>
      <Field label="顺序（阶段内）" className="col-2">
        <input type="number" min={1} className="form-control form-control-sm" value={value.seq}
          onChange={(e) => set('seq', Number(e.target.value))} />
      </Field>
      <Field label="模板键" className="col-6">
        <select className="form-select form-select-sm" value={value.validationKey}
          onChange={(e) => set('validationKey', e.target.value)}>
          {catalog.validationKeys.map((item) => <option key={item} value={item}>{item}</option>)}
        </select>
      </Field>
      <Field label="启用" className="col-12">
        <div className="form-check">
          <input id="rule-enabled" className="form-check-input" type="checkbox" checked={value.enabled}
            onChange={(e) => set('enabled', e.target.checked)} />
          <label className="form-check-label" htmlFor="rule-enabled">启用该规则</label>
        </div>
      </Field>
      <JsonField label="参数（闭式 JSON）" value={value.params ?? ''}
        onChange={(text) => set('params', text || null)} />
      <Field label="失败文案（可选）" className="col-6">
        <input className="form-control form-control-sm" value={value.message ?? ''}
          onChange={(e) => set('message', e.target.value || null)} />
      </Field>
      <Field label="溯源（可选）" className="col-6">
        <input className="form-control form-control-sm" value={value.sourceRef ?? ''}
          onChange={(e) => set('sourceRef', e.target.value || null)} />
      </Field>
      <Field label="说明（可选）" className="col-12">
        <input className="form-control form-control-sm" value={value.remark ?? ''}
          onChange={(e) => set('remark', e.target.value || null)} />
      </Field>
    </div>
  )
}

function ParamsEditor({
  effectKey,
  schema,
  json,
  onChange,
}: {
  effectKey: string
  schema: EffectParamSchema | undefined
  json: string | null
  onChange: (json: string | null) => void
}) {
  const parsed = useMemo(() => {
    if (!json) return {}
    try {
      const value = JSON.parse(json)
      return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
    } catch {
      return {}
    }
  }, [json])
  const rootKeys = schema?.rootKeys ?? []
  const unknownKeys = Object.keys(parsed).filter((key) => !rootKeys.includes(key))

  const updateRoot = (key: string, next: unknown) => {
    const nextObject: Record<string, unknown> = { ...parsed }
    if (next === undefined || next === '') delete nextObject[key]
    else nextObject[key] = next
    onChange(Object.keys(nextObject).length > 0 ? JSON.stringify(nextObject) : null)
  }

  return (
    <div>
      <div className="small text-secondary mb-1">
        效果键：<code>{effectKey}</code>
        {schema ? `（根键 ${rootKeys.join(' / ')}）` : '（未登记 Schema，禁止携带参数）'}
      </div>
      {unknownKeys.length > 0 ? (
        <div className="alert alert-warning py-1 small mb-2">
          存在 Schema 外根键（保存将被拒绝）：{unknownKeys.join('、')}
        </div>
      ) : null}
      <div className="d-flex flex-column gap-2">
        {rootKeys.map((key) => (
          <div key={key} className="row g-1 align-items-center">
            <div className="col-3">
              <label className="form-label small mb-0 text-nowrap">{key}</label>
            </div>
            <div className="col-9">
              <RootValueEditor value={parsed[key]} onChange={(next) => updateRoot(key, next)} />
            </div>
          </div>
        ))}
        {rootKeys.length === 0 ? <div className="text-secondary small">该效果不允许配置参数。</div> : null}
      </div>
    </div>
  )
}

function RootValueEditor({
  value,
  onChange,
}: {
  value: unknown
  onChange: (next: unknown) => void
}) {
  if (typeof value === 'boolean') {
    return (
      <div className="form-check">
        <input id="param-bool" className="form-check-input" type="checkbox" checked={value}
          onChange={(event) => onChange(event.target.checked)} />
        <label className="form-check-label small" htmlFor="param-bool">true</label>
      </div>
    )
  }
  if (typeof value === 'number') {
    return (
      <input type="number" className="form-control form-control-sm" value={value}
        onChange={(event) => onChange(event.target.value === '' ? undefined : Number(event.target.value))} />
    )
  }
  if (value !== null && typeof value === 'object') {
    return <JsonTextArea value={JSON.stringify(value)} onChange={(text) => {
      if (text === '') return onChange(undefined)
      try { onChange(JSON.parse(text)) } catch { /* 保持原值，blur 校验 */ }
    }} placeholder="{}" />
  }
  return (
    <input
      className="form-control form-control-sm font-monospace"
      value={typeof value === 'string' ? value : ''}
      placeholder="留空移除该键"
      onChange={(event) => onChange(event.target.value === '' ? undefined : event.target.value)}
    />
  )
}

function ReverseEditor({
  kinds,
  json,
  onChange,
}: {
  kinds: string[]
  json: string | null
  onChange: (json: string | null) => void
}) {
  const parsed = useMemo(() => {
    if (!json) return { kind: 'auto-reverse' as string, note: '' as string }
    try {
      const value = JSON.parse(json)
      return {
        kind: typeof value?.kind === 'string' ? value.kind : 'auto-reverse',
        note: typeof value?.note === 'string' ? value.note : '',
      }
    } catch {
      return { kind: 'auto-reverse', note: '' }
    }
  }, [json])
  const options = (kinds ?? []).length > 0 ? kinds : ['auto-reverse', 'no-reverse', 'recompute', 'reverse-flow', 'snapshot']
  const commit = (kind: string, note: string) => {
    if (!kind) {
      onChange(null)
      return
    }
    onChange(JSON.stringify(note ? { kind, note } : { kind }))
  }
  return (
    <div className="row g-2">
      <div className="col-4">
        <select className="form-select form-select-sm" value={parsed.kind}
          onChange={(event) => commit(event.target.value, parsed.note)}>
          <option value="">（无反向配置）</option>
          {options.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
        </select>
      </div>
      <div className="col-8">
        <input className="form-control form-control-sm" value={parsed.note} placeholder="反向说明（可选）"
          onChange={(event) => commit(parsed.kind, event.target.value)} />
      </div>
    </div>
  )
}

function JsonTextArea({
  value,
  onChange,
  placeholder,
}: {
  value: string
  onChange: (text: string) => void
  placeholder?: string
}) {
  const [invalid, setInvalid] = useState(false)
  const [text, setText] = useState(value)
  useEffect(() => setText(value), [value])
  return (
    <textarea
      className={`form-control form-control-sm font-monospace ${invalid ? 'is-invalid' : ''}`}
      rows={3}
      spellCheck={false}
      placeholder={placeholder}
      value={text}
      onChange={(event) => {
        const next = event.target.value
        setText(next)
        if (next === '') {
          setInvalid(false)
          onChange('')
          return
        }
        try {
          JSON.parse(next)
          setInvalid(false)
          onChange(next)
        } catch {
          setInvalid(true)
        }
      }}
    />
  )
}

function Field({
  label,
  className = 'col-12',
  children,
}: {
  label: string
  className?: string
  children: ReactNode
}) {
  return (
    <div className={className}>
      <label className="form-label small mb-1">{label}</label>
      {children}
    </div>
  )
}

function JsonField({
  label,
  value,
  onChange,
}: {
  label: string
  value: string
  onChange: (value: string) => void
}) {
  return (
    <Field label={label} className="col-12">
      <JsonArea value={value} onChange={onChange} />
    </Field>
  )
}

function JsonArea({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  return (
    <textarea
      className="form-control form-control-sm font-monospace"
      rows={3}
      spellCheck={false}
      value={value}
      onChange={(event) => onChange(event.target.value)}
      placeholder="{}"
    />
  )
}

function eventLabel(event: string, catalog: BusinessConfigCatalog): string {
  const labels: Record<string, string> = {
    SAVE: '保存后',
    APPROVE_EFFECT: '批核生效',
    DEAPPROVE: '解批',
    ENDCASE: '结案（占位）',
    UNENDCASE: '取消结案（占位）',
  }
  return catalog.events.includes(event) ? `${labels[event] ?? event}（${event}）` : `${event}（目录外）`
}

function emptyText(text: string): string {
  return text.trim() === '' ? '—' : text
}

/** 表标识：描述(表名)；缺描述时退化为表名，表名为空显示占位符。 */
function tableTitle(table: string | null, description?: string | null): string {
  const tableId = (table ?? '').trim()
  if (tableId === '') return '—'
  return `${(description ?? '').trim() || tableId}(${tableId})`
}
