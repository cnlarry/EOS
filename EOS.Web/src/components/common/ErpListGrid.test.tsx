import { fireEvent, render, screen } from '@testing-library/react'
import type { ColumnDef } from '../../lib/tanstackTable'
import { describe, expect, it, vi } from 'vitest'
import { ErpListGrid } from './ErpListGrid'

interface Row {
  id: string
  name: string
  amount: number
}

const rows: Row[] = [
  { id: '1', name: 'A', amount: 10 },
  { id: '2', name: 'B', amount: 25.5 },
]

function buildColumns(): ColumnDef<Row, unknown>[] {
  return [
    { accessorKey: 'id', header: '编号' },
    { accessorKey: 'name', header: '名称' },
    { accessorKey: 'amount', header: '金额' },
  ]
}

describe('ErpListGrid', () => {
  it('渲染卡片、命令栏与表格', () => {
    const { container } = render(
      <ErpListGrid
        listId="demo"
        columns={buildColumns()}
        rows={rows}
        rowKey={(row) => row.id}
        search={<input aria-label="搜索" />}
        actions={<button type="button">新增</button>}
      />,
    )
    expect(container.querySelector('.erp-list-card')).not.toBeNull()
    expect(screen.getByLabelText('搜索')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '新增' })).toBeInTheDocument()
    expect(screen.getByText('A')).toBeInTheDocument()
  })

  it('首载渲染加载态，出错渲染错误态并可重试', () => {
    const onRetry = vi.fn()
    const { rerender } = render(<ErpListGrid listId="demo" columns={buildColumns()} rows={undefined} loading loadingLabel="正在加载用户…" />)
    expect(screen.getByText('正在加载用户…')).toBeInTheDocument()
    rerender(<ErpListGrid listId="demo" columns={buildColumns()} rows={undefined} error="挂了" onRetry={onRetry} />)
    fireEvent.click(screen.getByRole('button', { name: '重新加载' }))
    expect(onRetry).toHaveBeenCalled()
  })

  it('给分页参数就渲染标准分页脚，点击翻页回调页码', () => {
    const onPageChange = vi.fn()
    render(
      <ErpListGrid
        listId="demo"
        columns={buildColumns()}
        rows={rows}
        rowKey={(row) => row.id}
        pagination={{ page: 1, pageSize: 20, total: 45, onPageChange }}
      />,
    )
    expect(screen.getByText('共 45 条，第 1/3 页')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '下一页' }))
    expect(onPageChange).toHaveBeenCalledWith(2)
  })

  it('配了选择态时点行即单选，选择回调收到纯值', () => {
    const onChange = vi.fn()
    render(
      <ErpListGrid
        listId="demo"
        columns={buildColumns()}
        rows={rows}
        rowKey={(row) => row.id}
        selection={{ value: {}, onChange }}
      />,
    )
    fireEvent.click(screen.getByText('B'))
    expect(onChange).toHaveBeenCalledWith({ '2': true })
  })

  it('tableProps 透传高级开关（区域选择）且不覆盖表本身', () => {
    const { container } = render(
      <ErpListGrid
        listId="demo"
        columns={buildColumns()}
        rows={rows}
        rowKey={(row) => row.id}
        tableProps={{ cellSelection: true, onClearRange: vi.fn() }}
      />,
    )
    const firstRow = container.querySelectorAll('tbody tr')[0]!.querySelectorAll('td')
    fireEvent.mouseDown(firstRow[0] as HTMLElement, { button: 0 })
    fireEvent.mouseUp(window)
    expect(container.querySelectorAll('td.erp-cell-range')).toHaveLength(1)
  })

  it('plain 形态不渲染卡片外壳（弹窗/面板内嵌用）', () => {
    const { container } = render(
      <ErpListGrid plain listId="demo" columns={buildColumns()} rows={rows} rowKey={(row) => row.id} />,
    )
    expect(container.querySelector('.erp-list-card')).toBeNull()
    expect(screen.getByText('A')).toBeInTheDocument()
  })
})
