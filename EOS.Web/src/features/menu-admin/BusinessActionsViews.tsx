import { useMemo, type ReactNode } from 'react'
import type { ColumnDef } from '@tanstack/react-table'
import {
  IconArrowDown,
  IconArrowUp,
  IconCopy,
  IconEdit,
  IconPlus,
  IconTrash,
} from '@tabler/icons-react'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { BusinessActionSheet } from './BusinessActionSheet'
import { MANUAL_EVENT } from './documentActionConfig'
import {
  actionKey,
  eventLabel,
  formatCondition,
  formatMatch,
  formatOpSentence,
  labelWithCode,
  placementLabel,
  ruleKey,
  summarizeAction,
  withTargetTable,
  type BusinessNameLookup,
} from './businessActionText'
import type {
  BusinessAction,
  BusinessActionOp,
  BusinessConfigCatalog,
  DocumentActionAuthorizationEntry,
  DocumentActionCatalogEntry,
  LabelLookups,
  ValidationRule,
} from './BusinessActionsPanel'

/**
 * 行为配置的三个页签视图：只做展示与回调转发，状态与查询都留在容器里
 * （容器不随页签切换卸载，否则未保存编辑会被装载副作用重置）。
 */

function emptyText(text: string): string {
  return text.trim() === '' ? '—' : text
}

/** 结构化 JSON 列的人话渲染；解析不出内容时回落到原文（半成品文本不算错误）。 */
function readableText(text: string | null | undefined, raw: string): ReactNode {
  if (text == null || text === '') return '—'
  return <span title={raw}>{text}</span>
}

/** 业务动作页签：系统触发（或单据事件顺带执行）的效果链。 */
export function ActionsView({
  catalog,
  labels,
  names,
  actions,
  documentActionLookup,
  selectedKey,
  onSelect,
  onCreate,
  onEdit,
  onDuplicate,
  onClone,
  onDelete,
  onMoveUp,
  onMoveDown,
  canEdit,
}: {
  catalog: BusinessConfigCatalog
  labels: LabelLookups
  names: BusinessNameLookup
  actions: BusinessAction[]
  documentActionLookup: (code: string | null | undefined) => string
  selectedKey: string | null
  onSelect: (key: string | null) => void
  onCreate: () => void
  onEdit: () => void
  onDuplicate: () => void
  onClone: () => void
  onDelete: () => void
  onMoveUp: () => void
  onMoveDown: () => void
  canEdit: boolean
}) {
  const columns = useMemo<ColumnDef<BusinessAction, unknown>[]>(() => [
    { accessorKey: 'eventCode', header: '事件', cell: (info) => eventLabel(String(info.getValue()), catalog, labels) },
    { accessorKey: 'seq', header: '顺序', meta: { minWidth: 64 } },
    {
      accessorKey: 'effectKey',
      header: '效果',
      cell: (info) => (
        <span title={String(info.getValue())}>
          {labelWithCode(
            info.row.original.eventCode === MANUAL_EVENT ? documentActionLookup : labels.effectKeys,
            String(info.getValue()),
          )}
        </span>
      ),
    },
    {
      accessorKey: 'effectName',
      header: '名称',
      cell: (info) => emptyText(String(info.row.original.label ?? info.getValue() ?? '')),
    },
    {
      accessorKey: 'enabled',
      header: '启用',
      cell: (info) => (info.getValue() ? '是' : '否'),
      meta: { minWidth: 60 },
    },
    {
      accessorKey: 'failMode',
      header: '失败模式',
      cell: (info) => labelWithCode(labels.failModes, String(info.getValue())),
      meta: { minWidth: 130 },
    },
    {
      id: 'impact',
      header: '影响（做什么）',
      meta: { minWidth: 320 },
      cell: ({ row }) => {
        const text = summarizeAction(row.original, names)
        return <span title={text}>{text}</span>
      },
    },
    { accessorKey: 'remark', header: '说明', cell: (info) => emptyText(String(info.getValue() ?? '')) },
  ], [catalog, labels, names, documentActionLookup])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1">
        <h6 className="mb-0">业务动作（{actions.length}）</h6>
        <div className="d-flex gap-1">
          <Button size="sm" icon={<IconPlus size={16} />} onClick={onCreate}>新增</Button>
          <Button size="sm" icon={<IconCopy size={16} />} onClick={onClone}>从其它模块复制</Button>
          <Button size="sm" icon={<IconArrowUp size={16} />} onClick={onMoveUp} disabled={!canEdit} aria-label="上移">上移</Button>
          <Button size="sm" icon={<IconArrowDown size={16} />} onClick={onMoveDown} disabled={!canEdit} aria-label="下移">下移</Button>
          <Button size="sm" icon={<IconCopy size={16} />} onClick={onDuplicate} disabled={!canEdit}>复制</Button>
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
      <div className="text-secondary small mt-2">
        自定义按钮（用户点击触发的那类）在「自定义按钮」页签里单独配。
      </div>
    </section>
  )
}

