import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ErpListCard } from './ErpListCard'

describe('ErpListCard', () => {
  it('渲染命令栏、主体与分页脚槽位', () => {
    render(
      <ErpListCard
        ariaLabel="测试列表"
        search={<input aria-label="搜索框" />}
        actions={<button>导出</button>}
        header={<h2>标题</h2>}
        footer={<div>分页脚</div>}
      >
        <div>表格主体</div>
      </ErpListCard>,
    )
    expect(screen.getByLabelText('测试列表')).toHaveClass('erp-list-command-bar')
    expect(screen.getByLabelText('搜索框')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '导出' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '标题' })).toBeInTheDocument()
    expect(screen.getByText('表格主体')).toBeInTheDocument()
    expect(screen.getByText('分页脚').closest('.erp-pagination-footer')).toBeInTheDocument()
  })

  it('无命令栏内容时不渲染命令栏', () => {
    const { container } = render(<ErpListCard>内容</ErpListCard>)
    expect(container.querySelector('.erp-list-command-bar')).not.toBeInTheDocument()
  })

})
