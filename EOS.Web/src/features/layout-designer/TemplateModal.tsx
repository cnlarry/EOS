import { useQuery } from '@tanstack/react-query'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
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
    <Modal
      title="模板库"
      onClose={onClose}
      dialogStyle={{ width: 620 }}
      scrollable
      footer={<Button variant="secondary" size="sm" onClick={onClose}>关闭</Button>}
    >
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
    </Modal>
  )
}