/** 选中动作的加工单：加工单视图与字段表视图切换，步骤工具栏在这里。 */
export function OpsView({
  action,
  ops,
  opView,
  onOpViewChange,
  selectedOpSeq,
  onSelectOp,
  names,
  labels,
  eventText,
  effectText,
  failModeText,
  reverseText,
  onCreateOp,
  onDuplicateOp,
  onEditOp,
  onDeleteOp,
  canEditOp,
}: {
  action: BusinessAction
  ops: BusinessActionOp[]
  opView: 'sheet' | 'grid'
  onOpViewChange: (view: 'sheet' | 'grid') => void
  selectedOpSeq: number | null
  onSelectOp: (opSeq: number | null) => void
  names: BusinessNameLookup
  labels: LabelLookups
  eventText: string
  effectText: string
  failModeText: string
  reverseText: string
  onCreateOp: () => void
  onDuplicateOp: () => void
  onEditOp: () => void
  onDeleteOp: () => void
  canEditOp: boolean
}) {
  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1 flex-wrap gap-2">
        <h6 className="mb-0">加工单（{ops.length} 步）</h6>
        <div className="d-flex gap-1 align-items-center flex-wrap">
          <div className="btn-group btn-group-sm" role="group" aria-label="加工单视图切换">
            <button
              type="button"
              className={`btn ${opView === 'sheet' ? 'btn-secondary' : 'btn-outline-secondary'}`}
              onClick={() => onOpViewChange('sheet')}
            >
              加工单
            </button>
            <button
              type="button"
              className={`btn ${opView === 'grid' ? 'btn-secondary' : 'btn-outline-secondary'}`}
              onClick={() => onOpViewChange('grid')}
            >
              字段表
            </button>
          </div>
          <Button size="sm" icon={<IconPlus size={16} />} onClick={onCreateOp}>新增步骤</Button>
          <Button size="sm" icon={<IconCopy size={16} />} onClick={onDuplicateOp} disabled={!canEditOp}>复制步骤</Button>
          <Button size="sm" icon={<IconEdit size={16} />} onClick={onEditOp} disabled={!canEditOp}>编辑</Button>
          <Button size="sm" variant="danger" icon={<IconTrash size={16} />} onClick={onDeleteOp} disabled={!canEditOp}>删除</Button>
        </div>
      </div>
      {opView === 'sheet' ? (
        <BusinessActionSheet
          action={action}
          names={names}
          eventText={eventText}
          effectText={effectText}
          failModeText={failModeText}
          reverseText={reverseText}
          selectedOpSeq={selectedOpSeq}
          onSelectOp={onSelectOp}
        />
      ) : (
        <OpTable labels={labels} names={names} ops={ops} selectedOpSeq={selectedOpSeq} onSelect={onSelectOp} />
      )}
    </section>
  )
}

