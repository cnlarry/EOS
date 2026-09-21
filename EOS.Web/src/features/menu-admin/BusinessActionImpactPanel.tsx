import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef } from '@tanstack/react-table'
import { ErpTable } from '../../components/common/ErpTable'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { analyzeImpact, type ImpactAction, type ImpactMatchEdge, type ImpactRule, type ImpactTable } from './businessActionImpact'
import { labelWithCode, type BusinessNameLookup } from './businessActionText'

/**
 * 保存前的影响面与自检面板：
 * - 汇总这批配置会写入的表/列（带中文名）、事件与阶段分布、系统开关引用；
 * - 按服务端保存期口径列出会拦住保存的硬伤（跨表无定位键写入、定位键未登记、开关键未登记、顺序号重复、JSON 非法）；
 * - 定位键覆盖检查复用 `GET /{moduleId}/relations`（与保存期校验同一事实源），逐目标表按需取数。
 *
 * 这是**静态影响面**：不连库试算、不改数据。
 */

interface RelationGroup {
  relationId: number
  relationName?: string | null
  keys: ImpactMatchEdge[]
}

/** 展示只需事件与阶段两类中文标签。 */
interface ImpactLabels {
  events: (code: string | null | undefined) => string
  validationStages: (code: string | null | undefined) => string
}

export function BusinessActionImpact({
  moduleId,
  masterTable,
  detailTable,
  names,
  labels,
  actions,
  rules,
}: {
  moduleId: number
  masterTable: string | null
  detailTable: string | null
  names: BusinessNameLookup
  labels: ImpactLabels
  actions: ImpactAction[]
  rules: ImpactRule[]
}) {
  const [open, setOpen] = useState(false)

  // 目标表 → 已登记关系边组：只对草稿里真正写入的表取数（通常 1~3 张）。
  const targetTables = useMemo(() => {
    const set = new Set<string>()
    for (const action of actions)
      for (const op of action.ops ?? []) {
        const table = (op.targetTable ?? '').trim().toUpperCase()
        if (table !== '') set.add(table)
      }
    return [...set].sort()
  }, [actions])
  const relationsQuery = useQuery({
    queryKey: ['module-business-config-impact-relations', moduleId, targetTables.join(',')],
    queryFn: async () => {
      const pairs = await Promise.all(targetTables.map(async (table) => {
        const groups = await apiClient.get<RelationGroup[]>(
          `/admin/module-business-config/${moduleId}/relations`,
          { query: { targetTable: table } },
        )
        return [table, groups.map((group) => group.keys)] as const
      }))
      return Object.fromEntries(pairs) as Record<string, ImpactMatchEdge[][]>
    },
    enabled: moduleId > 0 && targetTables.length > 0,
  })
  const switchQuery = useQuery({
    queryKey: ['system-parameters-switch-keys'],
    queryFn: () => apiClient.get<{ groups?: { parameters?: { key?: string }[] }[] }>('/settings/system'),
    staleTime: 5 * 60 * 1000,
  })
  const knownSwitchKeys = useMemo(() => {
    const groups = switchQuery.data?.groups
    if (!groups) return null
    return groups.flatMap((group) => (group.parameters ?? []).map((item) => String(item.key ?? ''))).filter(Boolean)
  }, [switchQuery.data])

  const report = useMemo(
    () => analyzeImpact(actions, rules, {
      masterTable,
      detailTable,
      relationsByTable: relationsQuery.data,
      knownSwitchKeys,
    }),
    [actions, rules, masterTable, detailTable, relationsQuery.data, knownSwitchKeys],
  )

  const blocks = report.issues.filter((issue) => issue.severity === 'block')
  const warns = report.issues.filter((issue) => issue.severity === 'warn')
  const columns = useMemo<ColumnDef<ImpactTable, unknown>[]>(() => [
    {
      accessorKey: 'table',
      header: '目标表',
      meta: { minWidth: 220 },
      cell: (info) => <span className="font-monospace">{names.table(String(info.getValue()))}</span>,
    },
    {
      id: 'columns',
      header: '写入字段',
      meta: { minWidth: 320 },
      cell: ({ row }) => (
        <span className="font-monospace" title={row.original.columns.join('、')}>
          {row.original.columns.map((column) => names.field(row.original.table, column)).join('、')}
        </span>
      ),
    },
    {
      accessorKey: 'actionCount',
      header: '涉及动作',
      meta: { minWidth: 90 },
    },
  ], [names])

  return (
    <section>
      <div className="d-flex align-items-center justify-content-between flex-wrap gap-2 mb-1">
        <h6 className="mb-0">影响面与保存前自检</h6>
        <div className="d-flex align-items-center gap-2">
          <span className={`badge ${blocks.length > 0 ? 'text-bg-danger' : 'text-bg-success'}`}>
            {blocks.length > 0 ? `${blocks.length} 项会拦住保存` : '无阻断项'}
          </span>
          {warns.length > 0 ? <span className="badge text-bg-warning">{warns.length} 项提示</span> : null}
          <Button size="sm" variant="ghost" onClick={() => setOpen(!open)}>
            {open ? '收起明细' : '展开明细'}
          </Button>
        </div>
      </div>

      <div className="card border">
        <div className="card-body py-2 px-3">
          <div className="d-flex flex-wrap gap-3 small">
            <span>动作 <strong>{report.actionCount}</strong>（启用 {report.enabledActionCount}）</span>
            <span>公式行 <strong>{report.stepCount}</strong></span>
            <span>校验规则 <strong>{report.ruleCount}</strong></span>
            <span>涉及表 <strong>{report.tables.length}</strong></span>
            <span>涉及列 <strong>{report.tables.reduce((sum, item) => sum + item.columns.length, 0)}</strong></span>
            <span>系统开关 <strong>{report.switchKeys.length}</strong></span>
            {report.eventCounts.length > 0 ? (
              <span>
                事件：
                {report.eventCounts.map((item) => `${labelWithCode(labels.events, item.eventCode)}×${item.count}`).join('、')}
              </span>
            ) : null}
            {report.stageCounts.length > 0 ? (
              <span>
                阶段：
                {report.stageCounts.map((item) => `${labelWithCode(labels.validationStages, item.stage)}×${item.count}`).join('、')}
              </span>
            ) : null}
          </div>

          {report.issues.length > 0 ? (
            <ul className="mb-0 mt-2 small">
              {report.issues.map((issue, index) => (
                <li key={index} className={issue.severity === 'block' ? 'text-danger' : 'text-warning-emphasis'}>
                  {issue.severity === 'block' ? '【会拦住保存】' : '【提示】'}{issue.message}
                </li>
              ))}
            </ul>
          ) : (
            <div className="text-secondary small mt-2">
              未发现阻断项（真正的落库结果仍以保存期服务端校验为准）。
            </div>
          )}

          {open ? (
            <div className="mt-3">
              <div className="small text-secondary mb-1">
                会写入的表与列（字段名带中文元数据；跨表写入都应有定位键，否则保存会被拒绝）
              </div>
              {report.tables.length === 0 ? (
                <div className="text-secondary small">当前没有字段级写入（可能全是参数型效果）。</div>
              ) : (
                <ErpTable
                  columns={columns}
                  data={report.tables}
                  getRowId={(row) => row.table}
                  clientSideSorting
                  copyable={false}
                  empty={<div className="p-3 text-secondary">没有字段级写入。</div>}
                />
              )}
              {report.switchKeys.length > 0 ? (
                <div className="small text-secondary mt-2">
                  系统开关引用：<code>{report.switchKeys.join('、')}</code>
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      </div>
    </section>
  )
}
