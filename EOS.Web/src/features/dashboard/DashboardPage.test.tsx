import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { DashboardPage } from './DashboardPage'

describe('DashboardPage', () => {
  it('渲染指标卡与概览', () => {
    render(<DashboardPage />)
    expect(screen.getByText('待审批单据')).toBeInTheDocument()
    expect(screen.getByText('¥ 286,420')).toBeInTheDocument()
    expect(screen.getByText('待入库批次')).toBeInTheDocument()
    expect(screen.getByText('采购执行概览')).toBeInTheDocument()
    expect(screen.getByText('待办事项')).toBeInTheDocument()
    expect(screen.getByText('供应商报价即将失效')).toBeInTheDocument()
  })
})
