import {
  IconArrowBarToDown,
  IconArrowBarToUp,
  IconArrowDown,
  IconArrowUp,
  IconChevronRight,
  IconColumns,
  IconEdit,
  IconFolder,
  IconHistory,
  IconPhoto,
  IconPlus,
  IconPower,
  IconRefresh,
  IconRocket,
  IconSearch,
  IconTrash,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpCommandBar } from '../../components/common/ErpCommandBar'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { ErpTable } from '../../components/common/ErpTable'
import type { ColumnDef } from '@tanstack/react-table'
import { Modal } from '../../components/ui/Modal'
import { TabbedPanel } from '../../components/common/TabbedPanel'
import { navigationIcons } from '../../components/layout/navigationIcons'
import { childPad, dotLeft, groupPad, lineTree } from '../../components/layout/menuDepth'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { notifyMenuChanged } from '../../services/menuEvents'
import { ApiError } from '../../types/api'
import { parseFilter } from './menuFilter'
import { MenuFieldPicker, MenuFilterBuilder } from './MenuFieldPickers'
import { describeApiError } from '../../lib/errors'
import { BusinessActionsPanel, type ModuleBusinessConfigDraft } from './BusinessActionsPanel'

/** 菜单编辑表单页签：基础 / 主表 / 子表 / 分组 / 统一表单 / 行为动作。 */
type MenuFormTab = 'basic' | 'master' | 'detail' | 'group' | 'form' | 'actions'

const MENU_FORM_TABS: { key: MenuFormTab; label: string }[] = [
  { key: 'basic', label: '基础' },
  { key: 'master', label: '主表' },
  { key: 'detail', label: '子表' },
  { key: 'group', label: '分组' },
  { key: 'form', label: '统一表单' },
  { key: 'actions', label: '行为动作' },
]

export interface MenuAdminModule {
  M_IDX: number
  M_ALIAS: string | null
  M_DESC: string
  M_URL: string | null
  NEW_URL: string | null
  MODI_URL: string | null
  HELP_URL: string | null
  DETAIL_NO_FIELDS: string | null
  DETAIL_NO_SAVE: boolean
  SEARCH_1: boolean
  SEARCH_2: boolean
  M_P_IDX: number | null
  SORT_IDX: number
  M_TAG: boolean
  AUTO_APPROVE: boolean
  IF_COPY: boolean
  ERROR_NO_SAVE: boolean
  SORT_FIELDS: string | null
  MASTER_TABLE: string | null
  FILTER: string | null
  DETAIL_TABLE: string | null
  NOT_BACK_FIELDS_M: string | null
  NOT_BACK_FIELDS: string | null
  GROUP1: boolean; GROUP_EXP1: string | null; GROUP_DESC1: string | null
  GROUP2: boolean; GROUP_EXP2: string | null; GROUP_DESC2: string | null
  GROUP3: boolean; GROUP_EXP3: string | null; GROUP_DESC3: string | null
  GROUP4: boolean; GROUP_EXP4: string | null; GROUP_DESC4: string | null
  GROUP5: boolean; GROUP_EXP5: string | null; GROUP_DESC5: string | null
  FORM_TABS: string | null
  FORM_COLUMNS: number | null
  FORM_BUTTONS: string | null
  LAST_UPDATE_BY: string | null
  LAST_UPDATE_DATE: string | null
  M_ICON: string | null
  Icon: string | null
  EFFECT_ENGINE_TAG: boolean
  /** 只读展示字段：操作主/副表描述（服务端由 TABLES.T_DESC 解析，保存时忽略）。 */
  MASTER_TABLE_DESC?: string | null
  DETAIL_TABLE_DESC?: string | null
  /** 只读状态字段：已保存未发布的改动、当前生效快照版本与发布时间。 */
  DIRTY_TAG?: boolean
  PUBLISH_VERSION?: number | null
  PUBLISHED_AT?: string | null
}

/** 发布校验结果与发布结果（与后端 WorkbenchPublishResult 对应）。 */
interface PublishValidationCheck {
  code: string
  passed: boolean
  message: string
  severity: string
}

interface WorkbenchPublishResult {
  moduleId: number
  title: string
  published: boolean
  version: number | null
  definitionVersion: string | null
  passed: boolean
  checks: PublishValidationCheck[]
  error?: string | null
}

/** 模块定义快照的历史版本（只读）。 */
interface MenuModuleVersion {
  version: number
  definitionVersion: string
  publishedBy: string | null
  publishedAt: string | null
  validationStatus: string
  isCurrent: boolean
}

/** 菜单同级排序动作。 */
type MenuSortAction = 'top' | 'up' | 'down' | 'bottom'

/** 拖拽落点：before=目标同级之前、after=目标同级之后、into=成为目标子节点（追加末尾）。 */
type DropMode = 'before' | 'after' | 'into'

const parentKeyOf = (module: MenuAdminModule) => (module.M_P_IDX != null && module.M_P_IDX > 0 ? module.M_P_IDX : 0)

const emptyDraft = (parentId: number | null): MenuAdminModule => ({
  M_IDX: 0,
  M_ALIAS: null,
  M_DESC: '',
  M_URL: null,
  NEW_URL: null,
  MODI_URL: null,
  HELP_URL: null,
  DETAIL_NO_FIELDS: null,
  DETAIL_NO_SAVE: false,
  SEARCH_1: false,
  SEARCH_2: false,
  M_P_IDX: parentId && parentId > 0 ? parentId : null,
  SORT_IDX: 0,
  M_TAG: true,
  AUTO_APPROVE: false,
  IF_COPY: false,
  ERROR_NO_SAVE: false,
  SORT_FIELDS: null,
  MASTER_TABLE: null,
  FILTER: null,
  DETAIL_TABLE: null,
  NOT_BACK_FIELDS_M: null,
  NOT_BACK_FIELDS: null,
  GROUP1: false, GROUP_EXP1: null, GROUP_DESC1: null,
  GROUP2: false, GROUP_EXP2: null, GROUP_DESC2: null,
  GROUP3: false, GROUP_EXP3: null, GROUP_DESC3: null,
  GROUP4: false, GROUP_EXP4: null, GROUP_DESC4: null,
  GROUP5: false, GROUP_EXP5: null, GROUP_DESC5: null,
  FORM_TABS: null,
  FORM_COLUMNS: null,
  FORM_BUTTONS: null,
  LAST_UPDATE_BY: null,
  LAST_UPDATE_DATE: null,
  M_ICON: null,
  Icon: null,
  EFFECT_ENGINE_TAG: false,
})

interface TreeEntry {
  module: MenuAdminModule
  children: TreeEntry[]
}

/**
 * 模块状态的直观提示：未保存 > 已保存未发布 > 已发布（未发布过则提示未发布）。
 * 「已保存未发布」表示运行时仍按当前生效快照版本执行，发布后新配置才生效。
 */
function moduleStateBadge(
  draft: MenuAdminModule | null,
  selected: MenuAdminModule | null,
  hasExtraDrafts = false,
): { label: string; className: string; title: string } | null {
  if (!draft) return null
  if (!selected) {
    return { label: '新增未保存', className: 'bg-warning-subtle text-warning', title: '该节点尚未保存到模块表。' }
  }
  if (hasExtraDrafts || JSON.stringify(draft) !== JSON.stringify(selected)) {
    return { label: '已修改未保存', className: 'bg-warning-subtle text-warning', title: '当前表单有改动尚未保存。' }
  }
  if (selected.DIRTY_TAG) {
    return {
      label: '已保存未发布',
      className: 'bg-warning-subtle text-warning',
      title: selected.PUBLISH_VERSION == null
        ? '已保存但尚未发布过定义快照，运行时按实时元数据读取。'
        : `运行时仍按 module-${selected.M_IDX}-v${selected.PUBLISH_VERSION} 执行，发布后才切换到新配置。`,
    }
  }
  if (selected.PUBLISH_VERSION != null) {
    return {
      label: `已发布 module-${selected.M_IDX}-v${selected.PUBLISH_VERSION}`,
      className: 'bg-success-subtle text-success',
      title: selected.PUBLISHED_AT
        ? `发布于 ${new Date(selected.PUBLISHED_AT).toLocaleString()}，与当前元数据一致。`
        : '已发布，与当前元数据一致。',
    }
  }
  return {
    label: '未发布',
    className: 'bg-secondary-subtle text-secondary',
    title: '该模块尚未发布过定义快照，运行时按实时元数据读取。',
  }
}

