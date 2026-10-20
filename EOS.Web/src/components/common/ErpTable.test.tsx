import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErpTable, computeVirtualWindow, parseNumericCell } from './ErpTable'

interface Row {
  id: string
  name: string
}

const rows: Row[] = [
  { id: '1', name: 'A' },
  { id: '2', name: 'B' },
]

class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = []
  callback: IntersectionObserverCallback

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback
    FakeIntersectionObserver.instances.push(this)
  }

  observe = vi.fn()
  unobserve = vi.fn()
  disconnect = vi.fn()

  trigger(entries: IntersectionObserverEntry[]) {
    this.callback(entries, this as unknown as IntersectionObserver)
  }
}

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
  beforeEach(() => {
    FakeIntersectionObserver.instances = []
  })

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

  it('量值列默认右对齐，整型与显式对齐不被覆盖', () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { id: 'amount', accessorFn: () => '1,200.00', header: '金额', meta: { dataType: 'decimal' } },
      { id: 'code', accessorFn: () => 'C001', header: '编号', meta: { dataType: 'int' } },
      { id: 'price', accessorFn: () => '9.00', header: '单价', meta: { dataType: 'decimal', cellClassName: 'text-start' } },
    ]
    render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)

    expect(screen.getByText('金额').closest('th')).toHaveClass('text-end')
    expect(screen.getByText('编号').closest('th')).not.toHaveClass('text-end')
    expect(screen.getAllByText('1,200.00')[0].closest('td')).toHaveClass('text-end')
    expect(screen.getAllByText('C001')[0].closest('td')).not.toHaveClass('text-end')
    expect(screen.getAllByText('9.00')[0].closest('td')).toHaveClass('text-start')
    expect(screen.getAllByText('9.00')[0].closest('td')).not.toHaveClass('text-end')
  })

  it('虚拟化滚到底时窗口收到末行，不留底部占位', () => {
    const total = 500
    const height = 28
    const viewport = 600
    // 含 36px 粘性表头
    const scrollHeight = total * height + 36
    const bottom = scrollHeight - viewport

    expect(computeVirtualWindow(bottom, viewport, scrollHeight, total, height).end).toBe(total)
    // 行高估值偏大时按公式会少算几行，到底仍必须收到末行
    expect(computeVirtualWindow(bottom, viewport, scrollHeight, total, height + 2).end).toBe(total)
    // 中途只渲染可视区 + 缓冲，不能退化成全量
    const middle = computeVirtualWindow(1200, viewport, scrollHeight, total, height)
    expect(middle.start).toBeGreaterThan(0)
    expect(middle.end).toBeLessThan(total)
    // 顶部不产生上方占位
    expect(computeVirtualWindow(0, viewport, scrollHeight, total, height).start).toBe(0)
  })

  it('后台刷新时保留旧行并标记 is-loading', () => {
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} loading />,
    )
    expect(container.querySelector('.erp-table-shell')).toHaveClass('is-loading')
    expect(screen.getByText('A')).toBeInTheDocument()
    expect(screen.getByText('B')).toBeInTheDocument()
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

  it('冻结列 sticky 偏移按列宽累计：选择列 33px 后接业务列不重叠', () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { id: 'select', header: '', enableSorting: false, enableHiding: false, meta: { className: 'erp-select-column', frozenLeft: true, resizable: false, truncate: false }, cell: () => null },
      { accessorKey: 'id', header: 'ID', enableSorting: false, meta: { frozenLeft: true, resizable: false, minWidth: 90 } },
      { accessorKey: 'name', header: '名称' },
    ]
    const { container } = render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    const ths = Array.from(container.querySelectorAll('thead th'))
    expect(ths[0]).toHaveStyle({ left: '0px' })
    expect(ths[1]).toHaveStyle({ left: '33px' })
    const tds = Array.from(container.querySelectorAll('tbody tr td'))
    expect(tds[0]).toHaveStyle({ left: '0px' })
    expect(tds[1]).toHaveStyle({ left: '33px' })
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

  it('表头拖拽重排列顺序', () => {
    const onColumnsReorder = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onColumnsReorder={onColumnsReorder} />)
    const labels = Array.from(document.querySelectorAll('.erp-header-label')) as HTMLElement[]
    const nameLabel = labels.find((el) => el.textContent === '名称')!
    const idLabel = labels.find((el) => el.textContent === 'ID')!
    fireEvent.dragStart(nameLabel)
    fireEvent.dragOver(idLabel)
    fireEvent.drop(idLabel)
    expect(onColumnsReorder).toHaveBeenCalledWith(['select', 'name', 'id'])
  })

  it('bit 单元格右键筛选使用 1/0 值', () => {
    const onColumnFilterChange = vi.fn()
    const bitRows = [
      { id: '1', name: 'A', flag: true },
      { id: '2', name: 'B', flag: false },
    ]
    const columns: ColumnDef<typeof bitRows[number], unknown>[] = [
      { accessorKey: 'flag', header: '标志', meta: { filterable: true, dataType: 'bit' } },
    ]
    const { container } = render(
      <ErpTable columns={columns} data={bitRows} getRowId={(row) => row.id} columnFilterValue={{}} onColumnFilterChange={onColumnFilterChange} />,
    )
    fireEvent.contextMenu(container.querySelector('tbody td')!)
    fireEvent.click(screen.getByRole('button', { name: '筛选：等于（选中）' }))
    expect(onColumnFilterChange).toHaveBeenCalledWith('flag', expect.objectContaining({ field: 'flag', operator: 'eq', value: '1' }))
  })

  it('非可调列不渲染拖拽手柄', async () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'id', header: 'ID', meta: { resizable: false } },
      { accessorKey: 'name', header: '名称' },
    ]
    const { container } = render(
      <ErpTable columns={columns} data={rows} getRowId={(row) => row.id} resizable storageKey="erp-resize-test" />,
    )
    await waitFor(() => expect(container.querySelectorAll('.erp-col-resizer').length).toBeGreaterThan(0))
    const idTh = container.querySelector('thead th')!
    expect(idTh).toHaveAttribute('data-col-resizable', 'false')
    expect(idTh.querySelector('.erp-col-resizer')).not.toBeInTheDocument()
    const nameTh = container.querySelectorAll('thead th')[1]
    expect(nameTh.querySelector('.erp-col-resizer')).toBeInTheDocument()
  })

  it('autoWidth 列（操作列）标记 data-col-autofit 并同样带拖拽手柄', async () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称' },
      {
        id: 'actions',
        header: '操作',
        enableSorting: false,
        meta: { frozenRight: true, truncate: false, autoWidth: true },
        cell: () => <button type="button">编辑</button>,
      },
    ]
    const { container } = render(
      <ErpTable columns={columns} data={rows} getRowId={(row) => row.id} resizable storageKey="erp-autofit-test" />,
    )
    await waitFor(() => expect(container.querySelectorAll('.erp-col-resizer').length).toBeGreaterThan(0))
    const actionsTh = container.querySelectorAll('thead th')[1]!
    expect(actionsTh).toHaveAttribute('data-col-autofit', 'true')
    expect(actionsTh.querySelector('.erp-col-resizer')).toBeInTheDocument()
  })

  it('clientSideSorting 使用 TanStack 内置排序', () => {
    const data = [
      { id: '1', name: 'B' },
      { id: '2', name: 'A' },
    ]
    const columns: ColumnDef<Row, unknown>[] = [{ accessorKey: 'name', header: '名称' }]
    const { rerender } = render(
      <ErpTable
        columns={columns}
        data={data}
        getRowId={(row) => row.id}
        clientSideSorting
        sorting={[{ id: 'name', desc: false }]}
      />,
    )
    expect(Array.from(document.querySelectorAll('tbody tr td')).map((td) => td.textContent)).toEqual(['A', 'B'])
    rerender(
      <ErpTable
        columns={columns}
        data={data}
        getRowId={(row) => row.id}
        clientSideSorting
        sorting={[{ id: 'name', desc: true }]}
      />,
    )
    expect(Array.from(document.querySelectorAll('tbody tr td')).map((td) => td.textContent)).toEqual(['B', 'A'])
  })

  it('clientSideSorting 未受控时表头排序走内部状态且不循环', () => {
    const data = [
      { id: '1', name: 'B' },
      { id: '2', name: 'A' },
    ]
    const columns: ColumnDef<Row, unknown>[] = [{ accessorKey: 'name', header: '名称' }]
    render(<ErpTable columns={columns} data={data} getRowId={(row) => row.id} clientSideSorting />)
    fireEvent.click(screen.getByRole('button', { name: '表头操作名称' }))
    fireEvent.click(screen.getByRole('button', { name: '升序' }))
    expect(Array.from(document.querySelectorAll('tbody tr td')).map((td) => td.textContent)).toEqual(['A', 'B'])
    // 再切一次确认内部状态可继续更新（未受控路径不卡死）
    fireEvent.click(screen.getByRole('button', { name: '表头操作名称' }))
    fireEvent.click(screen.getByRole('button', { name: '降序' }))
    expect(Array.from(document.querySelectorAll('tbody tr td')).map((td) => td.textContent)).toEqual(['B', 'A'])
  })

  it('rowClassName 应用到行类名', () => {
    render(
      <ErpTable
        columns={buildColumns()}
        data={rows}
        getRowId={(row) => row.id}
        rowClassName={(row) => row.id === '2' ? 'custom-row' : undefined}
      />,
    )
    expect(screen.getByText('B').closest('tr')).toHaveClass('custom-row')
    expect(screen.getByText('A').closest('tr')).not.toHaveClass('custom-row')
  })

  it('行双击回调携带原始行数据', () => {
    const onRowDoubleClick = vi.fn()
    render(<ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onRowDoubleClick={onRowDoubleClick} />)
    fireEvent.doubleClick(screen.getByText('A').closest('tr')!)
    expect(onRowDoubleClick).toHaveBeenCalledWith(rows[0])
  })

  it('responsive=false 时不外包 table-responsive', () => {
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} responsive={false} />,
    )
    expect(container.querySelector('.table-responsive')).not.toBeInTheDocument()
    expect(container.querySelector('table')?.parentElement).toHaveClass('erp-table-shell')
  })

  it('getRowId 撞键时按出现顺序补后缀：两行都渲染且不产生重复 React 键', () => {
    const duplicated: Row[] = [
      { id: 'same', name: 'A' },
      { id: 'same', name: 'B' },
    ]
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const consoleWarn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    const { container } = render(
      <ErpTable columns={buildColumns()} data={duplicated} getRowId={(row) => row.id} />,
    )
    const ids = Array.from(container.querySelectorAll('tbody tr[data-order-id]'))
      .map((row) => row.getAttribute('data-order-id'))
    expect(ids).toEqual(['same', 'same#2'])
    expect(consoleError.mock.calls.filter((args) => String(args[0]).includes('same key'))).toHaveLength(0)
    // 开发期把撞键的调用方点出来（表格仍正常工作，但页面自己的选择态只认第一行）
    expect(consoleWarn.mock.calls.some((args) => String(args[0]).includes('getRowId'))).toBe(true)
    consoleError.mockRestore()
    consoleWarn.mockRestore()
  })

  it('virtualize 开启但容器不可滚（无布局环境）时回退全量渲染', () => {
    const many = Array.from({ length: 120 }, (_, i) => ({ id: String(i), name: `行${i}` }))
    const { container } = render(<ErpTable columns={buildColumns()} data={many} getRowId={(row) => row.id} virtualize />)
    expect(container.querySelectorAll('tbody tr[data-order-id]').length).toBe(120)
    expect(container.querySelector('tbody .erp-virtual-spacer')).not.toBeInTheDocument()
  })

  it('默认单元格包裹省略层，title 悬停时延迟计算全文', () => {
    const columns: ColumnDef<Row, unknown>[] = [{ accessorKey: 'name', header: '名称' }]
    const { container } = render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    const cell = container.querySelector('tbody td')!
    expect(cell.querySelector('.erp-cell-ellipsis')).toBeInTheDocument()
    // 渲染热路径不预置 title，悬停时才计算
    expect(cell).not.toHaveAttribute('title')
    fireEvent.mouseEnter(cell)
    expect(cell).toHaveAttribute('title', 'A')
    expect(cell.textContent).toBe('A')
  })

  it('truncate=false 的列不包裹省略层，悬停也无 title', () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称', meta: { truncate: false } },
    ]
    const { container } = render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    const cell = container.querySelector('tbody td')!
    expect(cell.querySelector('.erp-cell-ellipsis')).not.toBeInTheDocument()
    fireEvent.mouseEnter(cell)
    expect(cell).not.toHaveAttribute('title')
  })

  it('meta.title 函数在悬停时用于全文（格式化显示）', () => {
    const columns: ColumnDef<Row, unknown>[] = [
      { accessorKey: 'name', header: '名称', meta: { title: ({ value }) => `格式化:${String(value)}` } },
    ]
    const { container } = render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} />)
    const cell = container.querySelector('tbody td')!
    expect(cell).not.toHaveAttribute('title')
    fireEvent.mouseEnter(cell)
    expect(cell).toHaveAttribute('title', '格式化:A')
  })

  it('滚动到底触发 onEndReached，加载中不重复触发', () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
    try {
      const onEndReached = vi.fn()
      const { rerender } = render(
        <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onEndReached={onEndReached} hasMore />,
      )
      const observer = FakeIntersectionObserver.instances.at(-1)!
      expect(observer.observe).toHaveBeenCalled()
      act(() => observer.trigger([{ isIntersecting: true } as IntersectionObserverEntry]))
      expect(onEndReached).toHaveBeenCalledTimes(1)
      // 加载更多进行中再次触底不重复请求
      rerender(
        <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onEndReached={onEndReached} hasMore loadingMore />,
      )
      act(() => observer.trigger([{ isIntersecting: true } as IntersectionObserverEntry]))
      expect(onEndReached).toHaveBeenCalledTimes(1)
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('已加载全部仅在滚动尝试时短暂显示，随后消失', () => {
    vi.useFakeTimers()
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver)
    try {
      render(
        <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} onEndReached={vi.fn()} hasMore={false} />,
      )
      // 保留滚动监听用于识别“再次滚动到底”的尝试
      expect(FakeIntersectionObserver.instances).toHaveLength(1)
      expect(screen.queryByText('已加载全部')).not.toBeInTheDocument()
      const observer = FakeIntersectionObserver.instances.at(-1)!
      // 首次触底（挂载时哨兵已可见）不提示
      act(() => observer.trigger([{ isIntersecting: true } as IntersectionObserverEntry]))
      expect(screen.queryByText('已加载全部')).not.toBeInTheDocument()
      // 再次滚动尝试到底 → 短暂提示
      act(() => observer.trigger([{ isIntersecting: true } as IntersectionObserverEntry]))
      expect(screen.getByText('已加载全部')).toBeInTheDocument()
      // 随后自动消失
      act(() => { vi.advanceTimersByTime(1600) })
      expect(screen.queryByText('已加载全部')).not.toBeInTheDocument()
    } finally {
      vi.useRealTimers()
      vi.unstubAllGlobals()
    }
  })
})

