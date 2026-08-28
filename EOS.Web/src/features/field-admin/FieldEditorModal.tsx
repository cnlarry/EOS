import { useRef, useState } from 'react'
import { Button } from '../../components/ui/Button'
import { FieldEditorForm, type FieldEditorEndpoints } from './FieldEditorForm'

// 类型 re-export（兼容既有引用方：FieldAdminPage/DocumentWorkbenchPage/测试）
export type {
  ChooserSource,
  ExpressionKind,
  ExpressionPreview,
  ExpressionValidation,
  FieldEditorEndpoints,
  FieldInput,
  FieldMeta,
  FieldSection,
  SetupLookup,
} from './FieldEditorForm'

export interface FieldEditorModalProps {
  open: boolean
  mode: 'new' | 'edit'
  tableId: string
  fieldKey?: string
  title?: string
  endpoints: FieldEditorEndpoints
  onClose: () => void
  onSaved: () => void
}

/**
 * 字段设置弹窗（旧承载形态，2026-08-28 起新入口统一走全尺寸页面 FieldEditorPage）。
 * 表单主体与页面共用 FieldEditorForm，避免双份逻辑漂移。
 */
export function FieldEditorModal({ open, mode, tableId, fieldKey, title, endpoints, onClose, onSaved }: FieldEditorModalProps) {
  const actionRef = useRef<{ save: () => void } | null>(null)
  const [, setSaveState] = useState({ canSave: false, saving: false })
  return open ? (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className="modal-dialog modal-lg modal-dialog-centered erp-field-settings-dialog">
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title ?? (mode === 'new' ? '新增字段' : '字段管理')}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            <FieldEditorForm
              mode={mode}
              tableId={tableId}
              fieldKey={fieldKey}
              endpoints={endpoints}
              onCancel={onClose}
              onSaved={onSaved}
              actionRef={actionRef}
              onStateChange={setSaveState}
              renderActions={({ canSave, saving, onSave, onCancel }) => (
                <div className="text-end mt-3">
                  <Button variant="secondary" className="me-2" onClick={onCancel}>取消</Button>
                  <Button variant="primary" loading={saving} disabled={!canSave} onClick={onSave}>保存</Button>
                </div>
              )}
            />
          </div>
        </div>
      </div>
    </div>
  ) : null
}
