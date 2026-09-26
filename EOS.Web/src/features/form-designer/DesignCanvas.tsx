import { useState, type CSSProperties, type ReactNode } from 'react'
import { useDraggable, useDroppable } from '@dnd-kit/core'
import { IconPlus } from '@tabler/icons-react'
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
  /** 拖拽预览中已被搬动的字段：在落点处画"待放置"占位，让后方字段让位的关系一眼可见 */
  ghostKey: string | null
  /** 当前落点，用于显示落点指示 */
  dropTarget: DropTarget
  /** 末尾「+」：打开统一选择器补字段 */
  onAddField: () => void
  onRenameTab: (no: number, title: string) => void
  onAddTab: () => void
  onDeleteTab: (no: number) => void
  /** 右键精修菜单（位置用视口坐标，菜单自己定位） */
  onRowContextMenu?: (key: string, x: number, y: number) => void
  onSectionContextMenu?: (sectionId: string, x: number, y: number) => void
}

/**
 * 设计态画布：与运行态**同一套装箱规则**（共用 `packFormSections`），
 * 但**不载入真实单据**——值用字段代号占位。版式（排布 / 占位 / 复合格 / 分节）是真实的，
 * 只有值是占位，这正是"所见即所得"要保证的部分，同时避开数据权限（设计者无需浏览权）。
 *
 * 页签沿用运行态统一表单的页签样式（同一套 `erp-form-tabs`），
 * 让"设计出来的表单长什么样"与运行态观感一致。
 *
 * 一格的取舍：**标签与控件同属一个可拖拽、可右键、可点选的单元**，格内不再挂动作按钮——
 * 前移/后移/占位/复合格/分节/隐藏全部走右键精修，画布因此干净到只剩排布本身。
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
  ghostKey,
  dropTarget,
  onAddField,
  onRenameTab,
  onAddTab,
  onDeleteTab,
  onRowContextMenu,
  onSectionContextMenu,
}: DesignCanvasProps) {
  const [renaming, setRenaming] = useState<number | null>(null)
  const [renameValue, setRenameValue] = useState('')

  const tabRows = draft.master.filter(row => row.tabNo === activeTabNo)
  const sections = packFormSections(tabRows, draft.columns, { fillHoles: compact })
  const dropKey = dropTarget?.kind === 'insert' || dropTarget?.kind === 'merge' ? dropTarget.key : null
  const dropClass = dropTarget?.kind === 'merge' ? 'is-drop-merge' : ''

  return (
    <div className="erp-designer-canvas">
      <ul className="nav nav-tabs erp-form-tabs erp-designer-tabs" role="tablist">
        {draft.tabs.map(tab => (
          <DroppableTab
            key={tab.no}
            tabNo={tab.no}
            active={dropTarget?.kind === 'tab' && dropTarget.tabNo === tab.no}
            closable={!preview && tab.no !== RESIDENT_TAB_NO}
            onDelete={() => onDeleteTab(tab.no)}
          >
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
                className={tab.no === activeTabNo ? 'nav-link active' : 'nav-link'}
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
          </DroppableTab>
        ))}
        {!preview ? (
          <li className="nav-item">
            <button type="button" className="nav-link erp-designer-tab-add" title="新增页签" onClick={onAddTab}>
              +
            </button>
          </li>
        ) : null}
      </ul>

      <div
        className="erp-form-grid erp-designer-sections"
        style={{ '--erp-form-cols': draft.columns } as CSSProperties}
      >
        {tabRows.length === 0 && !preview ? (
          <p className="erp-designer-empty">本页签还没有字段，点下方的「+」从字段池选择。</p>
        ) : null}
        {sections.map((section, sectionIndex) => (
          <section className="erp-form-group" key={section.title ?? `default-${sectionIndex}`}>
            {section.title ? (
              <DroppableSection
                sectionId={section.title}
                active={dropTarget?.kind === 'section' && dropTarget.sectionId === section.title}
                onContextMenu={
                  preview ? undefined : (x, y) => onSectionContextMenu?.(section.title as string, x, y)
                }
              >
                {section.title}
              </DroppableSection>
            ) : null}
            <div
              className="erp-designer-grid"
              style={{ gridTemplateColumns: `repeat(${draft.columns}, minmax(0, 1fr))` } as CSSProperties}
            >
              {section.cells.map(({ cell, placement }) => (
                <DesignerCell
                  key={cell[0].key}
                  cellKey={cell[0].key}
                  multi={cell.length > 1}
                  selected={cell.some(field => field.key === selectedKey)}
                  placement={placement}
                  ghost={ghostKey !== null && cell.some(field => field.key === ghostKey)}
                  preview={preview}
                  activeClass={dropTarget?.kind === 'insert' && dropTarget.key === cell[0].key
                    ? (dropTarget.before ? 'is-drop-before' : 'is-drop-after')
                    : dropKey === cell[0].key ? dropClass : ''}
                  onSelect={onSelect}
                  onContextMenu={preview ? undefined : (x, y) => onRowContextMenu?.(cell[0].key, x, y)}
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
                        dragging={draggingKey === field.key}
                        preview={preview}
                        onSelect={onSelect}
                      />
                    ))}
                  </div>
                </DesignerCell>
              ))}
            </div>
          </section>
        ))}
        {!preview ? (
          <div className="erp-designer-add-row">
            <button
              type="button"
              className="erp-designer-add-cell"
              title="添加字段（打开统一选择器）"
              onClick={onAddField}
            >
              <IconPlus size={16} />
              添加字段
            </button>
          </div>
        ) : null}
      </div>
    </div>
  )
}

function DroppableTab({
  tabNo,
  active,
  closable,
  onDelete,
  children,
}: {
  tabNo: number
  active: boolean
  closable: boolean
  onDelete: () => void
  children: ReactNode
}) {
  const { setNodeRef } = useDroppable({ id: `tab-drop:${tabNo}`, data: { kind: 'tab', tabNo } })
  const classes = ['nav-item', 'erp-designer-tab']
  if (active) classes.push('is-drop-target')
  if (closable) classes.push('is-closable')
  return (
    <li ref={setNodeRef} className={classes.join(' ')}>
      {children}
      {closable ? (
        <button
          type="button"
          className="erp-designer-tab-close"
          title="删除页签（其中的字段回到默认页签）"
          onClick={onDelete}
        >
          ×
        </button>
      ) : null}
    </li>
  )
}

function DroppableSection({
  sectionId,
  active,
  children,
  onContextMenu,
}: {
  sectionId: string
  active: boolean
  children: ReactNode
  onContextMenu?: (x: number, y: number) => void
}) {
  const { setNodeRef } = useDroppable({ id: `section-drop:${sectionId}`, data: { kind: 'section', sectionId } })
  return (
    <div
      ref={setNodeRef}
      className={active ? 'erp-form-group-title is-drop-target' : 'erp-form-group-title'}
      onContextMenu={event => {
        if (!onContextMenu) return
        event.preventDefault()
        onContextMenu(event.clientX, event.clientY)
      }}
      title={onContextMenu ? '右键：分节改名 / 删除分节' : undefined}
    >
      {children}
    </div>
  )
}

interface DesignerCellProps {
  cellKey: string
  multi: boolean
  selected: boolean
  placement: { col: number; row: number; span: number; rowSpan: number }
  activeClass: string
  /** 拖拽预览中字段搬到了这一格：画待放置占位 */
  ghost: boolean
  preview: boolean
  children: ReactNode
  onSelect: (key: string) => void
  onContextMenu?: (x: number, y: number) => void
}

