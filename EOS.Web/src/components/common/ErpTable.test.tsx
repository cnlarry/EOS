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

  it('列头菜单：升序/降序/默认触发 onSortingChange', () => {
    const onSortingChange = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onSortingChange={onSortingChange} />)
    fireEvent.click(screen.getByLabelText('表头操作名称'))
    fireEvent.click(screen.getByRole('button', { name: '升序' }))
    expect(onSortingChange).toHaveBeenCalledWith([{ id: 'name', desc: false }])
    fireEvent.click(screen.getByLabelText('表头操作名称'))
    fireEvent.click(screen.getByRole('button', { name: '降序' }))
    expect(onSortingChange).toHaveBeenCalledWith([{ id: 'name', desc: true }])
    fireEvent.click(screen.getByLabelText('表头操作名称'))
    fireEvent.click(screen.getByRole('button', { name: '默认' }))
    expect(onSortingChange).toHaveBeenCalledWith([])
  })

  it('不可排序且无菜单的列头不渲染表头操作按钮', () => {
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} />)
    expect(screen.queryByLabelText('表头操作ID')).not.toBeInTheDocument()
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

  it('表头输出列键与最小列宽', () => {
    const columns: ColumnDef<Row, unknown>[] = [
      {
        accessorKey: 'name',
        header: '名称',
        meta: { minWidth: 120 },
      },
    ]
    render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    const th = screen.getByText('名称').closest('th')!
    expect(th).toHaveAttribute('data-col-key', 'name')
    expect(th).toHaveAttribute('data-col-min-width', '120')
    expect(th).toHaveStyle({ minWidth: '120px' })
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

  it('鼠标点击行清除键盘焦点残留', () => {
    const { container } = render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} />)
    const shell = container.querySelector('.erp-table-shell')!
    fireEvent.keyDown(shell, { key: 'ArrowDown' })
    expect(container.querySelector('tr[data-kb-index="0"]')).toHaveClass('erp-row-focus')
    fireEvent.click(container.querySelector('tr[data-kb-index="1"]')!)
    expect(container.querySelector('tr[data-kb-index="0"]')).not.toHaveClass('erp-row-focus')
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

  it('列头筛选：菜单进入筛选弹层应用条件并标记激活', () => {
    const onColumnFilterChange = vi.fn()
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称', meta: { filterable: true } },
    ]
    const { rerender } = render(
      <ErpTable columns={columns} data={rows} getRowId={(row) => row.id} columnFilterValue={{}} onColumnFilterChange={onColumnFilterChange} />,
    )
    const trigger = screen.getByLabelText('表头操作名称')
    expect(trigger).not.toHaveClass('is-filtered')
    fireEvent.click(trigger)
    fireEvent.click(screen.getByRole('button', { name: '筛选' }))
    fireEvent.change(screen.getByLabelText('筛选运算符'), { target: { value: 'contains' } })
    fireEvent.change(screen.getByLabelText('筛选值'), { target: { value: 'A' } })
    fireEvent.click(screen.getByRole('button', { name: '应用' }))
    expect(onColumnFilterChange).toHaveBeenCalledWith('name', expect.objectContaining({ field: 'name', operator: 'contains', value: 'A' }))
    rerender(
      <ErpTable
        columns={columns}
        data={rows}
        getRowId={(row) => row.id}
        columnFilterValue={{ name: { field: 'name', operator: 'contains', value: 'A', valueTo: '', logic: 'and' } }}
        onColumnFilterChange={onColumnFilterChange}
      />,
    )
    expect(screen.getByLabelText('表头操作名称')).toHaveClass('is-filtered')
  })

  it('列头菜单支持自定义项（字段设置）', () => {
    const onFieldSettings = vi.fn()
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称', meta: { headerMenu: [{ label: '字段设置', onClick: onFieldSettings }] } },
    ]
    render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    fireEvent.click(screen.getByLabelText('表头操作名称'))
    fireEvent.click(screen.getByRole('button', { name: '字段设置' }))
    expect(onFieldSettings).toHaveBeenCalledTimes(1)
  })

  it('紧凑行高应用 erp-table-compact 类', () => {
    const { container } = render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} dense />)
    expect(container.querySelector('table')).toHaveClass('erp-table-compact')
  })

  it('单元格右键菜单：复制单元格与筛选等于', () => {
    const onColumnFilterChange = vi.fn()
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称', meta: { filterable: true } },
    ]
    const { container } = render(
      <ErpTable columns={columns} data={rows} getRowId={(row) => row.id} columnFilterValue={{}} onColumnFilterChange={onColumnFilterChange} />,
    )
    fireEvent.contextMenu(container.querySelector('tbody td')!)
    expect(screen.getByRole('button', { name: '复制单元格' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /筛选：等于/ }))
    expect(onColumnFilterChange).toHaveBeenCalledWith('name', expect.objectContaining({ field: 'name', operator: 'eq', value: 'A' }))
  })
})
