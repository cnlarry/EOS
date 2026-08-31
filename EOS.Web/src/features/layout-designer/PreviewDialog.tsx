import { useState } from 'react'
import { Button } from '../../components/ui/Button'
import type { PreviewSettings } from './types'

interface PreviewDialogProps {
  title: string
  onConfirm: (settings: PreviewSettings) => void
  onClose: () => void
}

const SCENARIOS: Array<{ label: string; rows?: number; variant?: string }> = [
  { label: '样例数据（1 行）' },
  { label: '10 行明细', rows: 10 },
  { label: '50 行明细', rows: 50 },
  { label: '100 行明细', rows: 100 },
  { label: '长文本', variant: 'longText' },
  { label: '空明细', variant: 'empty' },
]

export function PreviewDialog({ title, onConfirm, onClose }: PreviewDialogProps) {
  const [source, setSource] = useState<'sample' | 'real'>('sample')
  const [scenario, setScenario] = useState(0)
  const [key, setKey] = useState('')

  const confirm = () => {
    const scenarioItem = SCENARIOS[scenario]
    onConfirm(source === 'real'
      ? {
        source: 'real',
        key: key.trim(),
      }
      : {
        source: 'sample',
        rows: scenarioItem.rows,
        variant: scenarioItem.variant,
      })
    onClose()
  }

  return (
    <div className="position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center"
      style={{ background: 'rgba(0,0,0,0.4)', zIndex: 1000 }}
      onClick={onClose}
    >
      <div className="card shadow" style={{ width: 460 }} onClick={(e) => e.stopPropagation()}>
        <div className="card-header d-flex align-items-center justify-content-between">
          <strong>预览 {title}</strong>
          <button type="button" className="btn-close" aria-label="关闭" onClick={onClose} />
        </div>
        <div className="card-body d-flex flex-column gap-3">
          <div>
            <div className="text-secondary small mb-1">数据来源</div>
            <div className="d-flex gap-3">
              <label className="form-check">
                <input type="radio" className="form-check-input" name="preview-source"
                  checked={source === 'sample'} onChange={() => setSource('sample')} />
                <span className="form-check-label">样例数据</span>
              </label>
              <label className="form-check">
                <input type="radio" className="form-check-input" name="preview-source"
                  checked={source === 'real'} onChange={() => setSource('real')} />
                <span className="form-check-label">真实单据</span>
              </label>
            </div>
          </div>

          {source === 'sample' ? (
            <label className="form-label mb-0">
              数据场景
              <select className="form-select mt-1" value={scenario}
                onChange={(e) => setScenario(Number(e.target.value))}>
                {SCENARIOS.map((s, index) => <option key={s.label} value={index}>{s.label}</option>)}
              </select>
            </label>
          ) : (
            <label className="form-label mb-0">
              单据主键值（多个主键用英文逗号分隔，如 <code>DD13010001</code> 或 <code>A,DD13010001</code>）
              <input type="text" className="form-control mt-1" value={key}
                onChange={(e) => setKey(e.target.value)} placeholder="输入真实单据的单号" />
            </label>
          )}
        </div>
        <div className="card-footer d-flex justify-content-end gap-2">
          <Button variant="secondary" size="sm" onClick={onClose}>取消</Button>
          <Button size="sm" disabled={source === 'real' && !key.trim()} onClick={confirm}>生成预览</Button>
        </div>
      </div>
    </div>
  )
}
