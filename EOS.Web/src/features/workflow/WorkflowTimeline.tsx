/** 工作流审批状态标签（单据审批历史 / 流程监控日志共用，代码质量批 4 D5 收编）。 */
const WORKFLOW_STATE_LABEL: Record<string, string> = {
  Y: '同意',
  N: '驳回',
  S: '跳过',
  W: '撤回',
  A: '送审',
  '': '待批',
}

export interface WorkflowTimelineRow {
  step: string | null
  stepDesc: string | null
  approver: string | null
  state: string
  /** task=审批动作 / confirm=流程完成确认（单据审批历史端点） */
  kind?: 'task' | 'confirm'
  message: string | null
  date: string | null
}

/**
 * 工作流审批时间线（代码质量批 4 D5 收编）：
 * FormEditorPage 审批历史与 FlowMonitorPage 审批日志原先各持一份近似 li 列表
 * （step badge + 审批人 + 状态徽标 + 意见 + 时间），统一到本组件。
 */
export function WorkflowTimeline({ rows, emptyText = '暂无记录' }: { rows: WorkflowTimelineRow[]; emptyText?: string }) {
  if (rows.length === 0) return <div className="text-center text-secondary py-4">{emptyText}</div>
  return (
    <ul className="list-unstyled mb-0">
      {rows.map((row, index) => {
        const label = row.kind === 'confirm' ? '流程完成' : (WORKFLOW_STATE_LABEL[row.state] ?? row.state) || '—'
        const cls = row.state === 'Y' || row.kind === 'confirm' ? 'text-bg-success'
          : row.state === 'N' || row.state === 'W' ? 'text-bg-danger'
          : row.state === 'A' ? 'text-bg-info'
          : row.state === 'S' ? 'text-bg-secondary'
          : 'text-bg-warning'
        return (
          <li key={index} className="d-flex gap-2 align-items-start py-1 border-bottom">
            <span className="badge text-bg-light border mt-1" style={{ minWidth: 44 }}>{row.step || '—'}</span>
            <div className="flex-grow-1">
              <div className="small">
                <span className="fw-semibold">{row.stepDesc || label}</span>
                {row.approver && <span className="font-monospace text-secondary ms-2">{row.approver}</span>}
                <span className={`ms-2 badge ${cls}`}>{label}</span>
              </div>
              {row.message && <div className="small text-secondary">{row.message}</div>}
            </div>
            {row.date && <span className="small text-secondary text-nowrap">{row.date}</span>}
          </li>
        )
      })}
    </ul>
  )
}