interface DefaultColumnField { key: string; label: string; isSelected: boolean; order: number }
interface DefaultColumnsResponse { table: string; tableKind: string; fields: DefaultColumnField[] }

function buildTree(modules: MenuAdminModule[]): TreeEntry[] {
  const byId = new Map<number, TreeEntry>()
  modules.forEach((module) => byId.set(module.M_IDX, { module, children: [] }))
  const roots: TreeEntry[] = []
  modules.forEach((module) => {
    const entry = byId.get(module.M_IDX)!
    const parent = module.M_P_IDX != null && module.M_P_IDX > 0 ? byId.get(module.M_P_IDX) : undefined
    if (parent) parent.children.push(entry)
    else roots.push(entry)
  })
  const sortEntries = (entries: TreeEntry[]) => {
    entries.sort((a, b) => a.module.SORT_IDX - b.module.SORT_IDX || a.module.M_IDX - b.module.M_IDX)
    entries.forEach((entry) => sortEntries(entry.children))
  }
  sortEntries(roots)
  return roots
}

function Input({ label, value, onChange, type = 'text', placeholder, readOnly = false }: {
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  placeholder?: string
  readOnly?: boolean
}) {
  const inputId = `erp-menu-field-${label.replace(/[^\w\u4e00-\u9fa5]+/g, '-')}`
  return (
    <div className="mb-2">
      <label className="form-label mb-1" htmlFor={inputId}>{label}</label>
      <input
        id={inputId}
        className="form-control form-control-sm"
        type={type}
        value={value}
        placeholder={placeholder}
        readOnly={readOnly}
        onChange={(event) => onChange(event.target.value)}
      />
    </div>
  )
}

function Checkbox({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="form-check form-switch mb-2">
      <input className="form-check-input" type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span className="form-check-label">{label}</span>
    </label>
  )
}

