import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErpTable } from './ErpTable'

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
