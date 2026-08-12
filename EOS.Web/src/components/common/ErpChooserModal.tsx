import type { ColumnDef, SortingState } from '@tanstack/react-table'
import { useState, type ReactNode } from 'react'
import { Button } from '../ui/Button'
import { ErpTable } from './ErpTable'
import { ErrorState, LoadingState } from './AsyncState'

/**
 * 标准选择器插件（统一电子表格风格）：
 * 传入列定义、数据（或由调用方自行拉取后传入）、选择模式（single/multi）、行键，
 * 组件统一渲染「查询条 + ErpTable 网格 + 底部按钮」；multi 模式下内部维护勾选，
 * 确定时通过 onPick 回传所选行。与 document-workbench 的统一表单选择器同款观感。
 */
export function ErpChooserModal<T>({
  open,
  title,
  columns,
  data,
  getRowId,
  mode,
  onPick,
  onClose,
  searchText,
  onSearchChange,
  searchPlaceholder = '输入关键字搜索…',
  loading = false,
  error = null,
  onRetry,
  dialogSize = 'lg',
  extra,
  emptyText = '没有匹配的数据。',
  selectedKeys,
  onSelectedKeysChange,
}: {
  open: boolean
  title: string
  columns: ColumnDef<T, unknown>[]
  data: T[]
  getRowId: (row: T) => string
  mode: 'single' | 'multi'
  onPick: (rows: T[]) => void
  onClose: () => void
  searchText?: string
  onSearchChange?: (text: string) => void
  searchPlaceholder?: string
  loading?: boolean
  error?: string | null
  onRetry?: () => void
  dialogSize?: 'md' | 'lg'
  /** 网格下方附加内容（如已选顺序条） */
  extra?: ReactNode
  emptyText?: string
  /** 受控勾选（用于需要保持选择顺序/附加状态的场景）；不传则由组件内部维护 */
  selectedKeys?: Record<string, boolean>
  onSelectedKeysChange?: (selection: Record<string, boolean>) => void
}) {
  const [internalSelected, setInternalSelected] = useState<Record<string, boolean>>({})
  const selected = selectedKeys ?? internalSelected
  const setSelected = onSelectedKeysChange ?? setInternalSelected
  const [sorting, setSorting] = useState<SortingState>([])

  if (!open) return null

  const confirmMulti = () => {
    const picked = Object.keys(selected)
      .map((key) => data.find((row) => getRowId(row) === key))
      .filter((row): row is T => Boolean(row))
    if (picked.length > 0) onPick(picked)
  }

  return (
    <div className="modal modal-blur show d-block" role="dialog" aria-modal="true">
      <div className={`modal-dialog modal-dialog-centered ${dialogSize === 'lg' ? 'erp-dialog-lg' : 'erp-dialog-md'}`}>
        <div className="modal-content">
          <div className="modal-header">
            <h2 className="modal-title">{title}{mode === 'multi' ? '（可多选）' : ''}</h2>
            <button className="btn-close" aria-label="关闭" onClick={onClose} />
          </div>
          <div className="modal-body">
            {onSearchChange && (
              <div className="input-group mb-2">
                <input
                  className="form-control"
                  value={searchText ?? ''}
                  onChange={(event) => onSearchChange(event.target.value)}
                  placeholder={searchPlaceholder}
                  aria-label="搜索"
                />
              </div>
            )}
            {loading ? (
              <LoadingState label="正在加载…" />
            ) : error ? (
              <ErrorState message={error} onRetry={onRetry} />
            ) : (
              <>
                <div className="table-responsive" style={{ maxHeight: 360, overflow: 'auto' }}>
                  <ErpTable
                    columns={columns}
                    data={data}
                    getRowId={getRowId}
                    clientSideSorting
                    sorting={sorting}
                    onSortingChange={setSorting}
                    rowSelection={selected}
                    onRowSelectionChange={setSelected}
                    onRowClick={mode === 'single' ? (row) => onPick([row]) : (row) => {
                      const key = getRowId(row)
                      const next = { ...selected }
                      if (next[key]) delete next[key]
                      else next[key] = true
                      setSelected(next)
                    }}
                    onRowDoubleClick={mode === 'single' ? (row) => onPick([row]) : undefined}
                    resizable={false}
                    keyboardNavigation={false}
                    copyable={false}
                    responsive={false}
                    empty={<div className="text-secondary py-4 text-center">{emptyText}</div>}
                  />
                </div>
                <div className="d-flex justify-content-between align-items-center mt-2">
                  <span className="text-secondary small">共 {data.length} 条，{mode === 'single' ? '点击行选择' : '勾选后确定'}</span>
                  {mode === 'multi' && <span className="text-secondary small">已选 {Object.keys(selected).length} 项</span>}
                </div>
                {extra}
              </>
            )}
          </div>
          <div className="modal-footer">
            <Button onClick={onClose}>取消</Button>
            {mode === 'multi' && (
              <Button variant="primary" disabled={Object.keys(selected).length === 0} onClick={confirmMulti}>
                确定（已选 {Object.keys(selected).length} 项）
              </Button>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