export function MenuAdminPage() {
  const queryClient = useQueryClient()
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [draft, setDraft] = useState<MenuAdminModule | null>(null)
  const [expandedIds, setExpandedIds] = useState<Set<number>>(() => new Set())
  const [defaultColumnsOpen, setDefaultColumnsOpen] = useState<null | { table: 'master' | 'detail' }>(null)
  const [defaultColumnGroups, setDefaultColumnGroups] = useState<ColumnSelectorGroup[]>([])
  const [defaultColumnsLoading, setDefaultColumnsLoading] = useState(false)
  const [defaultColumnsError, setDefaultColumnsError] = useState<string | null>(null)
  const [draggedId, setDraggedId] = useState<number | null>(null)
  const [dropTarget, setDropTarget] = useState<{ id: number; mode: DropMode } | null>(null)
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; module: MenuAdminModule } | null>(null)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [renameValue, setRenameValue] = useState('')
  const [tableChooser, setTableChooser] = useState<'master' | 'detail' | null>(null)
  const [iconPickerModule, setIconPickerModule] = useState<MenuAdminModule | null>(null)
  const [fieldPicker, setFieldPicker] = useState<null | { target: 'sortFields' | 'detailNoFields' | 'notBackM' | 'notBack' }>(null)
  const [filterBuilderOpen, setFilterBuilderOpen] = useState(false)
  const [formTab, setFormTab] = useState<MenuFormTab>('basic')
  const [treeQuery, setTreeQuery] = useState('')
  const draggedIdRef = useRef<number | null>(null)
  // 行为动作/校验规则草稿：由行为动作页签上报（未打开该页签时为 null，保存时保持不动）
  const [actionsDraft, setActionsDraft] = useState<ModuleBusinessConfigDraft | null>(null)
  // 本次在界面上编辑过的默认查询列（未编辑的表不参与保存）
  const [defaultColumnDrafts, setDefaultColumnDrafts] = useState<Record<string, string[]> | null>(null)
  const [publishResult, setPublishResult] = useState<WorkbenchPublishResult | null>(null)
  const [publishError, setPublishError] = useState<string | null>(null)
  const [versionsOpen, setVersionsOpen] = useState(false)
  // 发布内部会先保存，避免保存成功提示打断发布流程
  const publishingRef = useRef(false)
  const modules = useQuery({ queryKey: ['menu-admin', 'modules'], queryFn: () => apiClient.get<{ total: number; modules: MenuAdminModule[] }>('/admin/menus') })
  const versions = useQuery({
    queryKey: ['menu-admin', 'versions', selectedId],
    queryFn: () => apiClient.get<MenuModuleVersion[]>(`/admin/menus/${selectedId}/versions`),
    enabled: versionsOpen && selectedId != null,
  })

  // ADR-013 §6.2 配置期提示：主表缺少 CONFIRM_TAG 但具备批核能力（自动批核/批核过程/
  // 批核按钮）时预警——发布会被 lifecycle_columns 门拦截。仅提示不阻断保存（草稿工作区），
  // 不自动补列（物理列变更走 DbUp 版本化迁移，不设一键补列）；是否发布由服务端终裁。
  const masterTableName = draft?.MASTER_TABLE ?? null
  const masterColumns = useQuery({
    queryKey: ['menu-admin', 'master-columns', masterTableName ?? ''],
    queryFn: async () => masterTableName
      ? apiClient.get<{ name: string }[]>(`/admin/tables/${encodeURIComponent(masterTableName)}/columns`)
      : [],
    enabled: Boolean(masterTableName),
  })
  const approveButtonActions = (draft?.FORM_BUTTONS ?? '').split(';').map((part) => {
    const eq = part.indexOf('=')
    return (eq >= 0 ? part.slice(eq + 1) : part).trim().toLowerCase()
  })
  const masterMissingConfirmTag = Boolean(masterTableName)
    && (draft?.AUTO_APPROVE === true || approveButtonActions.includes('approve') || approveButtonActions.includes('deapprove'))
    && masterColumns.data !== undefined
    && !masterColumns.data.some((column) => column.name.toLowerCase() === 'confirm_tag')

  const byId = useMemo(() => new Map((modules.data?.modules ?? []).map((module) => [module.M_IDX, module])), [modules.data])
  const filteredModules = useMemo(() => {
    const query = treeQuery.trim().toLowerCase()
    const all = modules.data?.modules ?? []
    if (!query) return all
    const localById = new Map(all.map((module) => [module.M_IDX, module]))
    const matched = new Set<number>()
    for (const module of all) {
      const hit = module.M_DESC.toLowerCase().includes(query)
        || (module.M_ALIAS ?? '').toLowerCase().includes(query)
        || String(module.M_IDX).includes(query)
      if (!hit) continue
      let current: number | null | undefined = module.M_IDX
      while (current != null && current > 0) {
        matched.add(current)
        current = localById.get(current)?.M_P_IDX ?? null
      }
    }
    return all.filter((module) => matched.has(module.M_IDX))
  }, [modules.data, treeQuery])
  const tree = useMemo(() => buildTree(filteredModules), [filteredModules])
  const selected = selectedId != null ? byId.get(selectedId) ?? null : null
  const stateBadge = moduleStateBadge(draft, selected, defaultColumnDrafts != null || actionsDraft?.dirty === true)

  // 切换模块时丢弃上一模块的关联草稿与发布结果，避免误提交到新模块
  useEffect(() => {
    setActionsDraft(null)
    setDefaultColumnDrafts(null)
    setPublishResult(null)
    setPublishError(null)
  }, [selectedId])

  const handleActionsDraftChange = useCallback((next: ModuleBusinessConfigDraft | null) => {
    setActionsDraft(next)
  }, [])

  const versionColumns = useMemo<ColumnDef<MenuModuleVersion, unknown>[]>(() => [
    { accessorKey: 'definitionVersion', header: '版本', cell: (info) => <code>{String(info.getValue())}</code> },
    {
      accessorKey: 'publishedAt',
      header: '发布时间',
      cell: (info) => {
        const value = info.getValue() as string | null
        return value ? new Date(value).toLocaleString() : '—'
      },
    },
    { accessorKey: 'publishedBy', header: '发布人', cell: (info) => String(info.getValue() ?? '—') },
    { accessorKey: 'validationStatus', header: '校验', cell: (info) => String(info.getValue() ?? '') },
    { accessorKey: 'isCurrent', header: '当前生效', cell: (info) => (info.getValue() ? '是' : '') },
  ], [])

  // 同级兄弟显示顺序（SORT_IDX,M_IDX），供排序按钮与拖拽定位使用
  const siblingsByParent = useMemo(() => {
    const map = new Map<number, number[]>()
    for (const module of modules.data?.modules ?? []) {
      const key = parentKeyOf(module)
      const list = map.get(key) ?? []
      list.push(module.M_IDX)
      map.set(key, list)
    }
    map.forEach((list) => list.sort((a, b) =>
      (byId.get(a)?.SORT_IDX ?? 0) - (byId.get(b)?.SORT_IDX ?? 0) || a - b))
    return map
  }, [modules.data, byId])
  const siblingIds = selected != null ? siblingsByParent.get(parentKeyOf(selected)) ?? [] : []
  const selectedIndex = selectedId != null ? siblingIds.indexOf(selectedId) : -1

  // 有子节点的菜单组集合（用于拖拽落点：仅菜单组中间区域允许拖入）
  const parentIds = useMemo(
    () => new Set(
      (modules.data?.modules ?? [])
        .map((module) => module.M_P_IDX)
        .filter((parent): parent is number => parent != null && parent > 0),
    ),
    [modules.data],
  )
  // 全部父节点均已展开时视为「已全部展开」，切换按钮显示折叠全部
  const allExpanded = useMemo(
    () => parentIds.size > 0 && [...parentIds].every((id) => expandedIds.has(id)),
    [parentIds, expandedIds],
  )

  /** nodeId 是否位于 ancestorId 的子树内（沿 M_P_IDX 上溯）。 */
  const isDescendantOf = (ancestorId: number, nodeId: number) => {
    let current: number | null | undefined = nodeId
    const visited = new Set<number>()
    while (current != null && current > 0 && !visited.has(current)) {
      if (current === ancestorId) return true
      visited.add(current)
      current = byId.get(current)?.M_P_IDX
    }
    return false
  }

  /** 目标节点在父级中的下一个同级 id（无则 null=末尾）。 */
  const nextSiblingId = (id: number) => {
    const module = byId.get(id)
    if (!module) return null
    const list = siblingsByParent.get(parentKeyOf(module)) ?? []
    const index = list.indexOf(id)
    return index >= 0 && index < list.length - 1 ? list[index + 1] : null
  }

  /** 计算拖拽移动是否会产生实际变化（同父且顺序一致时为无操作）。 */
  const wouldChange = (id: number, parentId: number | null, beforeId: number | null) => {
    const module = byId.get(id)
    if (!module) return false
    const parentKey = parentId ?? 0
    if (parentKeyOf(module) !== parentKey) return true
    // beforeId 指向自身：同父下与原位一致（例如“插到上一行之后”恰等于当前位置），直接视为无操作
    if (beforeId === id) return false
    const list = [...(siblingsByParent.get(parentKey) ?? [])]
    const index = list.indexOf(id)
    if (index >= 0) list.splice(index, 1)
    const insertAt = beforeId != null ? list.indexOf(beforeId) : list.length
    if (insertAt < 0) return true
    list.splice(insertAt, 0, id)
    return !(siblingsByParent.get(parentKey) ?? []).every((value, i) => value === list[i])
  }

  /**
   * 根据鼠标在目标行内的纵向位置判定落点模式：
   * - 前 30% before、后 30% after；
   * - 中间 40%：菜单组（有子节点）允许 into（成为其子节点）；
   *   叶子行一律按 before（插到该行之前）——拖到哪行就排到哪行，避免“拖到上一行
   *   中间被解析成 after 自身”而成为无操作。
   */
  const dropModeFor = (clientY: number, rect: DOMRect, canAcceptChildren: boolean): DropMode => {
    const height = rect.height || 1
    const ratio = (clientY - rect.top) / height
    if (ratio < 0.3) return 'before'
    if (ratio > 0.7) return 'after'
    return canAcceptChildren ? 'into' : 'before'
  }

  /**
   * 保存载荷：模块行 + 行为动作/校验规则 + 默认查询列，由服务端在同一事务内落库。
   * 未加载或未编辑过的部分传 null，表示保持不动。
   */
  const buildBusinessConfig = (input: MenuAdminModule) => {
    if (!actionsDraft || actionsDraft.moduleId !== input.M_IDX) return null
    if (input.MASTER_TABLE == null && input.DETAIL_TABLE == null) return null
    return {
      actions: actionsDraft.actions.map((action) => ({
        ...action,
        ops: (action.ops ?? []).map((op) =>
          op.sourceScope === 'CONSTANT' ? { ...op, sourceConstant: op.sourceConstant ?? '' } : op,
        ),
      })),
      validationRules: actionsDraft.validationRules,
    }
  }

  const buildDefaultColumns = () =>
    defaultColumnDrafts == null
      ? null
      : Object.entries(defaultColumnDrafts).map(([table, fieldIds]) => ({ table, fieldIds }))

  const save = useMutation({
    mutationFn: async (input: MenuAdminModule) => {
      const payload = {
        module: input,
        businessConfig: buildBusinessConfig(input),
        defaultColumns: buildDefaultColumns(),
      }
      if (selectedId != null && byId.has(selectedId)) {
        await apiClient.put(`/admin/menus/${selectedId}`, payload)
        return selectedId
      }
      const created = await apiClient.post<{ id: number }>('/admin/menus', payload)
      return created.id
    },
    onSuccess: async (savedId, input) => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      await queryClient.invalidateQueries({ queryKey: ['module-business-config', savedId] })
      notifyMenuChanged()
      setSelectedId(savedId)
      // 保存后保持该节点处于编辑态：用刷新后的列表数据回填草稿；列表尚未包含该节点时
      // 回退为本次提交的内容，避免表单被清空、需要用户重新在左侧选中。
      const data = queryClient.getQueryData<{ total: number; modules: MenuAdminModule[] }>(['menu-admin', 'modules'])
      const fresh = data?.modules.find((module) => module.M_IDX === savedId)
      setDraft(fresh ? { ...fresh } : { ...input, M_IDX: savedId })
      setDefaultColumnDrafts(null)
      setPublishResult(null)
      setPublishError(null)
      if (!publishingRef.current) window.alert('菜单保存成功。')
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `保存失败：${error.body.message}` : '保存失败。')
    },
  })

  /** 发布：先按当前编辑内容保存（同一事务），再发布模块定义快照。 */
  const publish = useMutation({
    mutationFn: async () => {
      if (!draft) throw new Error('没有可发布的模块。')
      publishingRef.current = true
      let savedId: number
      try {
        savedId = await save.mutateAsync(draft)
      } finally {
        publishingRef.current = false
      }
      const results = await apiClient.post<WorkbenchPublishResult[]>(
        `/admin/module-business-config/${savedId}/publish`,
      )
      return results[0]
    },
    onSuccess: async (result) => {
      setPublishError(null)
      setPublishResult(result)
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
    },
    onError: (error) => {
      setPublishResult(null)
      setPublishError(describeApiError(error, '发布失败。'))
    },
  })

  /** 树内改名：菜单名称属呈现属性，立即生效、不标脏、不需要发布。 */
  const rename = useMutation({
    mutationFn: async ({ id, description }: { id: number; description: string }) => {
      await apiClient.put(`/admin/menus/${id}/rename`, { description })
    },
    onSuccess: async (_, { id, description }) => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      notifyMenuChanged()
      setDraft((current) => (current && current.M_IDX === id ? { ...current, M_DESC: description } : current))
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `重命名失败：${error.body.message}` : '重命名失败。')
    },
  })

  /** 启用/停用：只决定侧栏是否显示，不标脏、不需要发布。 */
  const setEnabled = useMutation({
    mutationFn: async ({ id, enabled }: { id: number; enabled: boolean }) => {
      await apiClient.put(`/admin/menus/${id}/enabled`, { enabled })
    },
    onSuccess: async (_, { id, enabled }) => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      notifyMenuChanged()
      setDraft((current) => (current && current.M_IDX === id ? { ...current, M_TAG: enabled } : current))
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `启停失败：${error.body.message}` : '启停失败。')
    },
  })

  const remove = useMutation({
    mutationFn: async (id: number) => {
      await apiClient.delete(`/admin/menus/${id}`)
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      notifyMenuChanged()
      setSelectedId(null)
      setDraft(null)
      window.alert('删除成功。')
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `删除失败：${error.body.message}` : '删除失败。')
    },
  })

  const sort = useMutation({
    mutationFn: async ({ id, action }: { id: number; action: MenuSortAction }) => {
      await apiClient.put(`/admin/menus/${id}/sort`, { action })
    },
    onSuccess: async (_, { id }) => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      notifyMenuChanged()
      const data = queryClient.getQueryData<{ total: number; modules: MenuAdminModule[] }>(['menu-admin', 'modules'])
      const fresh = data?.modules.find((module) => module.M_IDX === id)
      if (fresh) {
        setSelectedId(id)
        setDraft({ ...fresh })
      }
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `排序失败：${error.body.message}` : '排序失败。')
    },
  })

  const canMoveUp = selectedId != null && selectedIndex > 0 && !sort.isPending
  const canMoveDown = selectedId != null && selectedIndex >= 0 && selectedIndex < siblingIds.length - 1 && !sort.isPending

  const move = useMutation({
    mutationFn: async ({ id, parentId, beforeId }: { id: number; parentId: number | null; beforeId: number | null }) => {
      await apiClient.put(`/admin/menus/${id}/move`, { parentId, beforeId })
    },
    onSuccess: async (_, { id }) => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      notifyMenuChanged()
      const data = queryClient.getQueryData<{ total: number; modules: MenuAdminModule[] }>(['menu-admin', 'modules'])
      const fresh = data?.modules.find((module) => module.M_IDX === id)
      if (fresh) {
        // 自动展开新父链，保证拖入后节点可见
        const freshById = new Map(data!.modules.map((module) => [module.M_IDX, module]))
        const ancestors: number[] = []
        let parent = fresh.M_P_IDX
        while (parent != null && parent > 0) {
          ancestors.push(parent)
          parent = freshById.get(parent)?.M_P_IDX ?? null
        }
        setExpandedIds((current) => {
          const next = new Set(current)
          ancestors.forEach((ancestor) => next.add(ancestor))
          return next.size === current.size ? current : next
        })
        setSelectedId(id)
        setDraft({ ...fresh })
      }
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `移动失败：${error.body.message}` : '移动失败。')
    },
  })

  const requestSort = (action: MenuSortAction) => {
    if (selectedId == null) return
    const dirty = draft != null && selected != null && JSON.stringify(draft) !== JSON.stringify(selected)
    if (dirty && !window.confirm('当前有未保存的菜单修改，调整顺序后将丢弃这些修改。继续吗？')) return
    void sort.mutate({ id: selectedId, action })
  }

  const resetDrag = () => {
    draggedIdRef.current = null
    setDraggedId(null)
    setDropTarget(null)
  }

  const closeContextMenu = () => setContextMenu(null)

  useEffect(() => {
    if (!contextMenu) return
    const close = () => setContextMenu(null)
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') close()
    }
    window.addEventListener('click', close)
    window.addEventListener('scroll', close, true)
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('click', close)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('keydown', onKey)
    }
  }, [contextMenu])

  const openContextMenu = (module: MenuAdminModule) => (event: React.MouseEvent) => {
    event.preventDefault()
    event.stopPropagation()
    selectModule(module)
    setContextMenu({
      x: Math.max(4, Math.min(event.clientX, window.innerWidth - 190)),
      y: Math.max(4, Math.min(event.clientY, window.innerHeight - 170)),
      module,
    })
  }

  const startRename = (module: MenuAdminModule) => {
    closeContextMenu()
    setEditingId(module.M_IDX)
    setRenameValue(module.M_DESC)
  }

  const commitRename = (module: MenuAdminModule) => {
    const value = renameValue.trim()
    setEditingId(null)
    if (value && value !== module.M_DESC) {
      rename.mutate({ id: module.M_IDX, description: value })
    }
  }

  const toggleEnabled = (module: MenuAdminModule) => {
    closeContextMenu()
    setEnabled.mutate({ id: module.M_IDX, enabled: !module.M_TAG })
  }

  /** 取消编辑：丢弃模块表单、默认查询列与行为动作的未保存改动。 */
  const discardDraft = () => {
    setDraft(selected ? { ...selected } : null)
    setDefaultColumnDrafts(null)
    setPublishResult(null)
    setPublishError(null)
    if (selectedId != null) void queryClient.invalidateQueries({ queryKey: ['module-business-config', selectedId] })
  }

  const addChildOf = (module: MenuAdminModule) => {
    closeContextMenu()
    setSelectedId(null)
    setDraft(emptyDraft(module.M_IDX))
    setExpandedIds((current) => {
      const next = new Set(current)
      next.add(module.M_IDX)
      return next
    })
  }

  const requestDelete = (module: MenuAdminModule) => {
    closeContextMenu()
    if (window.confirm(`确定删除菜单节点「${module.M_DESC}」吗？有下级菜单或已产生业务数据的模块不可删除。`)) {
      void remove.mutate(module.M_IDX)
    }
  }

  const openIconPicker = (module: MenuAdminModule) => {
    closeContextMenu()
    setIconPickerModule(module)
  }

  const iconEntries = useMemo(() => Object.entries(navigationIcons), [])

  const pickTable = (kind: 'master' | 'detail', tableId: string, tableDesc: string | null) => {
    patch((d) => ({
      ...d,
      ...(kind === 'master'
        ? { MASTER_TABLE: tableId, MASTER_TABLE_DESC: tableDesc }
        : { DETAIL_TABLE: tableId, DETAIL_TABLE_DESC: tableDesc }),
    }))
    setTableChooser(null)
  }

  const openTableChooser = (kind: 'master' | 'detail') => {
    setTableChooser(kind)
  }

  const tableKindLabel = (kind: string | null) => {
    const upper = kind?.toUpperCase()
    if (upper === 'P') return '主表'
    if (upper === 'S') return '副表'
    return upper ? upper : '其他'
  }

  /** 主表过滤条件文本的即时可读性判断（构建器语法子集；高级写法仍可手动编辑）。 */
  const filterStatus = useMemo(() => {
    const value = draft?.FILTER ?? ''
    if (!value.trim()) return { className: 'text-secondary small mt-1', text: '留空表示无行级限制' }
    const parsed = parseFilter(value)
    if (parsed == null) {
      return {
        className: 'text-warning small mt-1',
        text: '当前文本无法由构建器解析（可能是 ISNULL、函数或列运算等高级写法，或格式不完整），建议点击「构建…」检查',
      }
    }
    return { className: 'text-success small mt-1', text: `格式有效：${parsed.length} 个条件` }
  }, [draft?.FILTER])

  const handleDragStart = (module: MenuAdminModule) => (event: React.DragEvent) => {
    draggedIdRef.current = module.M_IDX
    setDraggedId(module.M_IDX)
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', String(module.M_IDX))
  }

  const handleDragOver = (module: MenuAdminModule) => (event: React.DragEvent) => {
    const dragId = draggedIdRef.current
    if (dragId == null || dragId === module.M_IDX || isDescendantOf(dragId, module.M_IDX)) return
    event.preventDefault()
    event.stopPropagation()
    const mode = dropModeFor(event.clientY, event.currentTarget.getBoundingClientRect(), parentIds.has(module.M_IDX))
    setDropTarget((current) => (current?.id === module.M_IDX && current.mode === mode ? current : { id: module.M_IDX, mode }))
  }

  const handleDrop = (module: MenuAdminModule) => (event: React.DragEvent) => {
    event.preventDefault()
    event.stopPropagation()
    const dragId = draggedIdRef.current
    if (dragId == null || dragId === module.M_IDX || isDescendantOf(dragId, module.M_IDX)) {
      resetDrag()
      return
    }
    const mode = dropModeFor(event.clientY, event.currentTarget.getBoundingClientRect(), parentIds.has(module.M_IDX))
    let parentId: number | null
    let beforeId: number | null
    if (mode === 'into') {
      parentId = module.M_IDX
      beforeId = null
    } else {
      parentId = module.M_P_IDX != null && module.M_P_IDX > 0 ? module.M_P_IDX : null
      beforeId = mode === 'before' ? module.M_IDX : nextSiblingId(module.M_IDX)
    }
    if (wouldChange(dragId, parentId, beforeId)) {
      void move.mutate({ id: dragId, parentId, beforeId })
    }
    resetDrag()
  }

  const openDefaultColumns = async (table: 'master' | 'detail') => {
    if (selectedId == null) {
      window.alert('请先保存该菜单节点，再设置默认查询列。')
      return
    }
    setDefaultColumnsLoading(true)
    setDefaultColumnsError(null)
    try {
      const data = await apiClient.get<DefaultColumnsResponse>(`/admin/menus/${selectedId}/default-columns`, { query: { table } })
      setDefaultColumnGroups([{
        id: 'default',
        label: table === 'master' ? '主表字段' : '副表字段',
        fields: data.fields.map((field) => ({ key: field.key, label: field.label })),
        visibleKeys: data.fields.filter((field) => field.isSelected).sort((a, b) => a.order - b.order).map((field) => field.key),
        defaultKeys: [],
      }])
      setDefaultColumnsOpen({ table })
    } catch (error) {
      setDefaultColumnsError(describeApiError(error, '加载默认查询列失败。'))
    } finally {
      setDefaultColumnsLoading(false)
    }
  }

  const selectModule = (module: MenuAdminModule) => {
    setSelectedId(module.M_IDX)
    setDraft({ ...module })
    setFormTab('basic')
  }

  const startNewRoot = () => {
    setSelectedId(null)
    setDraft(emptyDraft(null))
    setFormTab('basic')
  }

  const startNewChild = () => {
    if (selectedId == null) {
      window.alert('请先在左侧选择一父级菜单，然后再增加子节点。')
      return
    }
    setSelectedId(null)
    setDraft(emptyDraft(selectedId))
    setFormTab('basic')
  }

  const renderTree = (entries: TreeEntry[], depth: number) =>
    entries.map((entry) => {
      const module = entry.module
      const hasChildren = entry.children.length > 0
      const searching = treeQuery.trim().length > 0
      const expanded = searching || expandedIds.has(module.M_IDX)
      const active = module.M_IDX === selectedId
      const isDragging = draggedId === module.M_IDX
      const dropClass = dropTarget?.id === module.M_IDX ? ` erp-drop-${dropTarget.mode}` : ''
      const nodeClass = `erp-menu-node${dropClass}${isDragging ? ' erp-menu-dragging' : ''}`
      const Icon = navigationIcons[module.Icon ?? 'folder'] ?? IconFolder
      const label = editingId === module.M_IDX ? (
        <span className="nav-link-title erp-menu-rename-wrap" onClick={(event) => event.stopPropagation()}>
          <input
            className="form-control form-control-sm erp-menu-rename-input"
            autoFocus
            value={renameValue}
            onChange={(event) => setRenameValue(event.target.value)}
            onBlur={() => commitRename(module)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') commitRename(module)
              if (event.key === 'Escape') setEditingId(null)
            }}
          />
        </span>
      ) : (
        <span className="nav-link-title">
          <span className={module.M_TAG ? '' : 'text-secondary'}>{module.M_DESC || '（未命名）'}</span>
          <span className="erp-menu-tree-id">{module.M_IDX}</span>
          {!module.M_TAG && <span className="badge bg-secondary-subtle text-secondary ms-1">停用</span>}
        </span>
      )
      const dragProps = {
        draggable: true,
        onDragStart: handleDragStart(module),
        onDragEnd: resetDrag,
        onDragOver: handleDragOver(module),
        onDrop: handleDrop(module),
        onContextMenu: openContextMenu(module),
      }
      const groupStyle =
        depth > 0 ? ({ '--menu-gpad': `${groupPad(depth + 1)}px` } as React.CSSProperties) : undefined
      const childStyle =
        depth > 0
          ? ({ '--menu-cpad': `${childPad(depth + 1)}px`, '--menu-dot': `${dotLeft(depth + 1)}px` } as React.CSSProperties)
          : undefined
      return (
        <Fragment key={module.M_IDX}>
          {hasChildren ? (
            <>
              <div className={`${nodeClass} erp-nav-group erp-nav-group-depth-${depth + 1}`} style={groupStyle} {...dragProps}>
                <button
                  type="button"
                  className={`nav-link erp-nav-group-toggle ${active ? 'group-active' : ''}`}
                  aria-expanded={expanded}
                  onClick={() => {
                    selectModule(module)
                    setExpandedIds((current) => {
                      const next = new Set(current)
                      if (next.has(module.M_IDX)) next.delete(module.M_IDX)
                      else next.add(module.M_IDX)
                      return next
                    })
                  }}
                >
                  {depth === 0 && <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>}
                  {depth > 0 && <IconChevronRight className="erp-nav-chevron" size={13} />}
                  {label}
                </button>
              </div>
              {expanded && (
                <div
                  className={`erp-nav-children erp-nav-children-depth-${depth + 2}`}
                  style={{ '--menu-line': `${lineTree(depth + 2)}px` } as React.CSSProperties}
                >
                  {renderTree(entry.children, depth + 1)}
                </div>
              )}
            </>
          ) : (
            <div className={nodeClass} {...dragProps}>
              <button
                type="button"
                className={`nav-link ${depth > 0 ? `erp-nav-child erp-nav-child-depth-${depth + 1}` : ''} ${active ? 'active' : ''}`}
                onClick={() => selectModule(module)}
                style={childStyle}
              >
                {depth === 0 && <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>}
                {label}
              </button>
            </div>
          )}
        </Fragment>
      )
    })

  const patch = (updater: (draft: MenuAdminModule) => MenuAdminModule) => {
    if (draft) setDraft(updater(draft))
  }

  const setGroup = (index: number, field: 'enabled' | 'expression' | 'description', value: boolean | string | null) => {
    patch((d) => {
      const key = field === 'enabled' ? `GROUP${index}` : field === 'expression' ? `GROUP_EXP${index}` : `GROUP_DESC${index}`
      return { ...d, [key]: value } as MenuAdminModule
    })
  }

  const errorMessage = describeApiError(modules.error, '发生未知错误，请稍后重试。')

  return (
    <div className="erp-menu-admin d-flex flex-column gap-2">
      <div className="card">
        <div className="card-header d-flex align-items-center gap-2">
          <strong>{selected ? `已选择：${selected.M_DESC}（ID：${selected.M_IDX}）` : '已选择：—'}</strong>
          <div className="ms-auto d-flex gap-2">
            <Button size="sm" icon={<IconPlus size={16} />} onClick={startNewRoot}>新增根节点</Button>
            <Button size="sm" icon={<IconPlus size={16} />} onClick={startNewChild}>新增子节点</Button>
            <Button size="sm" icon={<IconHistory size={16} />} title="查看该模块的历史发布版本" disabled={selectedId == null} onClick={() => setVersionsOpen(true)}>版本历史</Button>
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void modules.refetch()}>刷新</Button>
          </div>
        </div>
        <div className="card-body p-0">
          <div className="row g-0">
            <div className="col-lg-5 border-end erp-menu-tree">
              <div className="erp-menu-tree-header">
                <div className="erp-menu-tree-toolbar p-2 border-bottom d-flex gap-1 align-items-center">
                  <div className="erp-nav-search erp-menu-search">
                    <IconSearch size={16} aria-hidden="true" />
                    <input
                      type="search"
                      value={treeQuery}
                      onChange={(event) => setTreeQuery(event.target.value)}
                      placeholder="搜索菜单…"
                      aria-label="搜索菜单树"
                    />
                    {treeQuery && (
                      <button type="button" className="erp-nav-search-clear" aria-label="清除搜索" onClick={() => setTreeQuery('')}>×</button>
                    )}
                  </div>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => setExpandedIds(allExpanded ? new Set() : new Set(parentIds))}
                  >
                    {allExpanded ? '折叠全部' : '展开全部'}
                  </Button>
                </div>
                <div className="erp-menu-sort-bar d-flex gap-1 p-2 border-bottom align-items-center">
                  <Button size="sm" variant="secondary" icon={<IconArrowBarToUp size={16} />} title="将所选菜单移动到同级最前" disabled={!canMoveUp} onClick={() => requestSort('top')}>最前</Button>
                  <Button size="sm" variant="secondary" icon={<IconArrowUp size={16} />} title="将所选菜单向前移动一位" disabled={!canMoveUp} onClick={() => requestSort('up')}>前一步</Button>
                  <Button size="sm" variant="secondary" icon={<IconArrowDown size={16} />} title="将所选菜单向后移动一位" disabled={!canMoveDown} onClick={() => requestSort('down')}>后一步</Button>
                  <Button size="sm" variant="secondary" icon={<IconArrowBarToDown size={16} />} title="将所选菜单移动到同级最后" disabled={!canMoveDown} onClick={() => requestSort('bottom')}>最后</Button>
                </div>
              </div>
              {modules.isPending ? <LoadingState label="正在加载菜单…" /> : modules.isError ? (
                <ErrorState message={errorMessage} onRetry={() => void modules.refetch()} />
              ) : (
                <div className="p-2">{renderTree(tree, 0)}</div>
              )}
            </div>
            {contextMenu && (
              <div
                className="erp-menu-context-menu"
                style={{ left: contextMenu.x, top: contextMenu.y }}
                role="menu"
                onClick={(event) => event.stopPropagation()}
              >
                <button type="button" role="menuitem" onClick={() => startRename(contextMenu.module)}>
                  <IconEdit size={14} /> 重命名
                </button>
                <button type="button" role="menuitem" onClick={() => toggleEnabled(contextMenu.module)}>
                  <IconPower size={14} /> {contextMenu.module.M_TAG ? '停用' : '启用'}
                </button>
                <button type="button" role="menuitem" onClick={() => addChildOf(contextMenu.module)}>
                  <IconPlus size={14} /> 添加子节点
                </button>
                {parentKeyOf(contextMenu.module) === 0 && (
                  <button type="button" role="menuitem" onClick={() => openIconPicker(contextMenu.module)}>
                    <IconPhoto size={14} /> 更换图标
                  </button>
                )}
                <button type="button" role="menuitem" className="text-danger" onClick={() => requestDelete(contextMenu.module)}>
                  <IconTrash size={14} /> 删除
                </button>
              </div>
            )}
            <div className="col-lg-7">
              {draft ? (
                <div className="p-3 erp-menu-form">
                  <TabbedPanel tabs={MENU_FORM_TABS} activeKey={formTab} onActiveKeyChange={setFormTab}>
                    {formTab === 'basic' && (
                      <>
                        <div className="row g-2">
                          <div className="col-6">
                            <Input label="菜单名称" value={draft.M_DESC} onChange={(value) => patch((d) => ({ ...d, M_DESC: value }))} />
                          </div>
                          <div className="col-6">
                            <Input label="菜单别名" value={draft.M_ALIAS ?? ''} onChange={(value) => patch((d) => ({ ...d, M_ALIAS: value || null }))} />
                          </div>
                        </div>
                        <Input label="页面链接（现代路由）" value={draft.M_URL ?? ''} placeholder="如 /workbench、/admin/menus（承载页不带编号）" onChange={(value) => patch((d) => ({ ...d, M_URL: value || null }))} />
                        <div className="row g-2">
                          <div className="col-6">
                            <Input label="新增URL地址" value={draft.NEW_URL ?? ''} placeholder="/workbench/{moduleId}/new 或精确路径" onChange={(value) => patch((d) => ({ ...d, NEW_URL: value || null }))} />
                          </div>
                          <div className="col-6">
                            <Input label="修改URL地址" value={draft.MODI_URL ?? ''} placeholder="/workbench/{moduleId}/edit 或精确路径" onChange={(value) => patch((d) => ({ ...d, MODI_URL: value || null }))} />
                          </div>
                        </div>
                        <div className="text-secondary small mb-2">新增/修改 URL 留空表示回退统一表单；保存时按现代路由契约校验（承载页不带编号，动作路由支持 {'{moduleId}'} 模板）。</div>
                        <Input label="帮助文件URL地址" value={draft.HELP_URL ?? ''} onChange={(value) => patch((d) => ({ ...d, HELP_URL: value || null }))} />
                      </>
                    )}
                    {formTab === 'master' && (
                      <>
                        <div className="row g-2">
                          <div className="col-6">
                            <Input label="操作主表名" readOnly value={draft.MASTER_TABLE ?? ''} onChange={(value) => patch((d) => ({ ...d, MASTER_TABLE: value || null }))} />
                            <div className="d-flex gap-2">
                              <Button size="sm" onClick={() => openTableChooser('master')}>选择…</Button>
                              <Button size="sm" icon={<IconColumns size={14} />} onClick={() => void openDefaultColumns('master')} disabled={!draft.MASTER_TABLE || selectedId == null}>默认列</Button>
                              <Button size="sm" variant="ghost" title="清除操作主表名" onClick={() => patch((d) => ({ ...d, MASTER_TABLE: null, MASTER_TABLE_DESC: null }))}>清除</Button>
                            </div>
                          </div>
                          <div className="col-6">
                            <Input label="主表过滤条件" readOnly value={draft.FILTER ?? ''} onChange={(value) => patch((d) => ({ ...d, FILTER: value || null }))} />
                            <div className={filterStatus.className}>{filterStatus.text}</div>
                            <div className="d-flex gap-2">
                              <Button size="sm" title="构建主表过滤条件" onClick={() => setFilterBuilderOpen(true)} disabled={!draft.MASTER_TABLE}>构建…</Button>
                              <Button size="sm" variant="ghost" title="清除主表过滤条件" onClick={() => patch((d) => ({ ...d, FILTER: null }))}>清除</Button>
                            </div>
                          </div>
                        </div>
                        {masterMissingConfirmTag && (
                          <div className="alert alert-warning py-2 px-3 small mb-2">
                            主表缺少 CONFIRM_TAG：当前配置具备批核能力（自动批核/批核过程/批核按钮），发布时将被 lifecycle_columns 门拦截。请补列后重发布，或关闭批核能力（ADR-013 §3.7）。
                          </div>
                        )}
                        <div className="row g-2">
                          <div className="col-6">
                            <Input label="排序字段" readOnly value={draft.SORT_FIELDS ?? ''} onChange={(value) => patch((d) => ({ ...d, SORT_FIELDS: value || null }))} />
                            <div className="d-flex gap-2">
                              <Button size="sm" title="选择排序字段" onClick={() => setFieldPicker({ target: 'sortFields' })} disabled={!draft.MASTER_TABLE}>选择…</Button>
                              <Button size="sm" variant="ghost" title="清除排序字段" onClick={() => patch((d) => ({ ...d, SORT_FIELDS: null }))}>清除</Button>
                            </div>
                          </div>
                          <div className="col-6">
                            <Input label="字段有值时不可解批（主表）" readOnly value={draft.NOT_BACK_FIELDS_M ?? ''} onChange={(value) => patch((d) => ({ ...d, NOT_BACK_FIELDS_M: value || null }))} />
                            <div className="d-flex gap-2">
                              <Button size="sm" title="选择不可解批主表字段" onClick={() => setFieldPicker({ target: 'notBackM' })} disabled={!draft.MASTER_TABLE}>选择…</Button>
                              <Button size="sm" variant="ghost" title="清除不可解批主表字段" onClick={() => patch((d) => ({ ...d, NOT_BACK_FIELDS_M: null }))}>清除</Button>
                            </div>
                          </div>
                        </div>
                        <div className="text-secondary small fw-semibold mt-2 mb-1">单据行为</div>
                        <div className="d-flex flex-wrap gap-3">
                          <Checkbox label="通用查询（主表）" checked={draft.SEARCH_1} onChange={(checked) => patch((d) => ({ ...d, SEARCH_1: checked }))} />
                          <Checkbox label="自动批核" checked={draft.AUTO_APPROVE} onChange={(checked) => patch((d) => ({ ...d, AUTO_APPROVE: checked }))} />
                          <Checkbox label="可以复制" checked={draft.IF_COPY} onChange={(checked) => patch((d) => ({ ...d, IF_COPY: checked }))} />
                          <Checkbox label="异常记录不可保存" checked={draft.ERROR_NO_SAVE} onChange={(checked) => patch((d) => ({ ...d, ERROR_NO_SAVE: checked }))} />
                          <Checkbox label="效果引擎（灰度开关）" checked={draft.EFFECT_ENGINE_TAG} onChange={(checked) => patch((d) => ({ ...d, EFFECT_ENGINE_TAG: checked }))} />
                        </div>
                      </>
                    )}
                    {formTab === 'detail' && (
                      <>
                        <div className="row g-2">
                          <div className="col-6">
                            <Input label="操作副表名" readOnly value={draft.DETAIL_TABLE ?? ''} onChange={(value) => patch((d) => ({ ...d, DETAIL_TABLE: value || null }))} />
                            <div className="d-flex gap-2">
                              <Button size="sm" onClick={() => openTableChooser('detail')}>选择…</Button>
                              <Button size="sm" icon={<IconColumns size={14} />} onClick={() => void openDefaultColumns('detail')} disabled={!draft.DETAIL_TABLE || selectedId == null}>默认列</Button>
                              <Button size="sm" variant="ghost" title="清除操作副表名" onClick={() => patch((d) => ({ ...d, DETAIL_TABLE: null, DETAIL_TABLE_DESC: null }))}>清除</Button>
                            </div>
                          </div>
                          <div className="col-6">
                            <Input label="新增明细时必需字段" readOnly value={draft.DETAIL_NO_FIELDS ?? ''} onChange={(value) => patch((d) => ({ ...d, DETAIL_NO_FIELDS: value || null }))} />
                            <div className="d-flex gap-2">
                              <Button size="sm" title="选择新增明细必需字段" onClick={() => setFieldPicker({ target: 'detailNoFields' })} disabled={!draft.DETAIL_TABLE}>选择…</Button>
                              <Button size="sm" variant="ghost" title="清除新增明细必需字段" onClick={() => patch((d) => ({ ...d, DETAIL_NO_FIELDS: null }))}>清除</Button>
                            </div>
                          </div>
                        </div>
                        <div className="row g-2">
                          <div className="col-6">
                            <Input label="字段有值时不可解批（副表）" readOnly value={draft.NOT_BACK_FIELDS ?? ''} onChange={(value) => patch((d) => ({ ...d, NOT_BACK_FIELDS: value || null }))} />
                            <div className="d-flex gap-2">
                              <Button size="sm" title="选择不可解批副表字段" onClick={() => setFieldPicker({ target: 'notBack' })} disabled={!draft.DETAIL_TABLE}>选择…</Button>
                              <Button size="sm" variant="ghost" title="清除不可解批副表字段" onClick={() => patch((d) => ({ ...d, NOT_BACK_FIELDS: null }))}>清除</Button>
                            </div>
                          </div>
                        </div>
                        <div className="d-flex flex-wrap gap-3 my-2">
                          <Checkbox label="通用查询（副表）" checked={draft.SEARCH_2} onChange={(checked) => patch((d) => ({ ...d, SEARCH_2: checked }))} />
                          <Checkbox label="无明细资料不可保存" checked={draft.DETAIL_NO_SAVE} onChange={(checked) => patch((d) => ({ ...d, DETAIL_NO_SAVE: checked }))} />
                        </div>
                      </>
                    )}
                    {formTab === 'group' && (
                      <>
                        {[1, 2, 3, 4, 5].map((index) => (
                          <div className="card mb-2 erp-menu-group-card" key={index}>
                            <div className="card-body py-2 px-3">
                              <div className="d-flex align-items-center gap-3">
                                <Checkbox
                                  label={`分组表达式${index}`}
                                  checked={draft[`GROUP${index}` as keyof MenuAdminModule] as boolean}
                                  onChange={(checked) => setGroup(index, 'enabled', checked)}
                                />
                                <Input
                                  label={`表达式描述${index}`}
                                  value={(draft[`GROUP_DESC${index}` as keyof MenuAdminModule] as string | null) ?? ''}
                                  onChange={(value) => setGroup(index, 'description', value || null)}
                                />
                              </div>
                              <Input
                                label={`表达式${index}（如 TABLE.COL、CASE 或日期函数）`}
                                value={(draft[`GROUP_EXP${index}` as keyof MenuAdminModule] as string | null) ?? ''}
                                onChange={(value) => setGroup(index, 'expression', value || null)}
                              />
                            </div>
                          </div>
                        ))}
                      </>
                    )}
                    {formTab === 'form' && (
                      <div className="card mb-2 erp-menu-form-card">
                        <div className="card-header py-2 px-3"><strong className="fs-6">统一表单设置</strong></div>
                        <div className="card-body py-2 px-3 row g-2">
                          <div className="col-12">
                            <Input
                              label="页签定义（FORM_TABS）"
                              value={draft.FORM_TABS ?? ''}
                              placeholder="如 1=客户订单--1;2=客户订单--2；留空为单页签"
                              onChange={(value) => patch((d) => ({ ...d, FORM_TABS: value || null }))}
                            />
                          </div>
                          <div className="col-6">
                            <Input
                              label="每行对数（FORM_COLUMNS）"
                              value={draft.FORM_COLUMNS == null ? '' : String(draft.FORM_COLUMNS)}
                              placeholder="留空默认 2"
                              onChange={(value) => patch((d) => ({ ...d, FORM_COLUMNS: value === '' ? null : Math.max(1, Math.min(6, Number(value) || 2)) }))}
                            />
                          </div>
                          <div className="col-6">
                            <Input
                              label="业务按钮（FORM_BUTTONS）"
                              value={draft.FORM_BUTTONS ?? ''}
                              placeholder="受控注册码，如 GEN_ORDER;FINISH_CASE"
                              onChange={(value) => patch((d) => ({ ...d, FORM_BUTTONS: value || null }))}
                            />
                          </div>
                        </div>
                      </div>
                    )}
                    {formTab === 'actions' && (
                      <BusinessActionsPanel module={draft} onDraftChange={handleActionsDraftChange} />
                    )}
                  </TabbedPanel>
                  {publishError && (
                    <div className="alert alert-danger py-2 mb-0 mt-3" role="alert">发布失败：{publishError}</div>
                  )}
                  {publishResult?.published && (
                    <div className="alert alert-success py-2 mb-0 mt-3" role="alert">
                      发布成功：{publishResult.definitionVersion ?? `module-${publishResult.moduleId}-v${publishResult.version}`}。
                    </div>
                  )}
                  {publishResult && !publishResult.published && (
                    <div className="alert alert-warning py-2 mb-0 mt-3" role="alert">
                      发布未通过校验，未写入新快照：
                      <ul className="mb-0 mt-1">
                        {publishResult.checks
                          .filter((check) => !check.passed)
                          .map((check) => (
                            <li key={check.code}>{check.message}</li>
                          ))}
                      </ul>
                    </div>
                  )}
                  <div className="d-flex align-items-center gap-2 mt-3 flex-wrap">
                    <ErpCommandBar
                      items={[
                        { action: 'save', label: '保存', variant: 'primary', loading: save.isPending, onClick: () => void save.mutate(draft) },
                        {
                          action: 'publish',
                          label: '发布',
                          icon: <IconRocket size={16} />,
                          loading: publish.isPending,
                          disabled: save.isPending,
                          onClick: () => publish.mutate(),
                        },
                        { action: 'cancel', label: '取消', onClick: discardDraft },
                      ]}
                    />
                    {stateBadge && (
                      <span className={`badge ${stateBadge.className}`} title={stateBadge.title}>{stateBadge.label}</span>
                    )}
                  </div>
                  {selected?.LAST_UPDATE_BY && (
                    <div className="text-secondary mt-2" style={{ fontSize: 12 }}>
                      最后更新：{selected.LAST_UPDATE_BY}（{selected.LAST_UPDATE_DATE ? new Date(selected.LAST_UPDATE_DATE).toLocaleString() : '—'}）
                    </div>
                  )}
                </div>
              ) : (
                <div className="p-4 text-secondary text-center">请在左侧选择菜单节点进行编辑，或点击「新增根节点 / 新增子节点」。</div>
              )}
            </div>
          </div>
        </div>
      </div>
      <UnifiedChooser
        open={tableChooser !== null}
        title={tableChooser === 'master' ? '选择操作主表' : '选择操作副表'}
        source={{ kind: 'sourceKey', key: 'menu-admin.tables' }}
        getRowId={(row) => String(row.T_ID)}
        mode="single"
        onPick={(rows) => {
          const row = rows[0]
          if (row && tableChooser) pickTable(tableChooser, String(row.T_ID), row.T_DESC == null ? null : String(row.T_DESC))
        }}
        onClose={() => setTableChooser(null)}
        searchPlaceholder="搜索表名/描述…"
        columnRenderers={{
          T_KIND: (row) => tableKindLabel(row.T_KIND as string | null),
        }}
        emptyText="没有匹配的表。"
      />
      {iconPickerModule && (
        <Modal
          title={`更换图标：${iconPickerModule.M_DESC}`}
          onClose={() => setIconPickerModule(null)}
          dialogClassName="erp-dialog-lg"
        >
          <div className="erp-menu-icon-grid">
                  <button
                    type="button"
                    className={`erp-menu-icon-option${iconPickerModule.M_ICON ? '' : ' active'}`}
                    title="默认图标"
                    onClick={() => {
                      void save.mutate({ ...iconPickerModule, M_ICON: null })
                      setIconPickerModule(null)
                    }}
                  >
                    <IconFolder size={20} />
                    <span>默认</span>
                  </button>
                  {iconEntries.map(([name, IconComponent]) => (
                    <button
                      key={name}
                      type="button"
                      className={`erp-menu-icon-option${iconPickerModule.M_ICON === name ? ' active' : ''}`}
                      title={name}
                      onClick={() => {
                        void save.mutate({ ...iconPickerModule, M_ICON: name })
                        setIconPickerModule(null)
                      }}
                    >
                      <IconComponent size={20} />
                      <span>{name}</span>
                    </button>
                  ))}
          </div>
        </Modal>
      )}
      <MenuFieldPicker
        open={fieldPicker !== null}
        title={fieldPicker?.target === 'sortFields' ? '选择排序字段（可升/降序）' : '选择字段'}
        table={fieldPicker === null
          ? null
          : fieldPicker.target === 'sortFields' || fieldPicker.target === 'notBackM'
            ? draft?.MASTER_TABLE ?? null
            : draft?.DETAIL_TABLE ?? null}
        mode={fieldPicker?.target === 'sortFields' ? 'sort' : 'multi'}
        value={fieldPicker === null || draft === null
          ? ''
          : draft[fieldPicker.target === 'sortFields' ? 'SORT_FIELDS' : fieldPicker.target === 'detailNoFields' ? 'DETAIL_NO_FIELDS' : fieldPicker.target === 'notBackM' ? 'NOT_BACK_FIELDS_M' : 'NOT_BACK_FIELDS'] ?? ''}
        onSave={(value) => {
          if (fieldPicker) {
            const key = fieldPicker.target === 'sortFields' ? 'SORT_FIELDS' : fieldPicker.target === 'detailNoFields' ? 'DETAIL_NO_FIELDS' : fieldPicker.target === 'notBackM' ? 'NOT_BACK_FIELDS_M' : 'NOT_BACK_FIELDS'
            patch((d) => ({ ...d, [key]: value || null }))
          }
        }}
        onClose={() => setFieldPicker(null)}
      />
      <MenuFilterBuilder
        open={filterBuilderOpen}
        table={draft?.MASTER_TABLE ?? null}
        value={draft?.FILTER ?? ''}
        onSave={(value) => patch((d) => ({ ...d, FILTER: value || null }))}
        onClose={() => setFilterBuilderOpen(false)}
      />
      {versionsOpen && (
        <Modal
          title={`版本历史：${selected?.M_DESC ?? ''}`}
          onClose={() => setVersionsOpen(false)}
          size="lg"
          scrollable
          footer={<div className="d-flex justify-content-end"><Button variant="secondary" onClick={() => setVersionsOpen(false)}>关闭</Button></div>}
        >
          {versions.isPending ? <LoadingState label="正在加载版本…" /> : versions.isError ? (
            <ErrorState message={describeApiError(versions.error, '加载版本历史失败。')} onRetry={() => void versions.refetch()} />
          ) : (
            <ErpTable
              columns={versionColumns}
              data={versions.data ?? []}
              getRowId={(row) => String(row.version)}
              clientSideSorting
              copyable={false}
              empty={<div className="p-3 text-secondary">该模块尚未发布过定义快照。</div>}
            />
          )}
        </Modal>
      )}
      <ErpColumnSelector
        open={defaultColumnsOpen !== null}
        title={defaultColumnsOpen?.table === 'detail' ? '副表默认查询列' : '主表默认查询列'}
        groups={defaultColumnGroups}
        loading={defaultColumnsLoading}
        loadError={defaultColumnsError}
        onRetry={() => defaultColumnsOpen && void openDefaultColumns(defaultColumnsOpen.table)}
        saving={false}
        onClose={() => { setDefaultColumnsOpen(null); setDefaultColumnGroups([]); setDefaultColumnsError(null) }}
        onSave={async (selection) => {
          // 只记录草稿，随「保存」与模块一起提交（同一事务）
          const table = defaultColumnsOpen?.table
          if (table) {
            const fieldIds = selection.default ?? []
            setDefaultColumnDrafts((current) => ({ ...(current ?? {}), [table]: fieldIds }))
          }
          setDefaultColumnsOpen(null)
          setDefaultColumnGroups([])
          setDefaultColumnsError(null)
        }}
      />
    </div>
  )
}
