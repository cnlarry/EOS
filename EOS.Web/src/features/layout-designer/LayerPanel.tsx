import type { LayoutDocument } from './types'

interface LayerPanelProps {
  doc: LayoutDocument
  selectedIds: Set<string>
  canAdjust: boolean
  onSelect: (id: string, additive: boolean) => void
  onToggleVisible: (id: string) => void
  onReorder: (id: string, direction: -1 | 1) => void
}

const TYPE_LABELS: Record<string, string> = {
  text: '文本', field: '字段', image: '图片', line: '分隔线', rect: '矩形', table: '表格',
}

const SECTION_LABELS: Record<string, string> = { header: '页头', content: '正文', footer: '页脚' }

export function LayerPanel({
  doc, selectedIds, canAdjust, onSelect, onToggleVisible, onReorder,
}: LayerPanelProps) {
  const sections = (['header', 'content', 'footer'] as const).map((key) => ({
    key,
    label: SECTION_LABELS[key],
    elements: doc.sections[key].elements,
  }))

  return (
    <div className="d-flex flex-column">
      <div className="px-3 pt-2 pb-1 text-secondary small">图层</div>
      <div className="flex-grow-1 overflow-auto" style={{ maxHeight: 210 }}>
        {sections.map(({ key, label, elements }) => (
          <div key={key}>
            <div className="px-3 py-1 bg-secondary-subtle text-secondary small">{label}</div>
            {elements.length === 0
              ? <div className="px-3 py-1 text-secondary small">（空）</div>
              : elements.map((el, index) => {
                const selected = selectedIds.has(el.id)
                return (
                  <div
                    key={el.id}
                    className={`d-flex align-items-center gap-1 px-3 py-1 small ${selected ? 'bg-primary-subtle' : ''}`}
                    style={{ cursor: 'pointer' }}
                    onClick={(e) => onSelect(el.id, e.shiftKey)}
                  >
                    <span className="flex-grow-1 text-truncate">{el.id}</span>
                    <span className="badge bg-secondary text-nowrap">{TYPE_LABELS[el.type] ?? el.type}</span>
                    <input
                      type="checkbox"
                      className="form-check-input"
                      title="可见"
                      checked={el.visible !== false}
                      disabled={!canAdjust}
                      onClick={(e) => e.stopPropagation()}
                      onChange={() => onToggleVisible(el.id)}
                    />
                    {canAdjust && (
                      <span className="d-inline-flex gap-1">
                        <button
                          type="button"
                          className="btn btn-sm py-0 px-1"
                          title="上移一层"
                          disabled={index === 0}
                          onClick={(e) => { e.stopPropagation(); onReorder(el.id, -1) }}
                        >↑</button>
                        <button
                          type="button"
                          className="btn btn-sm py-0 px-1"
                          title="下移一层"
                          disabled={index === elements.length - 1}
                          onClick={(e) => { e.stopPropagation(); onReorder(el.id, 1) }}
                        >↓</button>
                      </span>
                    )}
                  </div>
                )
              })}
          </div>
        ))}
      </div>
    </div>
  )
}
