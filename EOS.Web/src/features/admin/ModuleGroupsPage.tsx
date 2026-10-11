import { IconArrowDown, IconArrowUp, IconDeviceFloppy, IconPlus, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useRef, useState } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { ConfigWorkbench, WorkbenchPanelHeader, type WorkbenchModule } from './ConfigWorkbench'

/**
 * 模块分组（模块 230102，定制承载页 `/admin/module-groups`）：为**其它**模块维护列表分组
 * ——分组名称 + 分组表达式 + 顺序。
 *
 * 交互（ADR-033 拆页面后的重排）：**左栏常驻模块清单、右栏"分组表 + 选中行编辑区"**——
 * 没有"选择模块"弹窗，也没有"双击总览行才进编辑"这种隐藏手势。
 *
 * 为什么编辑区在表格外而不是行内输入：表格行对象是**快照**（TanStack 不因 `data` 变化重建行），
 * 行内受控输入会被快照里的旧值顶回去（用户看到"打不进去字"）。编辑区绑定页面状态，输入跟手，
 * 且长表达式与"插入字段"有了自己的地方——这也正是 230104 计划要用的"选中行 → 下方详情"模型。
 *
 * - **分组保存即生效**（不进工作台定义快照、没有发布这一步），故按钮叫「保存」；
 * - **表达式能不能用**由服务端受控编译器判定，页面不自行判断；**样例值**由服务端在主表上真跑一次；
 * - 顺序调整与删除是结构性动作，各自立即提交并确认。
 */

/** 一个分组（服务端 ModuleGroupAdminRow）。 */
interface ModuleGroupRow {
  groupId: number
  sortIdx: number
  description: string
  expression: string
  available: boolean
  error: string | null
  /** 样例值（服务端在目标主表上真跑一次）。字段随本批新增：旧进程还没重启时不下发，故可选。 */
  samples?: string[]
  sampleError?: string | null
}

interface ModuleGroupView {
  moduleId: number
  moduleDesc: string
  nodeKind: string
  masterTable: string
  groups: ModuleGroupRow[]
}

interface OverviewView {
  targets: WorkbenchModule[]
  groups: (ModuleGroupRow & { moduleId: number; moduleDesc: string })[]
  canWrite: boolean
}

/** 草稿行：`groupId` 为负 = 还没保存的新行（用负数当临时键，不与服务端身份混淆）。 */
interface DraftGroup {
  groupId: number
  description: string
  expression: string
}

