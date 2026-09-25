import { useState, type CSSProperties, type ReactNode } from 'react'
import { useDraggable, useDroppable } from '@dnd-kit/core'
import { packFormSections } from '../document-workbench/formLayout'
import { RESIDENT_TAB_NO, tabTitle } from './formDesignerDraft'
import { dragId, type DropTarget } from './formDesignerDrag'
import type { DesignDraft, DesignRow } from './types'

interface DesignCanvasProps {
  draft: DesignDraft
  activeTabNo: number
  onActiveTabChange: (no: number) => void
  selectedKey: string | null
  onSelect: (key: string | null) => void
  /** 紧凑排列：开启时允许后续字段回填空洞（关闭则空洞保留）。 */
  compact: boolean
  preview: boolean
  /** 正在拖的字段（画布内），用于淡化原位置 */
  draggingKey: string | null
  /** 当前落点，用于显示落点指示 */
  dropTarget: DropTarget
  onMove: (key: string, delta: number) => void
  onHide: (key: string) => void
  onForceNewLine: (key: string) => void
  onRenameTab: (no: number, title: string) => void
  onAddTab: () => void
  onDeleteTab: (no: number) => void
}

/**
 * 设计态画布：与运行态**同一套装箱规则**（共用 `packFormSections`），
 * 但**不载入真实单据**——值用字段代号占位。版式（排布 / 占位 / 复合格 / 分节）是真实的，
 * 只有值是占位，这正是"所见即所得"要保证的部分，同时避开数据权限（设计者无需浏览权）。
 *
 * 拖拽不是唯一手段：每个字段悬停给出快捷按钮（纯拖拽对精细操作不友好，键盘用户更用不了）。
 */
export default function DesignCanvas({
  draft,
  activeTabNo,
  onActiveTabChange,
  selectedKey,
  onSelect,
  compact,
  preview,
  draggingKey,
  dropTarget,
  onMove,
  onHide,
  onForceNewLine,
  onRenameTab,
  onAddTab,
  onDeleteTab,
}: DesignCanvasProps) {
  const [renaming, setRenaming] = useState<number | null>(null)
  const [renameValue, setRenameValue] = useState('')

  const tabRows = draft.master.filter(row => row.tabNo === activeTabNo)
  const sections = packFormSections(tabRows, draft.columns, { fillHoles: compact })
  const dropKey = dropTarget?.kind === 'insert' || dropTarget?.kind === 'merge' ? dropTarget.key : null
  const dropClass = dropTarget?.kind === 'merge' ? 'is-drop-merge' : ''

  return (
    <div className="erp-designer-canvas">
      <div className="erp-designer-tabs" role="tablist">
        {draft.tabs.map(tab => (
          <DroppableTab key={tab.no} tabNo={tab.no} active={dropTarget?.kind === 'tab' && dropTarget.tabNo === tab.no}>
            {renaming === tab.no ? (
              <input
                className="erp-designer-tab-input"
                value={renameValue}
                autoFocus
                onChange={event => setRenameValue(event.target.value)}
                onBlur={() => {
                  onRenameTab(tab.no, renameValue)
                  setRenaming(null)
                }}
                onKeyDown={event => {
                  if (event.key === 'Enter') {
                    onRenameTab(tab.no, renameValue)
                    setRenaming(null)
                  }
                  if (event.key === 'Escape') setRenaming(null)
                }}
              />
            ) : (
              <button
                type="button"
                role="tab"
                aria-selected={tab.no === activeTabNo}
                className="erp-designer-tab-btn"
                onClick={() => onActiveTabChange(tab.no)}
                onDoubleClick={() => {
                  if (preview) return
                  setRenaming(tab.no)
                  setRenameValue(tab.title)
                }}
                title="单击切换，双击改名；字段可拖到标签上移动到该页签"
              >
                {tabTitle(tab)}
              </button>
            )}
            {!preview && tab.no !== RESIDENT_TAB_NO ? (
              <button
                type="button"
                className="erp-designer-tab-close"
                title="删除页签（其中的字段回到默认页签）"
                onClick={() => onDeleteTab(tab.no)}
              >
                ×
              </button>
            ) : null}
          </DroppableTab>
        ))}
        {!preview ? (
          <button type="button" className="erp-designer-tab-add" title="新增页签" onClick={onAddTab}>
            +
          </button>
        ) : null}
      </div>

      {tabRows.length === 0 ? (
        <p className="erp-designer-empty">本页签还没有字段，从左侧字段池拖入或点击加入。</p>
      ) : null}

      <div className="erp-designer-sections">
        {sections.map((section, sectionIndex) => (
          <section className="erp-form-group" key={section.title ?? `default-${sectionIndex}`}>
            {section.title ? (
              <DroppableSection
                sectionId={section.title}
                active={dropTarget?.kind === 'section' && dropTarget.sectionId === section.title}
              >
                {section.title}
              </DroppableSection>
            ) : null}
            <div
              className="erp-designer-grid"
              style={
                {
                  '--erp-form-cols': draft.columns,
                  gridTemplateColumns: `repeat(${draft.columns}, minmax(0, 1fr))`,
                } as CSSProperties
              }
            >
              {section.cells.map(({ cell, placement }) => (
                <DesignerCell
                  key={cell[0].key}
                  cellKey={cell[0].key}
                  multi={cell.length > 1}
                  placement={placement}
                  active={dropKey === cell[0].key && dropTarget?.kind === 'insert'}
                  activeClass={dropTarget?.kind === 'insert' && dropTarget.key === cell[0].key
                    ? (dropTarget.before ? 'is-drop-before' : 'is-drop-after')
                    : dropKey === cell[0].key ? dropClass : ''}
                >
                  <label className="erp-form-label">
                    {cell[0].label}
                    {cell[0].required ? ' *' : ''}
                    {cell[0].locked ? (
                      <span className="erp-designer-lock" title={cell[0].lockReason ?? ''}>
                        🔒
                      </span>
                    ) : null}
                  </label>
                  <div className="erp-form-cell-controls">
                    {cell.map(field => (
                      <DesignField
                        key={field.key}
                        field={field}
                        selected={selectedKey === field.key}
                        dragging={draggingKey === field.key}
                        preview={preview}
                        onSelect={onSelect}
                        onMove={onMove}
                        onHide={onHide}
                        onForceNewLine={onForceNewLine}
                      />
                    ))}
                  </div>
                </DesignerCell>
              ))}
            </div>
          </section>
        ))}
      </div>
    </div>
  )
}

