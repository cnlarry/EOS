import type { DataContract, LayoutElement } from './types'

interface PropertyPanelProps {
  element: LayoutElement
  dataContract: DataContract
  systemFields: string[]
  canDesign: boolean
  simpleMode?: boolean
  onMove?: (dxMm: number, dyMm: number) => void
  onChange: (patch: Partial<LayoutElement>) => void
  onRemove: () => void
}

const FIELD_OPTIONS = (dataContract: DataContract, systemFields: string[]) => [
  ...dataContract.columns.map((c) => ({ value: `MASTER.${c.key}`, label: `MASTER.${c.key}（${c.label}）` })),
  ...dataContract.detailColumns.map((c) => ({ value: `DETAILS.${c.key}`, label: `DETAILS.${c.key}（${c.label}）` })),
  ...systemFields.map((f) => ({ value: f, label: f })),
]

function NumberField({
  label, value, onChange, step = 0.5,
}: {
  label: string
  value: number
  onChange: (value: number) => void
  step?: number
}) {
  return (
    <label className="form-label d-flex align-items-center justify-content-between gap-2">
      <span className="text-nowrap">{label}</span>
      <input
        type="number"
        className="form-control form-control-sm"
        style={{ maxWidth: 110 }}
        value={Number.isFinite(value) ? value : 0}
        step={step}
        min={0}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </label>
  )
}

