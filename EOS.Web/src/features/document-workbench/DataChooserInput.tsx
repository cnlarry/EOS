import { useState } from 'react'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import type { FormFieldDefinition } from './formDefinition'

export interface ChooserRow { [key: string]: unknown }
export interface ChooserColumn { key: string; label: string }
export interface ChooserData { columns: ChooserColumn[]; rows: ChooserRow[] }

interface DataChooserInputProps {
  moduleId: string
  field: FormFieldDefinition
  onPick: (row: ChooserRow) => void
  onClose: () => void
}

export function DataChooserInput({ moduleId, field, onPick, onClose }: DataChooserInputProps) {
  const [keyword, setKeyword] = useState('')
  const [data, setData] = useState<ChooserData | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const search = async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await apiClient.get<ChooserData>(`/document-workbench/${moduleId}/form-chooser/${encodeURIComponent(field.key)}`, { query: { keyword: keyword.trim() || undefined } })
      setData(result)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : '选择器加载失败。')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered modal-lg">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">选择 {field.label}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            <div className="input-group mb-2">
              <input className="form-control" value={keyword} placeholder="关键字过滤" onChange={event => setKeyword(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void search() }} />
              <Button variant="primary" loading={loading} onClick={() => void search()}>查询</Button>
            </div>
            {error ? <div className="alert alert-danger">{error}</div> : null}
            {data && data.rows.length === 0 ? <div className="text-secondary py-4 text-center">没有可选数据。</div> : null}
            {data && data.rows.length > 0 ? (
              <div className="table-responsive" style={{ maxHeight: 360, overflow: 'auto' }}>
                <table className="table table-sm table-hover mb-0">
                  <thead><tr>{data.columns.map(column => <th key={column.key}>{column.label}</th>)}<th /></tr></thead>
                  <tbody>
                    {data.rows.map((row, index) => (
                      <tr key={index} className="erp-clickable" onClick={() => onPick(row)}>
                        {data.columns.map(column => <td key={column.key}>{String(row[column.key] ?? '')}</td>)}
                        <td><Button size="sm" onClick={() => onPick(row)}>选择</Button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
          </div>
        </div>
      </div>
    </div>
  )
}