function DroppableTab({ tabNo, active, children }: { tabNo: number; active: boolean; children: ReactNode }) {
  const { setNodeRef } = useDroppable({ id: `tab-drop:${tabNo}`, data: { kind: 'tab', tabNo } })
  return (
    <span ref={setNodeRef} className={active ? 'erp-designer-tab is-drop-target' : 'erp-designer-tab'}>
      {children}
    </span>
  )
}

function DroppableSection({
  sectionId,
  active,
  children,
}: {
  sectionId: string
  active: boolean
  children: ReactNode
}) {
  const { setNodeRef } = useDroppable({ id: `section-drop:${sectionId}`, data: { kind: 'section', sectionId } })
  return (
    <div ref={setNodeRef} className={active ? 'erp-form-group-title is-drop-target' : 'erp-form-group-title'}>
      {children}
    </div>
  )
}

interface DesignerCellProps {
  cellKey: string
  multi: boolean
  placement: { col: number; row: number; span: number; rowSpan: number }
  active: boolean
  activeClass: string
  children: ReactNode
}

/** 一格的落点：左/右边缘插入、中心合并——数据交给 DndContext 判定（几何在页面里算）。 */
function DesignerCell({ cellKey, multi, placement, activeClass, children }: DesignerCellProps) {
  const { setNodeRef } = useDroppable({ id: `cell-drop:${cellKey}`, data: { kind: 'cell', key: cellKey } })
  const classes = [multi ? 'erp-form-cell erp-designer-cell' : 'erp-designer-cell']
  if (activeClass) classes.push(activeClass)
  return (
    <div
      ref={setNodeRef}
      className={classes.join(' ')}
      style={{ gridColumn: `${placement.col} / span ${placement.span}`, gridRow: `${placement.row} / span ${placement.rowSpan}` }}
    >
      {children}
    </div>
  )
}

interface DesignFieldProps {
  field: DesignRow
  selected: boolean
  dragging: boolean
  preview: boolean
  onSelect: (key: string | null) => void
  onMove: (key: string, delta: number) => void
  onHide: (key: string) => void
  onForceNewLine: (key: string) => void
}

/** 画布上的一个字段：值用字段代号占位；可拖拽排序/合并/移除，也可用悬停快捷按钮。 */
function DesignField({ field, selected, dragging, preview, onSelect, onMove, onHide, onForceNewLine }: DesignFieldProps) {
  const { attributes, listeners, setNodeRef } = useDraggable({
    id: dragId({ from: 'canvas', table: 'master', key: field.key }),
    data: { kind: 'field', key: field.key },
    disabled: preview,
  })
  const classes = ['erp-designer-field']
  if (selected) classes.push('is-selected')
  if (field.hidden) classes.push('is-hidden')
  if (!field.userVisible) classes.push('is-denied')
  if (dragging) classes.push('is-dragging')
  return (
    <div
      ref={setNodeRef}
      className={classes.join(' ')}
      onClick={event => {
        if (preview) return
        event.stopPropagation()
        onSelect(field.key)
      }}
      title={field.userVisible ? `${field.key}（可拖动调整位置）` : `${field.key}（当前用户不可见）`}
      {...attributes}
      {...listeners}
    >
      <span className="erp-designer-value">{field.key}</span>
      <span className="erp-designer-badges">
        {field.span > 1 || field.rowSpan > 1 ? <em>{`▦ ${field.span}×${field.rowSpan}`}</em> : null}
        {field.hidden ? <em className="is-hidden">已隐藏</em> : null}
        {field.cellRole === 1 ? <em>复合格主</em> : null}
        {field.cellRole === 2 ? <em>复合格从</em> : null}
        {field.isVirtual ? <em>虚拟</em> : null}
      </span>
      {!preview ? (
        <span className="erp-designer-field-actions">
          <button
            type="button"
            title="前移"
            onClick={event => {
              event.stopPropagation()
              onMove(field.key, -1)
            }}
          >
            ←
          </button>
          <button
            type="button"
            title="后移"
            onClick={event => {
              event.stopPropagation()
              onMove(field.key, 1)
            }}
          >
            →
          </button>
          <button
            type="button"
            title="另起一行"
            onClick={event => {
              event.stopPropagation()
              onForceNewLine(field.key)
            }}
          >
            ⤒
          </button>
          <button
            type="button"
            title={field.locked ? (field.lockReason ?? '不允许从表单移除') : '从表单移除（可再恢复）'}
            disabled={field.locked}
            onClick={event => {
              event.stopPropagation()
              onHide(field.key)
            }}
          >
            ✕
          </button>
        </span>
      ) : null}
    </div>
  )
}