export function ModuleGroupsPage() {
  const queryClient = useQueryClient()
  const [moduleId, setModuleId] = useState<number | null>(() => {
    const raw = new URLSearchParams(window.location.search).get('moduleId')
    const parsed = raw == null || raw === '' ? Number.NaN : Number(raw)
    return Number.isFinite(parsed) ? parsed : null
  })
  const [draft, setDraft] = useState<DraftGroup[] | null>(null)
  const [deletedIds, setDeletedIds] = useState<number[]>([])
  const [selectedKey, setSelectedKey] = useState<number | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveNotes, setSaveNotes] = useState<string[] | null>(null)
  const [pendingDelete, setPendingDelete] = useState<DraftGroup | null>(null)
  const nextTempId = useRef(-1)

  const overview = useQuery({
    queryKey: ['module-groups', 'overview'],
    queryFn: () => apiClient.get<OverviewView>('/admin/module-groups/overview'),
  })
  const view = useQuery({
    queryKey: ['module-groups', moduleId],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<ModuleGroupView>(`/admin/module-groups/${moduleId}`),
  })
  const isWorkbench = view.data?.nodeKind === 'WORKBENCH' && (view.data?.masterTable ?? '') !== ''
  const fields = useQuery({
    queryKey: ['module-groups', moduleId, 'fields'],
    enabled: moduleId != null && isWorkbench,
    queryFn: () => apiClient.get<{ fields: string[] }>(`/admin/module-groups/${moduleId}/fields`),
  })

  const targets = overview.data?.targets ?? []
  const canWrite = overview.data?.canWrite ?? false
  const selected = targets.find((item) => item.moduleId === moduleId) ?? null

  const serverRows = useMemo<DraftGroup[]>(
    () => (view.data?.groups ?? []).map((group) => ({
      groupId: group.groupId, description: group.description, expression: group.expression,
    })),
    [view.data])
  const serverByKey = useMemo(() => {
    const map = new Map<number, ModuleGroupRow>()
    for (const group of view.data?.groups ?? []) map.set(group.groupId, group)
    return map
  }, [view.data])

  const rows = draft ?? serverRows
  const visible = rows.filter((row) => !deletedIds.includes(row.groupId))
  const dirty = draft != null || deletedIds.length > 0
  const editing = visible.find((row) => row.groupId === selectedKey) ?? null
  const fieldOptions = fields.data?.fields ?? []

  const reset = useCallback(() => {
    setDraft(null)
    setDeletedIds([])
    setSelectedKey(null)
    setSaveError(null)
    setSaveNotes(null)
  }, [])

  const reload = useCallback(() => {
    reset()
    void queryClient.invalidateQueries({ queryKey: ['module-groups'] })
  }, [queryClient, reset])

  const selectModule = (nextId: number) => {
    if (nextId === moduleId) return
    if (dirty && !window.confirm('当前模块有未保存的改动，切换会丢掉它们。仍要切换？')) return
    setModuleId(nextId)
    reset()
    setPendingDelete(null)
    const url = new URL(window.location.href)
    url.searchParams.set('moduleId', String(nextId))
    window.history.replaceState({}, '', url)
  }

  const updateEditing = useCallback((changes: Partial<DraftGroup>) => {
    setDraft((prev) => (prev ?? serverRows).map((row) =>
      (row.groupId === selectedKey ? { ...row, ...changes } : row)))
  }, [selectedKey, serverRows])

  const addRow = useCallback(() => {
    const tempId = nextTempId.current
    nextTempId.current -= 1
    setDraft([...(draft ?? serverRows), { groupId: tempId, description: '', expression: '' }])
    setSelectedKey(tempId)
  }, [draft, serverRows])

  const confirmDelete = useCallback(() => {
    const target = pendingDelete
    if (target == null) return
    setDeletedIds((prev) => [...prev, target.groupId])
    if (target.groupId > 0)
      setDraft((prev) => (prev ?? serverRows).filter((row) => row.groupId !== target.groupId))
    if (selectedKey === target.groupId) setSelectedKey(null)
    setPendingDelete(null)
  }, [pendingDelete, selectedKey, serverRows])

  const move = useMutation({
    mutationFn: (input: { groupId: number; action: 'up' | 'down' }) =>
      apiClient.post<void>(`/admin/module-groups/${moduleId}/${input.groupId}/move`, { action: input.action }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['module-groups', moduleId] })
    },
    onError: (error) => setSaveError(describeApiError(error, '调整顺序失败。')),
  })

  const save = useMutation({
    mutationFn: async () => {
      // 一次「保存」提交全部改动：改过的行 → 新增的行 → 删除的行。
      // 每行各自立即生效（没有跨行事务），故某行失败不影响其余行；失败逐条回给用户，草稿保留。
      const notes: string[] = []
      const failedKeys = new Set<number>()
      const originalByKey = new Map(serverRows.map((row) => [row.groupId, row]))

      for (const row of rows) {
        if (deletedIds.includes(row.groupId)) continue
        const body = { description: row.description.trim(), expression: row.expression.trim() }
        const original = originalByKey.get(row.groupId)
        try {
          if (original == null) await apiClient.post<void>(`/admin/module-groups/${moduleId}`, body)
          else if (original.description !== body.description || original.expression !== body.expression)
            await apiClient.put<void>(`/admin/module-groups/${moduleId}/${row.groupId}`, body)
        } catch (error) {
          failedKeys.add(row.groupId)
          notes.push(`「${row.description.trim() || '（未命名）'}」未保存：${describeApiError(error, '保存失败。')}`)
        }
      }

      for (const groupId of deletedIds) {
        try {
          await apiClient.delete<void>(`/admin/module-groups/${moduleId}/${groupId}`)
        } catch (error) {
          failedKeys.add(groupId)
          notes.push(`删除分组失败：${describeApiError(error, '删除失败。')}`)
        }
      }

      return { notes, failedKeys }
    },
    onSuccess: async (result) => {
      setSaveError(null)
      setSaveNotes(result.notes.length === 0 ? null : result.notes)
      if (result.notes.length === 0) reset()
      else {
        setDeletedIds((prev) => prev.filter((id) => result.failedKeys.has(id)))
        setDraft((prev) => (prev == null ? null : prev.filter((row) => result.failedKeys.has(row.groupId))))
      }
      await queryClient.invalidateQueries({ queryKey: ['module-groups'] })
    },
    onError: (error) => {
      setSaveNotes(null)
      setSaveError(describeApiError(error, '保存失败。'))
    },
  })

  const columns = useMemo<ColumnDef<DraftGroup>[]>(() => [
    {
      id: 'sortIdx',
      header: '顺序',
      size: 108,
      cell: ({ row }) => {
        const index = visible.findIndex((item) => item.groupId === row.original.groupId)
        const saved = row.original.groupId > 0
        return (
          <div className="d-flex align-items-center gap-1">
            <Button
              size="sm"
              variant="ghost"
              icon={<IconArrowUp size={14} />}
              aria-label={`上移-${row.original.groupId}`}
              disabled={!canWrite || !saved || index <= 0}
              onClick={() => move.mutate({ groupId: row.original.groupId, action: 'up' })}
            />
            <Button
              size="sm"
              variant="ghost"
              icon={<IconArrowDown size={14} />}
              aria-label={`下移-${row.original.groupId}`}
              disabled={!canWrite || !saved || index < 0 || index >= visible.length - 1}
              onClick={() => move.mutate({ groupId: row.original.groupId, action: 'down' })}
            />
            <span className="text-secondary small">
              {saved ? serverByKey.get(row.original.groupId)?.sortIdx ?? '' : '新'}
            </span>
          </div>
        )
      },
    },
    {
      id: 'description',
      header: '分组名称',
      size: 220,
      cell: ({ row }) => (
        <span>{row.original.description.trim() || <span className="text-secondary">（未命名）</span>}</span>
      ),
    },
    {
      id: 'expression',
      header: '分组表达式',
      cell: ({ row }) => <span className="font-monospace small">{row.original.expression}</span>,
    },
    {
      id: 'samples',
      header: '样例值',
      size: 240,
      cell: ({ row }) => {
        const saved = serverByKey.get(row.original.groupId)
        if (saved == null) return <span className="text-secondary small">保存后可见</span>
        const changed = saved.expression !== row.original.expression
        return (
          <div className="d-flex flex-column small">
            <span className={saved.available ? '' : 'text-danger'} title={saved.error ?? ''}>
              {saved.available
                ? (saved.samples == null
                  ? '（样例值需重启服务后可见）'
                  : (saved.samples.length > 0 ? saved.samples.join(' / ') : '（主表当前没有行）'))
                : '表达式不可用'}
            </span>
            {changed ? <span className="text-secondary">已改，保存后更新</span> : null}
            {saved.sampleError ? <span className="text-warning-emphasis">样例执行失败：{saved.sampleError}</span> : null}
          </div>
        )
      },
    },
    {
      id: 'actions',
      header: '操作',
      size: 66,
      cell: ({ row }) => (
        <Button
          size="sm"
          variant="ghost"
          icon={<IconTrash size={16} />}
          aria-label={`删除-${row.original.groupId}`}
          disabled={!canWrite}
          onClick={() => setPendingDelete(row.original)}
        />
      ),
    },
  ], [canWrite, move, serverByKey, visible])

  const editingSaved = editing == null ? null : serverByKey.get(editing.groupId)

  return (
    <ConfigWorkbench
      title="模块分组"
      configuredUnit="分组"
      modules={targets}
      loading={overview.isPending}
      error={overview.isError ? overview.error : null}
      onReload={reload}
      selectedId={moduleId}
      onSelect={selectModule}
      dirtyId={dirty && moduleId != null ? moduleId : null}
      toolbar={(
        <>
          <Button
            size="sm"
            variant="ghost"
            icon={<IconPlus size={16} />}
            disabled={moduleId == null || !canWrite || !isWorkbench}
            onClick={addRow}
          >
            新增分组
          </Button>
          <Button
            size="sm"
            icon={<IconDeviceFloppy size={16} />}
            disabled={moduleId == null || !canWrite || !dirty || save.isPending}
            loading={save.isPending}
            onClick={() => save.mutate()}
          >
            保存
          </Button>
        </>
      )}
      right={selected == null ? (
        <EmptyState
          title="从左边挑一个模块"
          description="分组是列表右上角的分组下拉，配在具体模块上；左栏默认只列已经配过的模块。"
        />
      ) : (
        <div className="d-flex flex-column gap-2">
          <WorkbenchPanelHeader
            module={selected}
            canWrite={canWrite}
            extra={(
              <span className="text-secondary">
                分组保存即生效（不进快照、不需要发布）。表达式只支持主表字段、字面量、+ - * / 、括号与 CASE，
                以及 YEAR / MONTH / DATEPART / REPLICATE / CAST / CONVERT。
              </span>
            )}
          />
          {!isWorkbench ? (
            <div className="alert alert-warning py-2 mb-0" role="alert">
              该节点不是统一工作台模块（或没有操作主表），没有分组消费方：只能查看，不能配分组。
            </div>
          ) : null}
          {saveError ? <div className="alert alert-danger py-2 mb-0" role="alert">{saveError}</div> : null}
          {saveNotes ? (
            <div className="alert alert-warning py-2 mb-0" role="alert">
              部分改动没保存上（其余已生效）：
              <ul className="mb-0 mt-1">{saveNotes.map((note) => <li key={note}>{note}</li>)}</ul>
            </div>
          ) : null}
          {fields.error ? (
            <div className="alert alert-warning py-2 mb-0" role="alert">
              主表字段清单没取到，字段下拉暂不可用：{describeApiError(fields.error, '')}
            </div>
          ) : null}
          <ErpTable
            columns={columns}
            data={visible}
            getRowId={(row) => String(row.groupId)}
            rowClassName={(row) => (row.groupId === selectedKey ? 'table-active' : undefined)}
            onRowClick={(row) => setSelectedKey(row.groupId)}
            empty={(
              <EmptyState
                title="该模块还没有分组"
                description="点右上角「新增分组」加一条——没配分组时，列表右上角不出现分组下拉。"
              />
            )}
            resizable
            storageKey="module-groups"
          />

          {editing == null ? (
            <div className="text-secondary small px-1">点表格里的一行来编辑它；「新增分组」会直接进编辑区。</div>
          ) : (
            <div className="border rounded p-3 d-flex flex-column gap-2">
              <div className="fw-semibold small">
                {editing.groupId > 0 ? `编辑分组（${editing.groupId}）` : '新增分组（未保存）'}
              </div>
              <div className="d-flex flex-wrap gap-2">
                <div style={{ minWidth: '16rem', flex: '1 1 16rem' }}>
                  <label className="form-label small mb-1" htmlFor="group-description">分组名称</label>
                  <input
                    id="group-description"
                    className="form-control form-control-sm"
                    maxLength={40}
                    disabled={!canWrite}
                    value={editing.description}
                    onChange={(event) => updateEditing({ description: event.target.value })}
                  />
                </div>
                <div style={{ minWidth: '24rem', flex: '2 1 24rem' }}>
                  <label className="form-label small mb-1" htmlFor="group-expression">分组表达式</label>
                  <input
                    id="group-expression"
                    className="form-control form-control-sm font-monospace"
                    maxLength={500}
                    disabled={!canWrite}
                    value={editing.expression}
                    onChange={(event) => updateEditing({ expression: event.target.value })}
                  />
                </div>
                {canWrite && fieldOptions.length > 0 ? (
                  <div style={{ minWidth: '12rem', flex: '0 1 12rem' }}>
                    <label className="form-label small mb-1" htmlFor="group-field">插入主表字段</label>
                    <select
                      id="group-field"
                      className="form-select form-select-sm"
                      value=""
                      onChange={(event) => {
                        const field = event.target.value
                        if (field === '') return
                        updateEditing({
                          expression: editing.expression.trim() === ''
                            ? field
                            : `${editing.expression}${field}`,
                        })
                      }}
                    >
                      <option value="">选一个字段…</option>
                      {fieldOptions.map((field) => <option key={field} value={field}>{field}</option>)}
                    </select>
                  </div>
                ) : null}
              </div>
              {editingSaved ? (
                <div className="small text-secondary">
                  当前生效值：{editingSaved.available
                    ? (editingSaved.samples == null
                      ? '（样例值需重启服务后可见）'
                      : (editingSaved.samples.length > 0 ? editingSaved.samples.join(' / ') : '（主表当前没有行）'))
                    : `不可用 —— ${editingSaved.error ?? ''}`}
                </div>
              ) : null}
            </div>
          )}
        </div>
      )}
      footer={pendingDelete == null ? null : (
        <div className="alert alert-warning d-flex align-items-center gap-3 mb-0" role="alert">
          <span className="flex-fill">
            删除分组「{pendingDelete.description.trim() || '（未命名）'}」？列表页的分组下拉会少一项；
            改动在点「保存」后落到服务端。
          </span>
          <Button size="sm" variant="danger" onClick={confirmDelete}>确认删除</Button>
          <Button size="sm" variant="ghost" onClick={() => setPendingDelete(null)}>取消</Button>
        </div>
      )}
    />
  )
}
