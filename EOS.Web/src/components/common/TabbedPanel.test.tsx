import { fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { TabbedPanel } from './TabbedPanel'

const tabs = [
  { key: 'basic', label: '基础' },
  { key: 'master', label: '主表' },
  { key: 'detail', label: '子表' },
]

function Harness({ onChange }: { onChange?: (key: string) => void }) {
  const [active, setActive] = useState('basic')
  return (
    <TabbedPanel
      tabs={tabs}
      activeKey={active}
      onActiveKeyChange={(key) => {
        setActive(key)
        onChange?.(key)
      }}
    >
      {active === 'basic' && <div>基础内容</div>}
      {active === 'master' && <div>主表内容</div>}
      {active === 'detail' && <div>子表内容</div>}
    </TabbedPanel>
  )
}

describe('TabbedPanel', () => {
  it('渲染标签行与激活页签内容（ARIA）', () => {
    render(<Harness onChange={vi.fn()} />)
    expect(screen.getByRole('tablist')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: '基础' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: '主表' })).toHaveAttribute('aria-selected', 'false')
    expect(screen.getByRole('tabpanel')).toHaveTextContent('基础内容')
  })

  it('点击标签切换激活页签与内容', () => {
    render(<Harness onChange={vi.fn()} />)
    fireEvent.click(screen.getByRole('tab', { name: '主表' }))
    expect(screen.getByRole('tab', { name: '主表' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tabpanel')).toHaveTextContent('主表内容')
  })

  it('方向键与 Home/End 切换并聚焦目标标签', () => {
    const onChange = vi.fn()
    render(<Harness onChange={onChange} />)
    const tablist = screen.getByRole('tablist')

    fireEvent.keyDown(tablist, { key: 'ArrowRight' })
    expect(onChange).toHaveBeenCalledWith('master')
    expect(screen.getByRole('tab', { name: '主表' })).toHaveFocus()

    fireEvent.keyDown(tablist, { key: 'End' })
    expect(onChange).toHaveBeenCalledWith('detail')

    fireEvent.keyDown(tablist, { key: 'ArrowLeft' })
    expect(onChange).toHaveBeenCalledWith('master')

    fireEvent.keyDown(tablist, { key: 'Home' })
    expect(onChange).toHaveBeenCalledWith('basic')
  })
})
