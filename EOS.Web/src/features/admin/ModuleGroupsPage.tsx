import { IconArrowDown, IconArrowUp, IconDeviceFloppy, IconPencil, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { useMemo, useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

/** 一个分组（服务端 ModuleGroupAdminRow）。 */
interface ModuleGroupRow {
  groupId: number
  sortIdx: number
  description: string
  expression: string
  /** 受控编译器能否执行该表达式（不可用时分组的筛选项对所有人都是灰的）。 */
  available: boolean
  error: string | null
}

/** 配置面读模型（服务端 ModuleGroupAdminModule）。 */
interface ModuleGroupView {
  moduleId: number
  moduleDesc: string
  nodeKind: string
  masterTable: string
  groups: ModuleGroupRow[]
}

interface GroupDraft {
  mode: 'create' | 'edit'
  groupId: number | null
  description: string
  expression: string
}

const KIND_LABEL: Record<string, string> = {
  WORKBENCH: '统一工作台模块',
  CUSTOMPAGE: '自定义承载页',
  DIRECTORY: '目录节点',
}

/**
 * 模块分组（模块 2315，定制承载页 `/admin/module-groups`）：为**其它**模块维护列表分组
 * ——分组名称 + 分组表达式 + 顺序。
 *
 * 页面只负责选模块、编分组、把服务端拒绝的理由显示出来：
 * - **哪些模块能配**由服务端的候选数据源给出（统一工作台模块，与服务端拒存规则同源）；
 * - **表达式能不能用**由服务端受控编译器判定（`GroupExpressionParser`），保存时拒绝并给出
 *   指名到列的原因，本页不自行判断合法性；
 * - **保存即生效**：分组不进工作台定义快照，改完立刻对后续请求生效，**没有"发布"这一步**。
 */
export function ModuleGroupsPage() {
  const queryClient = useQueryClient()
  const [moduleId, setModuleId] = useState<number | null>(null)
  const [chooserOpen, setChooserOpen] = useState(false)
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [editor, setEditor] = useState<GroupDraft | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<ModuleGroupRow | null>(null)

  const view = useQuery({
    queryKey: ['module-groups', moduleId],
    queryFn: () => apiClient.get<ModuleGroupView>(`/admin/module-groups/${moduleId}`),
    enabled: moduleId != null,
  })
  const isWorkbench = view.data?.nodeKind === 'WORKBENCH' && (view.data?.masterTable ?? '') !== ''
  const fields = useQuery({
    queryKey: ['module-groups', moduleId, 'fields'],
    queryFn: () => apiClient.get<{ fields: string[] }>(`/admin/module-groups/${moduleId}/fields`),
    enabled: moduleId != null && isWorkbench,
  })

  const groups = useMemo(() => view.data?.groups ?? [], [view.data])
  const selectedId = useMemo(() => {
    const key = Object.keys(rowSelection).find((item) => rowSelection[item])
    return key == null ? null : Number(key)
  }, [rowSelection])
  const selected = groups.find((group) => group.groupId === selectedId) ?? null
  const selectedIndex = groups.findIndex((group) => group.groupId === selectedId)

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['module-groups', moduleId] })
  }

  const save = useMutation({
    mutationFn: async (draft: GroupDraft) => {
      const body = { description: draft.description.trim(), expression: draft.expression.trim() }
      return draft.mode === 'create'
        ? apiClient.post<ModuleGroupRow>(`/admin/module-groups/${moduleId}`, body)
        : apiClient.put<ModuleGroupRow>(`/admin/module-groups/${moduleId}/${draft.groupId}`, body)
    },
    onSuccess: async () => {
      setEditor(null)
      await refresh()
    },
  })

  const remove = useMutation({
    mutationFn: (group: ModuleGroupRow) =>
      apiClient.delete<void>(`/admin/module-groups/${moduleId}/${group.groupId}`),
    onSuccess: async () => {
      setDeleteTarget(null)
      setRowSelection({})
      await refresh()
    },
  })

  const move = useMutation({
    mutationFn: (input: { group: ModuleGroupRow; action: 'up' | 'down' }) =>
      apiClient.post<void>(`/admin/module-groups/${moduleId}/${input.group.groupId}/move`, { action: input.action }),
    onSuccess: refresh,
  })

  const columns = useMemo<ColumnDef<ModuleGroupRow, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: () => <span className="visually-hidden">选择</span>,
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="radio"
          aria-label={`选择 ${row.original.description}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    { accessorKey: 'sortIdx', header: '顺序', meta: { resizable: false } },
    { accessorKey: 'description', header: '分组名称' },
    {
      accessorKey: 'expression',
      header: '分组表达式',
      cell: (info) => <span className="font-monospace">{String(info.getValue())}</span>,
    },
    {
      id: 'available',
      enableSorting: false,
      header: '校验',
      cell: ({ row }) => row.original.available
        ? <span className="text-success">可用</span>
        : <span className="text-danger" title={row.original.error ?? ''}>不可用</span>,
    },
    {
      id: 'error',
      enableSorting: false,
      header: '不可用原因',
      cell: ({ row }) => <span className="text-secondary small">{row.original.error ?? ''}</span>,
    },
  ], [])

  const openCreate = () => setEditor({ mode: 'create', groupId: null, description: '', expression: '' })
  const openEdit = (group: ModuleGroupRow) => setEditor({
    mode: 'edit', groupId: group.groupId, description: group.description, expression: group.expression,
  })

  const appendField = (field: string) => {
    if (!editor) return
    setEditor({ ...editor, expression: editor.expression.trim() === '' ? field : `${editor.expression}${field}` })
  }

  const errorMessage = describeApiError(view.error ?? fields.error, '发生未知错误，请稍后重试。')
  const saveError = save.error ? describeApiError(save.error, '保存失败。') : null
  const listError = view.error ? <ErrorState message={errorMessage} onRetry={() => void refresh()} /> : null

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="模块分组"
        actions={(
          <>
            <Button size="sm" icon={<IconPlus size={16} />} onClick={() => setChooserOpen(true)}>
              {moduleId == null ? '选择模块' : '换一个模块'}
            </Button>
            <Button size="sm" icon={<IconPlus size={16} />} disabled={!isWorkbench} onClick={openCreate}>新增分组</Button>
            <Button size="sm" icon={<IconPencil size={16} />} disabled={!selected} onClick={() => selected && openEdit(selected)}>编辑</Button>
            <Button
              size="sm"
              variant="danger"
              icon={<IconTrash size={16} />}
              disabled={!selected}
              onClick={() => selected && setDeleteTarget(selected)}
            >
              删除
            </Button>
            <Button
              size="sm"
              icon={<IconArrowUp size={16} />}
              disabled={!selected || selectedIndex <= 0}
              onClick={() => selected && move.mutate({ group: selected, action: 'up' })}
            >
              上移
            </Button>
            <Button
              size="sm"
              icon={<IconArrowDown size={16} />}
              disabled={!selected || selectedIndex < 0 || selectedIndex >= groups.length - 1}
              onClick={() => selected && move.mutate({ group: selected, action: 'down' })}
            >
              下移
            </Button>
            <Button size="sm" icon={<IconRefresh size={16} />} disabled={moduleId == null} onClick={() => void refresh()}>刷新</Button>
          </>
        )}
        header={view.data ? (
          <div className="px-3 pt-2 small">
            <div className="fw-semibold">
              {view.data.moduleDesc}（{view.data.moduleId}）· 主表 {view.data.masterTable || '—'} ·
              形态 {KIND_LABEL[view.data.nodeKind] ?? view.data.nodeKind}
            </div>
            {!isWorkbench ? (
              <div className="text-danger">
                该节点不是统一工作台模块（或没有操作主表），没有分组消费方：只能查看，不能配分组。
              </div>
            ) : (
              <div className="text-secondary">
                分组保存即生效，不需要发布。表达式只支持主表字段、字面量、+ - * / 、括号与 CASE，
                以及 YEAR / MONTH / DATEPART / REPLICATE / CAST / CONVERT。
              </div>
            )}
          </div>
        ) : null}
      >
        {moduleId == null ? (
          <EmptyState title="还没有选择模块" description="点「选择模块」挑一个统一工作台模块，再维护它的列表分组。" />
        ) : view.isPending ? (
          <LoadingState label="正在加载分组…" />
        ) : listError ? listError : (
          <div className="d-flex flex-column gap-2 p-2">
            <ErpTable
              columns={columns}
              data={groups}
              getRowId={(row: ModuleGroupRow) => String(row.groupId)}
              empty={<EmptyState title="该模块还没有分组" description="没有分组行时，列表页不显示分组下拉。" />}
              resizable
              storageKey="module-groups"
              clientSideSorting
              rowClickSingleSelect
              rowSelection={rowSelection}
              onRowSelectionChange={(selection) => setRowSelection(selection)}
              onRowDoubleClick={(row) => openEdit(row)}
            />
          </div>
        )}
      </ErpListCard>

      {chooserOpen ? (
        <UnifiedChooser
          open
          title="选择要配置分组的模块"
          source={{ kind: 'sourceKey', key: 'module-groups.targets' }}
          mode="single"
          getRowId={(row) => String(row.M_IDX)}
          onPick={(rows) => {
            const picked = rows[0] as { M_IDX?: unknown } | undefined
            if (picked) {
              setModuleId(Number(picked.M_IDX))
              setRowSelection({})
            }
            setChooserOpen(false)
          }}
          onClose={() => setChooserOpen(false)}
          dialogSize="md"
          searchPlaceholder="按模块号或模块名搜索"
          emptyText="没有可配分组的模块（只有统一工作台模块才有分组消费方）。"
        />
      ) : null}

      {editor ? (
        <Modal
          title={editor.mode === 'create' ? '新增分组' : `编辑分组（${editor.description}）`}
          size="lg"
          onClose={() => setEditor(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setEditor(null)}>取消</Button>
              <Button
                size="sm"
                icon={<IconDeviceFloppy size={16} />}
                disabled={save.isPending || editor.description.trim() === '' || editor.expression.trim() === ''}
                loading={save.isPending}
                onClick={() => save.mutate(editor)}
              >
                保存
              </Button>
            </>
          )}
        >
          <div className="row g-2">
            <div className="col-6">
              <label className="form-label small mb-1">分组名称</label>
              <input
                className="form-control form-control-sm"
                aria-label="分组名称"
                maxLength={50}
                value={editor.description}
                onChange={(event) => setEditor({ ...editor, description: event.target.value })}
              />
              <div className="form-text">显示在列表页的分组下拉里，最长 50 个字符。</div>
            </div>
            <div className="col-12">
              <label className="form-label small mb-1">分组表达式</label>
              <textarea
                className="form-control form-control-sm font-monospace"
                aria-label="分组表达式"
                rows={3}
                maxLength={500}
                value={editor.expression}
                onChange={(event) => setEditor({ ...editor, expression: event.target.value })}
              />
              <div className="form-text">
                如 <span className="font-monospace">CLIENT_ID</span>、
                <span className="font-monospace">COP_QUOTE_M.CLIENT_ID</span>、
                <span className="font-monospace">CASE WHEN ... THEN ... END</span>；跨表列不可用。
              </div>
            </div>
            <div className="col-12">
              <label className="form-label small mb-1">
                可引用字段（{view.data?.masterTable}）——点一下追加到表达式
              </label>
              {fields.isPending ? (
                <div className="text-secondary small">正在加载字段…</div>
              ) : (fields.data?.fields ?? []).length === 0 ? (
                <div className="text-secondary small">该主表没有登记可用的分组字段。</div>
              ) : (
                <div className="d-flex flex-wrap gap-1">
                  {(fields.data?.fields ?? []).map((field) => (
                    <Button key={field} size="sm" variant="ghost" onClick={() => appendField(field)}>{field}</Button>
                  ))}
                </div>
              )}
            </div>
            {saveError ? <div className="col-12"><div className="alert alert-danger py-2 mb-0">{saveError}</div></div> : null}
          </div>
        </Modal>
      ) : null}

      {deleteTarget ? (
        <Modal
          title="删除分组"
          onClose={() => setDeleteTarget(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setDeleteTarget(null)}>取消</Button>
              <Button
                size="sm"
                variant="danger"
                icon={<IconTrash size={16} />}
                loading={remove.isPending}
                onClick={() => remove.mutate(deleteTarget)}
              >
                删除
              </Button>
            </>
          )}
        >
          <div className="small">
            确认删除该分组？删除后列表页的分组下拉里不再有它，已有的分组筛选链接会失效（403）。
          </div>
          <div className="mt-2">
            <div><span className="text-secondary">分组名称：</span>{deleteTarget.description}</div>
            <div className="text-break"><span className="text-secondary">表达式：</span><span className="font-monospace">{deleteTarget.expression}</span></div>
          </div>
          {remove.error ? (
            <div className="alert alert-danger py-2 mt-2 mb-0">{describeApiError(remove.error, '删除失败。')}</div>
          ) : null}
        </Modal>
      ) : null}
    </div>
  )
}
