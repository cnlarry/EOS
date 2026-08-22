import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { IconDownload, IconTrash } from '@tabler/icons-react'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import { ApiError } from '../../types/api'

export interface AttachmentItem {
  id: number
  moduleId: number
  masterTable: string
  keyValues: string
  serialNo: number
  fileName: string
  clientFileName: string
  contentType: string
  sizeBytes: number
  sha256: string
  remark: string | null
  uploadedBy: string
  uploadedByDisplay: string | null
  uploadedAt: string
}

interface AttachmentDialogProps {
  moduleId: number
  masterTable: string
  /** 主键值数组（按主键顺序，JSON 序列化后作为 key 传给后端） */
  recordKey: string[]
  title: string
  canUpload: boolean
  canEdit: boolean
  canDelete: boolean
  onClose: () => void
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function formatDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN', { hour12: false })
}

/**
 * 统一表单附件对话框：列出单据级附件（上传/下载/删除/备注编辑）。
 * 权限：上传需 FILE_UPDA、改备注需 FILE_EDIT、删除需 FILE_DELE（服务端同样强制校验）；
 * 文件二进制存服务端文件系统，元数据经本对话框展示与追溯。
 */
export function AttachmentDialog({ moduleId, masterTable, recordKey: key, title, canUpload, canEdit, canDelete, onClose }: AttachmentDialogProps) {
  const queryClient = useQueryClient()
  const [remark, setRemark] = useState('')
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement | null>(null)
  const encodedKey = encodeURIComponent(JSON.stringify(key))

  const listQuery = useQuery({
    queryKey: ['attachments', moduleId, masterTable, encodedKey],
    queryFn: () => apiClient.get<AttachmentItem[]>(`/document-workbench/${moduleId}/attachments`, { query: { key: JSON.stringify(key) } }),
  })

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  const upload = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData()
      form.append('key', JSON.stringify(key))
      form.append('remark', remark)
      form.append('file', file)
      const response = await fetch(`/api/document-workbench/${moduleId}/attachments`, { method: 'POST', credentials: 'include', body: form })
      if (!response.ok) {
        const problem = await response.json().catch(() => ({})) as { message?: string }
        throw new Error(problem.message ?? '上传失败。')
      }
      return response.json() as Promise<AttachmentItem>
    },
    onSuccess: () => {
      setRemark('')
      if (fileInputRef.current) fileInputRef.current.value = ''
      void queryClient.invalidateQueries({ queryKey: ['attachments', moduleId, masterTable] })
    },
    onError: (cause) => setError(cause instanceof Error ? cause.message : '上传失败。'),
  })

  const updateRemark = useMutation({
    mutationFn: async ({ id, text }: { id: number; text: string }) => apiClient.put<AttachmentItem>(`/document-workbench/${moduleId}/attachments/${id}/remark`, { remark: text }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['attachments', moduleId, masterTable] }),
    onError: (cause) => setError(cause instanceof Error ? cause.message : '更新备注失败。'),
  })

  const remove = useMutation({
    mutationFn: async (id: number) => {
      if (!window.confirm('确定删除该附件吗？删除后不可恢复。')) return null
      return apiClient.delete<AttachmentItem>(`/document-workbench/${moduleId}/attachments/${id}`)
    },
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['attachments', moduleId, masterTable] }),
    onError: (cause) => setError(cause instanceof Error ? cause.message : '删除失败。'),
  })

  const download = (item: AttachmentItem) => {
    const anchor = document.createElement('a')
    anchor.href = `/api/document-workbench/${moduleId}/attachments/${item.id}/download?key=${encodedKey}`
    anchor.download = item.clientFileName
    anchor.rel = 'noopener'
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
  }

  const handleFile = (file: File | undefined) => {
    if (!file) return
    setError(null)
    setUploading(true)
    upload.mutate(file, { onSettled: () => setUploading(false) })
  }

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true" aria-label={`${title}附件`}>
      <div className="modal-dialog modal-dialog-centered erp-dialog-md">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">附件：{title}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {error && <div className="alert alert-danger">{error}</div>}
            {canUpload && (
              <div className="d-flex gap-2 mb-3 align-items-center">
                <input
                  className="form-control form-control-sm flex-grow-1"
                  placeholder="附件说明（可选）"
                  value={remark}
                  onChange={(event) => setRemark(event.target.value)}
                />
                <label className={`btn btn-sm btn-outline-secondary text-nowrap mb-0${uploading || upload.isPending ? ' disabled' : ''}`}>
                  {uploading || upload.isPending ? '上传中…' : '上传文件'}
                  <input ref={fileInputRef} type="file" className="d-none" onChange={(event) => { handleFile(event.target.files?.[0]); event.target.value = '' }} />
                </label>
              </div>
            )}
            {listQuery.isPending ? (
              <div className="text-center text-secondary py-4">正在加载附件…</div>
            ) : listQuery.isError ? (
              <div className="alert alert-danger d-flex justify-content-between align-items-center mb-0">
                <span>{listQuery.error instanceof ApiError ? listQuery.error.body.message : '附件加载失败。'}</span>
                <button type="button" className="btn btn-sm btn-danger" onClick={() => void listQuery.refetch()}>重新加载</button>
              </div>
            ) : listQuery.data.length === 0 ? (
              <div className="text-center text-secondary py-4">暂无附件。</div>
            ) : (
              <ul className="list-group list-group-flush erp-attachment-list">
                {listQuery.data.map((item) => (
                  <li className="list-group-item d-flex align-items-start gap-2" key={item.id}>
                    <div className="flex-grow-1 min-w-0">
                      <div className="d-flex align-items-center gap-2">
                        <span className="fw-semibold text-truncate" title={item.clientFileName}>{item.clientFileName}</span>
                        <span className="badge text-bg-light text-secondary text-nowrap">{formatSize(item.sizeBytes)}</span>
                      </div>
                      <div className="small text-secondary">
                        {item.uploadedByDisplay ?? item.uploadedBy} · {formatDate(item.uploadedAt)}
                        {item.serialNo > 1 && ` · 第${item.serialNo}个`}
                      </div>
                      {item.remark ? <div className="small text-secondary">{item.remark}</div> : null}
                    </div>
                    {canEdit && (
                      <input
                        className="form-control form-control-sm erp-attachment-remark"
                        style={{ maxWidth: 200 }}
                        defaultValue={item.remark ?? ''}
                        placeholder="备注"
                        onBlur={(event) => {
                          const text = event.target.value.trim()
                          if (text !== (item.remark ?? '')) updateRemark.mutate({ id: item.id, text })
                        }}
                      />
                    )}
                    <Button size="sm" icon={<IconDownload size={16} />} title="下载" aria-label="下载" onClick={() => download(item)} />
                    {canDelete && (
                      <Button size="sm" variant="danger" icon={<IconTrash size={16} />} title="删除" aria-label="删除" loading={remove.isPending} onClick={() => void remove.mutate(item.id)} />
                    )}
                  </li>
                ))}
              </ul>
            )}
          </div>
          <div className="card-footer text-end px-3 py-3">
            <Button variant="secondary" onClick={onClose}>关闭</Button>
          </div>
        </div>
      </div>
    </div>
  )
}