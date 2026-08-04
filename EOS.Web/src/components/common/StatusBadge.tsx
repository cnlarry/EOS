const statusConfig = {
  draft: { label: '草稿', color: 'secondary' },
  pending: { label: '待审核', color: 'yellow' },
  approved: { label: '已审核', color: 'green' },
  rejected: { label: '已驳回', color: 'red' },
  cancelled: { label: '已作废', color: 'secondary' },
  closed: { label: '已关闭', color: 'azure' },
} as const

export type SupportedStatus = keyof typeof statusConfig

export function StatusBadge({ status }: { status: SupportedStatus }) {
  const config = statusConfig[status]
  return <span className={`badge bg-${config.color}-lt`}>{config.label}</span>
}