describe('ErpTable 渲染开销', () => {
  it('打开表头菜单不重渲染数据行（行级 memo）', () => {
    let cellRenders = 0
    function NameCell() {
      cellRenders += 1
      return <span>名称</span>
    }
    // 列与数据必须是稳定身份——这既是本用例的前提，也是真实页面的要求（useMemo 包住列定义）
    const columns: ColumnDef<Row, unknown>[] = [
      { id: 'name', accessorKey: 'name', header: '名称', cell: NameCell },
    ]
    const { container } = render(<ErpTable columns={columns} data={rows} getRowId={(row) => row.id} onSortingChange={vi.fn()} />)
    const before = cellRenders
    expect(before).toBeGreaterThan(0)
    fireEvent.click(screen.getByLabelText('表头操作名称'))
    // 菜单是表格自身的状态：只有表头该重画，数据行的 props 没变 ⇒ memo 应当整体跳过
    expect(cellRenders).toBe(before)
    expect(container.querySelectorAll('tbody tr')).toHaveLength(2)
  })
})

/** 可录入的列：用来验证"输入框里的方向键不被表格抢走" */
function buildEditableColumns(): ColumnDef<Row, unknown>[] {
  return [
    { id: 'id', accessorKey: 'id', header: 'ID', enableSorting: false },
    {
      id: 'name',
      accessorKey: 'name',
      header: '名称',
      enableSorting: false,
      cell: ({ row }) => <input aria-label={`名称 ${row.id}`} className="form-control" defaultValue={row.original.name} />,
    },
  ]
}

