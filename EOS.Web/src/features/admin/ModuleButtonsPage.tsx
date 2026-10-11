import { IconDeviceFloppy, IconPlus, IconTrash } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import { ConfigPublishAlerts, type ConfigSaveOutcome } from './ConfigPublishAlerts'
import { ConfigWorkbench, WorkbenchPanelHeader, type WorkbenchModule } from './ConfigWorkbench'
import { MANUAL_EVENT } from '../menu-admin/documentActionConfig'

/**
 * 自定义按钮（模块 230103，定制承载页 `/admin/module-buttons`）。
 *
 * 交互（ADR-033 拆页面后的重排）：**左栏常驻模块清单、右栏按钮表行内编辑**——
 * 不再有"选择模块"弹窗，也不再有"双击总览行才进编辑"这种隐藏手势。保存即生效（服务端自动发布）。
 *
 * 编辑对象是 `MODULE_BUSINESS_ACTION` 里 `EVENT_CODE='MANUAL'` 的行；保存走显式作用域 `manual`，
 * 该模块的行为动作与校验规则一行不动（真库防线见 `ModuleBusinessConfigScopeLiveTests`）。
 */

/** 服务端下发的可配置按钮处理器（`GET /admin/module-buttons/meta`）。 */
type DocumentActionOption = { key: string; label: string; placement: string }

/** 按钮行（字段与模块管理侧同一个 DTO：编辑时整行回传，未知字段原样带上）。 */
type ButtonRow = {
  seq: number
  eventCode: string
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode?: string
  condition?: string | null
  params?: string | null
  reverse?: string | null
  remark?: string | null
  sourceRef?: string | null
  ops?: unknown
  label?: string | null
}

/** 草稿行：`_key` 是界面用的稳定标识（新增行还没有序号，用键定位才不会被下标错位）。 */
type DraftButton = ButtonRow & { _key: string }

type ButtonsView = { moduleId: number; buttons: ButtonRow[] }

type OverviewView = {
  targets: WorkbenchModule[]
  actions: (ButtonRow & { moduleId: number; moduleDesc: string })[]
  canWrite: boolean
}

/** 授权镜子：每个按钮当前有几个用户 / 几个组可用（0/0 = 配了没人能用，界面必须说出来）。 */
type AuthorizationEntry = { seq: number; effectKey: string; label: string; users: number; groups: number }
type AuthorizationView = { buttons: AuthorizationEntry[] }

