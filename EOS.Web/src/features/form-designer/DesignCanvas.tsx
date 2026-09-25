import { useState, type CSSProperties } from 'react'
import { packFormGrid } from '../document-workbench/formLayout'
import { RESIDENT_TAB_NO, tabTitle } from './formDesignerDraft'
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
  onMove: (key: string, delta: number) => void
  onHide: (key: string) => void
  onForceNewLine: (key: string) => void
  onRenameTab: (no: number, title: string) => void
  onAddTab: () => void
  onDeleteTab: (no: number) => void
}

/**
 * 设计态画布：与运行态同一套栅格几何（同一份 packFormGrid 装箱规则），
 * 但**不载入真实单据**——值用字段代号占位。版式（排布 / 占位 / 复合格 / 分节）是真实的，
 * 只有值是占位，这正是"所见即所得"要保证的部分，同时避开数据权限（设计者无需浏览权）。
 */
export default function DesignCanvas({
  draft,
  activeTabNo,
  onActiveTabChange,
  selectedKey,
  onSelect,
  compact,
  preview,
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
  const sections = new Map<string, DesignRow[]>()
  for (const row of tabRows) {
    const title = row.sectionId?.trim() ?? ''
    sections.set(title, [...(sections.get(title) ?? []), row])
  }

  return (
    <div className="erp-designer-canvas">
      <div className="erp-designer-tabs" role="tablist">
        {draft.tabs.map(tab => (
          <span key={tab.no} className={tab.no === activeTabNo ? 'erp-designer-tab is-active' : 'erp-designer-tab'}>
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
                title="单击切换，双击改名"
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
          </span>
        ))}
        {!preview ? (
          <button type="button" className="erp-designer-tab-add" title="新增页签" onClick={onAddTab}>
            +
          </button>
        ) : null}
      </div>

      {tabRows.length === 0 ? (
        <p className="erp-designer-empty">本页签还没有字段，从左侧字段池加入。</p>
      ) : null}

      <div className="erp-designer-sections">
        {[...sections.entries()].map(([title, rows]) => (
          <SectionGrid
            key={title || 'default'}
            title={title}
            rows={rows}
            columns={draft.columns}
            compact={compact}
            preview={preview}
            selectedKey={selectedKey}
            onSelect={onSelect}
            onMove={onMove}
            onHide={onHide}
            onForceNewLine={onForceNewLine}
          />
        ))}
      </div>
    </div>
  )
}

interface SectionGridProps {
  title: string
  rows: DesignRow[]
  columns: number
  compact: boolean
  preview: boolean
  selectedKey: string | null
  onSelect: (key: string | null) => void
  onMove: (key: string, delta: number) => void
  onHide: (key: string) => void
  onForceNewLine: (key: string) => void
}

/** 一节一张栅格（节内独立装箱，row 从 1 起算）；复合格占主字段那一个格。 */
function SectionGrid({
  title,
  rows,
  columns,
  compact,
  preview,
  selectedKey,
  onSelect,
  onMove,
  onHide,
  onForceNewLine,
}: SectionGridProps) {
  const byGroup = new Map<string, DesignRow[]>()
  const standalones: DesignRow[] = []
  for (const row of rows) {
    const group = row.cellRole === 2 ? row.cellGroup?.trim() : null
    if (group) {
      byGroup.set(group, [...(byGroup.get(group) ?? []), row])
    } else {
      standalones.push(row)
    }
  }
  const cells = [...standalones, ...[...byGroup.values()].map(group => group[0])]
    .sort((left, right) => left.orderNo - right.orderNo)
    .map(row => ({ key: row.key, span: row.span, rowSpan: row.rowSpan, newLine: row.newLine }))
  const placements = packFormGrid(cells, columns, compact)

  const style = {
    '--erp-form-cols': columns,
    gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
  } as CSSProperties

  return (
    <section className="erp-form-group">
      {title ? <div className="erp-form-group-title">{title}</div> : null}
      <div className="erp-designer-grid" style={style}>
        {placements.map(placement => {
          const main = rows.find(row => row.key === placement.key)
          if (!main) return null
          const family = byGroup.get(main.cellGroup?.trim() ?? '') ?? [main]
          const cellStyle: CSSProperties = {
            gridColumn: `${placement.col} / span ${placement.span}`,
            gridRow: `${placement.row} / span ${placement.rowSpan}`,
          }
          return (
            <div
              key={main.key}
              className={family.length > 1 ? 'erp-form-cell erp-designer-cell' : 'erp-designer-cell'}
              style={cellStyle}
            >
              <label className="erp-form-label">
                {main.label}
                {main.required ? ' *' : ''}
                {main.locked ? <span className="erp-designer-lock" title={main.lockReason ?? ''}>🔒</span> : null}
              </label>
              <div className="erp-form-cell-controls">
                {family.map(field => (
                  <DesignField
                    key={field.key}
                    field={field}
                    selected={selectedKey === field.key}
                    preview={preview}
                    onSelect={onSelect}
                    onMove={onMove}
                    onHide={onHide}
                    onForceNewLine={onForceNewLine}
                  />
                ))}
              </div>
            </div>
          )
        })}
      </div>
    </section>
  )
}

interface DesignFieldProps {
  field: DesignRow
  selected: boolean
  preview: boolean
  onSelect: (key: string | null) => void
  onMove: (key: string, delta: number) => void
  onHide: (key: string) => void
  onForceNewLine: (key: string) => void
}

/** 画布上的一个字段：值用字段代号占位；悬停给出快捷按钮（纯拖拽对精细操作不友好）。 */
function DesignField({ field, selected, preview, onSelect, onMove, onHide, onForceNewLine }: DesignFieldProps) {
  const classes = ['erp-designer-field']
  if (selected) classes.push('is-selected')
  if (field.hidden) classes.push('is-hidden')
  if (!field.userVisible) classes.push('is-denied')
  return (
    <div
      className={classes.join(' ')}
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
