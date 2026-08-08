import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { RequireAuth, RequirePermission } from './RouteGuards'
import { useAuth } from './authContext'

vi.mock('./authContext', () => ({ useAuth: vi.fn() }))

const authed = {
  bootstrap: { user: { id: 'u', username: 'a', displayName: 'A', employeeId: 'E', avatarText: 'A', avatarUrl: null, roleName: 'R', organization: { id: 'o', name: 'O' } }, permissions: ['x'], navigation: [] },
  loading: false,
  login: vi.fn(),
  logout: vi.fn(),
  hasPermission: () => true,
}

describe('RouteGuards', () => {
  it('RequireAuth loading 显示加载状态', () => {
    vi.mocked(useAuth).mockReturnValue({ ...authed, loading: true })
    render(<MemoryRouter initialEntries={['/']}><Routes><Route element={<RequireAuth />}><Route path="/" element={<div>OK</div>} /></Route></Routes></MemoryRouter>)
    expect(screen.getByText('正在恢复登录状态…')).toBeInTheDocument()
  })

  it('RequireAuth 未登录跳转 /login 并携带来源', () => {
    vi.mocked(useAuth).mockReturnValue({ ...authed, bootstrap: null })
    render(
      <MemoryRouter initialEntries={['/document-workbench/1209']}>
        <Routes>
          <Route path="/login" element={<div>LOGIN_PAGE</div>} />
          <Route element={<RequireAuth />}><Route path="/document-workbench/:moduleId" element={<div>OK</div>} /></Route>
        </Routes>
      </MemoryRouter>,
    )
    expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument()
  })

  it('RequireAuth 已登录渲染子路由', () => {
    vi.mocked(useAuth).mockReturnValue(authed)
    render(<MemoryRouter initialEntries={['/x']}><Routes><Route element={<RequireAuth />}><Route path="/x" element={<div>OK</div>} /></Route></Routes></MemoryRouter>)
    expect(screen.getByText('OK')).toBeInTheDocument()
  })

  it('RequirePermission 无权限跳转 /forbidden', () => {
    vi.mocked(useAuth).mockReturnValue({ ...authed, hasPermission: () => false })
    render(
      <MemoryRouter initialEntries={['/p']}>
        <Routes>
          <Route path="/forbidden" element={<div>FORBIDDEN</div>} />
          <Route element={<RequirePermission permission="x" />}><Route path="/p" element={<div>OK</div>} /></Route>
        </Routes>
      </MemoryRouter>,
    )
    expect(screen.getByText('FORBIDDEN')).toBeInTheDocument()
  })

  it('RequirePermission 有权限渲染子路由', () => {
    vi.mocked(useAuth).mockReturnValue(authed)
    render(<MemoryRouter initialEntries={['/p']}><Routes><Route element={<RequirePermission permission="x" />}><Route path="/p" element={<div>OK</div>} /></Route></Routes></MemoryRouter>)
    expect(screen.getByText('OK')).toBeInTheDocument()
  })
})
