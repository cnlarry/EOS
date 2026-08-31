import { useQuery } from '@tanstack/react-query'
import { Button } from '../../components/ui/Button'
import { apiClient } from '../../services/api'
import type { LayoutTemplateInfo } from './types'

interface TemplateModalProps {
  currentModuleId: string
  canDesign: boolean
  onApply: (template: LayoutTemplateInfo) => void
  onClose: () => void
}

export function TemplateModal({ currentModuleId, canDesign, onApply, onClose }: TemplateModalProps) {
  const templates = useQuery({
    queryKey: ['layout-designer', 'templates'],
    queryFn: () => apiClient.get<LayoutTemplateInfo[]>('/layout-designer/templates'),
  })

  return (
    <div className="position-fixed top-0 start-0 w-100 h-100 d-flex align-items-center justify-content-center"
      style={{ background: 'rgba(0,0,0,0.4)', zIndex: 1000 }}
      onClick={onClose}
    >
      <div className="card shadow" style={{ width: 620, maxHeight: '75vh' }} onClick={(e) => e.stopPropagation()}>
        <div className="card-header d-flex align-items-center justify-content-between">
          <strong>模板库</strong>
          <button type="button" className="btn-close" aria-label="关闭" onClick={onClose} />
        </div>
        <div className="card-body overflow-auto">
          <div className="text-secondary small mb-2">
            从内置版式模板开始（覆盖当前画布，保存后按当前单据类型定制；当前单据类型版本式会丢失未保存改动）。
          </div>
          {templates.isPending && <div className="text-secondary small">加载中…</div>}
          {templates.isError && <div className="alert alert-danger small">模板清单加载失败。</div>}
          <div className="d-flex flex-column gap-2">
            {(templates.data ?? [])
              .filter((t) => t.kind === 'document')
              .map((template) => (
                <div key={template.formatId} className="border rounded p-2 d-flex align-items-center justify-content-between">
                  <div>
                    <strong>{template.title}</strong>
                    <span className="text-secondary ms-2 small font-monospace">{template.formatId}</span>
                    {template.formatId === currentModuleId && (
                      <span className="badge bg-primary-subtle text-primary ms-2 small">当前版式</span>
                    )}
                  </div>
                  <Button variant="secondary" size="sm" disabled={!canDesign || template.formatId === currentModuleId}
                    onClick={() => onApply(template)}>
                    套用模板
                  </Button>
                </div>
              ))}
          </div>
        </div>
        <div className="card-footer d-flex justify-content-end">
          <Button variant="secondary" size="sm" onClick={onClose}>关闭</Button>
        </div>
      </div>
    </div>
  )
}
