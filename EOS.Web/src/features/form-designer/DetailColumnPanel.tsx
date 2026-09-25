import type { DesignRow } from './types'

interface DetailColumnPanelProps {
  table: string
  rows: DesignRow[]
  selectedKey: string | null
  preview: boolean
  onSelect: (key: string) => void
  onMove: (key: string, delta: number) => void
  onHidden: (key: string, hidden: boolean) => void
}

/**
 * 明细列编辑：明细是**网格**不是格版式，故不进画布，单独一块列表。
 * 这里只做顺序与显隐——明细没有页签、分节、复合格、占位，故不提供这些入口
 * （提供了也看不到效果，只会让用户以为坏了）。
 */
export default function DetailColumnPanel({
  table,
  rows,
  selectedKey,
  preview,
  onSelect,
  onMove,
  onHidden,
}: DetailColumnPanelProps) {
  return (
    <section className="erp-designer-detail">
      <div className="erp-designer-detail-title">
        明细列（{table}）<span className="erp-designer-muted">{rows.length} 列</span>
      </div>
      <div className="erp-designer-detail-list">
        {rows.map((row, index) => (
          <div
            key={row.key}
            className={selectedKey === row.key ? 'erp-designer-detail-row is-selected' : 'erp-designer-detail-row'}
            onClick={() => onSelect(row.key)}
          >
            <span className="erp-designer-detail-index">{index + 1}</span>
            <span className="erp-designer-detail-label">{row.label}</span>
            <span className="erp-designer-muted">{row.key}</span>
            <span className="erp-designer-badges">
              {row.required ? <em>必填</em> : null}
              {row.isPrimaryKey ? <em>主键</em> : null}
              {row.locked ? <em title={row.lockReason ?? ''}>🔒</em> : null}
              {!row.userVisible ? <em title="当前用户不可见">不可见</em> : null}
            </span>
            {!preview ? (
              <span className="erp-designer-detail-actions">
                <button type="button" title="上移" disabled={index === 0} onClick={() => onMove(row.key, -1)}>
                  ↑
                </button>
                <button
                  type="button"
                  title="下移"
                  disabled={index === rows.length - 1}
                  onClick={() => onMove(row.key, 1)}
                >
                  ↓
                </button>
                <button
                  type="button"
                  title={row.locked ? (row.lockReason ?? '不允许隐藏') : row.hidden ? '显示该列' : '隐藏该列'}
                  disabled={row.locked}
                  onClick={() => onHidden(row.key, !row.hidden)}
                >
                  {row.hidden ? '○' : '●'}
                </button>
              </span>
            ) : null}
          </div>
        ))}
      </div>
    </section>
  )
}
