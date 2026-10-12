import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'
import { apiClient } from '../../services/api'
import type { LayoutVersionInfo } from './types'

interface HistoryModalProps {
  moduleId: string
  isCustom: boolean
  canDesign: boolean
  onClose: () => void
}

export function HistoryModal({ moduleId, isCustom, canDesign, onClose }: HistoryModalProps) {
  const queryClient = useQueryClient()
  const versions = useQuery({
    queryKey: ['layout-designer', moduleId, 'versions'],
    queryFn: () => apiClient.get<LayoutVersionInfo[]>(`/layout-designer/${moduleId}/versions`),
    enabled: isCustom,
  })
  const restoreMutation = useMutation({
    mutationFn: (version: number) =>
      apiClient.post(`/layout-designer/${moduleId}/versions/${version}/restore`, { clientId: null }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['layout-designer', moduleId] })
      void queryClient.invalidateQueries({ queryKey: ['layout-designer', moduleId, 'versions'] })
    },
  })

  return (
    <Modal
      title="版本历史"
      onClose={onClose}
      dialogStyle={{ width: 520 }}
      scrollable
      footer={<Button variant="secondary" size="sm" onClick={onClose}>关闭</Button>}
    >
          {!isCustom ? (
            <div className="text-secondary small">
              当前使用内置版式，尚无定制历史。保存首次自定义后，每次保存都会生成一个版本，可随时回滚。
            </div>
          ) : versions.isPending ? (
            <div className="text-secondary small">加载中…</div>
          ) : versions.isError ? (
            <div className="alert alert-danger small">版本历史加载失败。</div>
          ) : (
            <div className="d-flex flex-column gap-2">
              {(versions.data ?? []).map((version) => (
                <div key={version.version} className="border rounded p-2 d-flex align-items-center justify-content-between">
                  <div>
                    <strong>v{version.version}</strong>
                    <span className="text-secondary ms-2 small">
                      {new Date(version.createDate).toLocaleString('zh-CN')} · {version.createPerson || '未知'}
                    </span>
                  </div>
                  <Button variant="secondary" size="sm"
                    disabled={!canDesign || restoreMutation.isPending || version.version === (versions.data ?? [])[0]?.version}
                    onClick={() => restoreMutation.mutate(version.version)}>
                    回滚到该版本
                  </Button>
                </div>
              ))}
              {(versions.data ?? []).length === 0 && (
                <div className="text-secondary small">暂无历史版本。</div>
              )}
            </div>
          )}
    </Modal>
  )
}
