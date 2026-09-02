import { IconArrowLeft, IconChevronRight, IconCopy, IconFolder, IconSearch, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { childPad, dotLeft, groupPad, lineTree } from '../../components/layout/menuDepth'
import { navigationIcons, resolveRootIcon } from '../../components/layout/navigationIcons'
import { PermissionEditPanel } from './PermissionEditPanel'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import {
  DENY_FIELDS,
  emptyModuleInput,
  rowToInput,
  type DenyFieldKey,
  type ModuleRightsInput,
  type ModuleRightsRow,
  type RightsFieldInfo,
  type RightsModuleFields,
} from './types'
import { SourceBadge } from './PermissionEditPanel'

interface RightsMatrixProps {
  open: boolean
  mode: 'user' | 'group'
  targetId: string
  title?: string
  onClose: () => void
  onSaved?: () => void
  /** 弹窗（默认，供用户权限页等）或完整页面（供 2305 组权限页）。 */
  variant?: 'modal' | 'page'
}

interface TreeNode {
  row: ModuleRightsRow
  children: TreeNode[]
}

interface FieldPickerState {
  denyKey: DenyFieldKey
  fields: RightsModuleFields | null
  loading: boolean
  selection: Set<string>
}

export function RightsMatrix({ open, mode, targetId, title, onClose, onSaved, variant = 'modal' }: RightsMatrixProps) {
  const url = mode === 'user'
    ? `/admin/users/${encodeURIComponent(targetId.trim())}/rights`
    : `/admin/groups/${encodeURIComponent(targetId.trim())}/rights`
  const matrix = useQuery({
    queryKey: ['rights-admin', mode, targetId, 'matrix'],
    queryFn: () => apiClient.get<ModuleRightsRow[]>(url),
    enabled: open,
    staleTime: Number.POSITIVE_INFINITY,
    refetchOnWindowFocus: false,
  })
  const rows = useMemo(() => matrix.data ?? [], [matrix.data])
  const byId = useMemo(() => new Map(rows.map((row) => [row.moduleId, row])), [rows])

  const [draft, setDraft] = useState<Record<number, ModuleRightsInput>>({})
  const [dirty, setDirty] = useState<Set<number>>(new Set())
  const [selected, setSelected] = useState<number | null>(null)
  const [expanded, setExpanded] = useState<Set<number>>(new Set())
  const [search, setSearch] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [fieldPicker, setFieldPicker] = useState<FieldPickerState | null>(null)
  const [copyOpen, setCopyOpen] = useState(false)

  useEffect(() => {
    if (!open || !matrix.data) return
    const loaded = matrix.data
    const nextDraft: Record<number, ModuleRightsInput> = {}
    loaded.forEach((row) => { nextDraft[row.moduleId] = rowToInput(row) })
    setDraft(nextDraft)
    setDirty(new Set())
    setExpanded(new Set(loaded.filter((row) => row.parentId === 0).map((row) => row.moduleId)))
    setSelected((current) => current !== null && loaded.some((row) => row.moduleId === current)
      ? current
      : loaded[0]?.moduleId ?? null)
    setError(null)
    setNotice(null)
    setFieldPicker(null)
    setCopyOpen(false)
  }, [open, matrix.data])

  const tree = useMemo<TreeNode[]>(() => {
    const children = new Map<number, ModuleRightsRow[]>()
    for (const row of rows) {
      const key = row.parentId !== 0 && byId.has(row.parentId) ? row.parentId : 0
      children.set(key, [...(children.get(key) ?? []), row])
    }
    const build = (parentId: number): TreeNode[] =>
      (children.get(parentId) ?? []).map((row) => ({ row, children: build(row.moduleId) }))
    return build(0)
  }, [rows, byId])

  const filteredIds = useMemo(() => {
    const text = search.trim().toLowerCase()
    if (!text) return new Set(rows.map((row) => row.moduleId))
    const ancestors = new Map<number, TreeNode>()
    const walk = (node: TreeNode, parent?: TreeNode) => {
      if (parent) ancestors.set(node.row.moduleId, parent)
      node.children.forEach((child) => walk(child, node))
    }
    tree.forEach((node) => walk(node))
    const matched = new Set<number>()
    const mark = (node: TreeNode): boolean => {
      const label = `${node.row.title} ${node.row.groupPath} ${node.row.moduleId}`.toLowerCase()
      if (label.includes(text)) {
        let current: TreeNode | undefined = node
        while (current) {
          matched.add(current.row.moduleId)
          current = ancestors.get(current.row.moduleId)
        }
        return true
      }
      const childHit = node.children.some(mark)
      if (childHit) matched.add(node.row.moduleId)
      return childHit
    }
    tree.forEach(mark)
    return matched
  }, [rows, search, tree])

  const visibleTree = useMemo(() => {
    const filterNode = (node: TreeNode): TreeNode | null => {
      const children = node.children.map(filterNode).filter((child): child is TreeNode => child !== null)
      if (filteredIds.has(node.row.moduleId) || children.length > 0) {
        return { row: node.row, children }
      }
      return null
    }
    return tree.map(filterNode).filter((node): node is TreeNode => node !== null)
  }, [tree, filteredIds])

  const selectedRow = selected === null ? null : byId.get(selected) ?? null
  const selectedDraft = selected === null ? null : draft[selected] ?? (selectedRow ? rowToInput(selectedRow) : null)
  const configuredCount = rows.filter((row) => row.hasPersonal || row.effective.source !== 'none').length

  const setDraftValue = <K extends keyof ModuleRightsInput>(moduleId: number, key: K, value: ModuleRightsInput[K]) => {
    setDraft((current) => ({
      ...current,
      [moduleId]: { ...(current[moduleId] ?? rowToInput(byId.get(moduleId)!)), [key]: value },
    }))
    setDirty((current) => new Set(current).add(moduleId))
    setError(null)
  }

  const toggleExpanded = (moduleId: number) => {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(moduleId)) next.delete(moduleId)
      else next.add(moduleId)
      return next
    })
  }

  const renderNode = (node: TreeNode, depth: number) => {
    const hasChildren = node.children.length > 0
    const isExpanded = expanded.has(node.row.moduleId)
    const isSelected = selected === node.row.moduleId
    const groupStyle = depth > 0
      ? ({ '--menu-gpad': `${groupPad(depth + 1)}px` } as CSSProperties)
      : undefined
    const childStyle = depth > 0
      ? ({ '--menu-cpad': `${childPad(depth + 1)}px`, '--menu-dot': `${dotLeft(depth + 1)}px` } as CSSProperties)
      : undefined
    const badge = <span className="ms-auto"><SourceBadge row={node.row} mode={mode} /></span>
    const RootIcon = navigationIcons[resolveRootIcon(node.row.moduleId, node.row.title)] ?? IconFolder
    const label = (
      <>
        <span className="rights-tree-label">{node.row.title}</span>
        {node.row.groupPath && <span className="text-secondary small">{node.row.groupPath}</span>}
      </>
    )
    if (hasChildren) {
      return (
        <div key={node.row.moduleId}>
          <div className={`erp-nav-group erp-nav-group-depth-${depth + 1}`} style={groupStyle}>
            <div
              className={`nav-link erp-nav-group-toggle ${isSelected ? 'group-active' : ''}`}
              role="button"
              tabIndex={0}
              aria-expanded={isExpanded}
              onClick={() => toggleExpanded(node.row.moduleId)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault()
                  toggleExpanded(node.row.moduleId)
                }
              }}
            >
              {depth === 0 ? (
                <span className="nav-link-icon"><RootIcon size={18} stroke={1.7} /></span>
              ) : (
                <IconChevronRight className="erp-nav-chevron" size={13} />
              )}
              <span className="nav-link-title">
                {label}
                {badge}
              </span>
            </div>
          </div>
          {isExpanded && (
            <div
              className={`erp-nav-children erp-nav-children-depth-${depth + 2}`}
              style={{ '--menu-line': `${lineTree(depth + 2)}px` } as CSSProperties}
            >
              {node.children.map((child) => renderNode(child, depth + 1))}
            </div>
          )}
        </div>
      )
    }
    const rootLeaf = depth === 0
    return (
      <div
        key={node.row.moduleId}
        className={rootLeaf
          ? `nav-link erp-rights-leaf-root ${isSelected ? 'active' : ''}`
          : `erp-nav-child erp-nav-child-depth-${depth + 1} ${isSelected ? 'active' : ''}`}
        style={childStyle}
        role="treeitem"
        aria-selected={isSelected}
        tabIndex={0}
        onClick={() => setSelected(node.row.moduleId)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            setSelected(node.row.moduleId)
          }
        }}
      >
        {rootLeaf && <span className="nav-link-icon"><RootIcon size={18} stroke={1.7} /></span>}
        {label}
        {badge}
      </div>
    )
  }

  const clearSelectedRights = () => {
    if (selected === null) {
      setNotice('请先在左侧选择要清空的模块。')
      return
    }
    const label = mode === 'user' ? '个人' : '组'
    const title = selectedRow?.title ?? String(selected)
    if (!window.confirm(mode === 'user'
      ? `确定清空「${title}」的个人权限吗？\n保存后该模块个人记录将删除，权限回退到用户组（若没有组权限则无权限）。`
      : `确定清空「${title}」的组权限吗？\n保存后该模块组记录将删除，组内用户将失去对应模块权限。`)) return
    setDraft((current) => ({ ...current, [selected]: emptyModuleInput(selected) }))
    setDirty((current) => new Set(current).add(selected))
    setNotice(`已把「${title}」标记为「清空${label}权限」，点击「保存」生效。`)
  }

  const save = async () => {
    if (dirty.size === 0) {
      setNotice('没有需要保存的修改。')
      return
    }
    setSaving(true)
    setError(null)
    setNotice(null)
    try {
      const items = [...dirty].map((id) => draft[id] ?? (byId.has(id) ? rowToInput(byId.get(id)!) : emptyModuleInput(id)))
      await apiClient.put(url, { items })
      setNotice(`已保存 ${items.length} 个模块的权限。`)
      onSaved?.()
      await matrix.refetch()
    } catch (reason) {
      setError(describeApiError(reason, '保存失败，请稍后重试。'))
    } finally {
      setSaving(false)
    }
  }

  const copyFrom = async (sourceId: string) => {
    if (!sourceId || selected === null) return
    setSaving(true)
    setError(null)
    try {
      const sourceUrl = mode === 'user'
        ? `/admin/users/${encodeURIComponent(sourceId)}/rights`
        : `/admin/groups/${encodeURIComponent(sourceId)}/rights`
      const source = await apiClient.get<ModuleRightsRow[]>(sourceUrl)
      const sourceById = new Map(source.map((row) => [row.moduleId, row]))
      const sourceRow = sourceById.get(selected)
      if (!sourceRow) {
        setNotice(`来源 ${sourceId} 未配置「${selectedRow?.title ?? selected}」的权限，无法复制。`)
        return
      }
      setDraft((current) => ({ ...current, [selected]: rowToInput(sourceRow) }))
      setDirty((current) => new Set(current).add(selected))
      setCopyOpen(false)
      setNotice(`已从 ${sourceId} 复制「${selectedRow?.title ?? selected}」的权限，点击「保存」生效。`)
    } catch (reason) {
      setError(describeApiError(reason, '复制失败。'))
    } finally {
      setSaving(false)
    }
  }

  const openFieldPicker = async (denyKey: DenyFieldKey) => {
    if (selected === null || !selectedDraft) return
    const initial = new Set(String(selectedDraft[denyKey] ?? '').split(/[,;]/).map((item) => item.trim()).filter(Boolean))
    setFieldPicker({ denyKey, fields: null, loading: true, selection: initial })
    try {
      const fields = await apiClient.get<RightsModuleFields>(`/admin/rights/fields?moduleId=${selected}`)
      setFieldPicker((current) => current ? { ...current, fields, loading: false } : current)
    } catch (reason) {
      setError(describeApiError(reason, '字段加载失败。'))
      setFieldPicker(null)
    }
  }

  const togglePickerField = (fieldId: string) => {
    setFieldPicker((current) => {
      if (!current) return current
      const next = new Set(current.selection)
      if (next.has(fieldId)) next.delete(fieldId)
      else next.add(fieldId)
      return { ...current, selection: next }
    })
  }

  const confirmFieldPicker = () => {
    if (!fieldPicker || selected === null) return
    setDraftValue(selected, fieldPicker.denyKey, [...fieldPicker.selection].join(','))
    setFieldPicker(null)
  }

  const renderFieldList = (title: string, fields: RightsFieldInfo[]) => (
    <div>
      <div className="form-label mb-1">{title}</div>
      <div className="border rounded p-2 rights-field-list">
        {fields.length === 0 ? <div className="text-secondary small">无字段</div> : fields.map((field) => (
          <label key={field.fieldId} className="d-flex align-items-center gap-2 form-check-label small py-1">
            <input
              type="checkbox"
              className="form-check-input m-0"
              checked={fieldPicker!.selection.has(field.fieldId)}
              onChange={() => togglePickerField(field.fieldId)}
            />
            <span className="font-monospace">{field.fieldId}</span>
            <span className="text-secondary">{field.description}</span>
            {field.isCost && <span className="badge bg-warning-subtle text-warning">成本</span>}
            {field.isSecrecy && <span className="badge bg-danger-subtle text-danger">保密</span>}
          </label>
        ))}
      </div>
    </div>
  )

  if (!open) return null

  const headerTitle = title ?? (mode === 'user' ? `用户模块权限：${targetId.trim()}` : `用户组模块权限：${targetId.trim()}`)
  const formPanel = selectedRow && selectedDraft ? (
    <PermissionEditPanel
      variant="modal"
      mode={mode}
      row={selectedRow}
      draft={selectedDraft}
      dirty={dirty.has(selectedRow.moduleId)}
      onChange={(key, value) => setDraftValue(selectedRow.moduleId, key as keyof ModuleRightsInput, value as never)}
      onOpenFieldPicker={(denyKey) => void openFieldPicker(denyKey)}
    />
  ) : (
    <div className="text-secondary p-3">请选择左侧模块。</div>
  )

  const matrixBody = (
    <>
      {matrix.isPending ? <LoadingState label="正在加载权限矩阵…" /> : matrix.isError ? (
        <div className="alert alert-danger d-flex align-items-center justify-content-between">
          <span>{describeApiError(matrix.error, '权限矩阵加载失败。')}</span>
          <Button variant="danger" size="sm" onClick={() => void matrix.refetch()}>重试</Button>
        </div>
      ) : (
        <div className="rights-matrix-layout d-flex gap-3">
          <div className="rights-tree-panel border rounded p-2">
                  <div className="d-flex align-items-center gap-2 mb-2">
                    <div className="input-group input-group-sm">
                      <span className="input-group-text"><IconSearch size={14} /></span>
                      <input
                        className="form-control form-control-sm"
                        placeholder="搜索模块名称/编号"
                        value={search}
                        onChange={(event) => setSearch(event.target.value)}
                        aria-label="搜索模块"
                      />
                    </div>
                  </div>
                  <div className="d-flex align-items-center gap-2 mb-2 small text-secondary">
                    <span className="ms-auto">已配置 {configuredCount} / {rows.length}</span>
                  </div>
                  <div className="erp-menu-tree erp-rights-tree rights-tree-scroll" role="tree" aria-label="模块树">
                    {visibleTree.length === 0
                      ? <div className="text-secondary small p-2">没有匹配的模块</div>
                      : visibleTree.map((node) => renderNode(node, 0))}
                  </div>
          </div>
          <div className="flex-fill min-w-0">
                  {selectedRow && selectedDraft ? (
                    <PermissionEditPanel
                      variant="page"
                      mode={mode}
                      row={selectedRow}
                      draft={selectedDraft}
                      dirty={dirty.has(selectedRow.moduleId)}
                      onChange={(key, value) => setDraftValue(selectedRow.moduleId, key as keyof ModuleRightsInput, value as never)}
                      onOpenFieldPicker={(denyKey) => void openFieldPicker(denyKey)}
                    />
                  ) : (
                    <div className="text-secondary p-3">请选择左侧模块。</div>
                  )}
          </div>
        </div>
      )}
      {error && <div className="alert alert-danger py-2 mt-2 mb-0" role="alert">{error}</div>}
      {notice && <div className="alert alert-info py-2 mt-2 mb-0" role="status">{notice}</div>}
    </>
  )

  const copySourcePicker = copyOpen && (
    <UnifiedChooser
      open
      title={`选择复制来源${mode === 'user' ? '用户' : '组'}`}
      source={{ kind: 'sourceKey', key: mode === 'user' ? 'rights-admin.users' : 'rights-admin.groups' }}
      mode="single"
      getRowId={(row) => String((row as { USER_ID?: unknown }).USER_ID ?? (row as { G_IDX?: unknown }).G_IDX ?? '')}
      onPick={(rows) => {
        const picked = rows[0] as { USER_ID?: unknown; G_IDX?: unknown } | undefined
        const sourceId = picked ? String(picked.USER_ID ?? picked.G_IDX ?? '') : ''
        if (sourceId) void copyFrom(sourceId)
        setCopyOpen(false)
      }}
      onClose={() => setCopyOpen(false)}
      searchPlaceholder={mode === 'user' ? '按用户ID/姓名搜索' : '按组ID/组名搜索'}
    />
  )

  const matrixActions = (
    <>
      <Button size="sm" variant="ghost" icon={<IconTrash size={15} />} onClick={clearSelectedRights} disabled={selected === null}>
        清空{mode === 'user' ? '个人' : '组'}权限
      </Button>
      <Button size="sm" variant="secondary" icon={<IconCopy size={15} />} onClick={() => setCopyOpen(true)} disabled={rows.length === 0}>
        复制权限
      </Button>
    </>
  )

  const matrixFooter = (
    <div className="d-flex flex-wrap gap-2 align-items-center justify-content-between w-100">
      <div className="d-flex gap-2 align-items-center">{matrixActions}</div>
      <div className="d-flex gap-2">
        <Button onClick={onClose}>{variant === 'page' ? '返回' : '取消'}</Button>
        <Button variant="primary" onClick={() => void save()} loading={saving}>保存</Button>
      </div>
    </div>
  )

  const fieldPickerOverlay = fieldPicker && (
    <Modal
      title={`选择字段（${DENY_FIELDS.find((item) => item.key === fieldPicker.denyKey)?.label}）`}
      onClose={() => setFieldPicker(null)}
      footer={<>
        <Button onClick={() => setFieldPicker(null)}>取消</Button>
        <Button variant="primary" onClick={confirmFieldPicker} disabled={fieldPicker.loading}>确定</Button>
      </>}
    >
      {fieldPicker.loading ? <LoadingState label="正在加载字段…" /> : fieldPicker.fields ? (
        <div className="d-grid gap-3">
          {renderFieldList(`${fieldPicker.fields.masterTable}（主表）`, fieldPicker.fields.masterFields)}
          {fieldPicker.fields.detailTable && renderFieldList(`${fieldPicker.fields.detailTable}（副表）`, fieldPicker.fields.detailFields)}
        </div>
      ) : null}
    </Modal>
  )

  if (variant === 'page') {
    return (
      <div className="erp-menu-admin d-flex flex-column gap-2">
        <section className="card erp-list-card">
          <section className="erp-list-command-bar" aria-label="权限矩阵工具栏">
            <span className="fw-semibold small">{headerTitle}</span>
            <div className="erp-list-actions d-flex gap-2 align-items-center flex-wrap">
              <Button size="sm" variant="ghost" icon={<IconArrowLeft size={16} />} onClick={onClose}>
                {mode === 'user' ? '返回用户列表' : '返回用户组'}
              </Button>
              {matrixActions}
              <Button variant="primary" size="sm" onClick={() => void save()} loading={saving}>保存</Button>
            </div>
          </section>
          <div className="card-body p-0">
            <div className="row g-0">
              <div className="col-lg-5 border-end erp-menu-tree erp-rights-tree">
                <div className="erp-menu-tree-toolbar p-2 border-bottom d-flex flex-wrap gap-1 align-items-center">
                  <div className="erp-nav-search erp-menu-search">
                    <IconSearch size={16} aria-hidden="true" />
                    <input
                      type="search"
                      value={search}
                      onChange={(event) => setSearch(event.target.value)}
                      placeholder="搜索模块名称/编号"
                      aria-label="搜索模块"
                    />
                    {search && (
                      <button type="button" className="erp-nav-search-clear" aria-label="清除搜索" onClick={() => setSearch('')}>×</button>
                    )}
                  </div>
                  <span className="text-secondary small ms-auto">已配置 {configuredCount} / {rows.length}</span>
                </div>
                <div className="rights-tree-scroll p-2" role="tree" aria-label="模块树">
                  {matrix.isPending ? <LoadingState label="正在加载权限矩阵…" /> : matrix.isError ? (
                    <div className="alert alert-danger d-flex align-items-center justify-content-between">
                      <span>{describeApiError(matrix.error, '权限矩阵加载失败。')}</span>
                      <Button variant="danger" size="sm" onClick={() => void matrix.refetch()}>重试</Button>
                    </div>
                  ) : visibleTree.length === 0 ? (
                    <div className="text-secondary small p-2">没有匹配的模块</div>
                  ) : (
                    visibleTree.map((node) => renderNode(node, 0))
                  )}
                </div>
              </div>
              <div className="col-lg-7">
                <div className="p-3 erp-menu-form">
                  {formPanel}
                  {error && <div className="alert alert-danger py-2 mt-2 mb-0" role="alert">{error}</div>}
                  {notice && <div className="alert alert-info py-2 mt-2 mb-0" role="status">{notice}</div>}
                </div>
              </div>
            </div>
          </div>
        </section>
        {fieldPickerOverlay}
        {copySourcePicker}
      </div>
    )
  }

  return (
    <>
      <Modal title={headerTitle} onClose={onClose} size="xl" footer={matrixFooter}>
        {matrixBody}
      </Modal>
      {fieldPickerOverlay}
      {copySourcePicker}
    </>
  )
}
