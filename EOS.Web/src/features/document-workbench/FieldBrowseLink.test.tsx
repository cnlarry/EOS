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
  it('有目标模块且可浏览时渲染为工作台链接', () => {
    renderWithRouter({ value: 'P001', browseModuleId: 1206, canBrowse: true })
    const link = screen.getByRole('link', { name: 'P001' })
    expect(link).toHaveAttribute('href', '/document-workbench/1206')
  })

  it('无目标模块时按纯文本渲染', () => {
    const { container } = renderWithRouter({ value: 'P001', browseModuleId: null })
    expect(container.querySelector('a')).not.toBeInTheDocument()
    expect(screen.getByText('P001')).toBeInTheDocument()
  })

  it('目标模块为 0 或无权限时按纯文本渲染', () => {
    const { container } = renderWithRouter({ value: 'P001', browseModuleId: 0, canBrowse: true })
    expect(container.querySelector('a')).not.toBeInTheDocument()
    renderWithRouter({ value: 'P001', browseModuleId: 1206, canBrowse: false })
    expect(container.querySelector('a')).not.toBeInTheDocument()
  })

  it('空值不渲染链接', () => {
    const { container } = renderWithRouter({ value: '', browseModuleId: 1206, canBrowse: true })
    expect(container.querySelector('a')).not.toBeInTheDocument()
  })
})