export function ModuleButtonsPage() {
  const queryClient = useQueryClient()
  // 深链接：`?moduleId=170204` 直达该模块（从菜单管理的「配按钮」入口跳过来时用）
  const [moduleId, setModuleId] = useState<number | null>(() => {
    const raw = new URLSearchParams(window.location.search).get('moduleId')
    const parsed = raw == null || raw === '' ? Number.NaN : Number(raw)
    return Number.isFinite(parsed) ? parsed : null
  })
  const [draft, setDraft] = useState<DraftButton[] | null>(null)
  const [authTarget, setAuthTarget] = useState<DraftButton | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<ConfigSaveOutcome | null>(null)

  const overview = useQuery({
    queryKey: ['module-buttons', 'overview'],
    queryFn: () => apiClient.get<OverviewView>('/admin/module-buttons/overview'),
  })
  const meta = useQuery({
    queryKey: ['module-buttons', 'meta'],
    queryFn: () => apiClient.get<{ buttons: DocumentActionOption[] }>('/admin/module-buttons/meta'),
  })
  const view = useQuery({
    queryKey: ['module-buttons', moduleId],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<ButtonsView>(`/admin/module-buttons/${moduleId}`),
  })
  const authorization = useQuery({
    queryKey: ['module-buttons', moduleId, 'authorization'],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<AuthorizationView>(`/admin/module-buttons/${moduleId}/action-authorization`),
  })

  const targets = overview.data?.targets ?? []
  const canWrite = overview.data?.canWrite ?? false
  const selected = targets.find((item) => item.moduleId === moduleId) ?? null
  // 这两处都过 useMemo：`?? []` 每次渲染都是新数组，直接进 useMemo 依赖会让下游 memo 全部失效
  const options = useMemo(() => meta.data?.buttons ?? [], [meta.data])
  const loadedRows = useMemo<DraftButton[]>(
    () => (view.data?.buttons ?? []).map((row, index) => ({ ...row, _key: `saved-${index}` })),
    [view.data])
  const optionsByKey = useMemo(() => {
    const map = new Map<string, DocumentActionOption>()
    for (const option of options) map.set(option.key, option)
    return map
  }, [options])
  const authByKey = useMemo(() => {
    const map = new Map<string, AuthorizationEntry>()
    for (const entry of authorization.data?.buttons ?? []) map.set(entry.effectKey, entry)
    return map
  }, [authorization.data])

  const rows: DraftButton[] = draft ?? loadedRows
  const dirty = draft != null

  const reload = () => {
    setDraft(null)
    setOutcome(null)
    setSaveError(null)
    void queryClient.invalidateQueries({ queryKey: ['module-buttons'] })
  }

  const selectModule = (nextId: number) => {
    if (nextId === moduleId) return
    // 未保存改动：切模块会丢，先问一句（左栏该模块上已有「未保存」徽标提示）
    if (dirty && !window.confirm('当前模块有未保存改动，切换会丢掉它们。仍要切换？')) return
    setModuleId(nextId)
    setDraft(null)
    setOutcome(null)
    setSaveError(null)
    setAuthTarget(null)
    const url = new URL(window.location.href)
    url.searchParams.set('moduleId', String(nextId))
    window.history.replaceState({}, '', url)
  }

  // 三个编辑入口都用「函数式更新 + useCallback」：它们的身份必须稳定，否则列定义每次渲染都会重建
  // （列定义进了 useMemo，而它的依赖里带着这些回调）。
  const patch = useCallback((key: string, changes: Partial<DraftButton>) => {
    setDraft((prev) => (prev ?? loadedRows).map((row) => (row._key === key ? { ...row, ...changes } : row)))
  }, [loadedRows])

  const addRow = useCallback(() => {
    setDraft((prev) => {
      const current = prev ?? loadedRows
      const first = options[0]
      const nextSeq = current.reduce((max, row) => Math.max(max, row.seq), 0) + 1
      return [
        ...current,
        {
          _key: `new-${nextSeq}-${Date.now()}`,
          seq: nextSeq,
          eventCode: MANUAL_EVENT,
          effectKey: first?.key ?? '',
          enabled: true,
          failMode: 'BLOCK',
          label: first?.label ?? '',
        },
      ]
    })
  }, [loadedRows, options])

  // 直接收整行（列里本来就有它）：确认框要显示"正被删的那一行"，从参数取才不会闭包住会变的 rows。
  const removeRow = useCallback((row: DraftButton) => {
    const name = row.label?.trim() || row.effectKey || '这一行'
    if (!window.confirm(`删除按钮「${name}」？改动在点「保存并生效」后落到服务端。`)) return
    setDraft((prev) => (prev ?? loadedRows).filter((item) => item._key !== row._key))
  }, [loadedRows])

  const save = useMutation({
    mutationFn: async (list: DraftButton[]) => {
      // 整面替换：只发按钮行、不发校验规则；`_key` 是界面标识，不回传。
      return await apiClient.put<ConfigSaveOutcome>(`/admin/module-buttons/${moduleId}`, {
        actions: list.map(({ _key, ...row }) => ({ ...row, eventCode: MANUAL_EVENT })),
        validationRules: [],
      })
    },
    onSuccess: async (result) => {
      setSaveError(null)
      setOutcome(result ?? null)
      setDraft(null)
      await queryClient.invalidateQueries({ queryKey: ['module-buttons'] })
    },
    onError: (error) => {
      setOutcome(null)
      setSaveError(describeApiError(error, '保存自定义按钮失败。'))
    },
  })

  const columns = useMemo<ColumnDef<DraftButton>[]>(() => [
    { id: 'seq', header: '顺序', size: 70, cell: ({ row }) => row.original.seq },
    {
      id: 'label',
      header: '按钮文案',
      cell: ({ row }) => (
        <input
          className="form-control form-control-sm"
          aria-label={`按钮文案-${row.original._key}`}
          maxLength={50}
          disabled={!canWrite}
          value={row.original.label ?? ''}
          placeholder={optionsByKey.get(row.original.effectKey)?.label ?? ''}
          onChange={(event) => patch(row.original._key, { label: event.target.value })}
        />
      ),
    },
    {
      id: 'effectKey',
      header: '处理器',
      cell: ({ row }) => (
        <select
          className="form-select form-select-sm"
          aria-label={`处理器-${row.original._key}`}
          disabled={!canWrite}
          value={row.original.effectKey}
          onChange={(event) => patch(row.original._key, { effectKey: event.target.value })}
        >
          {options.length === 0 ? <option value="">（没有已登记的处理器）</option> : null}
          {options.map((option) => (
            <option key={option.key} value={option.key}>{option.label}（{option.key}）</option>
          ))}
        </select>
      ),
    },
    {
      id: 'enabled',
      header: '启用',
      size: 70,
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label={`启用-${row.original._key}`}
          disabled={!canWrite}
          checked={row.original.enabled}
          onChange={(event) => patch(row.original._key, { enabled: event.target.checked })}
        />
      ),
    },
    {
      id: 'authorization',
      header: '谁能用',
      size: 150,
      // 0/0 是 fail-closed 的正常态，但"配了没人能用"必须显眼——它是静默失效，不是错误。
      cell: ({ row }) => {
        const entry = authByKey.get(row.original.effectKey)
        const none = entry != null && entry.users === 0 && entry.groups === 0
        return (
          <button
            type="button"
            className="btn btn-link btn-sm p-0 text-decoration-none"
            aria-label={`授权-${row.original._key}`}
            onClick={() => setAuthTarget(row.original)}
          >
            {entry == null
              ? '查看'
              : none
                ? <span className="text-danger">⚠ 无人可用</span>
                : `${entry.users} 用户 / ${entry.groups} 组`}
          </button>
        )
      },
    },
    {
      id: 'remark',
      header: '备注',
      cell: ({ row }) => (
        <input
          className="form-control form-control-sm"
          aria-label={`备注-${row.original._key}`}
          maxLength={200}
          disabled={!canWrite}
          value={row.original.remark ?? ''}
          onChange={(event) => patch(row.original._key, { remark: event.target.value })}
        />
      ),
    },
    {
      id: 'actions',
      header: '操作',
      size: 70,
      cell: ({ row }) => (
        <Button
          size="sm"
          variant="ghost"
          icon={<IconTrash size={16} />}
          aria-label={`删除-${row.original._key}`}
          disabled={!canWrite}
          onClick={() => removeRow(row.original)}
        />
      ),
    },
  ], [authByKey, canWrite, options, optionsByKey, patch, removeRow])

  const authEntry = authTarget == null ? null : authByKey.get(authTarget.effectKey) ?? null
  const authName = authTarget == null
    ? ''
    : authTarget.label?.trim()
      || optionsByKey.get(authTarget.effectKey)?.label
      || authTarget.effectKey

  return (
    <>
      <ConfigWorkbench
        title="自定义按钮"
        configuredUnit="按钮"
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
              disabled={moduleId == null || !canWrite}
              onClick={addRow}
            >
              新增按钮
            </Button>
            <Button
              size="sm"
              icon={<IconDeviceFloppy size={16} />}
              disabled={moduleId == null || !canWrite || !dirty || save.isPending}
              loading={save.isPending}
              onClick={() => save.mutate(rows)}
            >
              保存并生效
            </Button>
          </>
        )}
        right={selected == null ? (
          <EmptyState
            title="从左边挑一个模块"
            description="按钮是「用户点一下执行」的动作，配在具体模块上；左栏默认只列已经配过的模块。"
          />
        ) : (
          <div className="d-flex flex-column gap-2">
            <WorkbenchPanelHeader module={selected} canWrite={canWrite} />
            {saveError ? <div className="alert alert-danger py-2 mb-0" role="alert">{saveError}</div> : null}
            <ConfigPublishAlerts outcome={outcome} />
            <ErpTable
              columns={columns}
              data={rows}
              getRowId={(row) => row._key}
              empty={(
                <EmptyState
                  title="该模块还没有自定义按钮"
                  description="点右上角「新增按钮」加一个——没配按钮时，单据面上不出现任何自定义按钮。"
                />
              )}
              resizable
              storageKey="module-buttons"
            />
          </div>
        )}
      />

      {authTarget ? (
        <Modal
          title={`按钮授权：${authName}`}
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
              处理器 <span className="font-monospace">{authTarget.effectKey}</span> · 当前可用
              <span className="fw-semibold ms-1">
                {authEntry?.users ?? 0} 个用户 / {authEntry?.groups ?? 0} 个组
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
    </>
  )
}
