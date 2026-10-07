import type { ColumnDef, RowData } from '../../lib/tanstackTable'
import type { MouseEvent } from 'react'

/**
 * 首列单选列（选择器单选模式同款）：radio + 冻结首列。
 * 单选语义由外部维护 selectedKey（radio 只认勾选事件，避免浏览器对“失去选中”
 * 也触发 change）；点击 radio 不冒泡，行点击由页面自行决定。
 */
export function radioSelectColumn<T extends RowData>(name: string, selectedKey: string | null, onSelect: (rowId: string) => void): ColumnDef<T, unknown> {
  return {
    id: 'select',
    enableSorting: false,
    enableHiding: false,
    meta: { className: 'erp-select-column', resizable: false, frozenLeft: true, truncate: false },
    header: () => null,
    cell: ({ row }) => (
      <input
        className="form-check-input"
        type="radio"
        name={name}
        aria-label="选择此行"
        checked={row.id === selectedKey}
        onChange={(event) => { if (event.target.checked) onSelect(row.id) }}
        onClick={(event: MouseEvent<HTMLInputElement>) => event.stopPropagation()}
      />
    ),
  }
}
