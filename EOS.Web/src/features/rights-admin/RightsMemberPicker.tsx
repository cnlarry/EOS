import { IconSearch } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { Button } from '../../components/ui/Button'

export interface PickerOption {
  id: string
  label: string
}

interface RightsMemberPickerProps {
  open: boolean
  title: string
  hint?: string
  options: PickerOption[]
  selected: string[]
  loading?: boolean
  onClose: () => void
  onSave: (ids: string[]) => Promise<void> | void
  /** 弹窗（默认）或完整页面（供 2305 组成员页）。 */
  variant?: 'modal' | 'page'
}

export function RightsMemberPicker({ open, title, hint, options, selected, loading = false, onClose, onSave, variant = 'modal' }: RightsMemberPickerProps) {
  const [selection, setSelection] = useState<Set<string>>(new Set())
  const [search, setSearch] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setSelection(new Set(selected.map((id) => id.trim().toLowerCase())))
    setSearch('')
    setError(null)
  }, [open, selected])

  const filtered = useMemo(() => {
    const text = search.trim().toLowerCase()
    return options.filter((option) => !text || `${option.id} ${option.label}`.toLowerCase().includes(text))
  }, [options, search])

  const toggle = (id: string) => {
    const key = id.trim().toLowerCase()
    setSelection((current) => {
      const next = new Set(current)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const save = async () => {
    setSaving(true)
    setError(null)
    try {
      const ids = options.filter((option) => selection.has(option.id.trim().toLowerCase())).map((option) => option.id.trim())
      await onSave(ids)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '保存失败，请稍后重试。')
    } finally {
      setSaving(false)
    }
  }

  if (!open) return null

  const pickerBody = (
    <>
      {hint && <div className="alert alert-info py-1 px-2 small mb-2">{hint}</div>}
      <div className="input-group input-group-sm mb-2">
        <span className="input-group-text"><IconSearch size={14} /></span>
        <input className="form-control form-control-sm" placeholder="搜索" value={search} onChange={(event) => setSearch(event.target.value)} aria-label="搜索" />
      </div>
      {loading ? <div className="text-secondary small py-3 text-center">加载中…</div> : (
        <div className={`border rounded p-2 rights-picker-list ${variant === 'page' ? 'rights-picker-list-page' : ''}`}>
          {filtered.length === 0 ? <div className="text-secondary small p-2">没有可选项</div> : filtered.map((option) => (
            <label key={option.id} className="d-flex align-items-center gap-2 form-check-label small py-1">
              <input
                type="checkbox"
                className="form-check-input m-0"
                checked={selection.has(option.id.trim().toLowerCase())}
                onChange={() => toggle(option.id)}
              />
              <span className="font-monospace">{option.id}</span>
              <span className="text-secondary">{option.label}</span>
            </label>
          ))}
        </div>
      )}
      <div className="small text-secondary mt-1">已选 {selection.size} 项</div>
      {error && <div className="alert alert-danger py-2 mt-2 mb-0" role="alert">{error}</div>}
    </>
  )

  const pickerFooter = (
    <div className="d-flex gap-2 ms-auto">
      <Button onClick={onClose}>{variant === 'page' ? '返回' : '取消'}</Button>
      <Button variant="primary" onClick={() => void save()} loading={saving} disabled={loading}>保存</Button>
    </div>
  )

  if (variant === 'page') {
    return (
      <div className="erp-full-list-page">
        <section className="card erp-list-card">
          <section className="erp-list-command-bar" aria-label="组成员工具栏">
            <span className="fw-semibold small">{title}</span>
            <div className="erp-list-actions">
              <Button size="sm" variant="ghost" onClick={onClose}>返回用户组</Button>
            </div>
          </section>
          <div className="p-2">{pickerBody}</div>
          <div className="card-footer">{pickerFooter}</div>
        </section>
      </div>
    )
  }

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">{pickerBody}</div>
          <div className="modal-footer">{pickerFooter}</div>
        </div>
      </div>
    </div>
  )
}
