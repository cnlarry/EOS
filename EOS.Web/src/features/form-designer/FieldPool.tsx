import { useMemo, useState } from 'react'
import { useDraggable, useDroppable } from '@dnd-kit/core'
import { dragId } from './formDesignerDrag'
import type { DesignTable } from './types'

export interface PoolEntry {
  key: string
  label: string
  dataType: string
  placed: boolean
  userVisible: boolean
  required: boolean
  isPrimaryKey: boolean
  isVirtual: boolean
}

interface FieldPoolProps {
  entries: PoolEntry[]
  activeTable: DesignTable
  hasDetail: boolean
  onTableChange: (table: DesignTable) => void
  onAdd: (key: string) => void
  disabled: boolean
}

/**
 * 字段池：把**已注册**的字段放回表单（不是新增字段定义，新增仍走字段维护）。
 * 单模块字段可上百，故必须支持关键字、按表筛选与「未排/全部」切换；
 * 字段可直接拖进画布（落到某个字段前后/页签/分节），也可点击加入默认页签末尾。
 */
export default function FieldPool({
  entries,
  activeTable,
  hasDetail,
  onTableChange,
  onAdd,
  disabled,
}: FieldPoolProps) {
  const [keyword, setKeyword] = useState('')
  const [showAll, setShowAll] = useState(false)
  // 把画布上的字段拖回这里 = 从表单移除（等价 ✕，可再恢复）
  const { setNodeRef, isOver } = useDroppable({ id: 'pool-drop', data: { kind: 'pool' } })

  const visible = useMemo(() => {
    const needle = keyword.trim().toLowerCase()
    return entries
      .filter(entry => (showAll ? true : !entry.placed))
      .filter(entry =>
        needle.length === 0
          ? true
          : entry.key.toLowerCase().includes(needle) || entry.label.toLowerCase().includes(needle),
      )
  }, [entries, keyword, showAll])

  const unplaced = entries.filter(entry => !entry.placed).length

  return (
    <aside className="erp-designer-pool" ref={setNodeRef}>
      <div className="erp-designer-pool-title">字段池</div>
      <div className="erp-designer-pool-tables">
        <button
          type="button"
          className={activeTable === 'master' ? 'erp-designer-chip is-active' : 'erp-designer-chip'}
          onClick={() => onTableChange('master')}
        >
          主表
        </button>
        <button
          type="button"
          className={activeTable === 'detail' ? 'erp-designer-chip is-active' : 'erp-designer-chip'}
          disabled={!hasDetail}
          title={hasDetail ? '' : '本模块没有明细表'}
          onClick={() => onTableChange('detail')}
        >
          明细
        </button>
      </div>
      <input
        className="form-control form-control-sm"
        placeholder="搜索字段"
        value={keyword}
        onChange={event => setKeyword(event.target.value)}
      />
      <div className="erp-designer-pool-filter">
        <label>
          <input type="checkbox" checked={showAll} onChange={event => setShowAll(event.target.checked)} />
          显示全部
        </label>
        <span className="erp-designer-muted">未排 {unplaced}</span>
      </div>
      <div className={isOver ? 'erp-designer-pool-list is-drop-remove' : 'erp-designer-pool-list'}>
        {isOver ? <p className="erp-designer-muted">松开即从表单移除</p> : null}
        {visible.length === 0 ? (
          <p className="erp-designer-muted">本模块所有字段都已在表单中。</p>
        ) : null}
        {visible.map(entry => (
          <PoolItem
            key={entry.key}
            entry={entry}
            table={activeTable}
            disabled={disabled}
            onAdd={onAdd}
          />
        ))}
      </div>
    </aside>
  )
}

function PoolItem({
  entry,
  table,
  disabled,
  onAdd,
}: {
  entry: PoolEntry
  table: DesignTable
  disabled: boolean
  onAdd: (key: string) => void
}) {
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({
    id: dragId({ from: 'pool', table, key: entry.key }),
    data: { kind: 'poolItem', key: entry.key },
    disabled: disabled || entry.placed,
  })
  const classes = ['erp-designer-pool-item']
  if (isDragging) classes.push('is-dragging')
  return (
    <button
      ref={setNodeRef}
      type="button"
      className={classes.join(' ')}
      disabled={disabled || entry.placed}
      onClick={() => onAdd(entry.key)}
      title={entry.userVisible ? `${entry.label}（可拖入表单）` : `${entry.label}（当前用户不可见）`}
      {...attributes}
      {...listeners}
    >
      <span className="erp-designer-pool-key">{entry.key}</span>
      <span className="erp-designer-pool-label">{entry.label}</span>
      <span className="erp-designer-badges">
        {entry.placed ? <em>已排</em> : null}
        {entry.required ? <em>必填</em> : null}
        {entry.isPrimaryKey ? <em>主键</em> : null}
        {entry.isVirtual ? <em>虚拟</em> : null}
        {!entry.userVisible ? <em title="当前用户不可见">🔒</em> : null}
      </span>
    </button>
  )
}