function OpTable({
  labels,
  names,
  ops,
  selectedOpSeq,
  onSelect,
}: {
  labels: LabelLookups
  names: BusinessNameLookup
  ops: BusinessActionOp[]
  selectedOpSeq: number | null
  onSelect: (opSeq: number | null) => void
}) {
  const columns = useMemo<ColumnDef<BusinessActionOp, unknown>[]>(() => [
    { accessorKey: 'opSeq', header: '序', meta: { minWidth: 48 } },
    {
      accessorKey: 'targetTable',
      header: '目标表',
      cell: (info) => names.table(String(info.getValue())),
    },
    {
      accessorKey: 'targetField',
      header: '目标字段',
      cell: ({ row }) => names.field(row.original.targetTable, row.original.targetField),
    },
    {
      accessorKey: 'opCode',
      header: '运算',
      cell: (info) => labelWithCode(labels.opCodes, String(info.getValue())),
    },
    {
      id: 'source',
      header: '来源',
      meta: { minWidth: 200 },
      cell: ({ row }) => (
        <span title={formatOpSentence(row.original, names)}>
          {formatOpSentence(row.original, names).split(' ').slice(2).join(' ')}
        </span>
      ),
    },
    {
      accessorKey: 'match',
      header: '定位键',
      meta: { minWidth: 220 },
      cell: (info) => readableText(formatMatch(String(info.getValue() ?? ''), names), String(info.getValue() ?? '')),
    },
    {
      accessorKey: 'condition',
      header: '条件',
      meta: { minWidth: 200 },
      cell: ({ row }) => {
        const raw = String(row.original.condition ?? '')
        return readableText(formatCondition(raw, withTargetTable(names, row.original.targetTable)), raw)
      },
    },
    { accessorKey: 'remark', header: '说明', cell: (info) => emptyText(String(info.getValue() ?? '')) },
  ], [labels, names])

  return (
    <ErpTable
      columns={columns}
      data={ops}
      getRowId={(row) => `${row.opSeq}`}
      activeRowId={selectedOpSeq == null ? undefined : `${selectedOpSeq}`}
      onRowClick={(row) => onSelect(row.opSeq)}
      rowClickSingleSelect
      clientSideSorting
      copyable={false}
      empty={<div className="p-3 text-secondary">该动作没有字段级步骤（参数型效果在动作的参数里）。</div>}
    />
  )
}

/** 校验规则页签：服务端在加载/批核/解批等阶段执行的校验模板。 */
export function RulesView({
  labels,
  rules,
  selectedRuleId,
  onSelect,
  onCreate,
  onEdit,
  onDelete,
}: {
  labels: LabelLookups
  rules: ValidationRule[]
  selectedRuleId: string | null
  onSelect: (key: string | null) => void
  onCreate: () => void
  onEdit: () => void
  onDelete: () => void
}) {
  const columns = useMemo<ColumnDef<ValidationRule, unknown>[]>(() => [
    { accessorKey: 'stage', header: '阶段', cell: (info) => labelWithCode(labels.validationStages, String(info.getValue())) },
    { accessorKey: 'seq', header: '顺序', meta: { minWidth: 64 } },
    {
      accessorKey: 'validationKey',
      header: '校验模板',
      cell: (info) => <span title={String(info.getValue())}>{labelWithCode(labels.validationKeys, String(info.getValue()))}</span>,
    },
    {
      accessorKey: 'enabled',
      header: '启用',
      cell: (info) => (info.getValue() ? '是' : '否'),
      meta: { minWidth: 60 },
    },
    { accessorKey: 'message', header: '失败文案', cell: (info) => emptyText(String(info.getValue() ?? '')) },
    { accessorKey: 'sourceRef', header: '溯源', cell: (info) => emptyText(String(info.getValue() ?? '')) },
  ], [labels])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1">
        <h6 className="mb-0">校验规则（{rules.length}）</h6>
        <div className="d-flex gap-1">
          <Button size="sm" icon={<IconPlus size={16} />} onClick={onCreate}>新增规则</Button>
          <Button size="sm" icon={<IconEdit size={16} />} onClick={onEdit} disabled={!selectedRuleId}>编辑</Button>
          <Button size="sm" variant="danger" icon={<IconTrash size={16} />} onClick={onDelete} disabled={!selectedRuleId}>删除</Button>
        </div>
      </div>
      <ErpTable
        columns={columns}
        data={rules}
        getRowId={(row) => ruleKey(row)}
        activeRowId={selectedRuleId ?? undefined}
        onRowClick={(row) => onSelect(ruleKey(row))}
        rowClickSingleSelect
        clientSideSorting
        copyable={false}
        empty={<div className="p-3 text-secondary">尚未配置模块校验规则（核心默认校验为代码内建，不在此表）。</div>}
      />
    </section>
  )
}

