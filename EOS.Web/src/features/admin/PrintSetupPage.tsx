import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useParams } from 'react-router-dom'
import { ErrorState, LoadingState } from '../../components/common/AsyncState'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

type Tab = 'headers' | 'tails' | 'footers'

interface FieldSpec { key: string; label: string; textarea?: boolean }

const TABS: Record<Tab, { label: string; module: string; idField: string; nameField: string; fields: FieldSpec[] }> = {
  headers: {
    label: '页头设置（2202）', module: '2202', idField: 'headerId', nameField: 'headerName',
    fields: [
      { key: 'headerId', label: '页头编号' },
      { key: 'headerName', label: '页头名称' },
      { key: 'companyName', label: '公司名称' },
      { key: 'companyNameEn', label: '公司英文名' },
      { key: 'headerText', label: '页头文字', textarea: true },
      { key: 'logoPath', label: 'LOGO 路径' },
    ],
  },
  tails: {
    label: '表尾设置（2204）', module: '2204', idField: 'tailId', nameField: 'tailName',
    fields: [
      { key: 'tailId', label: '表尾编号' },
      { key: 'tailName', label: '表尾名称' },
      { key: 'tailText', label: '表尾文字', textarea: true },
    ],
  },
  footers: {
    label: '页尾设置（2203）', module: '2203', idField: 'footerId', nameField: 'footerName',
    fields: [
      { key: 'footerId', label: '页尾编号' },
      { key: 'footerName', label: '页尾名称' },
      { key: 'footerText', label: '页尾文字', textarea: true },
    ],
  },
}

type Row = Record<string, string | null>

