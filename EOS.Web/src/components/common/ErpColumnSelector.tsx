import { IconArrowBarToDown, IconArrowBarToLeft, IconArrowBarToRight, IconArrowBarToUp, IconArrowLeft, IconArrowRight } from '@tabler/icons-react'
import { useEffect, useRef, useState } from 'react'
import { Button } from '../ui/Button'
import { LoadingState } from './AsyncState'

export interface ColumnSelectorGroup {
  id: string
  /** 组标题，如「主表字段」「子表字段」 */
  label: string
  /** 全量字段（有序，含隐藏字段），用于「可选字段」列表 */
  fields: { key: string; label: string }[]
  /** 当前已选列键（有序） */
  visibleKeys: string[]
  /** 系统默认列键（有序），用于「取默认字段」/「恢复默认」 */
  defaultKeys: string[]
}

interface ErpColumnSelectorProps {
  open: boolean
  title?: string
  groups: ColumnSelectorGroup[]
  loading?: boolean
  /** 加载失败提示；非空时显示错误与「重新加载」 */
  loadError?: string | null
  onRetry?: () => void
  saving?: boolean
  onClose: () => void
  onSave: (selection: Record<string, string[]>) => Promise<void> | void
  /** 保存按钮可用性（默认始终可用） */
  canSave?: (selection: Record<string, string[]>) => boolean
}

type SelectionState = Record<string, { available: string[]; visible: string[] }>

/**
 * 通用「选择列」弹窗：双栏多选 + 上移/下移排序 + 取默认字段。
 *
 * 只负责字段选择交互，不感知 API/权限：调用方传入全量字段、当前已选与默认配置，
 * 保存时通过 onSave 把「组 id → 列键数组」交回页面处理（写库、失效缓存等）。
 * 组件内部持有草稿，字段配置加载完成后初始化，用户编辑后不再被外部数据覆盖。
 */
