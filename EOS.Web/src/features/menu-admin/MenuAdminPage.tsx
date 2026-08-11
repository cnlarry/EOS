import { IconChevronDown, IconColumns, IconFolder, IconFolderOpen, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpColumnSelector, type ColumnSelectorGroup } from '../../components/common/ErpColumnSelector'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

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
  UPDATE_SP: string | null
  AFTERSAVE_SP: string | null
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
}

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
  UPDATE_SP: null,
  AFTERSAVE_SP: null,
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
})

interface TreeEntry {
  module: MenuAdminModule
  children: TreeEntry[]
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

function Input({ label, value, onChange, type = 'text', placeholder }: {
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  placeholder?: string
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
  const modules = useQuery({ queryKey: ['menu-admin', 'modules'], queryFn: () => apiClient.get<{ total: number; modules: MenuAdminModule[] }>('/admin/menus') })

  const byId = useMemo(() => new Map((modules.data?.modules ?? []).map((module) => [module.M_IDX, module])), [modules.data])
  const tree = useMemo(() => buildTree(modules.data?.modules ?? []), [modules.data])
  const selected = selectedId != null ? byId.get(selectedId) ?? null : null

  const save = useMutation({
    mutationFn: async (input: MenuAdminModule) => {
      if (selectedId != null && byId.has(selectedId)) await apiClient.put(`/admin/menus/${selectedId}`, input)
      else await apiClient.post('/admin/menus', input)
    },
    onSuccess: async (_, input) => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      setSelectedId(input.M_IDX)
      setDraft(null)
      window.alert('菜单保存成功。')
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `保存失败：${error.body.message}` : '保存失败。')
    },
  })

