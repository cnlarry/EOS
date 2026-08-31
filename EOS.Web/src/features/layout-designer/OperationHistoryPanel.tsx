interface HistoryItem {
  desc: string
}

interface OperationHistoryPanelProps {
  past: HistoryItem[]
  future: HistoryItem[]
  onJump: (index: number) => void
  onUndo: () => void
  onRedo: () => void
}

export function OperationHistoryPanel({ past, future, onJump, onUndo, onRedo }: OperationHistoryPanelProps) {
  return (
    <div className="p-3 d-flex flex-column gap-2">
      <div className="d-flex gap-2">
        <button type="button" className="btn btn-outline-secondary btn-sm" disabled={past.length === 0} onClick={onUndo}>
          ← 撤销
        </button>
        <button type="button" className="btn btn-outline-secondary btn-sm" disabled={future.length === 0} onClick={onRedo}>
          重做 →
        </button>
      </div>
      <div className="text-secondary small">点击历史步骤可跳转恢复（后续步骤转入可重做队列）。</div>
      <div className="d-flex flex-column gap-1">
        {past.map((entry, index) => (
          <button
            key={`past-${index}`}
            type="button"
            className="btn btn-sm border text-start px-2 py-1"
            style={{ background: index === past.length - 1 ? 'var(--tblr-primary-bg-subtle, #e7f0fb)' : '#fff' }}
            onClick={() => onJump(index)}
          >
            <span className="text-secondary me-1" style={{ fontSize: 9 }}>{index + 1}</span>
            {entry.desc}
          </button>
        ))}
        <div className="btn btn-sm border px-2 py-1 fw-semibold" style={{ background: '#f8f9fa' }}>
          当前状态
        </div>
        {future.map((entry, index) => (
          <button
            key={`future-${index}`}
            type="button"
            className="btn btn-sm border text-start px-2 py-1 text-secondary opacity-75"
            onClick={onRedo}
          >
            <span className="me-1" style={{ fontSize: 9 }}>↦</span>
            {entry.desc}
          </button>
        ))}
      </div>
    </div>
  )
}
