import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { StatusBadge } from './StatusBadge'

describe('StatusBadge', () => {
  it.each([
    ['draft', '草稿', 'bg-secondary-lt'],
    ['pending', '待审核', 'bg-yellow-lt'],
    ['approved', '已审核', 'bg-green-lt'],
    ['rejected', '已驳回', 'bg-red-lt'],
    ['cancelled', '已作废', 'bg-secondary-lt'],
    ['closed', '已关闭', 'bg-azure-lt'],
  ] as const)('状态 %s 显示为「%s」', (status, label, colorClass) => {
    render(<StatusBadge status={status} />)
    const badge = screen.getByText(label)
    expect(badge).toHaveClass('badge', colorClass)
  })
})