function stubClipboard(): ReturnType<typeof vi.fn> {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
  return writeText
}

/** 在 td 上按住鼠标拖到另一格：起选区（jsdom 没有真实布局，按元素事件驱动） */
function dragRange(from: HTMLElement, to: HTMLElement) {
  fireEvent.mouseDown(from, { button: 0 })
  fireEvent.mouseEnter(to)
  fireEvent.mouseUp(window)
}

describe('ErpTable 单元格区域选择', () => {
  it('编辑控件内的方向键与 Ctrl+C 不被表格接管', () => {
    const onRowClick = vi.fn()
    const writeText = stubClipboard()
    const { container } = render(
      <ErpTable columns={buildEditableColumns()} data={rows} getRowId={(row) => row.id} onRowClick={onRowClick} cellSelection />,
    )
    const input = screen.getByLabelText('名称 1')
    fireEvent.keyDown(input, { key: 'ArrowDown' })
    // 表格的行导航不应被触发（否则在格子里按方向键会挪走活动行）
    expect(onRowClick).not.toHaveBeenCalled()
    fireEvent.keyDown(input, { key: 'c', ctrlKey: true })
    expect(writeText).not.toHaveBeenCalled()
    expect(container.querySelector('.erp-table-shell')).toBeInTheDocument()
  })

  it('拖选成块后 Ctrl+C 复制纯块（不带表头）', () => {
    const writeText = stubClipboard()
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} cellSelection />,
    )
    const cells = container.querySelectorAll('tbody tr')
    const firstRow = cells[0]!.querySelectorAll('td')
    const secondRow = cells[1]!.querySelectorAll('td')
    // 第一列是勾选列，不参与区域选择；从 ID 列拖到名称列
    dragRange(firstRow[1] as HTMLElement, secondRow[2] as HTMLElement)
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(4)
    fireEvent.keyDown(container.querySelector('.erp-table-shell')!, { key: 'c', ctrlKey: true })
    expect(writeText).toHaveBeenCalledWith('1\tA\r\n2\tB')
  })

  it('有选区时 Delete 清空、Ctrl+V 按块粘贴', () => {
    const onClearRange = vi.fn()
    const onPasteRange = vi.fn()
    const { container } = render(
      <ErpTable
        columns={buildColumns()}
        data={rows}
        getRowId={(row) => row.id}
        cellSelection
        onClearRange={onClearRange}
        onPasteRange={onPasteRange}
      />,
    )
    const rowsEls = container.querySelectorAll('tbody tr')
    dragRange(rowsEls[0]!.querySelectorAll('td')[1] as HTMLElement, rowsEls[1]!.querySelectorAll('td')[2] as HTMLElement)
    const shell = container.querySelector('.erp-table-shell')!
    fireEvent.keyDown(shell, { key: 'Delete' })
    expect(onClearRange).toHaveBeenCalledWith({ rowIndices: [0, 1], columns: ['id', 'name'] })
    fireEvent.paste(shell, { clipboardData: { getData: () => 'X\tY\r\nZ\tW' } })
    expect(onPasteRange).toHaveBeenCalledWith({
      startRowIndex: 0,
      columns: ['id', 'name'],
      rows: [['X', 'Y'], ['Z', 'W']],
    })
  })

  it('方向键在选区内移动并扩选，Escape 取消选区', () => {
    const { container } = render(
      // 带区域动作的表格：单击一格即落一个活动格（纯浏览列表的单击是选行，见上一条用例）
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} cellSelection onClearRange={vi.fn()} />,
    )
    const rowsEls = container.querySelectorAll('tbody tr')
    const firstRow = rowsEls[0]!.querySelectorAll('td')
    fireEvent.mouseDown(firstRow[1] as HTMLElement, { button: 0 })
    fireEvent.mouseUp(window)
    const shell = container.querySelector('.erp-table-shell')!
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(1)
    fireEvent.keyDown(shell, { key: 'ArrowRight', shiftKey: true })
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(2)
    fireEvent.keyDown(shell, { key: 'ArrowDown' })
    // 不按 Shift：整块跟到下一行同一列，仍是一格
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(1)
    fireEvent.keyDown(shell, { key: 'Escape' })
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(0)
  })

  it('纯浏览列表单击一格不留下选区（单击仍是选行）', () => {
    const onRowClick = vi.fn()
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} cellSelection onRowClick={onRowClick} />,
    )
    const firstRow = container.querySelectorAll('tbody tr')[0]!.querySelectorAll('td')
    fireEvent.mouseDown(firstRow[1] as HTMLElement, { button: 0 })
    fireEvent.mouseUp(window)
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(0)
    // 键盘仍是行导航
    fireEvent.keyDown(container.querySelector('.erp-table-shell')!, { key: 'ArrowDown' })
    expect(onRowClick).toHaveBeenCalledWith(rows[0])
  })

  it('Home/End 落到第一个/最后一个可选列，Ctrl+方向键到边界（勾选列不参与）', () => {
    const { container } = render(
      <ErpTable columns={buildColumns()} data={rows} getRowId={(row) => row.id} cellSelection onClearRange={vi.fn()} />,
    )
    const shell = container.querySelector('.erp-table-shell')!
    const classNameAt = (row: number, col: number) => container.querySelectorAll('tbody tr')[row]!.querySelectorAll('td')[col]!.className
    // 落在名称列（第 2 个数据列，td 下标 2）
    fireEvent.mouseDown(container.querySelectorAll('tbody tr')[0]!.querySelectorAll('td')[2] as HTMLElement, { button: 0 })
    fireEvent.mouseUp(window)
    fireEvent.keyDown(shell, { key: 'Home' })
    expect(classNameAt(0, 1)).toContain('erp-cell-range')
    expect(classNameAt(0, 0)).not.toContain('erp-cell-range')
    fireEvent.keyDown(shell, { key: 'End' })
    expect(classNameAt(0, 2)).toContain('erp-cell-range')
    // Ctrl+Shift+下：从末列扩到末行
    fireEvent.keyDown(shell, { key: 'ArrowDown', ctrlKey: true, shiftKey: true })
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(2)
    // Ctrl+Home：回到首格（单格）
    fireEvent.keyDown(shell, { key: 'Home', ctrlKey: true })
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(1)
    expect(classNameAt(0, 1)).toContain('erp-cell-range')
  })

  it('Ctrl+A 全选行（只在配了选择回调的表格上生效）', () => {
    const onSelection = vi.fn()
    const { container } = render(
      <ErpTable
        columns={buildColumns()}
        data={rows}
        getRowId={(row) => row.id}
        rowSelection={{}}
        onRowSelectionChange={onSelection}
      />,
    )
    fireEvent.keyDown(container.querySelector('.erp-table-shell')!, { key: 'a', ctrlKey: true })
    expect(onSelection).toHaveBeenCalledWith({ '1': true, '2': true })
  })

  it('选区数值摘要：合计/平均/最值，清空选区后回调 null', () => {
    const onSummary = vi.fn()
    const data = [{ id: '1', amount: 10 }, { id: '2', amount: 25.5 }]
    const numericColumns: ColumnDef<(typeof data)[number], unknown>[] = [
      { accessorKey: 'id', header: 'ID' },
      { accessorKey: 'amount', header: '金额' },
    ]
    const { container } = render(
      <ErpTable
        columns={numericColumns}
        data={data}
        getRowId={(row) => row.id}
        cellSelection
        onClearRange={vi.fn()}
        onSelectionSummaryChange={onSummary}
      />,
    )
    const rowsEls = container.querySelectorAll('tbody tr')
    dragRange(rowsEls[0]!.querySelectorAll('td')[1] as HTMLElement, rowsEls[1]!.querySelectorAll('td')[1] as HTMLElement)
    const summary = onSummary.mock.calls[onSummary.mock.calls.length - 1]?.[0]
    expect(summary).toMatchObject({ cells: 2, numbers: 2, sum: 35.5, average: 17.75, min: 10, max: 25.5 })
    fireEvent.keyDown(container.querySelector('.erp-table-shell')!, { key: 'Escape' })
    expect(onSummary.mock.calls[onSummary.mock.calls.length - 1]?.[0]).toBeNull()
  })

  it('parseNumericCell 只认纯数字（千分位/货币符号可去，日期与编码不算）', () => {
    expect(parseNumericCell(12.5)).toBe(12.5)
    expect(parseNumericCell('1,234.50')).toBe(1234.5)
    expect(parseNumericCell(' ¥ 88 ')).toBe(88)
    expect(parseNumericCell('-3')).toBe(-3)
    expect(parseNumericCell('2026-10-20')).toBeNull()
    expect(parseNumericCell('A-01')).toBeNull()
    expect(parseNumericCell(true)).toBeNull()
    expect(parseNumericCell('')).toBeNull()
  })

  it('scrollResetKey 变化时把列表滚回顶部（排序/筛选换批后不留在中段）', () => {
    const props = { columns: buildColumns(), data: rows, getRowId: (row: Row) => row.id }
    const { container, rerender } = render(<ErpTable {...props} scrollResetKey="k1" />)
    const shell = container.querySelector<HTMLElement>('.erp-table-shell')!
    shell.scrollTop = 320
    rerender(<ErpTable {...props} scrollResetKey="k2" />)
    expect(shell.scrollTop).toBe(0)
  })
})
