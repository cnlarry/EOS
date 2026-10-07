import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '../../lib/tanstackTable'
import { useMemo } from 'react'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpListCard } from '../../components/common/ErpListCard'
import { ErpTable } from '../../components/common/ErpTable'
import { describeApiError } from '../../lib/errors'
import { getMechanism } from './api'
import type { AssistantActionInfo, AssistantToolInfo } from './api'

/** 风险分级的中文与配色（服务端下的是枚举名；未登记的取值原样显示，不猜）。 */
function riskBadge(risk: string) {
  const map: Record<string, { label: string; className: string }> = {
    Read: { label: '只读', className: 'bg-green-lt text-success' },
    Write: { label: '写入', className: 'bg-yellow-lt text-yellow' },
    AdminWrite: { label: '管理写入', className: 'bg-danger-lt text-danger' },
  }
  const item = map[risk] ?? { label: risk, className: 'bg-secondary-lt' }
  return <span className={`badge ${item.className}`}>{item.label}</span>
}

/**
 * 工作助手管理 → **机制与工具总览**（菜单组 31 / 模块 3104，路由 `/admin/assistant/mechanism`）。
 *
 * <para>
 * **纯只读**：把助手当前挂着的工具、可代理的动作与能力面边界摊开。它的价值在"治理可见"——
 * `docs/guide/60`/`61` 里写的"哪些能做、哪些结构上做不到"，这里能直接看到实现侧的真话，
 * 而且是**现算**的（工具来自 DI 注册表、动作来自静态目录），不会因为缓存而与代码脱节。
 * </para>
 *
 * <para>
 * 它**不提供任何写动作**，权限门只要 3104 的 `CanBrowse`。
 * </para>
 */
export function MechanismOverviewPage() {
  const mechanism = useQuery({
    queryKey: ['assistant-mechanism'],
    queryFn: getMechanism,
    // 这份数据是"代码现在长什么样"，不是业务数据：短时间内重复进页面不必重取
    staleTime: 60_000,
  })

  const tools = mechanism.data?.tools ?? []
  const actions = mechanism.data?.actions ?? []
  const boundaries = mechanism.data?.boundaries ?? []
  const errorMessage = describeApiError(mechanism.error, '加载机制总览失败，请稍后重试。')

  const toolColumns = useMemo<ColumnDef<AssistantToolInfo, unknown>[]>(() => [
    {
      accessorKey: 'name',
      header: '工具名',
      meta: { className: 'text-nowrap', minWidth: 180 },
      cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span>,
    },
    {
      accessorKey: 'risk',
      header: '风险',
      meta: { className: 'text-nowrap', minWidth: 90 },
      cell: (info) => riskBadge(String(info.getValue())),
    },
    { accessorKey: 'description', header: '说明' },
  ], [])

  const actionColumns = useMemo<ColumnDef<AssistantActionInfo, unknown>[]>(() => [
    {
      accessorKey: 'name',
      header: '动作',
      meta: { className: 'text-nowrap', minWidth: 100 },
      cell: (info) => <span className="font-monospace fw-semibold">{String(info.getValue())}</span>,
    },
    { accessorKey: 'target', header: '对什么' },
    { accessorKey: 'auditAction', header: '审计动作' },
    {
      accessorKey: 'implementation',
      header: '实现',
      meta: { className: 'text-nowrap' },
      cell: (info) => <span className="text-secondary font-monospace">{String(info.getValue())}</span>,
    },
  ], [])

  return (
    <div className="erp-full-list-page">
      <ErpListCard
        ariaLabel="助手机制与工具总览"
        header={<div className="card-header py-2 d-flex align-items-center gap-2 flex-wrap">
          <h2 className="card-title mb-0">机制与工具总览</h2>
          <span className="text-secondary small">
            共 {tools.length} 个工具、{actions.length} 个可代理动作；数据现算，不缓存
          </span>
        </div>}
      >
        {mechanism.isPending ? <LoadingState label="正在读取机制清单…" />
          : mechanism.isError ? <ErrorState message={errorMessage} onRetry={() => void mechanism.refetch()} />
            : (
              <div className="p-2 d-flex flex-column gap-3">
                <section>
                  <h3 className="h4 mb-2">能力面边界</h3>
                  <p className="text-secondary small mb-2">
                    这些是"结构上做不到"的事——写在这里是为了让"助手不能做什么"与"能做什么"同样可见。
                  </p>
                  <div className="d-flex flex-column gap-2">
                    {boundaries.map(item => (
                      <div key={item.title} className="alert alert-secondary mb-0 py-2">
                        <div className="fw-semibold">{item.title}</div>
                        <div className="small text-secondary">{item.detail}</div>
                      </div>
                    ))}
                  </div>
                </section>

                <section>
                  <h3 className="h4 mb-2">可代理动作（{actions.length}）</h3>
                  {actions.length === 0 ? <EmptyState title="没有登记任何动作" description="动作目录为空，助手只能读。" />
                    : (
                      <ErpTable
                        columns={actionColumns}
                        data={actions}
                        getRowId={(row) => row.name}
                        resizable
                        storageKey="assistant-mechanism-actions"
                      />
                    )}
                </section>

                <section>
                  <h3 className="h4 mb-2">工具清单（{tools.length}）</h3>
                  {tools.length === 0 ? <EmptyState title="没有登记任何工具" description="工具注册表为空。" />
                    : (
                      <ErpTable
                        columns={toolColumns}
                        data={tools}
                        getRowId={(row) => row.name}
                        resizable
                        storageKey="assistant-mechanism-tools"
                      />
                    )}
                </section>
              </div>
            )}
      </ErpListCard>
    </div>
  )
}
