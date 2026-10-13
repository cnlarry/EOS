import {
  IconArrowDown, IconArrowUp, IconDeviceFloppy, IconPencil, IconPlus, IconRefresh, IconTrash,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import type { ColumnDef, RowSelectionState, SortingState } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'

/**
 * 模块分组（模块 230102，定制承载页 `/admin/module-groups`）：为**其它**模块维护列表分组
 * ——分组名称 + 分组表达式 + 顺序。
 *
 * 形态与全站列表页同一套：**一张跨模块的扁平列表 + 新增 / 编辑弹窗**（与 230103 同构）。
 * 分组天生是"一个模块挂两三条"，所以模块号 / 模块名是行上的一列，清单按「模块号 + 顺序」聚拢。
 *
 * **不做跨行合并单元格**：`ErpTable` 每行每列必产出一个 `<td>`，跨行合并要改公共组件，
 * 而且会打坏复制（它按列索引取单元格）与排序（同模块的行一被重排，合并块就碎）。
 * 改为在每块的第一行画一条分隔线（`rowClassName` + `.erp-module-block-start`）：
 * 列排序一开，同模块的行本就会散开，分块线随之隐藏。
 *
 * 与按钮面（230103）的差别在**写路径**：分组是逐行增删改（`POST` / `PUT` / `DELETE` / `move`），
 * 不是"整面替换"，所以弹窗保存只动这一行——不存在"先取回整个模块再回传"的问题。
 * 分组**保存即生效**（组装工作台定义时实时读取，不进快照、没有发布步骤）。
 * 表达式能不能用由服务端受控编译器判定，页面不自行判断；**样例值**由服务端在主表上真跑一次，
 * 只在弹窗里显示（列表按可用性扫一眼就够，逐模块真跑查询不该压在首屏上）。
 */

/** 列表行：`GET /admin/module-groups/overview` 下发的全库分组行（含所属模块）。 */
type GroupRow = {
  moduleId: number
  moduleDesc: string
  groupId: number
  sortIdx: number
  description: string
  expression: string
  available: boolean
  error: string | null
}

/** 带"块首"标记的行：按「模块号 + 顺序」聚拢后，与上一行不同模块的即为块首。 */
type PlacedGroupRow = GroupRow & { blockStart: boolean }

/** 该模块的分组（`GET /admin/module-groups/{id}`）：比总览多一组样例值。 */
type ModuleGroupDetail = {
  groupId: number
  sortIdx: number
  description: string
  expression: string
  available: boolean
  error: string | null
  samples?: string[]
  sampleError?: string | null
}

type OverviewView = {
  groups: GroupRow[]
  canWrite: boolean
}

type ModuleGroupView = {
  moduleId: number
  moduleDesc: string
  nodeKind: string
  masterTable: string
  groups: ModuleGroupDetail[]
}

/** 新增 / 编辑弹窗的草稿（提交前是页面状态，不落到表格的行对象上）。 */
type EditorState = {
  mode: 'create' | 'edit'
  /** 新增时可能还没选模块；保存前必须填上。 */
  moduleId: number | null
  moduleDesc: string
  /** 编辑时定位那一行。 */
  groupId: number | null
  description: string
  expression: string
  error: string | null
}

type DeleteState = {
  moduleId: number
  moduleDesc: string
  groupId: number
  description: string
  error: string | null
}

/** 把样例值渲染成人话：没样例、主表没行、执行失败各有各的说法。 */
function describeSamples(detail: ModuleGroupDetail): string {
  if (!detail.available) return `不可用 —— ${detail.error ?? '表达式不被受控编译器接受'}`
  if (detail.sampleError) return `样例执行失败：${detail.sampleError}`
  if (detail.samples == null) return '（服务端未下发样例值）'
  if (detail.samples.length === 0) return '（主表当前没有行）'
  return detail.samples.join(' / ')
}

export function ModuleGroupsPage() {
  const queryClient = useQueryClient()
  // 深链接 `?moduleId=170204`：预填成关键字，直接落在该模块的分组行上
  const [keyword, setKeyword] = useState(() => new URLSearchParams(window.location.search).get('moduleId') ?? '')
  // 受控排序：只为知道"用户有没有按列重排"——分块线在按列排序后没有意义，要收起来
  const [sorting, setSorting] = useState<SortingState>([])
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [chooserOpen, setChooserOpen] = useState(false)
  const [deleting, setDeleting] = useState<DeleteState | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)

  const overview = useQuery({
    queryKey: ['module-groups', 'overview'],
    queryFn: () => apiClient.get<OverviewView>('/admin/module-groups/overview'),
  })
  // 弹窗打开时才按模块取一次：样例值要真跑查询，不适合压在列表首屏上
  const editorModuleId = editor?.moduleId ?? null
  const detail = useQuery({
    queryKey: ['module-groups', editorModuleId],
    enabled: editorModuleId != null,
    queryFn: () => apiClient.get<ModuleGroupView>(`/admin/module-groups/${editorModuleId}`),
  })
  const fields = useQuery({
    queryKey: ['module-groups', editorModuleId, 'fields'],
    enabled: editorModuleId != null,
    queryFn: () => apiClient.get<{ fields: string[] }>(`/admin/module-groups/${editorModuleId}/fields`),
  })

  const canWrite = overview.data?.canWrite ?? false

  // 按「模块号 + 顺序」聚拢后逐行标出块首；关键字只影响显示，不影响标记口径（先过滤再标记）
  const rows = useMemo<PlacedGroupRow[]>(() => {
    const needle = keyword.trim().toLowerCase()
    const kept = (overview.data?.groups ?? []).filter((row) =>
      needle === '' || `${row.moduleId} ${row.moduleDesc} ${row.description} ${row.expression}`
        .toLowerCase().includes(needle))
    const ordered = [...kept].sort((left, right) =>
      (left.moduleId - right.moduleId) || (left.sortIdx - right.sortIdx) || (left.groupId - right.groupId))
    // 用 reduce 累积而不是"边 map 边改外层变量"：块首只看上一行，不引入渲染期的可变状态
    return ordered.reduce<PlacedGroupRow[]>((acc, row) => {
      const previous = acc.length === 0 ? null : acc[acc.length - 1]
      acc.push({ ...row, blockStart: previous == null || previous.moduleId !== row.moduleId })
      return acc
    }, [])
  }, [overview.data, keyword])

  // 该行在**它所属模块内**是否还能上移 / 下移（按全量数据算，不受关键字过滤影响）
  const moveBounds = useMemo(() => {
    const bounds = new Map<number, { first: boolean; last: boolean }>()
    const byModule = new Map<number, GroupRow[]>()
    for (const group of overview.data?.groups ?? []) {
      const list = byModule.get(group.moduleId)
      if (list == null) byModule.set(group.moduleId, [group])
      else list.push(group)
    }
    for (const list of byModule.values()) {
      const ordered = [...list].sort((left, right) => (left.sortIdx - right.sortIdx) || (left.groupId - right.groupId))
      ordered.forEach((group, index) => {
        bounds.set(group.groupId, { first: index === 0, last: index === ordered.length - 1 })
      })
    }
    return bounds
  }, [overview.data])

  const refreshAll = useCallback(() => {
    setNotice(null)
    setPageError(null)
    void queryClient.invalidateQueries({ queryKey: ['module-groups'] })
  }, [queryClient])

  const move = useMutation({
    mutationFn: (input: { moduleId: number; groupId: number; action: 'up' | 'down' }) =>
      apiClient.post<void>(`/admin/module-groups/${input.moduleId}/${input.groupId}/move`, { action: input.action }),
    onSuccess: async () => {
      setPageError(null)
      await queryClient.invalidateQueries({ queryKey: ['module-groups'] })
    },
    onError: (error) => setPageError(describeApiError(error, '调整顺序失败。')),
  })

  const save = useMutation({
    mutationFn: async (draft: EditorState) => {
      if (draft.moduleId == null) throw new Error('请先选择模块。')
      const body = { description: draft.description.trim(), expression: draft.expression.trim() }
      // 逐行写：新增是 POST 一个模块下的新行，编辑是 PUT 那一行（不是整面替换）
      return draft.mode === 'create'
        ? await apiClient.post<void>(`/admin/module-groups/${draft.moduleId}`, body)
        : await apiClient.put<void>(`/admin/module-groups/${draft.moduleId}/${draft.groupId}`, body)
    },
    onSuccess: async () => {
      setEditor(null)
      setNotice('已保存。分组保存即生效（组装工作台定义时实时读取），不需要发布。')
      await queryClient.invalidateQueries({ queryKey: ['module-groups'] })
    },
    onError: (error) => {
      // 弹窗不关：输入还在用户手上，错误贴着它显示
      const message = describeApiError(error, '保存分组失败。')
      setEditor((prev) => prev && { ...prev, error: message })
    },
  })

  const remove = useMutation({
    mutationFn: (target: DeleteState) =>
      apiClient.delete<void>(`/admin/module-groups/${target.moduleId}/${target.groupId}`),
    onSuccess: async () => {
      setDeleting(null)
      setNotice('已删除。列表页右上角的分组下拉会少一项。')
      await queryClient.invalidateQueries({ queryKey: ['module-groups'] })
    },
    onError: (error) => setDeleting((prev) => prev && { ...prev, error: describeApiError(error, '删除分组失败。') }),
  })

  const openCreate = useCallback(() => {
    setNotice(null)
    setPageError(null)
    setEditor({
      mode: 'create', moduleId: null, moduleDesc: '', groupId: null,
      description: '', expression: '', error: null,
    })
  }, [])

  const openEdit = useCallback((row: GroupRow) => {
    setNotice(null)
    setPageError(null)
    setEditor({
      mode: 'edit', moduleId: row.moduleId, moduleDesc: row.moduleDesc, groupId: row.groupId,
      description: row.description, expression: row.expression, error: null,
    })
  }, [])

  // 单选语义：首列是单选框，一次只留一行（表格底层是开关语义，不收拢会留下多行）
  const handleRowSelectionChange = (updater: React.SetStateAction<RowSelectionState>) => {
    setRowSelection((prev) => {
      const next = typeof updater === 'function'
        ? (updater as (old: RowSelectionState) => RowSelectionState)(prev)
        : updater
      const picked = Object.keys(next).filter((key) => next[key])
      if (picked.length <= 1) return next
      return { [picked[picked.length - 1]]: true }
    })
  }

  const columns = useMemo<ColumnDef<PlacedGroupRow>[]>(() => [
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
          aria-label={`选择 ${row.original.moduleDesc} 的分组 ${row.original.description}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    {
      id: 'moduleId',
      header: '模块号',
      size: 90,
      accessorFn: (row) => row.moduleId,
      cell: ({ row }) => <span className="font-monospace">{row.original.moduleId}</span>,
    },
    {
      id: 'moduleDesc',
      header: '模块名',
      size: 180,
      accessorFn: (row) => row.moduleDesc,
      cell: ({ row }) => <span className="fw-semibold">{row.original.moduleDesc}</span>,
    },
    {
      id: 'description',
      header: '分组名称',
      size: 160,
      accessorFn: (row) => row.description,
      cell: ({ row }) => row.original.description.trim() || <span className="text-secondary">（未命名）</span>,
    },
    {
      id: 'expression',
      header: '分组表达式',
      accessorFn: (row) => row.expression,
      cell: ({ row }) => <span className="font-monospace small">{row.original.expression}</span>,
    },
    {
      id: 'sortIdx',
      header: '顺序',
      size: 110,
      enableSorting: false,
      cell: ({ row }) => {
        const bounds = moveBounds.get(row.original.groupId)
        return (
          <div className="d-flex align-items-center gap-1">
            <Button
              size="sm"
              variant="ghost"
              icon={<IconArrowUp size={14} />}
              aria-label={`上移 ${row.original.description}`}
              disabled={!canWrite || move.isPending || (bounds?.first ?? true)}
              onClick={() => move.mutate({
                moduleId: row.original.moduleId, groupId: row.original.groupId, action: 'up',
              })}
            />
            <Button
              size="sm"
              variant="ghost"
              icon={<IconArrowDown size={14} />}
              aria-label={`下移 ${row.original.description}`}
              disabled={!canWrite || move.isPending || (bounds?.last ?? true)}
              onClick={() => move.mutate({
                moduleId: row.original.moduleId, groupId: row.original.groupId, action: 'down',
              })}
            />
            <span className="text-secondary small">{row.original.sortIdx}</span>
          </div>
        )
      },
    },
    {
      id: 'available',
      header: '表达式校验',
      size: 150,
      accessorFn: (row) => (row.available ? 1 : 0),
      cell: ({ row }) => (row.original.available
        ? <span className="badge text-bg-success">可用</span>
        : <span className="badge text-bg-danger" title={row.original.error ?? ''}>不可用</span>),
    },
    {
      id: 'actions',
      header: '操作',
      size: 190,
      enableSorting: false,
      enableHiding: false,
      meta: { frozenRight: true, resizable: false, truncate: false, minWidth: 190 },
      cell: ({ row }) => (
        <div className="d-flex gap-1">
          <Button
            size="sm"
            variant="ghost"
            icon={<IconPencil size={14} />}
            disabled={!canWrite}
            onClick={() => openEdit(row.original)}
          >
            编辑
          </Button>
          <Button
            size="sm"
            variant="ghost"
            icon={<IconTrash size={14} />}
            disabled={!canWrite}
            onClick={() => {
              setNotice(null)
              setPageError(null)
              setDeleting({
                moduleId: row.original.moduleId,
                moduleDesc: row.original.moduleDesc,
                groupId: row.original.groupId,
                description: row.original.description.trim() || '（未命名）',
                error: null,
              })
            }}
          >
            删除
          </Button>
        </div>
      ),
    },
  ], [canWrite, move, moveBounds, openEdit])

  const errorMessage = describeApiError(overview.error, '加载模块分组失败。')
  const fieldOptions = fields.data?.fields ?? []
  // 编辑时显示"当前生效值"：样例值来自按模块读的那条路径
  const editorDetail = editor == null || editor.groupId == null
    ? null
    : (detail.data?.groups ?? []).find((group) => group.groupId === editor.groupId) ?? null

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="模块分组"
        search={(
          <ErpSearchBox
            value={keyword}
            onChange={setKeyword}
            placeholder="模块号 / 模块名 / 分组名称 / 表达式"
            ariaLabel="搜索分组"
          />
        )}
        actions={(
          <>
            <Button size="sm" icon={<IconPlus size={16} />} disabled={!canWrite} onClick={openCreate}>新增</Button>
            <Button size="sm" variant="ghost" icon={<IconRefresh size={16} />} onClick={refreshAll}>刷新</Button>
          </>
        )}
        header={(
          <>
            {canWrite ? null : (
              <div className="erp-list-header">
                本页只给了查看权：能看不能改，要改请让管理员放开本配置面的 SETUP。
              </div>
            )}
            {notice ? <div className="px-3 pt-2"><div className="alert alert-success py-2 mb-0" role="alert">{notice}</div></div> : null}
            {pageError ? <div className="px-3 pt-2"><div className="alert alert-danger py-2 mb-0" role="alert">{pageError}</div></div> : null}
          </>
        )}
      >
        {overview.isPending ? (
          <LoadingState label="正在加载模块分组…" />
        ) : overview.isError ? (
          <ErrorState message={errorMessage} onRetry={refreshAll} />
        ) : (
          <ErpTable
            columns={columns}
            data={rows}
            getRowId={(row) => String(row.groupId)}
            // 分块线只在"没按列排序"时出现：一按列重排，同模块的行就散开了，线反而误导
            rowClassName={(row) => (sorting.length === 0 && row.blockStart ? 'erp-module-block-start' : undefined)}
            empty={(
              <EmptyState
                title="系统里还没有模块分组"
                description="点右上角「新增」加一条——没配分组的模块，列表右上角不出现分组下拉。"
              />
            )}
            sorting={sorting}
            onSortingChange={setSorting}
            clientSideSorting
            resizable
            storageKey="module-groups"
            rowClickSingleSelect
            rowSelection={rowSelection}
            onRowSelectionChange={handleRowSelectionChange}
            onRowDoubleClick={(row) => openEdit(row)}
          />
        )}
      </ErpListCard>

      {editor ? (
        <Modal
          title={editor.mode === 'create' ? '新增分组' : `修改分组：${editor.description.trim() || '（未命名）'}`}
          size="lg"
          onClose={() => setEditor(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setEditor(null)}>取消</Button>
              <Button
                size="sm"
                icon={<IconDeviceFloppy size={16} />}
                disabled={editor.moduleId == null || save.isPending}
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
              <label className="form-label small mb-1">模块</label>
              {editor.mode === 'create' ? (
                <div className="d-flex align-items-center gap-2">
                  <Button size="sm" variant="ghost" icon={<IconPlus size={14} />} onClick={() => setChooserOpen(true)}>
                    选择模块
                  </Button>
                  <span className="small">
                    {editor.moduleId == null ? (
                      <span className="text-secondary">未选择</span>
                    ) : (
                      <>
                        <span className="fw-semibold">{editor.moduleDesc}</span>
                        <span className="text-secondary font-monospace ms-1">{editor.moduleId}</span>
                      </>
                    )}
                  </span>
                </div>
              ) : (
                <div className="form-control-plaintext small">
                  <span className="fw-semibold">{editor.moduleDesc}</span>
                  <span className="text-secondary font-monospace ms-1">{editor.moduleId}</span>
                </div>
              )}
              <div className="form-text">
                {editor.mode === 'create'
                  ? '分组配在某个模块上，只有统一工作台模块（有主表）才有分组消费方。'
                  : '分组归它所在的模块；要挪到别的模块请新增一条再删掉这个。'}
              </div>
            </div>
            <div className="col-6">
              <label className="form-label small mb-1" htmlFor="group-description">分组名称</label>
              <input
                id="group-description"
                className="form-control form-control-sm"
                maxLength={40}
                value={editor.description}
                onChange={(event) => setEditor({ ...editor, description: event.target.value })}
              />
              <div className="form-text">列表右上角分组下拉里显示的文字。</div>
            </div>
            <div className="col-12">
              <label className="form-label small mb-1" htmlFor="group-expression">分组表达式</label>
              <input
                id="group-expression"
                className="form-control form-control-sm font-monospace"
                maxLength={500}
                value={editor.expression}
                onChange={(event) => setEditor({ ...editor, expression: event.target.value })}
              />
              <div className="form-text">
                只支持主表字段、字面量、加减乘除、括号与 CASE，以及 YEAR / MONTH / DATEPART / REPLICATE / CAST / CONVERT。
                表达式能不能用由服务端判定，保存时才知道结果。
              </div>
            </div>
            {fieldOptions.length > 0 ? (
              <div className="col-6">
                <label className="form-label small mb-1" htmlFor="group-field">插入主表字段</label>
                <select
                  id="group-field"
                  className="form-select form-select-sm"
                  value=""
                  onChange={(event) => {
                    const field = event.target.value
                    if (field === '') return
                    setEditor({
                      ...editor,
                      expression: editor.expression.trim() === '' ? field : `${editor.expression}${field}`,
                    })
                  }}
                >
                  <option value="">选一个字段…</option>
                  {fieldOptions.map((field) => <option key={field} value={field}>{field}</option>)}
                </select>
                <div className="form-text">插到表达式末尾，可继续手工拼。</div>
              </div>
            ) : null}
            {editor.mode === 'edit' ? (
              <div className="col-12">
                <div className="form-text">
                  当前生效值：
                  {detail.isPending
                    ? '正在读取…'
                    : editorDetail == null
                      ? '（该行已不存在，请刷新）'
                      : describeSamples(editorDetail)}
                </div>
              </div>
            ) : null}
          </div>

          {editor.error ? (
            <div className="alert alert-danger py-1 px-2 small mt-2 mb-0" role="alert">{editor.error}</div>
          ) : null}
        </Modal>
      ) : null}

      {chooserOpen ? (
        <UnifiedChooser
          open
          title="选择模块"
          source={{ kind: 'sourceKey', key: 'module-groups.targets' }}
          mode="single"
          dialogSize="lg"
          storageKey="module-groups-targets"
          searchPlaceholder="搜索模块号 / 模块名 / 主表"
          emptyText="没有匹配的统一工作台模块。"
          getRowId={(row) => String(row.M_IDX ?? '')}
          onPick={(picked) => {
            const id = Number(picked[0]?.M_IDX ?? Number.NaN)
            if (Number.isFinite(id)) {
              setEditor((prev) => prev && { ...prev, moduleId: id, moduleDesc: String(picked[0]?.M_DESC ?? '') })
            }
            setChooserOpen(false)
          }}
          onClose={() => setChooserOpen(false)}
        />
      ) : null}

      {deleting ? (
        <Modal
          title="删除分组"
          onClose={() => setDeleting(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setDeleting(null)}>取消</Button>
              <Button
                size="sm"
                variant="danger"
                disabled={remove.isPending}
                loading={remove.isPending}
                onClick={() => remove.mutate(deleting)}
              >
                确认删除
              </Button>
            </>
          )}
        >
          <p className="small mb-0">
            确认删除模块 <span className="fw-semibold">{deleting.moduleDesc}</span>
            <span className="text-secondary font-monospace ms-1">{deleting.moduleId}</span> 的分组
            「<span className="fw-semibold">{deleting.description}</span>」？
            删除后该模块的列表右上角分组下拉会少一项。
          </p>
          {deleting.error ? (
            <div className="alert alert-danger py-1 px-2 small mt-2 mb-0" role="alert">{deleting.error}</div>
          ) : null}
        </Modal>
      ) : null}
    </div>
  )
}
