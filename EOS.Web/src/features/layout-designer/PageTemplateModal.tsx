import { useState } from 'react'
import { Button } from '../../components/ui/Button'
import type { LayoutDocument, LayoutElement, LayoutSection } from './types'

type TemplateKey = 'first' | 'continuation' | 'last'
type PartKey = 'header' | 'footer'

interface PageTemplateModalProps {
  doc: LayoutDocument
  onApply: (templates: NonNullable<LayoutDocument['pageTemplates']>) => void
  onClose: () => void
}

const TAB_LABELS: Record<TemplateKey, string> = { first: '第一页', continuation: '续页', last: '末页' }
const PART_LABELS: Record<PartKey, string> = { header: '页头', footer: '页脚' }
const TYPE_LABELS: Record<string, string> = { text: '文本', field: '字段', line: '分隔线' }

function newId(prefix: string): string {
  return `${prefix}${Date.now().toString(36).slice(-4)}${Math.floor(Math.random() * 36).toString(36)}`
}

function defaultElement(type: LayoutElement['type']): LayoutElement {
  switch (type) {
    case 'field': return { id: newId('f'), type, x: 0, y: 2, w: 80, h: 6, field: '', style: { fontSize: 9 } }
    case 'line': return { id: newId('l'), type, x: 0, y: 22, w: 187.4, h: 0.5, style: { lineWidth: 0.5 } }
    default: return { id: newId('t'), type, x: 0, y: 2, w: 187.4, h: 6, content: '页头文本', style: { fontSize: 12 } }
  }
}

export function PageTemplateModal({ doc, onApply, onClose }: PageTemplateModalProps) {
  const [tab, setTab] = useState<TemplateKey>('first')
  const [part, setPart] = useState<PartKey>('header')
  const [templates, setTemplates] = useState<NonNullable<LayoutDocument['pageTemplates']>>(
    doc.pageTemplates ?? {},
  )

  const section = (): LayoutSection => {
    const template = templates[tab] ?? {}
    return template[part] ?? { height: 26, elements: [] }
  }

  const setSection = (elements: LayoutElement[]) => {
    setTemplates((prev) => ({
      ...prev,
      [tab]: { ...prev[tab], [part]: { height: part === 'header' ? 26 : 14, elements } },
    }))
  }

  const updateElement = (id: string, patch: Partial<LayoutElement>) => {
    setSection(section().elements.map((el) => (el.id === id ? { ...el, ...patch } : el)))
  }

  const elements = section().elements

  return (
    <div className="position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center"
      style={{ background: 'rgba(0,0,0,0.4)', zIndex: 1000 }}
      onClick={onClose}
    >
      <div className="card shadow" style={{ width: 720, maxHeight: '80vh' }} onClick={(e) => e.stopPropagation()}>
        <div className="card-header d-flex align-items-center justify-content-between">
          <strong>页模板（多页版式：第一页 / 续页 / 末页）</strong>
          <button type="button" className="btn-close" aria-label="关闭" onClick={onClose} />
        </div>
        <div className="card-body overflow-auto">
          <div className="d-flex gap-2 mb-2">
            {(['first', 'continuation', 'last'] as const).map((key) => (
              <button
                key={key}
                type="button"
                className={`btn btn-sm ${tab === key ? 'btn-primary' : 'btn-outline-secondary'}`}
                onClick={() => setTab(key)}
              >
                {TAB_LABELS[key]}
              </button>
            ))}
          </div>
          <div className="d-flex gap-2 mb-2">
            {(['header', 'footer'] as const).map((key) => (
              <button
                key={key}
                type="button"
                className={`btn btn-sm ${part === key ? 'btn-primary' : 'btn-outline-secondary'}`}
                onClick={() => setPart(key)}
              >
                {PART_LABELS[key]}
              </button>
            ))}
            <span className="ms-auto d-flex gap-1">
              {Object.keys(TYPE_LABELS).map((type) => (
                <Button key={type} variant="secondary" size="sm"
                  onClick={() => setSection([...elements, defaultElement(type as LayoutElement['type'])])}>
                  + {TYPE_LABELS[type]}
                </Button>
              ))}
            </span>
          </div>
          <div className="text-secondary small mb-2">
            {TAB_LABELS[tab]} · {PART_LABELS[part]}：该页使用此{part === 'header' ? '页头' : '页脚'}；未配置时回退默认版式。
          </div>
          {elements.length === 0 ? (
            <div className="text-secondary small">（空，回退默认{part === 'header' ? '页头' : '页脚'}）</div>
          ) : (
            <div className="d-flex flex-column gap-2">
              {elements.map((el) => (
                <div key={el.id} className="border rounded p-2">
                  <div className="d-flex align-items-center gap-2 mb-1">
                    <span className="badge bg-secondary">{TYPE_LABELS[el.type] ?? el.type}</span>
                    <span className="text-secondary small font-monospace">{el.id}</span>
                    <button
                      type="button"
                      className="btn btn-outline-danger btn-sm ms-auto"
                      onClick={() => setSection(elements.filter((e) => e.id !== el.id))}
                    >
                      删除
                    </button>
                  </div>
                  <div className="row g-1">
                    <div className="col-2">
                      <label className="form-label small mb-0">X
                        <input type="number" className="form-control form-control-sm" value={el.x} step={0.5}
                          onChange={(e) => updateElement(el.id, { x: Number(e.target.value) })} />
                      </label>
                    </div>
                    <div className="col-2">
                      <label className="form-label small mb-0">Y
                        <input type="number" className="form-control form-control-sm" value={el.y} step={0.5}
                          onChange={(e) => updateElement(el.id, { y: Number(e.target.value) })} />
                      </label>
                    </div>
                    <div className="col-2">
                      <label className="form-label small mb-0">宽
                        <input type="number" className="form-control form-control-sm" value={el.w} step={0.5}
                          onChange={(e) => updateElement(el.id, { w: Number(e.target.value) })} />
                      </label>
                    </div>
                    <div className="col-2">
                      <label className="form-label small mb-0">高
                        <input type="number" className="form-control form-control-sm" value={el.h} step={0.5}
                          onChange={(e) => updateElement(el.id, { h: Number(e.target.value) })} />
                      </label>
                    </div>
                    <div className="col-2">
                      <label className="form-label small mb-0">字号
                        <input type="number" className="form-control form-control-sm"
                          value={el.style?.fontSize ?? 9} step={0.5}
                          onChange={(e) => updateElement(el.id, { style: { ...el.style, fontSize: Number(e.target.value) } })} />
                      </label>
                    </div>
                  </div>
                  {el.type === 'text' && (
                    <input type="text" className="form-control form-control-sm mt-1" value={el.content ?? ''}
                      placeholder="文本内容"
                      onChange={(e) => updateElement(el.id, { content: e.target.value })} />
                  )}
                  {el.type === 'field' && (
                    <input type="text" className="form-control form-control-sm mt-1" value={el.field ?? ''}
                      placeholder="字段引用（MASTER.XXX / SYS.XXX）"
                      onChange={(e) => updateElement(el.id, { field: e.target.value })} />
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="card-footer d-flex justify-content-end gap-2">
          <Button variant="secondary" size="sm" onClick={onClose}>取消</Button>
          <Button size="sm" onClick={() => { onApply(templates); onClose() }}>保存页模板</Button>
        </div>
      </div>
    </div>
  )
}