/** 自定义按钮页签：用户点击触发的按钮行，及其授权镜子（谁点得动）。 */
export function ManualButtonsView({
  actions,
  documentActions,
  authorization,
  selectedKey,
  onSelect,
  onCreate,
  onEdit,
  onDuplicate,
  onDelete,
  onMoveUp,
  onMoveDown,
  canEdit,
}: {
  actions: BusinessAction[]
  documentActions: DocumentActionCatalogEntry[]
  authorization?: DocumentActionAuthorizationEntry[] | null
  selectedKey: string | null
  onSelect: (key: string | null) => void
  onCreate: () => void
  onEdit: () => void
  onDuplicate: () => void
  onDelete: () => void
  onMoveUp: () => void
  onMoveDown: () => void
  canEdit: boolean
}) {
  const entryByKey = useMemo(
    () => new Map(documentActions.map((item) => [item.key, item])),
    [documentActions],
  )
  const authorizationByKey = useMemo(
    () => new Map((authorization ?? []).map((item) => [item.key, item])),
    [authorization],
  )
  const entryOf = (key: string) => entryByKey.get(key)
  const unauthorized = actions.filter((action) => {
    const entry = authorizationByKey.get(action.effectKey)
    return !entry || (entry.users === 0 && entry.groups === 0)
  })

  const columns = useMemo<ColumnDef<BusinessAction, unknown>[]>(() => [
    { accessorKey: 'seq', header: '顺序', meta: { minWidth: 64 } },
    {
      accessorKey: 'effectKey',
      header: '按钮键',
      meta: { minWidth: 240 },
      cell: ({ row }) => {
        const entry = entryByKey.get(row.original.effectKey)
        return (
          <span title={row.original.effectKey}>
            {entry ? `${entry.label}（${entry.key}）` : `${row.original.effectKey}（未登记实现）`}
          </span>
        )
      },
    },
    {
      id: 'title',
      header: '标题',
      meta: { minWidth: 160 },
      cell: ({ row }) => emptyText(String(row.original.label ?? row.original.effectName ?? '')),
    },
    {
      id: 'placement',
      header: '落点',
      meta: { minWidth: 90 },
      cell: ({ row }) => placementLabel(entryByKey.get(row.original.effectKey)?.placement),
    },
    {
      accessorKey: 'confirmTag',
      header: '需确认',
      meta: { minWidth: 80 },
      cell: (info) => (info.getValue() ? '是' : '否'),
    },
    {
      accessorKey: 'enabled',
      header: '启用',
      meta: { minWidth: 60 },
      cell: (info) => (info.getValue() ? '是' : '否'),
    },
    {
      id: 'authorization',
      header: '可点用户·组',
      meta: { minWidth: 130 },
      cell: ({ row }) => {
        const entry = authorizationByKey.get(row.original.effectKey)
        if (!entry) return '—'
        const none = entry.users === 0 && entry.groups === 0
        return (
          <span
            className={none ? 'text-warning-emphasis' : undefined}
            title={none ? '尚无任何授权，发布后无人可点' : undefined}
          >
            {entry.users} 用户 / {entry.groups} 组
          </span>
        )
      },
    },
  ], [entryByKey, authorizationByKey])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between mb-1">
        <h6 className="mb-0">自定义按钮（{actions.length}）</h6>
        <div className="d-flex gap-1">
          <Button
            size="sm"
            icon={<IconPlus size={16} />}
            onClick={onCreate}
            disabled={documentActions.length === 0}
            title={documentActions.length === 0 ? '操作注册表里没有可配置的按钮键' : undefined}
          >
            新增
          </Button>
          <Button size="sm" icon={<IconArrowUp size={16} />} onClick={onMoveUp} disabled={!canEdit} aria-label="上移">上移</Button>
          <Button size="sm" icon={<IconArrowDown size={16} />} onClick={onMoveDown} disabled={!canEdit} aria-label="下移">下移</Button>
          <Button size="sm" icon={<IconCopy size={16} />} onClick={onDuplicate} disabled={!canEdit}>复制</Button>
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
        empty={(
          <div className="p-3 text-secondary">
            尚未配置自定义按钮，点「新增」从操作注册表挑一个按钮键。
          </div>
        )}
      />
      {actions.length > 0 ? (
        <div className="alert alert-secondary py-2 px-3 mb-2 mt-2" role="status">
          <div className="small mb-1">
            按钮授权（<strong>fail-closed 名单</strong>）：未授权即不可点，按钮也不出现在单据上；
            在「用户权限设定 / 用户组管理」的按钮权限页签里按人/按组授权。
          </div>
          {unauthorized.length > 0 ? (
            <div className="small">
              尚无任何授权，发布后无人可点：
              {unauthorized.map((action) => entryOf(action.effectKey)?.label ?? action.effectKey).join('、')}。
            </div>
          ) : null}
        </div>
      ) : null}
      <div className="text-secondary small mt-2">
        内置动作（批核、结案等受控注册码）不在这里，也<strong>不需要配</strong>：它由能力（工作流 /
        结案 / 权限位）与单据状态决定，随单据工具栏出现——此前那套「按注册码配白名单」的列已退役。
      </div>
    </section>
  )
}
