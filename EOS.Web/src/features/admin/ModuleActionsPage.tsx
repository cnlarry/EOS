import { IconDeviceFloppy } from '@tabler/icons-react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { TabbedPanel } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import {
  BusinessActionsPanel,
  type BusinessActionsView,
  type BusinessConfigTargetModule,
  type ModuleBusinessConfigDraft,
} from '../menu-admin/BusinessActionsPanel'
import { MANUAL_EVENT } from '../menu-admin/documentActionConfig'
import { makeLabelLookup } from '../menu-admin/businessActionText'
import { ConfigPublishAlerts, type ConfigSaveOutcome } from './ConfigPublishAlerts'
import { ConfigWorkbench, WorkbenchPanelHeader, type WorkbenchModule } from './ConfigWorkbench'

/** 总览行：动作 / 校验规则 + 它配在哪个模块（未选中模块时，右栏按系统维度列全库事实）。 */
type OverviewActionRow = {
  moduleId: number
  moduleDesc: string
  eventCode: string
  seq: number
  effectKey: string
  effectName?: string | null
  enabled: boolean
  failMode: string
  label?: string | null
  remark?: string | null
}

type OverviewRuleRow = {
  moduleId: number
  moduleDesc: string
  seq: number
  stage: string
  validationKey: string
  enabled: boolean
  message?: string | null
}

/** 服务端下发的目标模块（`configured` 是动作 + 规则的合计，界面按行重新分列计数）。 */
type OverviewTarget = {
  moduleId: number
  moduleDesc: string
  masterTable: string
  masterTableDesc: string
  detailTable: string
  detailTableDesc: string
  configured: number
}

type OverviewView = {
  targets: OverviewTarget[]
  canWrite: boolean
  actions: OverviewActionRow[]
  validationRules: OverviewRuleRow[]
}

type OverviewMode = 'modules' | 'actions' | 'rules'

/** 按模块聚合出的一行：动作数 / 规则数 / 停用动作数 / 事件分布。 */
type ModuleSummaryRow = WorkbenchModule & {
  disabledActions: number
  eventSummary: string
}

/**
 * 行为动作（模块 230104，定制承载页 `/admin/module-actions`，ADR-033 批 3）。
 *
 * 交互与另外两个配置面一致（ADR-033 拆页面后的重排）：**左栏常驻模块清单、右栏是选中模块的配置**；
 * 未选中时右栏给出**全库总览**——按模块聚合（动作数 / 规则数 / 停用数 / 事件分布），
 * 需要逐行事实时切到「动作明细 / 规则明细」。没有"选择模块"弹窗，也没有首屏空态。
 *
 * 页内两个页签与 230101 里的那两个一一对应，编辑面**复用** `BusinessActionsPanel`（不另造一套 UI）；
 * 两者的差别只在"保存往哪走"：这里走 `PUT /admin/module-business-config/{id}?scope=actions`——
 * 服务端按作用域只替换效果链动作与校验规则，该模块的自定义按钮一行不动（按钮归 230103）。
 *
 * **保存即发布**（ADR-033 §7.3 方案 B）：服务端保存成功后自动发布一次，故按钮就叫「保存」。
 * 校验不通过时语义是"**已保存，但未生效**"，逐条原因随 `checks` 回来，这里照实展示。
 */

/** 目标模块形状（`GET /admin/module-business-config/{id}/target`，与面板要的字段一致）。 */
type ModuleConfigTarget = BusinessConfigTargetModule

