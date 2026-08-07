import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ErpColumnSelector, type ColumnSelectorGroup } from './ErpColumnSelector'

const groups: ColumnSelectorGroup[] = [
  {
    id: 'master',
    label: '主表字段',
    fields: [
      { key: 'A', label: '字段A' },
      { key: 'B', label: '字段B' },
      { key: 'C', label: '字段C' },
    ],
    visibleKeys: ['A', 'B'],
    defaultKeys: ['A', 'C'],
  },
]

function optionsOf(select: HTMLSelectElement): string[] {
  return Array.from(select.options).map((option) => option.value)
}

function selectOption(select: HTMLSelectElement, value: string) {
  const option = Array.from(select.options).find((item) => item.value === value)
  expect(option).toBeDefined()
  option!.selected = true
  fireEvent.change(select)
}

describe('ErpColumnSelector', () => {
  it('按已选配置初始化可选/已选字段', () => {
    render(<ErpColumnSelector open groups={groups} onClose={vi.fn()} onSave={vi.fn()} />)
    expect(optionsOf(screen.getByLabelText('主表字段可选字段'))).toEqual(['C'])
    expect(optionsOf(screen.getByLabelText('主表字段已选字段'))).toEqual(['A', 'B'])
  })

  it('加入选中字段到已选列表', () => {
    render(<ErpColumnSelector open groups={groups} onClose={vi.fn()} onSave={vi.fn()} />)
    selectOption(screen.getByLabelText('主表字段可选字段'), 'C')
    fireEvent.click(screen.getByTitle('加入选中字段'))
    expect(optionsOf(screen.getByLabelText('主表字段已选字段'))).toEqual(['A', 'B', 'C'])
    expect(optionsOf(screen.getByLabelText('主表字段可选字段'))).toEqual([])
  })

  it('移除选中字段', () => {
    render(<ErpColumnSelector open groups={groups} onClose={vi.fn()} onSave={vi.fn()} />)
    selectOption(screen.getByLabelText('主表字段已选字段'), 'B')
    fireEvent.click(screen.getByTitle('移除选中字段'))
    expect(optionsOf(screen.getByLabelText('主表字段已选字段'))).toEqual(['A'])
    expect(optionsOf(screen.getByLabelText('主表字段可选字段'))).toContain('B')
  })

  it('上移调整已选字段顺序', () => {
    render(<ErpColumnSelector open groups={groups} onClose={vi.fn()} onSave={vi.fn()} />)
    selectOption(screen.getByLabelText('主表字段已选字段'), 'B')
    fireEvent.click(screen.getByTitle('上移'))
    expect(optionsOf(screen.getByLabelText('主表字段已选字段'))).toEqual(['B', 'A'])
  })

  it('取默认字段恢复该组默认配置', () => {
    render(<ErpColumnSelector open groups={groups} onClose={vi.fn()} onSave={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: '取默认字段' }))
    expect(optionsOf(screen.getByLabelText('主表字段已选字段'))).toEqual(['A', 'C'])
  })

  it('保存时把组配置交给 onSave', () => {
    const onSave = vi.fn()
    render(<ErpColumnSelector open groups={groups} onClose={vi.fn()} onSave={onSave} />)
    fireEvent.click(screen.getByRole('button', { name: '保存' }))
    expect(onSave).toHaveBeenCalledWith({ master: ['A', 'B'] })
  })

  it('canSave 为空时禁用保存', () => {
    const emptyGroups = [{ ...groups[0], visibleKeys: [] }]
    render(
      <ErpColumnSelector
        open
        groups={emptyGroups}
        onClose={vi.fn()}
        onSave={vi.fn()}
        canSave={(selection) => Boolean(selection.master?.length)}
      />,
    )
    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled()
  })

  it('取消触发 onClose', () => {
    const onClose = vi.fn()
    render(<ErpColumnSelector open groups={groups} onClose={onClose} onSave={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('加载失败显示错误与重新加载', () => {
    const onRetry = vi.fn()
    render(<ErpColumnSelector open groups={[]} loadError="加载失败" onRetry={onRetry} onClose={vi.fn()} onSave={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: '重新加载' }))
    expect(onRetry).toHaveBeenCalledTimes(1)
  })
})
