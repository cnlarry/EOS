import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import type { CompanyOption, LayoutHeaderOption } from './types'

interface HeaderManagerModalProps {
  moduleId: string
  currentHeaderId: string | null
  tailId: string | null
  company: CompanyOption | null
  canDesign: boolean
  onClose: () => void
}

export function HeaderManagerModal({
  moduleId, currentHeaderId, tailId, company, canDesign, onClose,
}: HeaderManagerModalProps) {
  const queryClient = useQueryClient()
  const [selectedHeaderId, setSelectedHeaderId] = useState(currentHeaderId ?? '')
  const [editing, setEditing] = useState<LayoutHeaderOption | null>(null)
  const [editForm, setEditForm] = useState({ company: '', companyEn: '', headerText: '', logoPath: '' })

  const headers = useQuery({
    queryKey: ['layout-designer', 'headers'],
    queryFn: () => apiClient.get<LayoutHeaderOption[]>('/layout-designer/headers'),
  })

  const bindingMutation = useMutation({
    mutationFn: () => apiClient.post(`/layout-designer/${moduleId}/binding`, {
      clientId: null,
      headerId: selectedHeaderId || null,
      tailId,
      printPrice: null,
    }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['layout-designer', moduleId] })
      onClose()
    },
  })

  const saveHeaderMutation = useMutation({
    mutationFn: () => apiClient.post('/layout-designer/headers', {
      headerId: editing!.headerId,
      name: editing!.name,
      company: editForm.company,
      companyEn: editForm.companyEn,
      headerText: editForm.headerText,
      logoPath: editForm.logoPath,
    }),
    onSuccess: () => {
      setEditing(null)
      void queryClient.invalidateQueries({ queryKey: ['layout-designer', 'headers'] })
    },
  })

  return (
    <Modal
      title="页头字典（HEADER_ID 引用）"
      onClose={onClose}
      dialogStyle={{ width: 620 }}
      scrollable
      footer={<Button variant="secondary" size="sm" onClick={onClose}>关闭</Button>}
    >
          <div className="mb-3">
            <div className="text-secondary small mb-1">本版式使用的页头（打印时按 CLIENT → SYS_USER_REPORT_PRINT → REPORT 链解析）</div>
            <div className="d-flex gap-2">
              <select
                className="form-select"
                value={selectedHeaderId}
                onChange={(e) => setSelectedHeaderId(e.target.value)}
                disabled={headers.isPending}
              >
                <option value="">（跟随报表默认，不指定）</option>
                {(headers.data ?? []).map((h) => (
                  <option key={h.headerId} value={h.headerId}>
                    {h.name}（{h.headerId}）{h.company ? ` · ${h.company}` : ''}
                  </option>
                ))}
              </select>
              <Button size="sm" disabled={bindingMutation.isPending} onClick={() => bindingMutation.mutate()}>
                保存页头选择
              </Button>
            </div>
          </div>

          <div className="alert alert-secondary py-2 small mb-3">
            公司名 / 英文名 / 地址 / 电话来自 <strong>110101 公司基本资料</strong>
            （打印时只认报表上显式绑定的公司，系统默认公司编号 <code>DEFAULT</code>）。
            页头字典只维护 <strong>LOGO 与页头文本</strong>；
            版式里可用 <code>{'{{COMPANY.NAME_CN}}'}</code>、<code>{'{{COMPANY.TEL}}'}</code> 等公司字段。
            {company && (
              <div className="mt-1">
                当前生效公司：<strong>{company.nameCn}</strong>（{company.companyId}）
                ——改绑在「报表设置 → 报表定义」的开单公司（<code>/admin/report-setup</code>），不在此处。
              </div>
            )}
          </div>

          <div className="text-secondary small mb-2">页头条目（{headers.data?.length ?? 0}）</div>
          {headers.isPending && <div className="text-secondary small">加载中…</div>}
          {(headers.data ?? []).map((header) => (
            <div key={header.headerId} className="border rounded p-2 mb-2">
              {editing?.headerId === header.headerId ? (
                <div className="d-flex flex-column gap-1">
                  <input
                    type="text" className="form-control form-control-sm" placeholder="电话/地址行（可选）"
                    value={editForm.headerText}
                    onChange={(e) => setEditForm((f) => ({ ...f, headerText: e.target.value }))}
                  />
                  <input
                    type="text" className="form-control form-control-sm" placeholder="LOGO 路径（~/...，可留空）"
                    value={editForm.logoPath}
                    onChange={(e) => setEditForm((f) => ({ ...f, logoPath: e.target.value }))}
                  />
                  <div className="d-flex gap-1">
                    <Button size="sm" disabled={saveHeaderMutation.isPending} onClick={() => saveHeaderMutation.mutate()}>
                      保存条目
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => setEditing(null)}>取消</Button>
                  </div>
                </div>
              ) : (
                <div className="d-flex align-items-center justify-content-between gap-2">
                  <div className="flex-grow-1">
                    <strong>{header.name}</strong>
                    <span className="text-secondary ms-2 small font-monospace">{header.headerId}</span>
                    {header.headerText && <div className="text-secondary small">{header.headerText}</div>}
                  </div>
                  {canDesign && (
                    <Button variant="ghost" size="sm"
                      onClick={() => {
                        setEditing(header)
                        setEditForm({
                          company: header.company ?? '', companyEn: header.companyEn ?? '',
                          headerText: header.headerText ?? '', logoPath: header.logoPath ?? '',
                        })
                      }}>
                      编辑
                    </Button>
                  )}
                </div>
              )}
            </div>
          ))}
    </Modal>
  )
}