export function PrintSetupPage() {
  const params = useParams<{ tab?: string }>()
  const tab: Tab = params.tab === 'tails' || params.tab === 'footers' ? params.tab : 'headers'
  const config = TABS[tab]
  const [draft, setDraft] = useState<Record<string, string>>({})
  const [editingId, setEditingId] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [uploadingLogo, setUploadingLogo] = useState(false)

  const list = useQuery({
    queryKey: ['print-admin', tab],
    queryFn: () => apiClient.get<Row[]>(`/print-admin/${tab}`),
  })

  const rows = list.data ?? []
  const idField = config.idField

  const resetForm = () => { setDraft({}); setEditingId(null); setError(null) }

  const startEdit = (row: Row) => {
    setEditingId(row[idField] as string)
    const next: Record<string, string> = {}
    for (const field of config.fields) next[field.key] = row[field.key] ?? ''
    setDraft(next)
    setError(null)
  }

  const submit = async () => {
    const id = (draft[idField] ?? '').trim()
    if (!id) { setError('编号不能为空。'); return }
    setSaving(true)
    setError(null)
    try {
      const body = Object.fromEntries(config.fields.map((field) => [field.key, draft[field.key] ?? '']))
      if (editingId) {
        await apiClient.put(`/print-admin/${tab}/${encodeURIComponent(editingId)}`, body)
      } else {
        await apiClient.post(`/print-admin/${tab}`, body)
      }
      await list.refetch()
      resetForm()
    } catch (err) {
      setError(err instanceof ApiError ? err.body.message : '保存失败。')
    } finally {
      setSaving(false)
    }
  }

  const remove = async (row: Row) => {
    const id = row[idField] as string
    if (!window.confirm(`确定删除「${row[config.nameField] ?? id}」？`)) return
    setError(null)
    try {
      await apiClient.delete(`/print-admin/${tab}/${encodeURIComponent(id)}`)
      await list.refetch()
      if (editingId === id) resetForm()
    } catch (err) {
      setError(err instanceof ApiError ? err.body.message : '删除失败。')
    }
  }

  const uploadLogo = async (file: File) => {
    setUploadingLogo(true)
    setError(null)
    try {
      const form = new FormData()
      form.append('file', file)
      const response = await fetch('/api/print-admin/logo', { method: 'POST', credentials: 'include', body: form })
      if (!response.ok) {
        const problem = await response.json().catch(() => ({})) as { message?: string }
        throw new Error(problem.message ?? 'LOGO 上传失败。')
      }
      const data = await response.json() as { logoPath: string }
      setDraft((current) => ({ ...current, logoPath: data.logoPath }))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'LOGO 上传失败。')
    } finally {
      setUploadingLogo(false)
    }
  }

  return (
    <div className="d-grid gap-2">
      <ul className="nav nav-tabs">
        {(Object.keys(TABS) as Tab[]).map((key) => (
          <li className="nav-item" key={key}>
            <a className={`nav-link ${tab === key ? 'active' : ''}`} href={`/admin/print-setup/${key}`}>{TABS[key].label}</a>
          </li>
        ))}
      </ul>
      <div className="card">
        <div className="card-header py-2 d-flex align-items-center gap-2">
          <span className="fw-semibold small">{config.label}维护</span>
          <span className="text-secondary small">页头/表尾/页脚供报表打印与单据打印使用</span>
        </div>
        <div className="card-body py-2">
          {error && <div className="text-danger small mb-2">{error}</div>}
          {list.isPending ? <LoadingState label="正在加载…" /> : list.isError ? (
            <ErrorState message={list.error instanceof ApiError ? list.error.body.message : '加载失败。'} onRetry={() => void list.refetch()} />
          ) : (
            <div className="table-responsive">
              <table className="table table-sm align-middle">
                <thead>
                  <tr>
                    {config.fields.map((field) => <th key={field.key} className="small">{field.label}</th>)}
                    <th className="small">操作</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((row) => (
                    <tr key={row[idField] as string}>
                      {config.fields.map((field) => (
                        <td key={field.key} className="small">{field.textarea ? (row[field.key] ?? '').slice(0, 60) : row[field.key] ?? '—'}</td>
                      ))}
                      <td className="small text-nowrap">
                        <Button size="sm" className="me-1" onClick={() => startEdit(row)}>编辑</Button>
                        <Button size="sm" variant="danger" onClick={() => void remove(row)}>删除</Button>
                      </td>
                    </tr>
                  ))}
                  {rows.length === 0 && <tr><td colSpan={config.fields.length + 1} className="text-center text-secondary py-3 small">暂无记录</td></tr>}
                </tbody>
              </table>
            </div>
          )}
          <div className="border-top pt-2 mt-2">
            <div className="d-flex align-items-center gap-2 mb-2">
              <span className="small fw-semibold">{editingId ? `编辑：${editingId}` : '新增'}</span>
              {editingId && <Button size="sm" variant="ghost" onClick={resetForm}>取消</Button>}
            </div>
            <div className="row g-2">
              {config.fields.map((field) => (
                <div className="col-md-4" key={field.key}>
                  <label className="form-label small mb-1">{field.label}</label>
                  {field.textarea ? (
                    <textarea className="form-control form-control-sm" rows={2} value={draft[field.key] ?? ''} onChange={(event) => setDraft((current) => ({ ...current, [field.key]: event.target.value }))} />
                  ) : (
                    <div className="d-flex gap-1">
                      <input className="form-control form-control-sm" value={draft[field.key] ?? ''} disabled={field.key === idField && editingId !== null} onChange={(event) => setDraft((current) => ({ ...current, [field.key]: event.target.value }))} />
                      {field.key === 'logoPath' && tab === 'headers' && (
                        <label className="btn btn-sm btn-outline-secondary text-nowrap mb-0">
                          {uploadingLogo ? '上传中…' : '上传 LOGO'}
                          <input type="file" accept="image/png,image/jpeg,image/gif" className="d-none" onChange={(event) => { const file = event.target.files?.[0]; if (file) void uploadLogo(file); event.target.value = '' }} />
                        </label>
                      )}
                    </div>
                  )}
                </div>
              ))}
            </div>
            <div className="mt-2">
              <Button size="sm" onClick={() => void submit()} loading={saving}>{editingId ? '保存修改' : '新增记录'}</Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