  const remove = useMutation({
    mutationFn: async (id: number) => {
      await apiClient.delete(`/admin/menus/${id}`)
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['menu-admin', 'modules'] })
      setSelectedId(null)
      setDraft(null)
      window.alert('删除成功。')
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `删除失败：${error.body.message}` : '删除失败。')
    },
  })

  const saveDefaultColumns = useMutation({
    mutationFn: async (selection: Record<string, string[]>) => {
      if (selectedId == null || defaultColumnsOpen == null) return
      await apiClient.put(`/admin/menus/${selectedId}/default-columns`, { table: defaultColumnsOpen.table, fieldIds: selection.default ?? [] })
    },
    onSuccess: async () => {
      setDefaultColumnsOpen(null)
      setDefaultColumnGroups([])
      window.alert('默认查询列保存成功。')
    },
    onError: (error) => {
      window.alert(error instanceof ApiError ? `保存失败：${error.body.message}` : '保存失败。')
    },
  })

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
      setDefaultColumnsError(error instanceof ApiError ? error.body.message : '加载默认查询列失败。')
    } finally {
      setDefaultColumnsLoading(false)
    }
  }

  const selectModule = (module: MenuAdminModule) => {
    setSelectedId(module.M_IDX)
    setDraft({ ...module })
  }

  const startNewRoot = () => {
    setSelectedId(null)
    setDraft(emptyDraft(null))
  }

  const startNewChild = () => {
    if (selectedId == null) {
      window.alert('请先在左侧选择一父级菜单，然后再增加子节点。')
      return
    }
    setSelectedId(null)
    setDraft(emptyDraft(selectedId))
  }

  const renderTree = (entries: TreeEntry[], depth: number) =>
    entries.map((entry) => {
      const hasChildren = entry.children.length > 0
      const expanded = expandedIds.has(entry.module.M_IDX)
      const active = entry.module.M_IDX === selectedId
      return (
        <div key={entry.module.M_IDX}>
          <button
            type="button"
            className={`erp-menu-tree-row ${active ? 'active' : ''}`}
            style={{ paddingLeft: 10 + depth * 16 }}
            onClick={() => {
              selectModule(entry.module)
              if (hasChildren) {
                setExpandedIds((current) => {
                  const next = new Set(current)
                  if (next.has(entry.module.M_IDX)) next.delete(entry.module.M_IDX)
                  else next.add(entry.module.M_IDX)
                  return next
                })
              }
            }}
          >
            {hasChildren ? (expanded ? <IconFolderOpen size={15} /> : <IconChevronDown size={15} />) : <IconFolder size={14} />}
            <span className="erp-menu-tree-label">
              <span className="erp-menu-tree-id">{entry.module.M_IDX}</span>
              <span className={entry.module.M_TAG ? '' : 'text-secondary'}>{entry.module.M_DESC || '（未命名）'}</span>
            </span>
          </button>
          {hasChildren && expanded && renderTree(entry.children, depth + 1)}
        </div>
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

  const errorMessage = modules.error instanceof ApiError ? modules.error.body.message : '发生未知错误，请稍后重试。'

  return (
    <div className="erp-menu-admin d-grid gap-2">
      <div className="card">
        <div className="card-header d-flex align-items-center gap-2">
          <strong>菜单管理（模块 2301）</strong>
          <div className="ms-auto d-flex gap-2">
            <Button size="sm" icon={<IconPlus size={16} />} onClick={startNewRoot}>新增根节点</Button>
            <Button size="sm" icon={<IconPlus size={16} />} onClick={startNewChild}>新增子节点</Button>
            <Button size="sm" icon={<IconRefresh size={16} />} onClick={() => void modules.refetch()}>刷新</Button>
          </div>
        </div>
        <div className="card-body p-0">
          <div className="row g-0">
            <div className="col-lg-5 border-end erp-menu-tree">
              {modules.isPending ? <LoadingState label="正在加载菜单…" /> : modules.isError ? (
                <ErrorState message={errorMessage} onRetry={() => void modules.refetch()} />
              ) : (
                <div className="p-2">{renderTree(tree, 0)}</div>
              )}
            </div>
            <div className="col-lg-7">
              {draft ? (
                <div className="p-3 erp-menu-form">
                  <div className="row g-2">
                    <div className="col-3">
                      <Input label="菜单编号" type="number" value={String(draft.M_IDX || '')} onChange={(value) => patch((d) => ({ ...d, M_IDX: Number(value) || 0 }))} />
                    </div>
                    <div className="col-3">
                      <Input label="排序号" type="number" value={String(draft.SORT_IDX ?? 0)} onChange={(value) => patch((d) => ({ ...d, SORT_IDX: Number(value) || 0 }))} />
                    </div>
                    <div className="col-3">
                      <Input label="上级菜单" type="number" value={String(draft.M_P_IDX ?? 0)} onChange={(value) => patch((d) => ({ ...d, M_P_IDX: Number(value) || null }))} />
                    </div>
                    <div className="col-3 d-flex align-items-end pb-2">
                      <Checkbox label="启用" checked={draft.M_TAG} onChange={(checked) => patch((d) => ({ ...d, M_TAG: checked }))} />
                    </div>
                  </div>
                  <div className="row g-2">
                    <div className="col-6">
                      <Input label="菜单名称" value={draft.M_DESC} onChange={(value) => patch((d) => ({ ...d, M_DESC: value }))} />
                    </div>
                    <div className="col-6">
                      <Input label="菜单别名" value={draft.M_ALIAS ?? ''} onChange={(value) => patch((d) => ({ ...d, M_ALIAS: value || null }))} />
                    </div>
                  </div>
                  <Input label="链接URL地址" value={draft.M_URL ?? ''} onChange={(value) => patch((d) => ({ ...d, M_URL: value || null }))} />
                  <div className="row g-2">
                    <div className="col-6">
                      <Input label="新增URL地址" value={draft.NEW_URL ?? ''} onChange={(value) => patch((d) => ({ ...d, NEW_URL: value || null }))} />
                    </div>
                    <div className="col-6">
                      <Input label="修改URL地址" value={draft.MODI_URL ?? ''} onChange={(value) => patch((d) => ({ ...d, MODI_URL: value || null }))} />
                    </div>
                  </div>
                  <Input label="帮助文件URL地址" value={draft.HELP_URL ?? ''} onChange={(value) => patch((d) => ({ ...d, HELP_URL: value || null }))} />
                  <div className="row g-2">
                    <div className="col-6">
                      <Input label="操作主表名" value={draft.MASTER_TABLE ?? ''} onChange={(value) => patch((d) => ({ ...d, MASTER_TABLE: value || null }))} />
                      <Button size="sm" icon={<IconColumns size={14} />} onClick={() => void openDefaultColumns('master')} disabled={!draft.MASTER_TABLE}>默认查询（主表）</Button>
                    </div>
                    <div className="col-6">
                      <Input label="主表过滤条件" value={draft.FILTER ?? ''} onChange={(value) => patch((d) => ({ ...d, FILTER: value || null }))} />
                    </div>
                  </div>
                  <div className="row g-2">
                    <div className="col-6">
                      <Input label="操作副表名" value={draft.DETAIL_TABLE ?? ''} onChange={(value) => patch((d) => ({ ...d, DETAIL_TABLE: value || null }))} />
                      <Button size="sm" icon={<IconColumns size={14} />} onClick={() => void openDefaultColumns('detail')} disabled={!draft.DETAIL_TABLE}>默认查询（副表）</Button>
                    </div>
                    <div className="col-6">
                      <Input label="排序字段" value={draft.SORT_FIELDS ?? ''} onChange={(value) => patch((d) => ({ ...d, SORT_FIELDS: value || null }))} />
                    </div>
                  </div>
                  <div className="row g-2">
                    <div className="col-6">
                      <Input label="存盘后执行存储过程" value={draft.AFTERSAVE_SP ?? ''} onChange={(value) => patch((d) => ({ ...d, AFTERSAVE_SP: value || null }))} />
                    </div>
                    <div className="col-6">
                      <Input label="数据更新存储过程" value={draft.UPDATE_SP ?? ''} onChange={(value) => patch((d) => ({ ...d, UPDATE_SP: value || null }))} />
                    </div>
                  </div>
                  <div className="row g-2">
                    <div className="col-6">
                      <Input label="新增明细时必需字段" value={draft.DETAIL_NO_FIELDS ?? ''} onChange={(value) => patch((d) => ({ ...d, DETAIL_NO_FIELDS: value || null }))} />
                    </div>
                    <div className="col-6">
                      <Input label="字段有值时不可解批（主表）" value={draft.NOT_BACK_FIELDS_M ?? ''} onChange={(value) => patch((d) => ({ ...d, NOT_BACK_FIELDS_M: value || null }))} />
                    </div>
                  </div>
                  <Input label="字段有值时不可解批（副表）" value={draft.NOT_BACK_FIELDS ?? ''} onChange={(value) => patch((d) => ({ ...d, NOT_BACK_FIELDS: value || null }))} />
                  <div className="d-flex flex-wrap gap-3 my-2">
                    <Checkbox label="通用查询（主表）" checked={draft.SEARCH_1} onChange={(checked) => patch((d) => ({ ...d, SEARCH_1: checked }))} />
                    <Checkbox label="通用查询（副表）" checked={draft.SEARCH_2} onChange={(checked) => patch((d) => ({ ...d, SEARCH_2: checked }))} />
                    <Checkbox label="无明细资料不可保存" checked={draft.DETAIL_NO_SAVE} onChange={(checked) => patch((d) => ({ ...d, DETAIL_NO_SAVE: checked }))} />
                    <Checkbox label="自动批核" checked={draft.AUTO_APPROVE} onChange={(checked) => patch((d) => ({ ...d, AUTO_APPROVE: checked }))} />
                    <Checkbox label="可以复制" checked={draft.IF_COPY} onChange={(checked) => patch((d) => ({ ...d, IF_COPY: checked }))} />
                    <Checkbox label="异常记录不可保存" checked={draft.ERROR_NO_SAVE} onChange={(checked) => patch((d) => ({ ...d, ERROR_NO_SAVE: checked }))} />
                  </div>
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
                  <div className="d-flex gap-2 mt-3">
                    <Button size="sm" loading={save.isPending} onClick={() => void save.mutate(draft)}>保存</Button>
                    <Button size="sm" variant="secondary" onClick={() => setDraft(selected ? { ...selected } : null)}>取消</Button>
                    {selectedId != null && (
                      <Button
                        size="sm"
                        variant="danger"
                        icon={<IconTrash size={16} />}
                        loading={remove.isPending}
                        onClick={() => {
                          if (window.confirm(`确定删除菜单节点 ${selectedId} 及其全部子节点吗？此操作不可撤销。`)) void remove.mutate(selectedId)
                        }}
                      >
                        删除
                      </Button>
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
      <ErpColumnSelector
        open={defaultColumnsOpen !== null}
        title={defaultColumnsOpen?.table === 'detail' ? '副表默认查询列' : '主表默认查询列'}
        groups={defaultColumnGroups}
        loading={defaultColumnsLoading}
        loadError={defaultColumnsError}
        onRetry={() => defaultColumnsOpen && void openDefaultColumns(defaultColumnsOpen.table)}
        saving={saveDefaultColumns.isPending}
        onClose={() => { setDefaultColumnsOpen(null); setDefaultColumnGroups([]); setDefaultColumnsError(null) }}
        onSave={(selection) => saveDefaultColumns.mutateAsync(selection)}
      />
    </div>
  )
}
