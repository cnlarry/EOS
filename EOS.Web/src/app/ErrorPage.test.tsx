import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ErrorPage } from './ErrorPage'

const useRouteErrorMock = vi.hoisted(() => vi.fn())

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return { ...actual, useRouteError: useRouteErrorMock }
})

describe('ErrorPage', () => {
  beforeEach(() => {
    useRouteErrorMock.mockReset()
  })

  it('404 显示未找到页面', () => {
    useRouteErrorMock.mockReturnValue({ status: 404, statusText: 'Not Found', internal: false, data: {} })
    render(<MemoryRouter><ErrorPage /></MemoryRouter>)
    expect(screen.getByText('404')).toBeInTheDocument()
    expect(screen.getByText('没有找到这个页面')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /返回工作台/ })).toHaveAttribute('href', '/dashboard')
  })

  it('非 404 显示通用错误', () => {
    useRouteErrorMock.mockReturnValue(new Error('boom'))
    render(<MemoryRouter><ErrorPage /></MemoryRouter>)
    expect(screen.getByText('出现错误')).toBeInTheDocument()
    expect(screen.getByText('暂时无法加载页面')).toBeInTheDocument()
  })
})
