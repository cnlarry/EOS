import { IconDeviceFloppy, IconRefresh } from '@tabler/icons-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { UnifiedChooser } from '../../components/common/UnifiedChooser'
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

/**
 * 行为动作 + 校验规则（模块 230104，定制承载页 `/admin/module-actions`，ADR-033 批 3）。
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
  const [moduleId, setModuleId] = useState<number | null>(null)
  const [view, setView] = useState<'actions' | 'rules'>('actions')
  const [draft, setDraft] = useState<ModuleBusinessConfigDraft | null>(null)
  const [reloadSignal, setReloadSignal] = useState(0)
  const [chooserOpen, setChooserOpen] = useState(false)
  const [outcome, setOutcome] = useState<ConfigSaveOutcome | null>(null)
  const [saveError, setSaveError] = useState<string | null>(null)

  const target = useQuery({
    queryKey: ['module-actions', moduleId, 'target'],
    enabled: moduleId != null,
    queryFn: () => apiClient.get<ModuleConfigTarget>(`/admin/module-business-config/${moduleId}/target`),
  })

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

  const refresh = async () => {
    setOutcome(null)
    setSaveError(null)
    await queryClient.invalidateQueries({ queryKey: ['module-business-config', moduleId] })
    setReloadSignal((value) => value + 1)
  }

  return (
    <div className="d-flex flex-column gap-3">
      <ErpListCard
        header={(
          <div className="px-3 pt-2 small">
            <div className="fw-semibold">行为动作 + 校验规则</div>
            <div className="text-secondary">
              两者在同一条执行链上（同一事务、同一个发布门），故同页两个页签一起提交。
              配置只作用于**统一工作台模块**；保存后服务端自动发布一次，校验不通过则是"已保存，但未生效"。
            </div>
          </div>
        )}
        actions={(
          <>
            <Button size="sm" variant="ghost" icon={<IconRefresh size={16} />} onClick={() => setChooserOpen(true)}>
              选择模块
            </Button>
            <Button
              size="sm"
              variant="ghost"
              icon={<IconRefresh size={16} />}
              disabled={moduleId == null}
              onClick={() => void refresh()}
            >
              刷新
            </Button>
            <Button
              size="sm"
              icon={<IconDeviceFloppy size={16} />}
              disabled={moduleId == null || draft == null || save.isPending}
              loading={save.isPending}
              onClick={() => save.mutate()}
            >
              保存
            </Button>
          </>
        )}
      >
        {moduleId == null ? (
          <EmptyState
            title="还没有选择模块"
            description="行为动作与校验规则挂在各模块上：先点右上角「选择模块」，再配置该模块的效果链。"
          />
        ) : target.isPending ? (
          <LoadingState label="正在加载模块…" />
        ) : target.isError ? (
          <ErrorState
            message={describeApiError(target.error, '加载模块失败。')}
            onRetry={() => void target.refetch()}
          />
        ) : (
          <div className="d-flex flex-column gap-2">
            <div className="text-secondary small">
              正在配置模块 <span className="font-monospace">{target.data?.M_IDX}</span>
              {target.data?.M_DESC ? ` ${target.data.M_DESC}` : ''} · 主表 {target.data?.MASTER_TABLE || '—'}
              {target.data?.DETAIL_TABLE ? ` · 副表 ${target.data.DETAIL_TABLE}` : ''}
            </div>
            {saveError ? <div className="alert alert-danger py-2 mb-0" role="alert">{saveError}</div> : null}
            <ConfigPublishAlerts outcome={outcome} />
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
            <BusinessActionsPanel
              module={target.data as ModuleConfigTarget}
              view={view as BusinessActionsView}
              reloadSignal={reloadSignal}
              onDraftChange={setDraft}
            />
          </div>
        )}
      </ErpListCard>

      {chooserOpen ? (
        <UnifiedChooser
          open
          title="选择要配置行为动作的模块"
          source={{ kind: 'sourceKey', key: 'module-actions.targets' }}
          mode="single"
          getRowId={(row) => String(row.M_IDX)}
          onPick={(pickedRows) => {
            const picked = pickedRows[0] as { M_IDX?: unknown } | undefined
            if (picked) {
              setModuleId(Number(picked.M_IDX))
              setDraft(null)
              setOutcome(null)
              setSaveError(null)
            }
            setChooserOpen(false)
          }}
          onClose={() => setChooserOpen(false)}
          dialogSize="md"
          searchPlaceholder="按模块号或模块名搜索"
          emptyText="没有可配行为动作的模块（只有统一工作台模块才有效果链消费方）。"
        />
      ) : null}
    </div>
  )
}
