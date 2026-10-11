import { IconDeviceFloppy } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { describeApiError } from '../../lib/errors'
import {
  BusinessActionsPanel,
  type BusinessActionsView,
  type BusinessConfigTargetModule,
  type ModuleBusinessConfigDraft,
} from '../menu-admin/BusinessActionsPanel'
import { MANUAL_EVENT } from '../menu-admin/documentActionConfig'
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

type OverviewView = {
  targets: WorkbenchModule[]
  canWrite: boolean
  actions: OverviewActionRow[]
  validationRules: OverviewRuleRow[]
}

/**
 * 行为动作、校验规则（模块 230104，定制承载页 `/admin/module-actions`，ADR-033 批 3）。
 *
 * 交互与另外两个配置面一致（ADR-033 拆页面后的重排）：**左栏常驻模块清单、右栏是选中模块的配置**；
 * 未选中时右栏给出**全库总览**（哪些模块配了什么），点行即进——没有"选择模块"弹窗，也没有首屏空态。
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

const TABS: { key: 'actions' | 'rules'; label: string }[] = [
  { key: 'actions', label: '行为动作' },
  { key: 'rules', label: '校验规则' },
]

export function ModuleActionsPage() {
  const queryClient = useQueryClient()
  const [moduleId, setModuleId] = useState<number | null>(() => {
    const raw = new URLSearchParams(window.location.search).get('moduleId')
    const parsed = raw == null || raw === '' ? Number.NaN : Number(raw)
    return Number.isFinite(parsed) ? parsed : null
  })
  const [view, setView] = useState<'actions' | 'rules'>('actions')
  const [draft, setDraft] = useState<ModuleBusinessConfigDraft | null>(null)
  const [reloadSignal, setReloadSignal] = useState(0)
  const [outcome, setOutcome] = useState<ConfigSaveOutcome | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)

  const target = useQuery({
    queryKey: ['module-actions', moduleId, 'target'],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<ModuleConfigTarget>(`/admin/module-business-config/${moduleId}/target`),
  })

  // 左栏模块清单 + 未选中时的全库总览：同一份事实（谁配了什么），与写路径同源。
  const overview = useQuery({
    queryKey: ['module-actions', 'overview'],
    queryFn: () => apiClient.get<OverviewView>('/admin/module-business-config/overview'),
  })

  const targets = overview.data?.targets ?? []
  const canWrite = overview.data?.canWrite ?? false
  const selected = targets.find((item) => item.moduleId === moduleId) ?? null
  const dirty = draft != null

  const actionOverviewColumns = useMemo<ColumnDef<OverviewActionRow>[]>(() => [
    { accessorKey: 'moduleId', header: '模块号', size: 90 },
    { accessorKey: 'moduleDesc', header: '模块名' },
    { accessorKey: 'eventCode', header: '事件', size: 130 },
    {
      accessorKey: 'effectKey',
      header: '效果键',
      cell: ({ row }) => <span className="font-monospace">{row.original.effectKey}</span>,
    },
    { accessorKey: 'seq', header: '顺序', size: 70 },
    { accessorKey: 'enabled', header: '启用', size: 70, cell: ({ row }) => (row.original.enabled ? '是' : '否') },
    { accessorKey: 'failMode', header: '失败策略', size: 90 },
  ], [])

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
    { accessorKey: 'enabled', header: '启用', size: 70, cell: ({ row }) => (row.original.enabled ? '是' : '否') },
    { accessorKey: 'message', header: '提示文案', cell: ({ row }) => row.original.message ?? '' },
  ], [])

  const selectModule = useCallback((nextId: number) => {
    if (nextId === moduleId) return
    if (dirty && !window.confirm('当前模块有未保存的改动，切换会丢掉它们。仍要切换？')) return
    setModuleId(nextId)
    setDraft(null)
    setOutcome(null)
    setSaveError(null)
    const url = new URL(window.location.href)
    url.searchParams.set('moduleId', String(nextId))
    window.history.replaceState({}, '', url)
  }, [dirty, moduleId])

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
      await queryClient.invalidateQueries({ queryKey: ['module-business-config', moduleId] })
      setReloadSignal((value) => value + 1)
    },
    onError: (error) => {
      setOutcome(null)
      setSaveError(describeApiError(error, '保存失败。'))
    },
  })

  const reload = useCallback(() => {
    setDraft(null)
    setOutcome(null)
    setSaveError(null)
    void queryClient.invalidateQueries({ queryKey: ['module-actions'] })
  }, [queryClient])

  // 页签在"总览"与"编辑"两种模式下共用：它决定看哪一份清单（动作 / 规则），与在哪个模式下看无关。
  const tabBar = (
    <ul className="nav nav-tabs" role="tablist">
      {TABS.map((tab) => (
        <li className="nav-item" key={tab.key}>
          <button
            type="button"
            role="tab"
            aria-selected={view === tab.key}
            className={`nav-link ${view === tab.key ? 'active' : ''}`}
            onClick={() => setView(tab.key)}
          >
            {tab.label}
          </button>
        </li>
      ))}
    </ul>
  )

  return (
    <ConfigWorkbench
      title="行为动作、校验规则"
      configuredUnit="动作"
      modules={targets}
      loading={overview.isPending}
      error={overview.isError ? overview.error : null}
      onReload={reload}
      selectedId={moduleId}
      onSelect={selectModule}
      dirtyId={dirty && moduleId != null ? moduleId : null}
      toolbar={(
        <Button
          size="sm"
          icon={<IconDeviceFloppy size={16} />}
          disabled={moduleId == null || draft == null || save.isPending}
          loading={save.isPending}
          onClick={() => save.mutate()}
        >
          保存
        </Button>
      )}
      right={selected == null ? (
        <div className="d-flex flex-column gap-2">
          <div className="px-1 small">
            <div className="fw-semibold">全库总览</div>
            <div className="text-secondary">
              两者在同一条执行链上（同一事务、同一个发布门），故同页两个页签一起提交。点一行进那个模块。
            </div>
          </div>
          {tabBar}
          {view === 'actions' ? (
            <ErpTable
              columns={actionOverviewColumns}
              data={overview.data?.actions ?? []}
              getRowId={(row) => `${row.moduleId}#${row.eventCode}#${row.seq}`}
              onRowClick={(row) => selectModule(row.moduleId)}
              empty={(
                <EmptyState
                  title="系统里还没有行为动作"
                  description="从左边挑一个统一工作台模块，为它配效果链动作。"
                />
              )}
              resizable
              storageKey="module-actions-overview"
            />
          ) : (
            <ErpTable
              columns={ruleOverviewColumns}
              data={overview.data?.validationRules ?? []}
              getRowId={(row) => `${row.moduleId}#${row.stage}#${row.seq}`}
              onRowClick={(row) => selectModule(row.moduleId)}
              empty={(
                <EmptyState
                  title="系统里还没有校验规则"
                  description="从左边挑一个统一工作台模块，为它配校验规则。"
                />
              )}
              resizable
              storageKey="module-rules-overview"
            />
          )}
        </div>
      ) : (
        <div className="d-flex flex-column gap-2">
          <WorkbenchPanelHeader
            module={selected}
            canWrite={canWrite}
            extra={<span className="text-secondary">保存后服务端自动发布一次；校验不通过则是「已保存，但未生效」。</span>}
          />
          {saveError ? <div className="alert alert-danger py-2 mb-0" role="alert">{saveError}</div> : null}
          <ConfigPublishAlerts outcome={outcome} />
          {target.isPending ? (
            <LoadingState label="正在加载模块…" />
          ) : target.isError ? (
            <ErrorState
              message={describeApiError(target.error, '加载模块失败。')}
              onRetry={() => void target.refetch()}
            />
          ) : (
            <>
              {tabBar}
              <BusinessActionsPanel
                module={target.data as ModuleConfigTarget}
                view={view as BusinessActionsView}
                reloadSignal={reloadSignal}
                onDraftChange={setDraft}
              />
            </>
          )}
        </div>
      )}
    />
  )
}
