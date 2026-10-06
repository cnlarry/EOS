import { useState, type CSSProperties, type ReactNode } from 'react'
import { useDraggable, useDroppable } from '@dnd-kit/core'
import { IconListDetails, IconPlus } from '@tabler/icons-react'
import { packFormSections, resolveTabColumns } from '../document-workbench/formLayout'
import { normalizeFormOpenMode, resolveDialogSize } from '../document-workbench/formOpenMode'
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
  /** 打开「表单呈现」配置弹窗（按钮在页签行最右端——那里本来就是空白） */
  onOpenPresentation: () => void
  /** 呈现配置摘要（按钮 title：打开方式与窗体尺寸一眼可见） */
  presentationSummary: string
  /** 右键精修菜单（位置用视口坐标，菜单自己定位） */
  onRowContextMenu?: (key: string, x: number, y: number) => void
  onSectionContextMenu?: (sectionId: string, x: number, y: number) => void
  onTabContextMenu?: (no: number, x: number, y: number) => void
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
 * 前移/后移/占位/复合格/分节/移出表单全部走右键精修，画布因此干净到只剩排布本身。
 */
export default function DesignCanvas({
  draft,
  activeTabNo,
  onActiveTabChange,
  selectedKey,
  onSelect,
  compact,
  draggingKey,
  ghostKey,
  dropTarget,
  onAddField,
  onRenameTab,
  onAddTab,
  onDeleteTab,
  onOpenPresentation,
  presentationSummary,
  onRowContextMenu,
  onSectionContextMenu,
  onTabContextMenu,
}: DesignCanvasProps) {
  const [renaming, setRenaming] = useState<number | null>(null)
  const [renameValue, setRenameValue] = useState('')

  // 栅格列数是**页签级事实**：画板按当前页签的列数排（页签 1 两列、页签 2 一列时各排各的），
  // 与运行态同一处解析（resolveTabColumns）——画板折行位置必须与运行态一致
  const columns = resolveTabColumns(draft.tabs, activeTabNo)
  const tabRows = draft.master.filter(row => row.tabNo === activeTabNo)
  const sections = packFormSections(tabRows, columns, { fillHoles: compact })
  const dropKey = dropTarget?.kind === 'insert' || dropTarget?.kind === 'merge' ? dropTarget.key : null
  const dropClass = dropTarget?.kind === 'merge' ? 'is-drop-merge' : ''
  // 画板尺寸 = 运行态容器尺寸：弹窗方式下就是模块声明的窗体宽高（所见即所得，
  // 行内几个字段、在哪折行与运行态一致）；本页签/新页签方式表单占满可用区域，画板照旧铺满。
  // 高度取 min-height 而不是 height：内容超出窗体时**设计区继续滚动**（拖拽的滚动补偿挂在设计区上），
  // 运行态则是在窗体内滚动——高度上不硬裁，免得设计时看不到后面的字段。
  const dialogSize = normalizeFormOpenMode(draft.openMode) === 'DIALOG'
    ? resolveDialogSize(draft.dialogWidth, draft.dialogHeight)
    : null

  return (
    <div
      className={dialogSize ? 'erp-designer-canvas is-dialog' : 'erp-designer-canvas'}
      style={dialogSize ? { width: dialogSize.width, minHeight: dialogSize.height } : undefined}
    >
      <ul className="nav nav-tabs erp-form-tabs erp-designer-tabs" role="tablist">
        {draft.tabs.map(tab => (
          <DroppableTab
            key={tab.no}
            tabNo={tab.no}
            active={dropTarget?.kind === 'tab' && dropTarget.tabNo === tab.no}
            closable={tab.no !== RESIDENT_TAB_NO}
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
                  setRenaming(tab.no)
                  setRenameValue(tab.title)
                }}
                onContextMenu={event => {
                  event.preventDefault()
                  onTabContextMenu?.(tab.no, event.clientX, event.clientY)
                }}
                title={`${tabTitle(tab)}（一行 ${resolveTabColumns(draft.tabs, tab.no)} 列）：单击切换，双击改名，右键改布局列数/删除页签；字段可拖到标签上移动到该页签`}
              >
                {tabTitle(tab)}
              </button>
            )}
          </DroppableTab>
        ))}
        <li className="nav-item">
          <button type="button" className="nav-link erp-designer-tab-add" title="新增页签" onClick={onAddTab}>
            +
          </button>
        </li>
        {/* 页签行最右端（原本的空白处）：表单呈现配置入口。
            放在这里而不是画布上方，是因为它是"整张表单的呈现方式"，与页签行同高更像窗体标题栏那一排 */}
        <li className="nav-item erp-designer-present-item">
          <button
            type="button"
            className="nav-link erp-designer-present"
            aria-label="表单呈现"
            title={`表单呈现：${presentationSummary}（打开方式与窗体尺寸）`}
            onClick={onOpenPresentation}
          >
            {presentationSummary}
          </button>
        </li>
      </ul>

      <div
        className="erp-form-grid erp-designer-sections"
        style={{ '--erp-form-cols': columns } as CSSProperties}
      >
        {tabRows.length === 0 ? (
          <p className="erp-designer-empty">本页签还没有字段，点下方的「+」从字段池选择。</p>
        ) : null}
        {sections.map((section, sectionIndex) => (
          <section className="erp-form-group" key={section.title ?? `default-${sectionIndex}`}>
            {section.title ? (
              <DroppableSection
                sectionId={section.title}
                active={dropTarget?.kind === 'section' && dropTarget.sectionId === section.title}
                onContextMenu={(x, y) => onSectionContextMenu?.(section.title as string, x, y)}
              >
                {section.title}
              </DroppableSection>
            ) : null}
            <div
              className="erp-designer-grid"
              style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` } as CSSProperties}
            >
              {section.cells.map(({ cell, placement }) => (
                <DesignerCell
                  key={cell[0].key}
                  cellKey={cell[0].key}
                  multi={cell.length > 1}
                  selected={cell.some(field => field.key === selectedKey)}
                  placement={placement}
                  ghost={ghostKey !== null && cell.some(field => field.key === ghostKey)}
                  activeClass={dropTarget?.kind === 'insert' && dropTarget.key === cell[0].key
                    ? (dropTarget.before ? 'is-drop-before' : 'is-drop-after')
                    : dropKey === cell[0].key ? dropClass : ''}
                  onSelect={onSelect}
                  onContextMenu={(x, y) => onRowContextMenu?.(cell[0].key, x, y)}
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
                        table={draft.masterTable}
                        dragging={draggingKey === field.key}
                        onSelect={onSelect}
                      />
                    ))}
                  </div>
                </DesignerCell>
              ))}
            </div>
          </section>
        ))}
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
          title="删除页签"
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
  children,
  onSelect,
  onContextMenu,
}: DesignerCellProps) {
  const { setNodeRef: setDropRef } = useDroppable({ id: `cell-drop:${cellKey}`, data: { kind: 'cell', key: cellKey } })
  const { setNodeRef: setDragRef, attributes, listeners } = useDraggable({
    id: dragId({ from: 'canvas', table: 'master', key: cellKey }),
    data: { kind: 'field', key: cellKey },
  })
  const classes = [multi ? 'erp-form-cell erp-designer-cell' : 'erp-designer-cell']
  if (selected) classes.push('is-selected')
  if (ghost) classes.push('is-drop-ghost')
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
      title={onContextMenu ? '拖动调整位置；右键精修（前移后移/占位/复合格/分节/移出表单）' : undefined}
      {...attributes}
      {...listeners}
    >
      {children}
    </div>
  )
}

interface DesignFieldProps {
  field: DesignRow
  /** 字段所属表名：悬停提示按 表名.字段名 给出完整定位 */
  table: string
  dragging: boolean
  onSelect: (key: string | null) => void
}

/** 格内的一个字段：值用字段代号占位。整格的拖动/右键由所在格承接，这里只做显示与单选。 */
function DesignField({ field, table, dragging, onSelect }: DesignFieldProps) {
  const classes = ['erp-designer-field']
  if (field.hidden) classes.push('is-hidden')
  if (!field.userVisible) classes.push('is-denied')
  // 虚拟列没有物理列（选择器回写的伴生显示列）：靠占位框样式区分，不再挂文字角标
  if (field.isVirtual) classes.push('is-virtual')
  if (dragging) classes.push('is-dragging')
  const name = `${table}.${field.key}${field.isVirtual ? '（虚拟列）' : ''}`
  const hasBadges = field.span > 1 || field.rowSpan > 1 || !field.userVisible || field.hidden
  return (
    <div
      className={classes.join(' ')}
      data-designer-field={field.key}
      onClick={event => {
        event.stopPropagation()
        onSelect(field.key)
      }}
      title={field.userVisible ? name : `${name}（当前用户不可见）`}
    >
      <span className="erp-designer-value">{field.key}</span>
      {/* 挂有数据源的字段在设计态也要看得见选择器：否则排出来的表单看不出哪些是选入的 */}
      {field.hasChooser ? (
        <span className="erp-designer-chooser" title={`${field.key} 挂有数据源（运行态经选择器选入）`}>
          <IconListDetails size={14} />
        </span>
      ) : null}
      {/* 角标只在有话说时才占位：空容器也会吃一份 flex gap，
          让"字段—选择器按钮—下一个字段"里最后一段距离比前一段多半格 */}
      {hasBadges ? (
        <span className="erp-designer-badges">
          {field.span > 1 || field.rowSpan > 1 ? <em>{`▦ ${field.span}×${field.rowSpan}`}</em> : null}
          {/* 排了也不会出现的字段先说清楚，免得排布白调 */}
          {!field.userVisible ? <em className="is-denied">运行态不显示</em> : null}
          {field.hidden ? <em className="is-hidden">已移出表单</em> : null}
        </span>
      ) : null}
    </div>
  )
}
