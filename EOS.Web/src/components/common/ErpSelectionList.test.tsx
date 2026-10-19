import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { ErpSelectionList, type ErpSelectionItem } from './ErpSelectionList'

const items: ErpSelectionItem[] = [
  { id: 'PRO_NO', label: 'PRO_NO', description: '产品编号', monospace: true, meta: '主键' },
  { id: 'QTY', label: 'QTY', description: '数量', monospace: true },
]

describe('ErpSelectionList', () => {
  it('渲染主行、次行与右侧附加信息，选中项带 is-active 与 aria-current', () => {
    render(<ErpSelectionList ariaLabel="字段列表" items={items} activeId="PRO_NO" onSelect={() => {}} />)

    const active = screen.getByRole('button', { name: /PRO_NO/ })
    expect(active).toHaveClass('is-active')
    expect(active).toHaveAttribute('aria-current', 'true')
    expect(screen.getByRole('button', { name: /QTY/ })).not.toHaveClass('is-active')
  })

  it('点击条目把它的 id 交给调用方（选中态由调用方决定）', () => {
    const onSelect = vi.fn()
    render(<ErpSelectionList ariaLabel="字段列表" items={items} activeId="PRO_NO" onSelect={onSelect} />)

    fireEvent.click(screen.getByRole('button', { name: /QTY/ }))
    expect(onSelect).toHaveBeenCalledWith('QTY')
  })

  it('加载中显示加载文案，不显示清单', () => {
    render(
      <ErpSelectionList ariaLabel="字段列表" items={[]} loading loadingLabel="加载字段…" onSelect={() => {}} />,
    )

    expect(screen.getByText('加载字段…')).toBeInTheDocument()
    expect(screen.queryByRole('list')).not.toBeInTheDocument()
  })

  it('空清单显示空态；有错误时错误节点优先', () => {
    const { rerender } = render(
      <ErpSelectionList
        ariaLabel="字段列表"
        items={[]}
        empty={<div>这张表还没有字段。</div>}
        onSelect={() => {}}
      />,
    )
    expect(screen.getByText('这张表还没有字段。')).toBeInTheDocument()

    rerender(
      <ErpSelectionList
        ariaLabel="字段列表"
        items={[]}
        error={<div role="alert">加载失败</div>}
        empty={<div>这张表还没有字段。</div>}
        onSelect={() => {}}
      />,
    )
    expect(screen.getByRole('alert')).toHaveTextContent('加载失败')
    expect(screen.queryByText('这张表还没有字段。')).not.toBeInTheDocument()
  })
})
