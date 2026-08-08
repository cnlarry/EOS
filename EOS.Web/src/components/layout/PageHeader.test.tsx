import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { PageHeader } from './PageHeader'

describe('PageHeader', () => {
  it('渲染全部区域', () => {
    render(<PageHeader eyebrow="模块" title="标题" description="说明" actions={<button>操作</button>} />)
    expect(screen.getByText('模块')).toBeInTheDocument()
    expect(screen.getByText('标题')).toBeInTheDocument()
    expect(screen.getByText('说明')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '操作' })).toBeInTheDocument()
  })

  it('可选区域缺省时不渲染', () => {
    const { container } = render(<PageHeader title="标题" />)
    expect(screen.getByText('标题')).toBeInTheDocument()
    expect(container.querySelector('.page-pretitle')).toBeNull()
    expect(container.querySelector('.erp-page-actions')).toBeNull()
  })
})
