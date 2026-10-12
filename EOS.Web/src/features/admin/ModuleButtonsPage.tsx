import {
  IconDeviceFloppy, IconPencil, IconPlus, IconRefresh, IconTrash, IconUsers,
} from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import type { ColumnDef, RowSelectionState } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpSearchBox } from '../../components/common/ErpSearchBox'
import { ErpTable } from '../../components/common/ErpTable'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'
import { describeApiError } from '../../lib/errors'
import { ConfigPublishAlerts, type ConfigSaveOutcome } from './ConfigPublishAlerts'
import { MANUAL_EVENT } from '../menu-admin/documentActionConfig'

/**
 * 自定义按钮（模块 230103，定制承载页 `/admin/module-buttons`）。
 *
 * 形态与全站其它列表页同一套（统一电子表格）：**一张跨模块的扁平列表 + 新增 / 编辑弹窗**。
 * 按钮配在"别的"模块上，所以模块是行上的一列（可排序、可搜索），而不是左栏常驻清单——
 * "系统里配了什么"一眼看全，找一个按钮不必先猜它在哪个模块上。
 *
 * 编辑对象是 `MODULE_BUSINESS_ACTION` 里 `EVENT_CODE='MANUAL'` 的行；保存走显式作用域 `manual`，
 * 该模块的行为动作与校验规则一行不动（真库防线见 `ModuleBusinessConfigScopeLiveTests`）。
 * 服务端保存成功后自动发布一次，故按钮就叫「保存」；发布不过关时的说法是"已保存，但未生效"。
 */

/** 服务端下发的可配置按钮处理器（`GET /admin/module-buttons/meta`）。 */
type DocumentActionOption = { key: string; label: string; placement: string }

/** 列表行：`GET /admin/module-buttons/overview` 下发的全库按钮行（含所属模块）。 */
type ButtonRow = {
  moduleId: number
  moduleDesc: string
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode: string
  label?: string | null
  remark?: string | null
}

/** 授权镜像的一行（0/0 = 配了没人能用，属 fail-closed 的正常态，界面上必须说出来）。 */
type AuthorizationEntry = { seq: number; key: string; label: string; users: number; groups: number }

type OverviewView = {
  actions: ButtonRow[]
  canWrite: boolean
  buttonAuthorization?: { moduleId: number; buttons: AuthorizationEntry[] }[]
}

/**
 * 该模块的完整按钮行（`GET /admin/module-buttons/{id}`）。
 *
 * 保存是"整模块替换"，回传时必须把界面**不显示**的字段（触发条件 / 参数 / 反向 / 公式行）原样带上，
 * 否则一次编辑就把它们抹成空值——所以这份类型刻意宽松，只列界面用得到的键。
 */
type ButtonDetail = Record<string, unknown> & { seq: number; effectKey: string; enabled: boolean }

type ButtonsView = { moduleId: number; buttons: ButtonDetail[] }

/** 新增 / 编辑弹窗的草稿（提交前是页面状态，不落到表格的行对象上）。 */
type EditorState = {
  mode: 'create' | 'edit'
  /** 新增时可能还没选模块；保存前必须填上。 */
  moduleId: number | null
  moduleDesc: string
  /** 编辑时定位那一行：同一模块内 SEQ 唯一。 */
  seq: number | null
  label: string
  effectKey: string
  enabled: boolean
  remark: string
  error: string | null
}

type DeleteState = {
  moduleId: number
  moduleDesc: string
  seq: number
  label: string
  error: string | null
}

type AuthorizationState = {
  moduleId: number
  moduleDesc: string
  seq: number
  effectKey: string
  label: string
}

/** 一次保存要动的东西：三种动作都是"取该模块完整行 → 改 → 整面替换"。 */
type SaveChanges =
  | { kind: 'create'; draft: EditorState }
  | { kind: 'update'; draft: EditorState }
  | { kind: 'remove'; moduleId: number; seq: number }

/** 授权索引键：授权粒度是「模块 + 按钮键」，键比较大小写不敏感（库内两处写入口径不完全一致）。 */
function authorizationKey(moduleId: number, effectKey: string): string {
  return `${moduleId}#${effectKey.trim().toUpperCase()}`
}

/** 保存前的本地拒绝（如"这一行在别处已被删"）也说清楚，而不是笼统报"保存失败"。 */
function describeSaveError(error: unknown): string {
  if (error instanceof ApiError) return describeApiError(error, '保存自定义按钮失败。')
  return error instanceof Error && error.message !== '' ? error.message : '保存自定义按钮失败。'
}

