import { useState } from 'react'
import PlacementPicker from './PlacementPicker'
import { MAX_ROW_SPAN } from './formDesignerDraft'
import type { DesignDraft, DesignRow, DesignTable } from './types'

interface PropertyPanelProps {
  draft: DesignDraft
  table: DesignTable
  row: DesignRow | null
  disabled: boolean
  onPlacement: (span: number, rowSpan: number) => void
  onNewLine: (value: boolean) => void
  onSection: (sectionId: string | null) => void
  onTab: (tabNo: number) => void
  onHidden: (hidden: boolean) => void
  onMergeCompanion: (companionKey: string | null) => void
  onResetRow: () => void
}

/**
 * 属性面板：只放**版式**属性（顺序、占位、复合格、分节、表单内隐藏）。
 * 字段自身的属性（标签/类型/必填/只读/格式/下拉选项/选择器来源）归字段维护——
 * 两处都能改同一件事就会双写，故此处不提供入口。
 */
export default function PropertyPanel({
  draft,
  table,
  row,
  disabled,
  onPlacement,
  onNewLine,
  onSection,
  onTab,
  onHidden,
  onMergeCompanion,
  onResetRow,
}: PropertyPanelProps) {
  const [showPlacement, setShowPlacement] = useState(false)

  if (!row) {
    return (
      <aside className="erp-designer-props">
        <div className="erp-designer-props-title">属性</div>
        <p className="erp-designer-muted">在画布上点选一个字段后，在这里调整它的版式。</p>
      </aside>
    )
  }

  const isCompanion = row.cellRole === 2
  const mergeCandidates = draft.master.filter(
    candidate =>
      candidate.key !== row.key && candidate.cellRole === 0 && !candidate.locked && !candidate.required,
  )
  const sections = [
    ...new Set(
      draft.master
        .map(item => item.sectionId?.trim() ?? '')
        .filter(section => section.length > 0),
    ),
  ]
  const groupMates = row.cellGroup
    ? draft.master.filter(item => item.cellGroup?.trim() === row.cellGroup?.trim())
    : []

  return (
    <aside className="erp-designer-props">
      <div className="erp-designer-props-title">属性</div>
      <div className="erp-designer-props-head">
        <strong>{row.label}</strong>
        <span className="erp-designer-muted">{row.key}</span>
      </div>

      {table === 'master' ? (
        <>
          <div className="erp-designer-prop">
            <label>占位</label>
            <div className="erp-designer-inline">
              <button
                type="button"
                className="erp-command-btn"
                disabled={disabled}
                onClick={() => setShowPlacement(value => !value)}
              >
                {`▦ ${row.span}×${row.rowSpan}`}
              </button>
              <label className="erp-designer-check">
                <input
                  type="checkbox"
                  checked={row.newLine}
                  disabled={disabled}
                  onChange={event => onNewLine(event.target.checked)}
                />
                另起一行
              </label>
            </div>
            {showPlacement ? (
              <PlacementPicker
                columns={draft.columns}
                span={row.span}
                rowSpan={row.rowSpan}
                onPick={onPlacement}
                onClose={() => setShowPlacement(false)}
              />
            ) : null}
          </div>

          <div className="erp-designer-prop">
            <label>页签</label>
            <select
              className="form-select form-select-sm"
              value={row.tabNo}
              disabled={disabled}
              onChange={event => onTab(Number(event.target.value))}
            >
              {draft.tabs.map(tab => (
                <option key={tab.no} value={tab.no}>
                  {tab.title.trim().length > 0 ? tab.title : '默认'}
                </option>
              ))}
            </select>
          </div>

          <div className="erp-designer-prop">
            <label>分节</label>
            <input
              className="form-control form-control-sm"
              list="erp-designer-sections"
              value={row.sectionId ?? ''}
              disabled={disabled}
              placeholder="留空表示不分组"
              onChange={event => onSection(event.target.value)}
            />
            <datalist id="erp-designer-sections">
              {sections.map(section => (
                <option key={section} value={section} />
              ))}
            </datalist>
          </div>

          <div className="erp-designer-prop">
            <label>复合格</label>
            {isCompanion ? (
              <div className="erp-designer-inline">
                <span className="erp-designer-muted">
                  与 {groupMates.find(item => item.cellRole === 1)?.label ?? '主字段'} 同格
                </span>
                <button
                  type="button"
                  className="erp-command-btn"
                  disabled={disabled}
                  onClick={() => onMergeCompanion(null)}
                >
                  取消合并
                </button>
              </div>
            ) : (
              <div className="erp-designer-inline">
                <select
                  className="form-select form-select-sm"
                  value={groupMates.find(item => item.cellRole === 2)?.key ?? ''}
                  disabled={disabled || !row.hasChooser}
                  title={row.hasChooser ? '' : '该字段没有启用中的选择器来源，合出来是个没有选择按钮的空壳'}
                  onChange={event => onMergeCompanion(event.target.value.length > 0 ? event.target.value : null)}
                >
                  <option value="">不合并</option>
                  {mergeCandidates.map(candidate => (
                    <option key={candidate.key} value={candidate.key}>
                      {candidate.label}
                    </option>
                  ))}
                </select>
                {!row.hasChooser ? <span className="erp-designer-muted">无选择器来源</span> : null}
              </div>
            )}
          </div>
        </>
      ) : null}

      <div className="erp-designer-prop">
        <label>表单内隐藏</label>
        <div className="erp-designer-inline">
          <input
            type="checkbox"
            checked={row.hidden}
            disabled={disabled || row.locked}
            onChange={event => onHidden(event.target.checked)}
          />
          {row.locked ? <span className="erp-designer-muted">{row.lockReason}</span> : null}
        </div>
      </div>

      <div className="erp-designer-props-actions">
        <button type="button" className="erp-command-btn" disabled={disabled} onClick={onResetRow}>
          恢复该字段默认
        </button>
      </div>
      <p className="erp-designer-muted">
        最大占位 {draft.columns} 列 × {MAX_ROW_SPAN} 行；字段自身的属性（标签/类型/必填/格式）在字段维护里改。
      </p>
    </aside>
  )
}