export function ModuleActionsPage() {
  const queryClient = useQueryClient()
  const [moduleId, setModuleId] = useState<number | null>(() => {
    const raw = new URLSearchParams(window.location.search).get('moduleId')
    const parsed = raw == null || raw === '' ? Number.NaN : Number(raw)
    return Number.isFinite(parsed) ? parsed : null
  })
  const [view, setView] = useState<'actions' | 'rules'>('actions')
  const [keyword, setKeyword] = useState('')
  const [overviewMode, setOverviewMode] = useState<OverviewMode>('modules')
  const [draft, setDraft] = useState<ModuleBusinessConfigDraft | null>(null)
  const [reloadSignal, setReloadSignal] = useState(0)
  const [outcome, setOutcome] = useState<ConfigSaveOutcome | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)
  // 切模块 / 刷新的脏确认都走 Modal（原生 confirm 与全站不是一套视觉）
  const [pendingModuleId, setPendingModuleId] = useState<number | null>(null)
  const [confirmReload, setConfirmReload] = useState(false)

  const target = useQuery({
    queryKey: ['module-actions', moduleId, 'target'],
    placeholderData: keepPreviousData,
    enabled: moduleId != null,
    queryFn: () => apiClient.get<ModuleConfigTarget>(`/admin/module-business-config/${moduleId}/target`),
  })

  // 左栏模块清单 + 未选中时的全库总览：同一份事实（谁配了什么），与写路径同源。
  const overview = useQuery({
    queryKey: ['module-actions', 'overview'],
    placeholderData: keepPreviousData,
    queryFn: () => apiClient.get<OverviewView>('/admin/module-business-config/overview'),
  })

  // 总览要把事件目录码渲染成人话；与面板共用同一个查询键，故不会多发请求。
  const catalog = useQuery({
    queryKey: ['module-business-config-meta'],
    queryFn: () => apiClient.get<{ labels?: { events?: Record<string, string> } | null }>('/admin/module-business-config/meta'),
  })

  const eventText = useMemo(
    () => makeLabelLookup(catalog.data?.labels?.events),
    [catalog.data],
  )

  // 服务端只给"动作 + 规则合计"，这里按行重新分列——左栏一个总数看不出配的是动作还是规则。
  const modules = useMemo<WorkbenchModule[]>(() => {
    const rows = overview.data
    if (!rows) return []
    const counts = new Map<number, { actions: number; rules: number }>()
    const bump = (id: number, key: 'actions' | 'rules') => {
      const current = counts.get(id) ?? { actions: 0, rules: 0 }
      current[key] += 1
      counts.set(id, current)
    }
    for (const row of rows.actions) bump(row.moduleId, 'actions')
    for (const row of rows.validationRules) bump(row.moduleId, 'rules')
    return rows.targets.map((item) => ({
      moduleId: item.moduleId,
      moduleDesc: item.moduleDesc,
      masterTable: item.masterTable,
      masterTableDesc: item.masterTableDesc,
      detailTable: item.detailTable,
      detailTableDesc: item.detailTableDesc,
      actionCount: counts.get(item.moduleId)?.actions ?? 0,
      ruleCount: counts.get(item.moduleId)?.rules ?? 0,
    }))
  }, [overview.data])

  const canWrite = overview.data?.canWrite ?? false
  const selected = modules.find((item) => item.moduleId === moduleId) ?? null
  const dirty = draft?.dirty === true

  const actionOverviewColumns = useMemo<ColumnDef<OverviewActionRow>[]>(() => [
    { accessorKey: 'moduleId', header: '模块号', size: 90 },
    { accessorKey: 'moduleDesc', header: '模块名' },
    {
      accessorKey: 'eventCode',
      header: '事件',
      size: 130,
      cell: ({ row }) => eventText(row.original.eventCode),
    },
    {
      accessorKey: 'effectKey',
      header: '效果键',
      cell: ({ row }) => <span className="font-monospace">{row.original.effectKey}</span>,
    },
    { accessorKey: 'seq', header: '顺序', size: 70 },
    {
      accessorKey: 'enabled',
      header: '启用',
      size: 80,
      cell: ({ row }) => (
        <span className={`badge ${row.original.enabled ? 'text-bg-success' : 'text-bg-secondary'}`}>
          {row.original.enabled ? '启用' : '停用'}
        </span>
      ),
    },
    { accessorKey: 'failMode', header: '失败策略', size: 100 },
  ], [eventText])

  const ruleOverviewColumns = useMemo<ColumnDef<OverviewRuleRow>[]>(() => [
    { accessorKey: 'moduleId', header: '模块号', size: 90 },
    { accessorKey: 'moduleDesc', header: '模块名' },
    { accessorKey: 'stage', header: '阶段', size: 130 },
    {
      accessorKey: 'validationKey',
      header: '校验键',
      cell: ({ row }) => <span className="font-monospace">{row.original.validationKey}</span>,
    },
    { accessorKey: 'seq', header: '顺序', size: 70 },
    {
      accessorKey: 'enabled',
      header: '启用',
      size: 80,
      cell: ({ row }) => (
        <span className={`badge ${row.original.enabled ? 'text-bg-success' : 'text-bg-secondary'}`}>
          {row.original.enabled ? '启用' : '停用'}
        </span>
      ),
    },
    { accessorKey: 'message', header: '提示文案', cell: ({ row }) => row.original.message ?? '' },
  ], [])

  const summaryRows = useMemo<ModuleSummaryRow[]>(() => {
    const rows = overview.data
    if (!rows) return []
    const needle = keyword.trim().toLowerCase()
    const byModule = new Map<number, { disabled: number; events: string[] }>()
    for (const row of rows.actions) {
      const entry = byModule.get(row.moduleId) ?? { disabled: 0, events: [] }
      if (!row.enabled) entry.disabled += 1
      const label = eventText(row.eventCode)
      if (!entry.events.includes(label)) entry.events.push(label)
      byModule.set(row.moduleId, entry)
    }
    return modules
      .filter((item) => needle === ''
        || `${item.moduleId} ${item.moduleDesc} ${item.masterTable} ${item.masterTableDesc}`
          .toLowerCase().includes(needle))
      .map((item) => {
        const entry = byModule.get(item.moduleId)
        return {
          ...item,
          disabledActions: entry?.disabled ?? 0,
          eventSummary: entry && entry.events.length > 0 ? entry.events.join('、') : '—',
        }
      })
  }, [overview.data, modules, keyword, eventText])

  const summaryColumns = useMemo<ColumnDef<ModuleSummaryRow>[]>(() => [
    {
      accessorKey: 'moduleId',
      header: '模块号',
      size: 90,
      cell: ({ row }) => <span className="font-monospace">{row.original.moduleId}</span>,
    },
    {
      accessorKey: 'moduleDesc',
      header: '模块名',
      size: 200,
      cell: ({ row }) => <span className="fw-semibold">{row.original.moduleDesc}</span>,
    },
    {
      accessorKey: 'masterTableDesc',
      header: '主表',
      cell: ({ row }) => row.original.masterTableDesc || row.original.masterTable || '—',
    },
    {
      accessorKey: 'actionCount',
      header: '动作',
      size: 80,
      cell: ({ row }) => (row.original.actionCount > 0
        ? <span className="badge text-bg-primary">{row.original.actionCount}</span>
        : <span className="text-secondary">0</span>),
    },
    {
      accessorKey: 'ruleCount',
      header: '规则',
      size: 80,
      cell: ({ row }) => (row.original.ruleCount > 0
        ? <span className="badge text-bg-primary">{row.original.ruleCount}</span>
        : <span className="text-secondary">0</span>),
    },
    {
      accessorKey: 'disabledActions',
      header: '停用',
      size: 90,
      cell: ({ row }) => (row.original.disabledActions > 0
        ? <span className="text-warning-emphasis">{row.original.disabledActions} 条</span>
        : <span className="text-secondary">—</span>),
    },
    {
      accessorKey: 'eventSummary',
      header: '事件',
      meta: { minWidth: 220 },
    },
  ], [])

  const filteredActionRows = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    const rows = overview.data?.actions ?? []
    if (needle === '') return rows
    return rows.filter((row) =>
      `${row.moduleId} ${row.moduleDesc} ${row.eventCode} ${row.effectKey}`.toLowerCase().includes(needle))
  }, [overview.data, keyword])

  const filteredRuleRows = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    const rows = overview.data?.validationRules ?? []
    if (needle === '') return rows
    return rows.filter((row) =>
      `${row.moduleId} ${row.moduleDesc} ${row.stage} ${row.validationKey} ${row.message ?? ''}`
        .toLowerCase().includes(needle))
  }, [overview.data, keyword])

  /** 真正切模块：清掉上一模块的草稿与结果，URL 随行（深链接可分享）。 */
  const applyModule = useCallback((nextId: number) => {
    setModuleId(nextId)
    setDraft(null)
    setOutcome(null)
    setSaveError(null)
    const url = new URL(window.location.href)
    url.searchParams.set('moduleId', String(nextId))
    window.history.replaceState({}, '', url)
  }, [])

  const selectModule = useCallback((nextId: number) => {
    if (nextId === moduleId) return
    if (dirty) {
      setPendingModuleId(nextId)
      return
    }
    applyModule(nextId)
  }, [applyModule, dirty, moduleId])

  const save = useMutation({
    mutationFn: async () => {
      if (moduleId == null || draft == null) return null
      // 作用域 actions（ADR-033 §6）：请求体只带效果链动作 + 全部校验规则——按钮行归 230103，
      // 越域的行服务端会拒（显式优于推导），客户端也就不带。
      return await apiClient.put<ConfigSaveOutcome>(`/admin/module-business-config/${moduleId}?scope=actions`, {
        actions: draft.actions
          .filter((action) => action.eventCode !== MANUAL_EVENT)
          .map((action) => ({
            ...action,
            ops: (action.ops ?? []).map((op) =>
              op.sourceScope === 'CONSTANT' ? { ...op, sourceConstant: op.sourceConstant ?? '' } : op,
            ),
          })),
        validationRules: draft.validationRules,
      })
    },
    onSuccess: async (result) => {
      setSaveError(null)
      setOutcome(result ?? null)
      // 先等新配置回来，再让面板按新的装载点重灌草稿：否则面板会用旧数据重灌一次，
      // 之后同一装载点不再接受更新，界面就停在"保存前"的草稿上。
      await queryClient.invalidateQueries({ queryKey: ['module-business-config', moduleId] })
      setDraft(null)
      setReloadSignal((value) => value + 1)
      await queryClient.invalidateQueries({ queryKey: ['module-actions'] })
    },
    onError: (error) => {
      setOutcome(null)
      setSaveError(describeApiError(error, '保存失败。'))
    },
  })

  /** 刷新 = 丢弃草稿 + 重取（有未保存改动时先确认；不做两个不同语义的刷新按钮）。 */
  const reload = useCallback(() => {
    setDraft(null)
    setOutcome(null)
    setSaveError(null)
    // 两个查询键都要失效：`module-actions` 是页面自己的（左栏 / 总览），
    // 未保存的模块配置在 `module-business-config`（面板持有）。只失效前者会"刷了个寂寞"。
    // 刷新信号等重取完成再发，否则面板会按缓存里的旧配置重灌一次草稿。
    void Promise.all([
      queryClient.invalidateQueries({ queryKey: ['module-business-config', moduleId] }),
      queryClient.invalidateQueries({ queryKey: ['module-actions'] }),
    ]).then(() => setReloadSignal((value) => value + 1))
  }, [queryClient, moduleId])

  /** 命令栏「刷新」：有未保存改动时先确认，确认后才丢弃草稿并重取。 */
  const requestReload = useCallback(() => {
    if (dirty) {
      setConfirmReload(true)
      return
    }
    reload()
  }, [dirty, reload])

  // 有未保存改动时离开页面要拦一道（切模块另走 Modal 确认）。
  useEffect(() => {
    if (!dirty) return
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  const draftCounts = draft == null
    ? undefined
    : {
      actions: draft.actions.filter((action) => action.eventCode !== MANUAL_EVENT).length,
      rules: draft.validationRules.length,
    }

  const saveState = save.isPending ? (
    <span className="badge text-bg-info">保存中…</span>
  ) : dirty ? (
    <span className="badge text-bg-warning">未保存</span>
  ) : outcome?.publish?.published ? (
    <span className="badge text-bg-success">
      已发布 {outcome.publish.definitionVersion ?? `v${outcome.publish.version}`}
    </span>
  ) : outcome?.publish ? (
    <span className="badge text-bg-warning">已保存，未生效</span>
  ) : null

  const saveDisabledReason = !canWrite
    ? '只读：没有本配置面的 SETUP 权'
    : !dirty
      ? '没有未保存的改动'
      : undefined

  const tabBar = (
    <TabbedPanel<'actions' | 'rules'>
      label="行为配置页签"
      activeKey={view}
      onActiveKeyChange={(key) => setView(key)}
      tabs={[
        {
          key: 'actions',
          label: `${dirty ? '● ' : ''}行为动作（${draftCounts?.actions ?? selected?.actionCount ?? 0}）`,
        },
        {
          key: 'rules',
          label: `${dirty ? '● ' : ''}校验规则（${draftCounts?.rules ?? selected?.ruleCount ?? 0}）`,
        },
      ]}
    >
      {target.isPending ? (
        <LoadingState label="正在加载模块…" />
      ) : target.isError ? (
        <ErrorState
          message={describeApiError(target.error, '加载模块失败。')}
          onRetry={() => void target.refetch()}
        />
      ) : target.data ? (
        <BusinessActionsPanel
          module={target.data as ModuleConfigTarget}
          view={view as BusinessActionsView}
          canWrite={canWrite}
          reloadSignal={reloadSignal}
          onDraftChange={setDraft}
          onOutcome={setOutcome}
        />
      ) : null}
    </TabbedPanel>
  )

  const overviewCounts = useMemo(() => ({
    modules: modules.filter((item) => item.actionCount + item.ruleCount > 0).length,
    actions: overview.data?.actions.length ?? 0,
    rules: overview.data?.validationRules.length ?? 0,
  }), [modules, overview.data])

  return (
    <>
    <ConfigWorkbench
      modules={modules}
      loading={overview.isPending}
      error={overview.isError ? overview.error : null}
      onReload={requestReload}
      selectedId={moduleId}
      onSelect={selectModule}
      dirtyId={dirty && moduleId != null ? moduleId : null}
      keyword={keyword}
      onKeywordChange={setKeyword}
      toolbar={(
        <Button
          size="sm"
          icon={<IconDeviceFloppy size={16} />}
          disabled={moduleId == null || !canWrite || !dirty || save.isPending}
          loading={save.isPending}
          title={saveDisabledReason}
          onClick={() => save.mutate()}
        >
          保存
        </Button>
      )}
      right={selected == null ? (
        <div className="erp-config-overview" data-testid="config-overview">
          <div className="erp-config-overview-head">
            <div>
              <div className="erp-config-detail-title">全库总览</div>
              <div className="erp-config-detail-meta">
                {overviewCounts.modules} 个模块有配置 · {overviewCounts.actions} 条动作 · {overviewCounts.rules} 条规则
                <span className="erp-config-meta-sep">·</span>
                点一行进那个模块
              </div>
            </div>
            <div className="btn-group btn-group-sm" role="group" aria-label="总览视图">
              {([
                { key: 'modules', label: '按模块' },
                { key: 'actions', label: '动作明细' },
                { key: 'rules', label: '规则明细' },
              ] as const).map((item) => (
                <button
                  key={item.key}
                  type="button"
                  className={`btn ${overviewMode === item.key ? 'btn-secondary' : 'btn-outline-secondary'}`}
                  aria-pressed={overviewMode === item.key}
                  onClick={() => setOverviewMode(item.key)}
                >
                  {item.label}
                </button>
              ))}
            </div>
          </div>

          {overview.isPending ? (
            <LoadingState label="正在加载全库配置…" />
          ) : overview.isError ? (
            <ErrorState
              message={describeApiError(overview.error, '加载全库总览失败。')}
              onRetry={() => void overview.refetch()}
            />
          ) : overviewMode === 'modules' ? (
            <ErpTable
              loading={overview.isFetching}
              columns={summaryColumns}
              data={summaryRows}
              getRowId={(row) => String(row.moduleId)}
              onRowClick={(row) => selectModule(row.moduleId)}
              empty={(
                <EmptyState
                  title="系统里还没有行为配置"
                  description="从左边挑一个模块，为它配效果链动作或校验规则。"
                />
              )}
              clientSideSorting
              resizable
              storageKey="module-actions-overview-modules"
            />
          ) : overviewMode === 'actions' ? (
            <ErpTable
              loading={overview.isFetching}
              columns={actionOverviewColumns}
              data={filteredActionRows}
              getRowId={(row) => `${row.moduleId}#${row.eventCode}#${row.seq}`}
              onRowClick={(row) => selectModule(row.moduleId)}
              empty={(
                <EmptyState
                  title="系统里还没有行为动作"
                  description="从左边挑一个模块，为它配效果链动作。"
                />
              )}
              clientSideSorting
              resizable
              storageKey="module-actions-overview-actions"
            />
          ) : (
            <ErpTable
              loading={overview.isFetching}
              columns={ruleOverviewColumns}
              data={filteredRuleRows}
              getRowId={(row) => `${row.moduleId}#${row.stage}#${row.seq}`}
              onRowClick={(row) => selectModule(row.moduleId)}
              empty={(
                <EmptyState
                  title="系统里还没有校验规则"
                  description="从左边挑一个模块，为它配校验规则。"
                />
              )}
              clientSideSorting
              resizable
              storageKey="module-actions-overview-rules"
            />
          )}
        </div>
      ) : (
        <div className="erp-config-pane">
          <WorkbenchPanelHeader
            module={selected}
            canWrite={canWrite}
            counts={draftCounts}
            extra={saveState}
          />
          {saveError ? <div className="alert alert-danger py-2 mb-0" role="alert">{saveError}</div> : null}
          <ConfigPublishAlerts outcome={outcome} />
          {tabBar}
        </div>
      )}
    />
    {pendingModuleId != null ? (
      <SwitchModuleConfirm
        moduleName={modules.find((item) => item.moduleId === pendingModuleId)?.moduleDesc ?? String(pendingModuleId)}
        onCancel={() => setPendingModuleId(null)}
        onConfirm={() => {
          const next = pendingModuleId
          setPendingModuleId(null)
          if (next != null) applyModule(next)
        }}
      />
    ) : null}
    {confirmReload ? (
      <Modal
        title="刷新会丢弃未保存的改动"
        onClose={() => setConfirmReload(false)}
        footer={(
          <>
            <Button size="sm" variant="ghost" onClick={() => setConfirmReload(false)}>取消</Button>
            <Button
              size="sm"
              variant="danger"
              onClick={() => {
                setConfirmReload(false)
                reload()
              }}
            >
              丢弃改动并刷新
            </Button>
          </>
        )}
      >
        <p className="small mb-0">当前模块有未保存的改动，刷新会丢弃它们并从服务端重取配置。</p>
      </Modal>
    ) : null}
    </>
  )
}

/** 切模块的脏确认：离开当前模块会丢掉未保存改动。 */
function SwitchModuleConfirm({
  moduleName,
  onCancel,
  onConfirm,
}: {
  moduleName: string
  onCancel: () => void
  onConfirm: () => void
}) {
  return (
    <Modal
      title="有未保存的改动"
      onClose={onCancel}
      footer={(
        <>
          <Button size="sm" variant="ghost" onClick={onCancel}>留在本模块</Button>
          <Button size="sm" variant="danger" onClick={onConfirm}>丢掉改动并切换</Button>
        </>
      )}
    >
      <p className="small mb-0">
        当前模块有未保存的改动。切到「{moduleName}」会丢掉它们。
      </p>
    </Modal>
  )
}
