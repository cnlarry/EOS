import type { ColumnDef, RowData } from '../../lib/tanstackTable'

/**
 * 列表首列复选框（工作表头全选 / 行选择）：与 `radioSelectColumn` 同形，只是多选语义。
 *
 * 为什么要抽出来：所有列表的第一列都是这一列，先前它在各页手抄（工作台的版本还带
 * `indeterminate` 半选态）——抄漏一处就表现为"勾得上但全选不生效"或首列不再冻结。
 * 冻结与宽度口径 (`erp-select-column` / `frozenLeft` / 不可拖宽) 随本函数一处定义。
 */
export function checkboxSelectColumn<T extends RowData>(): ColumnDef<T, unknown> {
  return {
    id: 'select',
    enableSorting: false,
    enableHiding: false,
    meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false },
    header: ({ table }) => (
      <input
        className="form-check-input"
        type="checkbox"
        aria-label="选择当前页"
        checked={table.getIsAllPageRowsSelected()}
        ref={(input) => { if (input) input.indeterminate = table.getIsSomePageRowsSelected() }}
        onChange={table.getToggleAllPageRowsSelectedHandler()}
      />
    ),
    cell: ({ row }) => (
      <input
        className="form-check-input"
        type="checkbox"
        aria-label="选择此行"
        checked={row.getIsSelected()}
        onChange={row.getToggleSelectedHandler()}
        onClick={(event) => event.stopPropagation()}
      />
    ),
  }
}
