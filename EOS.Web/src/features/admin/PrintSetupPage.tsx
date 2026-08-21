import { IconEdit, IconPlus, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import type { ColumnDef, RowSelectionState } from '@tanstack/react-table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { EmptyState, ErrorState, LoadingState } from '../../components/common/AsyncState'
import { ErpTable } from '../../components/common/ErpTable'
import { TabbedPanel } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

type Tab = 'headers' | 'tails' | 'footers'

const TABS_ARRAY: { key: Tab; label: string }[] = [
  { key: 'headers', label: '页头设置（2202）' },
  { key: 'tails', label: '表尾设置（2204）' },
  { key: 'footers', label: '页尾设置（2203）' },
]

interface FieldSpec { key: string; label: string; textarea?: boolean }

interface TabConfig { label: string; module: string; idField: string; nameField: string; fields: FieldSpec[] }

const TABS: Record<Tab, TabConfig> = {
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

type EditorState = { mode: 'new' } | { mode: 'edit'; row: Row }

export function PrintSetupPage() {
  const params = useParams<{ tab?: string }>()
  const navigate = useNavigate()
  const tab: Tab = params.tab === 'tails' || params.tab === 'footers' ? params.tab : 'headers'
  const config = TABS[tab]
  const [editor, setEditor] = useState<EditorState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [rowSelection, setRowSelection] = useState<RowSelectionState>({})

  useEffect(() => { setRowSelection({}) }, [tab])

  const list = useQuery({
    queryKey: ['print-admin', tab],
    queryFn: () => apiClient.get<Row[]>(`/print-admin/${tab}`),
  })

  const rows = list.data ?? []
  const idField = config.idField
  const selectedRows = rows.filter((row) => rowSelection[String(row[idField] ?? '')])

  const openEdit = () => {
    if (selectedRows.length !== 1) return
    setEditor({ mode: 'edit', row: selectedRows[0] })
  }

  const removeSelected = useCallback(async () => {
    if (selectedRows.length === 0) return
    const ids = selectedRows.map((row) => row[idField] as string)
    const name = selectedRows[0][config.nameField] ?? ids[0]
    const message = ids.length > 1 ? `确定删除选中的 ${ids.length} 条记录吗？` : `确定删除「${name}」吗？`
    if (!window.confirm(message)) return
    setError(null)
    try {
      for (const id of ids) {
        await apiClient.delete(`/print-admin/${tab}/${encodeURIComponent(id)}`)
      }
      setRowSelection({})
      await list.refetch()
    } catch (err) {
      setError(err instanceof ApiError ? err.body.message : '删除失败。')
    }
  }, [selectedRows, config, idField, tab, list])

  const columns = useMemo<ColumnDef<Row, unknown>[]>(() => [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
      header: ({ table }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          className="form-check-input"
          type="checkbox"
          aria-label="选择此行"
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
          onClick={(event) => event.stopPropagation()}
        />
      ),
    },
    ...config.fields.map((field): ColumnDef<Row, unknown> => ({
      id: field.key,
      accessorKey: field.key,
      header: field.label,
      enableSorting: true,
      meta: {
        title: field.textarea ? ({ value }) => (value == null || value === '' ? undefined : String(value)) : undefined,
      },
      cell: (info) => {
        const value = info.getValue()
        if (value == null || value === '') return '—'
        const text = String(value)
        return field.textarea && text.length > 60 ? `${text.slice(0, 60)}…` : text
      },
    })),
  ], [config])

  return (
    <div className="d-grid gap-2">
      <TabbedPanel
        tabs={TABS_ARRAY}
        activeKey={tab}
        onActiveKeyChange={(key) => navigate(`/admin/print-setup/${key}`)}
        label="报表版式设置"
      >
        <div className="d-flex align-items-center gap-2 mb-2">
          <span className="fw-semibold small">{config.label}维护</span>
          <span className="text-secondary small">页头/表尾/页脚供报表打印与单据打印使用</span>
          <div className="ms-auto d-flex gap-2 erp-command-bar-icon">
            {selectedRows.length > 0 && (
              <>
                <Button size="sm" icon={<IconEdit size={16} />} title="编辑" aria-label="编辑" disabled={selectedRows.length !== 1} onClick={openEdit} />
                <Button size="sm" variant="danger" icon={<IconTrash size={16} />} title="删除" aria-label="删除" onClick={() => void removeSelected()} />
              </>
            )}
            <Button size="sm" icon={<IconPlus size={16} />} title="新增" aria-label="新增" onClick={() => setEditor({ mode: 'new' })} />
          </div>
        </div>
        <div>
          {error && <div className="text-danger small mb-2">{error}</div>}
          {list.isPending ? <LoadingState label="正在加载…" /> : list.isError ? (
            <ErrorState message={list.error instanceof ApiError ? list.error.body.message : '加载失败。'} onRetry={() => void list.refetch()} />
          ) : (
            <ErpTable
              columns={columns}
              data={rows}
              getRowId={(row) => String(row[idField] ?? '')}
              resizable
              storageKey={`print-admin-${tab}`}
              clientSideSorting
              rowClickSingleSelect
              rowSelection={rowSelection}
              onRowSelectionChange={setRowSelection}
              empty={<EmptyState title="暂无记录" description="点击右上角「新增」添加一条记录。" />}
            />
          )}
        </div>
      </TabbedPanel>
      {editor && (
        <PrintEditorModal
          tab={tab}
          config={config}
          mode={editor.mode}
          row={editor.mode === 'edit' ? editor.row : undefined}
          onClose={() => setEditor(null)}
          onSaved={() => { setEditor(null); void list.refetch() }}
        />
      )}
    </div>
  )
}

interface PrintEditorModalProps {
  tab: Tab
  config: TabConfig
  mode: 'new' | 'edit'
  row?: Row
  onClose: () => void
  onSaved: () => void
}

function PrintEditorModal({ tab, config, mode, row, onClose, onSaved }: PrintEditorModalProps) {
  const idField = config.idField
  const editingId = mode === 'edit' ? (row?.[idField] as string | undefined) : undefined
  const [draft, setDraft] = useState<Record<string, string>>(() => {
    const next: Record<string, string> = {}
    if (row) for (const field of config.fields) next[field.key] = row[field.key] ?? ''
    return next
  })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [uploadingLogo, setUploadingLogo] = useState(false)

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
      onSaved()
    } catch (err) {
      setError(err instanceof ApiError ? err.body.message : '保存失败。')
    } finally {
      setSaving(false)
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
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-dialog-centered erp-dialog-sm">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{mode === 'edit' ? `编辑${config.label}：${editingId ?? ''}` : `新增${config.label}`}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {error && <div className="alert alert-danger">{error}</div>}
            <div className="row g-3">
              {config.fields.map((field) => (
                <div className="col-12" key={field.key}>
                  <label className="form-label">{field.label}</label>
                  {field.textarea ? (
                    <textarea className="form-control" rows={2} value={draft[field.key] ?? ''} onChange={(event) => setDraft((current) => ({ ...current, [field.key]: event.target.value }))} />
                  ) : (
                    <div className="d-flex gap-1">
                      <input className="form-control" value={draft[field.key] ?? ''} disabled={field.key === idField && mode === 'edit'} onChange={(event) => setDraft((current) => ({ ...current, [field.key]: event.target.value }))} />
                      {field.key === 'logoPath' && tab === 'headers' && (
                        <label className="btn btn-outline-secondary text-nowrap mb-0">
                          {uploadingLogo ? '上传中…' : '上传 LOGO'}
                          <input type="file" accept="image/png,image/jpeg,image/gif" className="d-none" onChange={(event) => { const file = event.target.files?.[0]; if (file) void uploadLogo(file); event.target.value = '' }} />
                        </label>
                      )}
                    </div>
                  )}
                </div>
              ))}
            </div>
          </div>
          <div className="card-footer text-end px-3 py-3">
            <Button variant="secondary" className="me-2" onClick={onClose}>取消</Button>
            <Button variant="primary" loading={saving} onClick={() => void submit()}>{mode === 'edit' ? '保存修改' : '新增记录'}</Button>
          </div>
        </div>
      </div>
    </div>
  )
}