/**
 * 一格 = 一个整体单元：标签与控件同属一个可拖拽、可右键、可点选的载体。
 * 标签本身也是拖动/右键的把手——它只是字段的另一半，不是只能看的装饰。
 * 落点判定仍由 DndContext 负责（几何在页面里算）：左/右边缘插入、中心合并。
 */
function DesignerCell({
  cellKey,
  multi,
  selected,
  placement,
  activeClass,
  ghost,
  preview,
  children,
  onSelect,
  onContextMenu,
}: DesignerCellProps) {
  const { setNodeRef: setDropRef } = useDroppable({ id: `cell-drop:${cellKey}`, data: { kind: 'cell', key: cellKey } })
  const { setNodeRef: setDragRef, attributes, listeners } = useDraggable({
    id: dragId({ from: 'canvas', table: 'master', key: cellKey }),
    data: { kind: 'field', key: cellKey },
    disabled: preview,
  })
  const classes = [multi ? 'erp-form-cell erp-designer-cell' : 'erp-designer-cell']
  if (selected) classes.push('is-selected')
  if (ghost) classes.push('is-drop-ghost')
  if (preview) classes.push('is-preview')
  if (activeClass) classes.push(activeClass)
  return (
    <div
      ref={node => {
        setDragRef(node)
        setDropRef(node)
      }}
      className={classes.join(' ')}
      data-designer-cell={cellKey}
      style={{ gridColumn: `${placement.col} / span ${placement.span}`, gridRow: `${placement.row} / span ${placement.rowSpan}` }}
      onClick={event => {
        if (preview) return
        event.stopPropagation()
        onSelect(cellKey)
      }}
      onContextMenu={event => {
        if (!onContextMenu) return
        event.preventDefault()
        event.stopPropagation()
        // 右键落在哪个字段上就选哪个：复合格里对从字段的操作（如移出复合格）不能误作用到主字段
        const node = (event.target as HTMLElement).closest('[data-designer-field]')
        onSelect(node?.getAttribute('data-designer-field') ?? cellKey)
        onContextMenu(event.clientX, event.clientY)
      }}
      title={onContextMenu ? '拖动调整位置；右键精修（前移后移/占位/复合格/分节/隐藏）' : undefined}
      {...attributes}
      {...listeners}
    >
      {children}
    </div>
  )
}

interface DesignFieldProps {
  field: DesignRow
  dragging: boolean
  preview: boolean
  onSelect: (key: string | null) => void
}

/** 格内的一个字段：值用字段代号占位。整格的拖动/右键由所在格承接，这里只做显示与单选。 */
function DesignField({ field, dragging, preview, onSelect }: DesignFieldProps) {
  const classes = ['erp-designer-field']
  if (field.hidden) classes.push('is-hidden')
  if (!field.userVisible) classes.push('is-denied')
  if (dragging) classes.push('is-dragging')
  return (
    <div
      className={classes.join(' ')}
      data-designer-field={field.key}
      onClick={event => {
        if (preview) return
        event.stopPropagation()
        onSelect(field.key)
      }}
      title={field.userVisible ? field.key : `${field.key}（当前用户不可见）`}
    >
      <span className="erp-designer-value">{field.key}</span>
      <span className="erp-designer-badges">
        {field.span > 1 || field.rowSpan > 1 ? <em>{`▦ ${field.span}×${field.rowSpan}`}</em> : null}
        {/* 排了也不会出现的字段（虚拟查找列、当前用户不可见）先说清楚，免得排布白调 */}
        {!field.userVisible ? <em className="is-denied">运行态不显示</em> : null}
        {field.hidden ? <em className="is-hidden">已隐藏</em> : null}
        {field.cellRole === 1 ? <em>复合格主</em> : null}
        {field.cellRole === 2 ? <em>复合格从</em> : null}
        {field.isVirtual ? <em>虚拟</em> : null}
      </span>
    </div>
  )
}
