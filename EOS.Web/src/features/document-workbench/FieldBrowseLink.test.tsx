import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { FieldBrowseLink } from './FieldBrowseLink'

function renderWithRouter(props: React.ComponentProps<typeof FieldBrowseLink>) {
  return render(
    <MemoryRouter>
      <FieldBrowseLink {...props} />
    </MemoryRouter>,
  )
}

describe('FieldBrowseLink', () => {
  it('有键源列且行值完整时渲染为目标记录浏览链接（同页签 view，主键路径段）', () => {
    renderWithRouter({ value: 'P001', browseModuleId: 1201, browseKeyFields: ['CLIENT_ID'], row: { CLIENT_ID: 'C1' }, canBrowse: true })
    const link = screen.getByRole('link', { name: 'P001' })
    expect(link).toHaveAttribute('href', '/workbench/1201/view/C1')
  })

  it('复合键按键源列顺序组装主键路径段', () => {
    renderWithRouter({ value: 'P001', browseModuleId: 170101, browseKeyFields: ['ACCOUNT_TYPE', 'ACCOUNT_NO'], row: { ACCOUNT_TYPE: 'A', ACCOUNT_NO: 'B' }, canBrowse: true })
    const link = screen.getByRole('link', { name: 'P001' })
    expect(link).toHaveAttribute('href', '/workbench/170101/view/A/B')
  })

  it('跨模块浏览链接携带来源模块 from 参数（query）', () => {
    renderWithRouter({ value: 'P001', browseModuleId: 1201, browseKeyFields: ['CLIENT_ID'], row: { CLIENT_ID: 'C1' }, fromModuleId: 1405, canBrowse: true })
    const link = screen.getByRole('link', { name: 'P001' })
    expect(link).toHaveAttribute('href', '/workbench/1201/view/C1?from=1405')
  })

  it('列表降级链接不携带来源模块参数', () => {
    renderWithRouter({ value: 'P001', browseModuleId: 1201, fromModuleId: 1405, canBrowse: true })
    const link = screen.getByRole('link', { name: 'P001' })
    expect(link).toHaveAttribute('href', '/workbench/1201')
  })

  it('有目标模块但无键源列时渲染为工作台列表链接', () => {
    renderWithRouter({ value: 'P001', browseModuleId: 1201, canBrowse: true })
    const link = screen.getByRole('link', { name: 'P001' })
    expect(link).toHaveAttribute('href', '/workbench/1201')
  })

  it('无目标模块时按纯文本渲染', () => {
    const { container } = renderWithRouter({ value: 'P001', browseModuleId: null })
    expect(container.querySelector('a')).not.toBeInTheDocument()
    expect(screen.getByText('P001')).toBeInTheDocument()
  })

  it('目标模块为 0 或无权限时按纯文本渲染', () => {
    const { container } = renderWithRouter({ value: 'P001', browseModuleId: 0, canBrowse: true })
    expect(container.querySelector('a')).not.toBeInTheDocument()
    renderWithRouter({ value: 'P001', browseModuleId: 1201, canBrowse: false })
    expect(container.querySelector('a')).not.toBeInTheDocument()
  })

  it('空值不渲染链接', () => {
    const { container } = renderWithRouter({ value: '', browseModuleId: 1201, canBrowse: true })
    expect(container.querySelector('a')).not.toBeInTheDocument()
  })
})