export function PropertyPanel({
  element, dataContract, systemFields, canDesign, simpleMode = false, onMove, onChange, onRemove,
}: PropertyPanelProps) {
  const options = FIELD_OPTIONS(dataContract, systemFields)
  if (simpleMode) {
    return (
      <div className="p-3 d-flex flex-column gap-3">
        <div>
          <strong className="text-truncate">{element.id}</strong>
          <span className="badge bg-secondary ms-2 text-nowrap">{element.type}</span>
        </div>

        {onMove && (
          <div>
            <div className="text-secondary small mb-1">位置微调（mm）</div>
            <div className="d-flex align-items-center justify-content-center gap-1">
              <button type="button" className="btn btn-outline-secondary btn-sm" title="左移"
                onClick={() => onMove(-1, 0)}>←</button>
              <div className="d-flex flex-column gap-1">
                <button type="button" className="btn btn-outline-secondary btn-sm" title="上移"
                  onClick={() => onMove(0, -1)}>↑</button>
                <button type="button" className="btn btn-outline-secondary btn-sm" title="下移"
                  onClick={() => onMove(0, 1)}>↓</button>
              </div>
              <button type="button" className="btn btn-outline-secondary btn-sm" title="右移"
                onClick={() => onMove(1, 0)}>→</button>
            </div>
          </div>
        )}

        <div className="row g-2">
          <div className="col-6"><NumberField label="X (mm)" value={element.x} onChange={(v) => onChange({ x: v })} /></div>
          <div className="col-6"><NumberField label="Y (mm)" value={element.y} onChange={(v) => onChange({ y: v })} /></div>
          <div className="col-6"><NumberField label="宽 (mm)" value={element.w} onChange={(v) => onChange({ w: v })} /></div>
          <div className="col-6"><NumberField label="高 (mm)" value={element.h} onChange={(v) => onChange({ h: v })} /></div>
        </div>

        {element.type === 'text' && (
          <label className="form-label mb-0">
            内容
            <textarea
              className="form-control form-control-sm mt-1"
              rows={3}
              value={element.content ?? ''}
              onChange={(e) => onChange({ content: e.target.value })}
              placeholder="静态文本，可用 {{MASTER.XXX}} / {{SYS.XXX}} 引用"
            />
          </label>
        )}

        {element.type === 'field' && (
          <label className="form-label mb-0">
            字段绑定
            <select
              className="form-select form-select-sm mt-1"
              value={element.field ?? ''}
              onChange={(e) => onChange({ field: e.target.value })}
            >
              <option value="">未绑定</option>
              {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
        )}

        <label className="form-check">
          <input
            type="checkbox"
            className="form-check-input"
            checked={element.visible !== false}
            onChange={(e) => onChange({ visible: e.target.checked })}
          />
          <span className="form-check-label">可见</span>
        </label>
      </div>
    )
  }
  return (
    <div className="p-3 d-flex flex-column gap-3">
      <div>
        <div className="d-flex align-items-center justify-content-between">
          <strong className="text-truncate">{element.id}</strong>
          <span className="badge bg-secondary text-nowrap">{element.type}</span>
        </div>
        <div className="text-secondary small">{element.type} 元素属性</div>
      </div>

      <div className="d-flex flex-column gap-2">
        <div className="row g-2">
          <div className="col-6"><NumberField label="X (mm)" value={element.x} onChange={(v) => onChange({ x: v })} /></div>
          <div className="col-6"><NumberField label="Y (mm)" value={element.y} onChange={(v) => onChange({ y: v })} /></div>
          <div className="col-6"><NumberField label="宽 (mm)" value={element.w} onChange={(v) => onChange({ w: v })} /></div>
          <div className="col-6"><NumberField label="高 (mm)" value={element.h} onChange={(v) => onChange({ h: v })} /></div>
        </div>
        <label className="form-check">
          <input
            type="checkbox"
            className="form-check-input"
            checked={element.visible !== false}
            onChange={(e) => onChange({ visible: e.target.checked })}
          />
          <span className="form-check-label">可见</span>
        </label>
      </div>

      {element.type === 'text' && (
        <div className="d-flex flex-column gap-2">
          <label className="form-label mb-0">
            内容
            <textarea
              className="form-control form-control-sm mt-1"
              rows={3}
              value={element.content ?? ''}
              onChange={(e) => onChange({ content: e.target.value })}
              placeholder="静态文本，可用 {{MASTER.XXX}} / {{SYS.XXX}} 引用"
            />
          </label>
          <div className="row g-2">
            <div className="col-6">
              <label className="form-label mb-0">
                字号
                <input
                  type="number"
                  className="form-control form-control-sm mt-1"
                  value={element.style?.fontSize ?? 9}
                  step={0.5}
                  min={6}
                  onChange={(e) => onChange({ style: { ...element.style, fontSize: Number(e.target.value) } })}
                />
              </label>
            </div>
            <div className="col-6">
              <label className="form-label mb-0">
                对齐
                <select
                  className="form-select form-select-sm mt-1"
                  value={element.style?.align ?? 'left'}
                  onChange={(e) => onChange({ style: { ...element.style, align: e.target.value } })}
                >
                  <option value="left">左</option>
                  <option value="center">中</option>
                  <option value="right">右</option>
                </select>
              </label>
            </div>
          </div>
          <label className="form-check">
            <input
              type="checkbox"
              className="form-check-input"
              checked={element.style?.bold === true}
              onChange={(e) => onChange({ style: { ...element.style, bold: e.target.checked } })}
            />
            <span className="form-check-label">加粗</span>
          </label>
        </div>
      )}

      {element.type === 'field' && (
        <div className="d-flex flex-column gap-2">
          <label className="form-label mb-0">
            字段绑定
            <select
              className="form-select form-select-sm mt-1"
              value={element.field ?? ''}
              onChange={(e) => onChange({ field: e.target.value })}
            >
              <option value="">未绑定</option>
              {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
          <label className="form-label mb-0">
            字号
            <input
              type="number"
              className="form-control form-control-sm mt-1"
              value={element.style?.fontSize ?? 9}
              step={0.5}
              min={6}
              onChange={(e) => onChange({ style: { ...element.style, fontSize: Number(e.target.value) } })}
            />
          </label>
        </div>
      )}

      {element.type === 'line' && (
        <label className="form-label mb-0">
          线宽 (pt)
          <input
            type="number"
            className="form-control form-control-sm mt-1"
            value={element.style?.lineWidth ?? 0.5}
            step={0.1}
            min={0.1}
            onChange={(e) => onChange({ style: { ...element.style, lineWidth: Number(e.target.value) } })}
          />
        </label>
      )}

      {element.type === 'rect' && (
        <div className="d-flex flex-column gap-2">
          <label className="form-label mb-0">
            边框宽 (pt)
            <input
              type="number"
              className="form-control form-control-sm mt-1"
              value={element.style?.borderWidth ?? 0.5}
              step={0.1}
              min={0}
              onChange={(e) => onChange({ style: { ...element.style, borderWidth: Number(e.target.value) } })}
            />
          </label>
          <label className="form-label mb-0">
            背景色
            <input
              type="color"
              className="form-control form-control-sm mt-1"
              value={element.style?.backgroundColor ?? '#ffffff'}
              onChange={(e) => onChange({ style: { ...element.style, backgroundColor: e.target.value } })}
            />
          </label>
        </div>
      )}

      {element.type === 'table' && (
        <div className="d-flex flex-column gap-2">
          <label className="form-label mb-0">
            数据源
            <select
              className="form-select form-select-sm mt-1"
              value={element.dataSource ?? 'details'}
              disabled={!canDesign}
              onChange={(e) => onChange({ dataSource: e.target.value })}
            >
              <option value="details">明细行（DETAILS.*）</option>
              <option value="master">主档字段（MASTER.*）</option>
            </select>
          </label>
          {canDesign ? (
            <div className="d-flex flex-column gap-2">
              <div className="d-flex align-items-center justify-content-between">
                <span className="text-secondary small">列定义</span>
                <button
                  type="button"
                  className="btn btn-outline-secondary btn-sm"
                  onClick={() => onChange({
                    columns: [...(element.columns ?? []), { field: '', label: '新列', width: 20, align: 'left' }],
                  })}
                >
                  + 添加列
                </button>
              </div>
              {(element.columns ?? []).map((column, index) => (
                <div key={`${column.field}-${index}`} className="border rounded p-2 d-flex flex-column gap-1">
                  <div className="d-flex gap-1">
                    <select
                      className="form-select form-select-sm flex-grow-1"
                      value={column.field}
                      aria-label={`列 ${index + 1} 字段`}
                      onChange={(e) => {
                        const columns = [...(element.columns ?? [])]
                        columns[index] = { ...column, field: e.target.value }
                        onChange({ columns })
                      }}
                    >
                      <option value="">字段…</option>
                      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                    <button
                      type="button"
                      className="btn btn-outline-danger btn-sm"
                      title="删除列"
                      onClick={() => onChange({ columns: (element.columns ?? []).filter((_, i) => i !== index) })}
                    >
                      ×
                    </button>
                  </div>
                  <div className="d-flex gap-1">
                    <input
                      type="text"
                      className="form-control form-control-sm"
                      value={column.label}
                      aria-label={`列 ${index + 1} 标签`}
                      onChange={(e) => {
                        const columns = [...(element.columns ?? [])]
                        columns[index] = { ...column, label: e.target.value }
                        onChange({ columns })
                      }}
                    />
                    <input
                      type="number"
                      className="form-control form-control-sm"
                      style={{ width: 70 }}
                      value={column.width ?? 20}
                      aria-label={`列 ${index + 1} 宽度`}
                      step={0.5}
                      min={1}
                      onChange={(e) => {
                        const columns = [...(element.columns ?? [])]
                        columns[index] = { ...column, width: Number(e.target.value) }
                        onChange({ columns })
                      }}
                    />
                    <select
                      className="form-select form-select-sm"
                      style={{ width: 70 }}
                      value={column.align ?? 'left'}
                      aria-label={`列 ${index + 1} 对齐`}
                      onChange={(e) => {
                        const columns = [...(element.columns ?? [])]
                        columns[index] = { ...column, align: e.target.value }
                        onChange({ columns })
                      }}
                    >
                      <option value="left">左</option>
                      <option value="center">中</option>
                      <option value="right">右</option>
                    </select>
                  </div>
                </div>
              ))}
              {!element.columns?.length && (
                <div className="text-secondary small">尚无列，点击「添加列」开始。</div>
              )}
            </div>
          ) : (
            <div className="text-secondary small">微调模式不可改 table 列结构（ADR-010 决策 5）。</div>
          )}
        </div>
      )}

      {element.type === 'image' && (
        <div className="text-secondary small">
          图片资源引用服务端白名单（SYS.LOGO），设计器不可上传任意图片。
          <div className="mt-1">资源：{element.resourceId ?? '未设置'}</div>
        </div>
      )}

      {canDesign && element.type !== 'table' && (
        <button type="button" className="btn btn-outline-danger btn-sm" onClick={onRemove}>
          删除元素
        </button>
      )}
    </div>
  )
}