export function ErpColumnSelector({
  open,
  title = '选择列',
  groups,
  loading = false,
  loadError = null,
  onRetry,
  saving = false,
  onClose,
  onSave,
  canSave,
}: ErpColumnSelectorProps) {
  const [draft, setDraft] = useState<Record<string, string[]>>({})
  const [selection, setSelection] = useState<SelectionState>({})
  const [saveError, setSaveError] = useState<string | null>(null)
  const initialized = useRef(false)

  useEffect(() => {
    if (!open) {
      setDraft({})
      setSelection({})
      setSaveError(null)
      initialized.current = false
      return
    }
    if (initialized.current || groups.length === 0) return
    setDraft(Object.fromEntries(groups.map((group) => [group.id, [...group.visibleKeys]])))
    setSelection(Object.fromEntries(groups.map((group) => [group.id, { available: [], visible: [] }])))
    initialized.current = true
  }, [open, groups])

  const setSideSelection = (groupId: string, side: 'available' | 'visible', options: HTMLOptionsCollection) => {
    setSelection((current) => ({
      ...current,
      [groupId]: {
        ...current[groupId],
        [side]: Array.from(options).filter((option) => option.selected).map((option) => option.value),
      },
    }))
  }

  const transfer = (groupId: string, add: boolean, all = false) => {
    const group = groups.find((item) => item.id === groupId)
    if (!group) return
    const currentSelection = selection[groupId] ?? { available: [], visible: [] }
    const currentDraft = draft[groupId] ?? []
    const keys = all
      ? add
        ? group.fields.filter((field) => !currentDraft.includes(field.key)).map((field) => field.key)
        : currentDraft
      : add
        ? currentSelection.available
        : currentSelection.visible
    setDraft((current) => ({
      ...current,
      [groupId]: add
        ? [...currentDraft, ...keys.filter((key) => !currentDraft.includes(key))]
        : currentDraft.filter((key) => !keys.includes(key)),
    }))
    setSelection((current) => ({ ...current, [groupId]: { available: [], visible: [] } }))
  }

  const moveSelected = (groupId: string, direction: -1 | 1) => {
    const keys = selection[groupId]?.visible ?? []
    if (keys.length === 0) return
    setDraft((current) => {
      const list = [...(current[groupId] ?? [])]
      for (const key of keys) {
        const index = list.indexOf(key)
        const target = index + direction
        if (index < 0 || target < 0 || target >= list.length) continue
        ;[list[index], list[target]] = [list[target], list[index]]
      }
      return { ...current, [groupId]: list }
    })
  }

  const applyDefault = (groupId: string) => {
    const group = groups.find((item) => item.id === groupId)
    if (!group) return
    setDraft((current) => ({ ...current, [groupId]: [...group.defaultKeys] }))
    setSelection((current) => ({ ...current, [groupId]: { available: [], visible: [] } }))
  }

  const restoreDefaults = () => {
    setDraft(Object.fromEntries(groups.map((group) => [group.id, [...group.defaultKeys]])))
    setSelection(Object.fromEntries(groups.map((group) => [group.id, { available: [], visible: [] }])))
  }

  const handleSave = async () => {
    setSaveError(null)
    try {
      await onSave(draft)
    } catch (error) {
      setSaveError(error instanceof Error ? error.message : '保存列配置失败。')
    }
  }

  if (!open) return null

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className={`modal-dialog modal-dialog-centered erp-columns-dialog ${groups.length > 1 ? 'erp-columns-dialog-paired' : 'erp-columns-dialog-single'}`}>
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {loading ? (
              <LoadingState label="正在加载完整字段配置…" />
            ) : loadError ? (
              <div className="alert alert-danger d-flex align-items-center justify-content-between">
                <span>{loadError}</span>
                {onRetry && (
                  <button type="button" className="btn btn-danger btn-sm" onClick={onRetry}>
                    重新加载
                  </button>
                )}
              </div>
            ) : (
              <div className="erp-columns-layout">
                {groups.map((group) => {
                  const visible = draft[group.id] ?? []
                  const sideSelection = selection[group.id] ?? { available: [], visible: [] }
                  return (
                    <div className="erp-columns-group" key={group.id}>
                      <h3 className="mb-2">{group.label}</h3>
                      <div className="erp-columns-transfer">
                        <div>
                          <label className="form-label">{group.label}可选字段</label>
                          <select
                            multiple
                            size={18}
                            className="form-select erp-column-select"
                            aria-label={`${group.label}可选字段`}
                            value={sideSelection.available}
                            onChange={(event) => setSideSelection(group.id, 'available', event.currentTarget.options)}
                            onDoubleClick={() => transfer(group.id, true, false)}
                          >
                            {group.fields.filter((field) => !visible.includes(field.key)).map((field) => (
                              <option key={field.key} value={field.key}>{field.label}</option>
                            ))}
                          </select>
                          <div className="small text-secondary mt-1">Ctrl/Shift 多选，双击加入</div>
                        </div>
                        <div className="col-auto d-flex flex-column justify-content-center gap-2">
                          <button type="button" className="btn btn-outline-primary erp-column-action" title="加入选中字段" onClick={() => transfer(group.id, true, false)}>
                            <IconArrowRight size={20} />
                          </button>
                          <button type="button" className="btn btn-outline-primary erp-column-action" title="加入全部字段" onClick={() => transfer(group.id, true, true)}>
                            <IconArrowBarToRight size={20} />
                          </button>
                          <button type="button" className="btn btn-outline-secondary erp-column-action" title="移除选中字段" onClick={() => transfer(group.id, false, false)}>
                            <IconArrowLeft size={20} />
                          </button>
                          <button type="button" className="btn btn-outline-secondary erp-column-action" title="移除全部字段" onClick={() => transfer(group.id, false, true)}>
                            <IconArrowBarToLeft size={20} />
                          </button>
                        </div>
                        <div className="col">
                          <label className="form-label">
                            {group.label}已选字段（
                            <button type="button" className="erp-column-default-link" aria-label="取默认字段" onClick={() => applyDefault(group.id)}>
                              取默认字段
                            </button>
                            ）
                          </label>
                          <div className="d-flex gap-2">
                            <select
                              multiple
                              size={18}
                              className="form-select erp-column-select"
                              aria-label={`${group.label}已选字段`}
                              value={sideSelection.visible}
                              onChange={(event) => setSideSelection(group.id, 'visible', event.currentTarget.options)}
                              onDoubleClick={() => transfer(group.id, false, false)}
                            >
                              {visible.map((key) => {
                                const field = group.fields.find((item) => item.key === key)
                                return <option key={key} value={key}>{field?.label ?? key}</option>
                              })}
                            </select>
                            <div className="d-flex flex-column justify-content-center gap-2">
                              <button type="button" className="btn btn-outline-secondary erp-column-action" title="上移" onClick={() => moveSelected(group.id, -1)}>
                                <IconArrowBarToUp size={20} />
                              </button>
                              <button type="button" className="btn btn-outline-secondary erp-column-action" title="下移" onClick={() => moveSelected(group.id, 1)}>
                                <IconArrowBarToDown size={20} />
                              </button>
                            </div>
                          </div>
                          <div className="small text-secondary mt-1">双击移除，选择后调整顺序</div>
                        </div>
                      </div>
                    </div>
                  )
                })}
              </div>
            )}
            {saveError && <div className="alert alert-danger m-3 mb-0">{saveError}</div>}
          </div>
          <div className="modal-footer justify-content-between">
            <Button onClick={restoreDefaults} disabled={!groups.some((group) => group.defaultKeys.length > 0)}>
              恢复默认
            </Button>
            <div className="d-flex gap-2">
              <Button onClick={onClose}>取消</Button>
              <Button variant="primary" onClick={() => void handleSave()} loading={saving} disabled={Boolean(canSave && !canSave(draft))}>
                保存
              </Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