export function ModuleButtonsPage() {
  const queryClient = useQueryClient()
  // 深链接 `?moduleId=170204` 从菜单管理的「配按钮」入口跳过来：预填成关键字，直接落在该模块的行上
  const [keyword, setKeyword] = useState(() => new URLSearchParams(window.location.search).get('moduleId') ?? '')
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [chooserOpen, setChooserOpen] = useState(false)
  const [deleting, setDeleting] = useState<DeleteState | null>(null)
  const [authTarget, setAuthTarget] = useState<AuthorizationState | null>(null)
  const [outcome, setOutcome] = useState<ConfigSaveOutcome | null>(null)

  const overview = useQuery({
    queryKey: ['module-buttons', 'overview'],
    queryFn: () => apiClient.get<OverviewView>('/admin/module-buttons/overview'),
  })
  const meta = useQuery({
    queryKey: ['module-buttons', 'meta'],
    queryFn: () => apiClient.get<{ buttons: DocumentActionOption[] }>('/admin/module-buttons/meta'),
  })

  const canWrite = overview.data?.canWrite ?? false
  // 这两处都过 useMemo：`?? []` 每次渲染都是新数组，直接进依赖会让下游 memo 全部失效
  const options = useMemo(() => meta.data?.buttons ?? [], [meta.data])
  const optionsByKey = useMemo(() => {
    const map = new Map<string, DocumentActionOption>()
    for (const option of options) map.set(option.key.toUpperCase(), option)
    return map
  }, [options])
  const authByKey = useMemo(() => {
    const map = new Map<string, AuthorizationEntry>()
    for (const group of overview.data?.buttonAuthorization ?? []) {
      for (const entry of group.buttons) map.set(authorizationKey(group.moduleId, entry.key), entry)
    }
    return map
  }, [overview.data])

  const rows = useMemo(() => {
    const all = overview.data?.actions ?? []
    const needle = keyword.trim().toLowerCase()
    if (needle === '') return all
    return all.filter((row) =>
      `${row.moduleId} ${row.moduleDesc} ${row.label ?? ''} ${row.effectKey}`.toLowerCase().includes(needle))
  }, [overview.data, keyword])
  const refreshAll = useCallback(() => {
    setOutcome(null)
    void queryClient.invalidateQueries({ queryKey: ['module-buttons'] })
  }, [queryClient])

  const save = useMutation({
    mutationFn: async (changes: SaveChanges) => {
      const moduleId = changes.kind === 'remove' ? changes.moduleId : changes.draft.moduleId
      if (moduleId == null) throw new Error('请先选择模块。')
      // 整面替换前先取该模块的**完整**行：总览下发的是投影（不含条件 / 参数 / 反向 / 公式行），
      // 拿它回传会把界面不显示的那些字段抹掉；顺带也拿到了最新数据，不会被别的改动作废。
      const current = await apiClient.get<ButtonsView>(`/admin/module-buttons/${moduleId}`)
      let buttons = current.buttons ?? []
      if (changes.kind === 'create') {
        const nextSeq = buttons.reduce((max, row) => Math.max(max, row.seq), 0) + 1
        buttons = [...buttons, {
          seq: nextSeq,
          eventCode: MANUAL_EVENT,
          effectKey: changes.draft.effectKey,
          enabled: changes.draft.enabled,
          failMode: 'BLOCK',
          label: changes.draft.label.trim() === '' ? null : changes.draft.label.trim(),
          remark: changes.draft.remark.trim() === '' ? null : changes.draft.remark.trim(),
        }]
      } else if (changes.kind === 'update') {
        if (!buttons.some((row) => row.seq === changes.draft.seq)) {
          throw new Error('这一行已不存在（可能被别处删掉了），请刷新后重试。')
        }
        buttons = buttons.map((row) => row.seq === changes.draft.seq ? {
          ...row,
          effectKey: changes.draft.effectKey,
          enabled: changes.draft.enabled,
          label: changes.draft.label.trim() === '' ? null : changes.draft.label.trim(),
          remark: changes.draft.remark.trim() === '' ? null : changes.draft.remark.trim(),
        } : row)
      } else {
        buttons = buttons.filter((row) => row.seq !== changes.seq)
      }
      // 只发按钮行、不发校验规则；`eventCode` 一律钉成 MANUAL（作用域与请求体必须一致）。
      return await apiClient.put<ConfigSaveOutcome>(`/admin/module-buttons/${moduleId}`, {
        actions: buttons.map((row) => ({ ...row, eventCode: MANUAL_EVENT })),
        validationRules: [],
      })
    },
    onSuccess: async (result) => {
      setOutcome(result ?? null)
      setEditor(null)
      setDeleting(null)
      await queryClient.invalidateQueries({ queryKey: ['module-buttons'] })
    },
    onError: (error, changes) => {
      const message = describeSaveError(error)
      // 弹窗不关：输入还在用户手上，错误要贴着它显示
      if (changes.kind === 'remove') setDeleting((prev) => prev && { ...prev, error: message })
      else setEditor((prev) => prev && { ...prev, error: message })
    },
  })

  const openCreate = useCallback(() => {
    setEditor({
      mode: 'create',
      moduleId: null,
      moduleDesc: '',
      seq: null,
      label: '',
      effectKey: options[0]?.key ?? '',
      enabled: true,
      remark: '',
      error: null,
    })
  }, [options])

  const openEdit = useCallback((row: ButtonRow) => {
    setEditor({
      mode: 'edit',
      moduleId: row.moduleId,
      moduleDesc: row.moduleDesc,
      seq: row.seq,
      label: row.label ?? '',
      effectKey: row.effectKey,
      enabled: row.enabled,
      remark: row.remark ?? '',
      error: null,
    })
  }, [])

  // 单选语义：首列是单选框，一次只留一行（表格底层是开关语义，不收拢会留下多行）。
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

  const columns = useMemo<ColumnDef<ButtonRow>[]>(() => [
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
          aria-label={`选择 ${row.original.moduleDesc} 的按钮 ${row.original.seq}`}
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
      id: 'label',
      header: '按钮文案',
      size: 160,
      accessorFn: (row) => row.label ?? row.effectName ?? '',
      cell: ({ row }) => {
        const label = row.original.label?.trim()
        if (label) return label
        // 没写自定义文案时按钮显示的是处理器默认名——把它显示出来，别让人以为按钮没名字
        const fallback = row.original.effectName
          ?? optionsByKey.get(row.original.effectKey.trim().toUpperCase())?.label
        return fallback
          ? <span className="text-secondary">{fallback}（默认）</span>
          : <span className="text-secondary">—</span>
      },
    },
    {
      id: 'effectKey',
      header: '处理器',
      size: 200,
      accessorFn: (row) => row.effectKey,
      cell: ({ row }) => {
        const option = optionsByKey.get(row.original.effectKey.trim().toUpperCase())
        return (
          <>
            <span className="font-monospace">{row.original.effectKey}</span>
            {option ? <span className="text-secondary ms-1">{option.label}</span> : null}
          </>
        )
      },
    },
    {
      id: 'seq',
      header: '顺序',
      size: 70,
      accessorFn: (row) => row.seq,
    },
    {
      id: 'enabled',
      header: '启用',
      size: 80,
      accessorFn: (row) => (row.enabled ? 1 : 0),
      cell: ({ row }) => (
        <span className={`badge ${row.original.enabled ? 'text-bg-success' : 'text-bg-secondary'}`}>
          {row.original.enabled ? '启用' : '停用'}
        </span>
      ),
    },
    {
      id: 'authorization',
      header: '谁能用',
      size: 140,
      enableSorting: false,
      cell: ({ row }) => {
        if (!row.original.enabled) return <span className="text-secondary">停用不计</span>
        const entry = authByKey.get(authorizationKey(row.original.moduleId, row.original.effectKey))
        if (entry == null) return <span className="text-secondary">—</span>
        // 0/0 是 fail-closed 的正常态，但"配了没人能用"是静默失效，必须显眼
        if (entry.users === 0 && entry.groups === 0) return <span className="text-danger">⚠ 无人可用</span>
        return `${entry.users} 用户 / ${entry.groups} 组`
      },
    },
    {
      id: 'remark',
      header: '备注',
      size: 200,
      accessorFn: (row) => row.remark ?? '',
      cell: ({ row }) => row.original.remark?.trim() || <span className="text-secondary">—</span>,
    },
    {
      id: 'actions',
      header: '操作',
      size: 230,
      enableSorting: false,
      enableHiding: false,
      meta: { frozenRight: true, resizable: false, truncate: false, minWidth: 230 },
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
            icon={<IconUsers size={14} />}
            onClick={() => setAuthTarget({
              moduleId: row.original.moduleId,
              moduleDesc: row.original.moduleDesc,
              seq: row.original.seq,
              effectKey: row.original.effectKey,
              label: row.original.label?.trim()
                || row.original.effectName
                || optionsByKey.get(row.original.effectKey.trim().toUpperCase())?.label
                || row.original.effectKey,
            })}
          >
            授权
          </Button>
          <Button
            size="sm"
            variant="ghost"
            icon={<IconTrash size={14} />}
            disabled={!canWrite}
            onClick={() => setDeleting({
              moduleId: row.original.moduleId,
              moduleDesc: row.original.moduleDesc,
              seq: row.original.seq,
              label: row.original.label?.trim()
                || row.original.effectName
                || optionsByKey.get(row.original.effectKey.trim().toUpperCase())?.label
                || row.original.effectKey,
              error: null,
            })}
          >
            删除
          </Button>
        </div>
      ),
    },
  ], [authByKey, canWrite, openEdit, optionsByKey])

  const errorMessage = describeApiError(overview.error ?? meta.error, '加载自定义按钮失败。')
  const editorDefaultLabel = editor == null
    ? ''
    : optionsByKey.get(editor.effectKey.trim().toUpperCase())?.label ?? ''
  const authEntry = authTarget == null
    ? null
    : authByKey.get(authorizationKey(authTarget.moduleId, authTarget.effectKey)) ?? null

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="自定义按钮"
        search={(
          <ErpSearchBox
            value={keyword}
            onChange={setKeyword}
            placeholder="模块号 / 模块名 / 按钮文案 / 处理器"
            ariaLabel="搜索按钮"
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
            {outcome ? (
              <div className="px-3 pt-2"><ConfigPublishAlerts outcome={outcome} /></div>
            ) : null}
          </>
        )}
      >
        {/* 表格区必须是卡片的直接子项：卡内滚动（表格区内部滚、表头吸顶）靠
            `.erp-list-card`（flex 列 + overflow:hidden）→ `.erp-table-shell`（flex:1 + overflow:auto）
            这条链撑着。中间再套一层容器，那一层会按内容高度撑开、把表格一起顶出卡片，
            被 overflow:hidden 裁掉——表现成"看不到滚动条、下方内容也露不出来"。 */}
        {overview.isPending || meta.isPending ? (
          <LoadingState label="正在加载自定义按钮…" />
        ) : overview.isError || meta.isError ? (
          <ErrorState message={errorMessage} onRetry={refreshAll} />
        ) : (
          <ErpTable
            columns={columns}
            data={rows}
            getRowId={(row) => `${row.moduleId}#${row.seq}`}
            empty={(
              <EmptyState
                title="系统里还没有自定义按钮"
                description="点右上角「新增」加一个——没配按钮的模块，单据面上不出现任何自定义按钮。"
              />
            )}
            resizable
            storageKey="module-buttons"
            clientSideSorting
            rowClickSingleSelect
            rowSelection={rowSelection}
            onRowSelectionChange={handleRowSelectionChange}
            onRowDoubleClick={(row) => openEdit(row)}
          />
        )}
      </ErpListCard>

      {editor ? (
        <Modal
          title={editor.mode === 'create'
            ? '新增自定义按钮'
            : `修改按钮：${editor.label.trim() || editorDefaultLabel || editor.effectKey}`}
          size="lg"
          onClose={() => setEditor(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setEditor(null)}>取消</Button>
              <Button
                size="sm"
                icon={<IconDeviceFloppy size={16} />}
                disabled={editor.moduleId == null || editor.effectKey === '' || save.isPending}
                loading={save.isPending}
                onClick={() => save.mutate({ kind: editor.mode === 'create' ? 'create' : 'update', draft: editor })}
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
                  <Button
                    size="sm"
                    variant="ghost"
                    icon={<IconPlus size={14} />}
                    onClick={() => setChooserOpen(true)}
                  >
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
                  ? '按钮配在某个模块上，只有统一工作台模块有按钮消费方。'
                  : '按钮归它所在的模块；要挪到别的模块请新增一个再删掉这个。'}
              </div>
            </div>
            <div className="col-6">
              <label className="form-label small mb-1" htmlFor="module-button-label">按钮文案</label>
              <input
                id="module-button-label"
                className="form-control form-control-sm"
                maxLength={50}
                placeholder={editorDefaultLabel}
                value={editor.label}
                onChange={(event) => setEditor({ ...editor, label: event.target.value })}
              />
              <div className="form-text">留空则用处理器的默认名称。</div>
            </div>
            <div className="col-6">
              <label className="form-label small mb-1" htmlFor="module-button-key">处理器</label>
              <select
                id="module-button-key"
                className="form-select form-select-sm"
                value={editor.effectKey}
                onChange={(event) => setEditor({ ...editor, effectKey: event.target.value })}
              >
                {options.length === 0 ? <option value="">（没有已登记的处理器）</option> : null}
                {options.map((option) => (
                  <option key={option.key} value={option.key}>{option.label}（{option.key}）</option>
                ))}
              </select>
              <div className="form-text">只能从已登记的处理器里挑——挑不到的键保存时会被拒。</div>
            </div>
            <div className="col-6">
              <label className="form-label small mb-1" htmlFor="module-button-enabled">启用</label>
              <div className="form-check">
                <input
                  id="module-button-enabled"
                  className="form-check-input"
                  type="checkbox"
                  checked={editor.enabled}
                  onChange={(event) => setEditor({ ...editor, enabled: event.target.checked })}
                />
                <label className="form-check-label small" htmlFor="module-button-enabled">
                  在单据面上显示这个按钮
                </label>
              </div>
            </div>
            <div className="col-12">
              <label className="form-label small mb-1" htmlFor="module-button-remark">备注</label>
              <input
                id="module-button-remark"
                className="form-control form-control-sm"
                maxLength={200}
                value={editor.remark}
                onChange={(event) => setEditor({ ...editor, remark: event.target.value })}
              />
            </div>
          </div>

          <div className="text-secondary small mt-2">
            保存后服务端自动发布一次。按钮的<b>可用名单</b>由用户权限设定（2306）按「模块 + 按钮键」维护：
            本页只配按钮本身，不代替授权——能配不等于能用。
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
          source={{ kind: 'sourceKey', key: 'module-buttons.targets' }}
          mode="single"
          dialogSize="lg"
          storageKey="module-buttons-targets"
          searchPlaceholder="搜索模块号 / 模块名 / 主表"
          emptyText="没有匹配的统一工作台模块。"
          getRowId={(row) => String(row.M_IDX ?? '')}
          onPick={(picked) => {
            const id = Number(picked[0]?.M_IDX ?? Number.NaN)
            if (Number.isFinite(id)) {
              setEditor((prev) => prev && {
                ...prev,
                moduleId: id,
                moduleDesc: String(picked[0]?.M_DESC ?? ''),
              })
            }
            setChooserOpen(false)
          }}
          onClose={() => setChooserOpen(false)}
        />
      ) : null}

      {deleting ? (
        <Modal
          title="删除自定义按钮"
          onClose={() => setDeleting(null)}
          footer={(
            <>
              <Button size="sm" variant="ghost" onClick={() => setDeleting(null)}>取消</Button>
              <Button
                size="sm"
                variant="danger"
                disabled={save.isPending}
                loading={save.isPending}
                onClick={() => save.mutate({ kind: 'remove', moduleId: deleting.moduleId, seq: deleting.seq })}
              >
                确认删除
              </Button>
            </>
          )}
        >
          <p className="small mb-0">
            确认删除模块 <span className="fw-semibold">{deleting.moduleDesc}</span>
            <span className="text-secondary font-monospace ms-1">{deleting.moduleId}</span> 上的按钮
            「<span className="fw-semibold">{deleting.label}</span>」（顺序 {deleting.seq}）？
            删除后该按钮从单据面上消失；服务端保存后会重新发布一次。
          </p>
          {deleting.error ? (
            <div className="alert alert-danger py-1 px-2 small mt-2 mb-0" role="alert">{deleting.error}</div>
          ) : null}
        </Modal>
      ) : null}

      {authTarget ? (
        <Modal
          title={`按钮授权：${authTarget.label}`}
          size="lg"
          onClose={() => setAuthTarget(null)}
          footer={(
            <>
              <a className="btn btn-sm btn-outline-secondary" href="/admin/users">去用户权限设定</a>
              <Button size="sm" variant="ghost" onClick={() => setAuthTarget(null)}>关闭</Button>
            </>
          )}
        >
          <div className="d-flex flex-column gap-2 small">
            <div>
              <span>{authTarget.moduleDesc}</span>
              <span className="text-secondary font-monospace ms-1">{authTarget.moduleId}</span>
              <span className="ms-2">处理器 <span className="font-monospace">{authTarget.effectKey}</span></span>
              <span className="ms-2">
                当前可用
                <span className="fw-semibold ms-1">
                  {authEntry?.users ?? 0} 个用户 / {authEntry?.groups ?? 0} 个组
                </span>
              </span>
              {authEntry != null && authEntry.users === 0 && authEntry.groups === 0 ? (
                <span className="text-danger ms-1">
                  （配了没人能用：按钮的运行期授权是 fail-closed 名单，默认全场不可点）
                </span>
              ) : null}
            </div>
            <div className="text-secondary">
              按钮的可用名单由<span className="fw-semibold">用户权限设定</span>（2306）按"模块 + 按钮键"维护：
              本页只做<span className="fw-semibold">配置</span>（配什么按钮），不代替
              <span className="fw-semibold">授权</span>（谁能点）。两件事不混——能配不等于能用。
            </div>
          </div>
        </Modal>
      ) : null}
    </div>
  )
}
