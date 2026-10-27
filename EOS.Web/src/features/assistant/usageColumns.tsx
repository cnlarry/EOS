import type { ColumnDef } from '../../lib/tanstackTable'
import type { AssistantUsageModelRow, AssistantUsageTrendRow } from './types'

/**
 * 用量两张表的列定义，**管理面（3102「模型与用量」）与个人面（「我的用量」）共用**。
 *
 * <p>
 * 放在一处而不是各写一遍：列名、对齐、千分位与金额精度是同一套呈现口径，
 * 抄两份之后改一处就会长出差量——同一个用量在两个页面上数字长得不一样，
 * 读的人第一反应是"哪个算错了"，而不是"格式不同"。
 * </p>
 */

/**
 * 趋势里的"天"是一个**日期标签**，不是时刻。
 *
 * <p>
 * 服务端按 UTC 自然日切分（`CAST(CREATED_TIME AS date)`），序列化出来是 `2026-10-01T00:00:00`。
 * 这里取前 10 位，而**不是** `new Date(…)` 再格式化——后者会按本地时区重新解释这个值，
 * 西半球时区把日期挪到前一天（`T00:00:00` 在西五区是前一天 19:00），于是"10 月 1 日的用量"
 * 出现在 9 月 30 日那一行。日期标签没有时区可挪，也就不该被时区挪。
 * </p>
 */
const dayLabel = (value: string) => value.slice(0, 10)

/** 按模型聚合。 */
export const usageModelColumns: ColumnDef<AssistantUsageModelRow, unknown>[] = [
  { accessorKey: 'modelName', header: '模型', cell: (info) => <span className="font-monospace">{String(info.getValue())}</span> },
  {
    accessorKey: 'requests', header: '回复数',
    meta: { className: 'text-end text-nowrap', minWidth: 84 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span>,
  },
  {
    accessorKey: 'promptTokens', header: '输入 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'completionTokens', header: '输出 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'estimatedCostYuan', header: '估算成本',
    meta: { className: 'text-end text-nowrap', minWidth: 100 },
    cell: (info) => <span className="text-secondary">¥{Number(info.getValue() ?? 0).toFixed(4)}</span>,
  },
]

/** 按 UTC 自然日聚合。 */
export const usageTrendColumns: ColumnDef<AssistantUsageTrendRow, unknown>[] = [
  {
    accessorKey: 'day', header: '日期',
    meta: { className: 'text-nowrap', minWidth: 110 },
    // 显示 yyyy-MM-dd，排序仍用原始值（ISO 串按字典序排 = 按时间排，不会因为显示改了而排错）
    cell: (info) => <span className="text-secondary">{dayLabel(String(info.getValue() ?? ''))}</span>,
  },
  {
    accessorKey: 'requests', header: '回复数',
    meta: { className: 'text-end text-nowrap', minWidth: 84 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0)}</span>,
  },
  {
    accessorKey: 'promptTokens', header: '输入 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'completionTokens', header: '输出 tokens',
    meta: { className: 'text-end text-nowrap', minWidth: 110 },
    cell: (info) => <span className="text-secondary">{Number(info.getValue() ?? 0).toLocaleString('zh-CN')}</span>,
  },
  {
    accessorKey: 'estimatedCostYuan', header: '估算成本',
    meta: { className: 'text-end text-nowrap', minWidth: 100 },
    cell: (info) => <span className="text-secondary">¥{Number(info.getValue() ?? 0).toFixed(4)}</span>,
  },
]
