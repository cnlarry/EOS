import { useEffect, useState } from 'react'
import { Button } from '../../components/ui/Button'
import { Modal } from '../../components/ui/Modal'

export interface TableInput {
  description: string
  kind: string | null
  type: string | null
  remark: string | null
}

export interface TableDetail {
  tableId: string
  description: string
  kind: string | null
  type: string | null
  remark: string | null
  fkTable1: string | null
  fkTable2: string | null
  fkTable3: string | null
  fkTable4: string | null
  fkTable5: string | null
  queryRelation: string | null
  defaultCondition: string | null
  defaultVerify: string | null
  canImport: boolean
  lastUpdatedBy: string | null
  lastUpdatedAt: string | null
}

export interface TableEditorEndpoints {
  load: () => Promise<TableDetail | null>
  save: (input: TableInput, tableId: string, original: TableInput | null) => Promise<void>
}

interface TableEditorModalProps {
  open: boolean
  tableId: string
  endpoints: TableEditorEndpoints
  onClose: () => void
  onSaved: () => void
}

const KIND_OPTIONS = [
  { value: '', label: '未指定' },
  { value: 'P', label: 'P 主表' },
  { value: 'S', label: 'S 明细' },
  { value: 'O', label: 'O 其它' },
  { value: 'V', label: 'V 视图' },
]

const TYPE_OPTIONS = [
  { value: 'TABLE', label: 'TABLE 表' },
  { value: 'VIEW', label: 'VIEW 视图' },
  { value: 'UNKNOW', label: 'UNKNOW 未知' },
]

function emptyDraft(tableId: string): TableDetail {
  return {
    tableId, description: '', kind: 'P', type: 'TABLE', remark: null,
    fkTable1: null, fkTable2: null, fkTable3: null, fkTable4: null, fkTable5: null,
    queryRelation: null, defaultCondition: null, defaultVerify: null,
    canImport: false, lastUpdatedBy: null, lastUpdatedAt: null,
  }
}

function extractInput(detail: TableDetail): TableInput {
  return { description: detail.description, kind: detail.kind, type: detail.type, remark: detail.remark }
}

/**
 * 已登记表信息的编辑弹窗（低风险字段子集）。
 * 新增表元数据走「选取物理表/视图」（PhysicalTablePickerModal）按物理结构自动生成，不在这里手敲。
 */
export function TableEditorModal({ open, tableId, endpoints, onClose, onSaved }: TableEditorModalProps) {
  const [draft, setDraft] = useState<TableDetail | null>(null)
  const [original, setOriginal] = useState<TableDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (!open) return
    setSaveError(null)
    let cancelled = false
    setLoading(true)
    setLoadError(false)
    void endpoints.load().then((detail) => {
      if (cancelled) return
      setDraft(detail ? { ...detail } : emptyDraft(tableId))
      setOriginal(detail ? { ...detail } : null)
      setLoadError(detail == null)
    }).catch(() => {
      if (!cancelled) setLoadError(true)
    }).finally(() => {
      if (!cancelled) setLoading(false)
    })
    return () => { cancelled = true }
  }, [open, tableId, endpoints])

  if (!open) return null

  const canSave = draft != null && draft.description.trim().length > 0 && !loading && !loadError

  const handleSave = async () => {
    if (!draft) return
    setSaving(true)
    setSaveError(null)
    try {
      await endpoints.save(extractInput(draft), draft.tableId.trim(), original ? extractInput(original) : null)
      onSaved()
    } catch (error) {
      setSaveError(error instanceof Error ? error.message : '保存失败。')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      title={`数据表信息（${draft?.tableId ?? tableId}）`}
      onClose={onClose}
      size="lg"
      footer={<>
        <Button variant="secondary" className="me-2" onClick={onClose}>取消</Button>
        <Button variant="primary" loading={saving} disabled={!canSave} onClick={() => void handleSave()}>保存</Button>
      </>}
    >
      <div className="alert alert-warning">
        仅维护低风险表信息（描述/性质/类型/备注）；关联表、查询联表、默认条件等高风险配置由受控机制另行维护。
      </div>
      {loadError && <div className="alert alert-danger">无法加载该数据表元数据，请确认当前账号具有数据表维护权限。</div>}
      {draft && (
        <div className="row g-3">
          <div className="col-md-6">
            <label className="form-label">数据表名</label>
            <input className="form-control" value={draft.tableId} disabled />
          </div>
          <div className="col-md-6">
            <label className="form-label">数据表描述</label>
            <input className="form-control" value={draft.description} onChange={(event) => setDraft({ ...draft, description: event.target.value })} />
          </div>
          <div className="col-md-4">
            <label className="form-label">性质（T_KIND）</label>
            <select className="form-select" value={draft.kind ?? ''} onChange={(event) => setDraft({ ...draft, kind: event.target.value || null })}>
              {KIND_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
          </div>
          <div className="col-md-4">
            <label className="form-label">类型（T_TYPE）</label>
            <select className="form-select" value={draft.type ?? 'TABLE'} onChange={(event) => setDraft({ ...draft, type: event.target.value || null })}>
              {TYPE_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
          </div>
          <div className="col-md-4">
            <label className="form-label">可否导入（CAN_IMPORT）</label>
            <input className="form-control" value={draft.canImport ? '是' : '否'} disabled />
          </div>
          <div className="col-12">
            <label className="form-label">备注（T_REMARK）</label>
            <textarea className="form-control" rows={2} value={draft.remark ?? ''} onChange={(event) => setDraft({ ...draft, remark: event.target.value || null })} />
          </div>
          {draft.lastUpdatedBy && (
            <div className="col-12 text-secondary" style={{ fontSize: 12 }}>
              最后更新：{draft.lastUpdatedBy}（{draft.lastUpdatedAt ? new Date(draft.lastUpdatedAt).toLocaleString('zh-CN') : '—'}）
            </div>
          )}
        </div>
      )}
      {saveError && <div className="alert alert-danger mt-3 mb-0">{saveError}</div>}
    </Modal>
  )
}
