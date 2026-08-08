import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ColumnDef } from '@tanstack/react-table'
import { describe, expect, it, vi } from 'vitest'
import { ErpTable } from './ErpTable'

interface Row {
  id: string
  name: string
}

const rows: Row[] = [
  { id: '1', name: 'A' },
  { id: '2', name: 'B' },
]

function buildColumns(): ColumnDef<Row, unknown>[] {
  return [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { className: 'erp-select-column' },
      header: ({ table }) => (
        <input
          type="checkbox"
          aria-label="选择当前页"
          checked={table.getIsAllPageRowsSelected()}
          onChange={table.getToggleAllPageRowsSelectedHandler()}
        />
      ),
      cell: ({ row }) => (
        <input
          type="checkbox"
          aria-label={`选择 ${row.original.name}`}
          checked={row.getIsSelected()}
          onChange={row.getToggleSelectedHandler()}
        />
      ),
    },
    { accessorKey: 'id', header: 'ID', enableSorting: false },
    { id: 'name', accessorKey: 'name', header: '名称' },
  ]
}

describe('ErpTable', () => {
  it('渲染表头与数据行', () => {
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} />)
    expect(screen.getByText('ID')).toBeInTheDocument()
    expect(screen.getByText('名称')).toBeInTheDocument()
    expect(screen.getByText('A')).toBeInTheDocument()
    expect(screen.getByText('B')).toBeInTheDocument()
  })

  it('点击可排序列头触发 onSortingChange（首击升序）', () => {
    const onSortingChange = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onSortingChange={onSortingChange} />)
    fireEvent.click(screen.getByRole('button', { name: '名称' }))
    expect(onSortingChange).toHaveBeenCalledWith([{ id: 'name', desc: false }])
  })

  it('不可排序的列头不渲染排序按钮', () => {
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} />)
    expect(screen.queryByRole('button', { name: 'ID' })).not.toBeInTheDocument()
  })

  it('行点击回调携带原始行数据', () => {
    const onRowClick = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onRowClick={onRowClick} />)
    fireEvent.click(screen.getByText('A').closest('tr')!)
    expect(onRowClick).toHaveBeenCalledWith(rows[0])
  })

  it('activeRowId 对应的行高亮', () => {
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} activeRowId="2" />)
    expect(screen.getByText('B').closest('tr')).toHaveClass('erp-row-active')
    expect(screen.getByText('A').closest('tr')).not.toHaveClass('erp-row-active')
  })

  it('行复选框触发行选择变更', () => {
    const onRowSelectionChange = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} rowSelection={{}} onRowSelectionChange={onRowSelectionChange} />)
    fireEvent.click(screen.getByLabelText('选择 A'))
    expect(onRowSelectionChange).toHaveBeenCalledWith({ '1': true })
  })

  it('表头复选框全选当前页', () => {
    const onRowSelectionChange = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} rowSelection={{}} onRowSelectionChange={onRowSelectionChange} />)
    fireEvent.click(screen.getByLabelText('选择当前页'))
    expect(onRowSelectionChange).toHaveBeenCalledWith({ '1': true, '2': true })
  })

  it('表头输出列键、最小列宽并支持右键回调', () => {
    const onHeaderContextMenu = vi.fn()
    const columns: ColumnDef<Row, unknown>[] = [
      {
        accessorKey: 'name',
        header: '名称',
        meta: { minWidth: 120, onHeaderContextMenu },
      },
    ]
    render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    const th = screen.getByText('名称').closest('th')!
    expect(th).toHaveAttribute('data-col-key', 'name')
    expect(th).toHaveAttribute('data-col-min-width', '120')
    expect(th).toHaveStyle({ minWidth: '120px' })
    fireEvent.contextMenu(th)
    expect(onHeaderContextMenu).toHaveBeenCalledTimes(1)
  })

  it('空数据时渲染 empty 插槽；传 null 时保留空表格', () => {
    const { unmount } = render(
      <ErpTable columns={buildColumns()} data={[]} getRowId={(row) => row.id} empty={<div>没有数据</div>} />,
    )
    expect(screen.getByText('没有数据')).toBeInTheDocument()
    expect(document.querySelector('thead')).not.toBeInTheDocument()
    unmount()

    render(<ErpTable columns={buildColumns()} data={[]} getRowId={(row) => row.id} empty={null} />)
    expect(document.querySelector('thead')).toBeInTheDocument()
  })

  it('开启 resizable 时注入 colgroup', async () => {
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} resizable storageKey="erp-table-test" />,
    )
    await waitFor(() => expect(container.querySelector('table colgroup')).toBeInTheDocument())
  })

  it('键盘导航：方向键移动活动行，Enter 触发行点击', () => {
    const onRowClick = vi.fn()
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onRowClick={onRowClick} />,
    )
    const shell = container.querySelector('.erp-table-shell')!
    fireEvent.keyDown(shell, { key: 'ArrowDown' })
    expect(onRowClick).toHaveBeenLastCalledWith(rows[0])
    fireEvent.keyDown(shell, { key: 'ArrowDown' })
    expect(onRowClick).toHaveBeenLastCalledWith(rows[1])
    fireEvent.keyDown(shell, { key: 'Enter' })
    expect(onRowClick).toHaveBeenLastCalledWith(rows[1])
    fireEvent.keyDown(shell, { key: 'Home' })
    expect(onRowClick).toHaveBeenLastCalledWith(rows[0])
  })

  it('冻结列应用 sticky 类与偏移', () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'id', header: 'ID', meta: { frozenLeft: true } },
      { accessorKey: 'name', header: '名称' },
    ]
    const { container } = render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    expect(container.querySelector('thead th')).toHaveClass('erp-frozen-left')
    expect(container.querySelector('thead th')).toHaveStyle({ left: '0px' })
    expect(container.querySelector('tbody tr td')).toHaveClass('erp-frozen-left')
  })

  it('列头筛选：打开弹层应用条件并标记激活', () => {
    const onColumnFilterChange = vi.fn()
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称', meta: { filterable: true } },
    ]
    const { rerender } = render(
      <ErpTable columns={columns} data={rows} getRowId={(row) => row.id} columnFilterValue={{}} onColumnFilterChange={onColumnFilterChange} />,
    )
    const trigger = screen.getByLabelText('筛选名称')
    expect(trigger).not.toHaveClass('is-active')
    fireEvent.click(trigger)
    fireEvent.change(screen.getByLabelText('筛选运算符'), { target: { value: 'contains' } })
    fireEvent.change(screen.getByLabelText('筛选值'), { target: { value: 'A' } })
    fireEvent.click(screen.getByRole('button', { name: '应用' }))
    expect(onColumnFilterChange).toHaveBeenCalledWith('name', expect.objectContaining({ operator: 'contains', value: 'A' }))
    rerender(
      <ErpTable
        columns={columns}
        data={rows}
        getRowId={(row) => row.id}
        columnFilterValue={{ name: { field: 'name', operator: 'contains', value: 'A', valueTo: '', logic: 'and' } }}
        onColumnFilterChange={onColumnFilterChange}
      />,
    )
    expect(screen.getByLabelText('筛选名称')).toHaveClass('is-active')
  })
})
