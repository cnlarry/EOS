import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { LoginPage } from './LoginPage'
import { useAuth } from './authContext'

vi.mock('./authContext', () => ({ useAuth: vi.fn() }))

function renderLogin() {
  return render(
    <MemoryRouter initialEntries={[{ pathname: '/login', state: { from: '/dashboard' } }]}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/dashboard" element={<div>DASHBOARD_OK</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('LoginPage', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.mocked(useAuth).mockReturnValue({
      bootstrap: null,
      loading: false,
      login: vi.fn(),
      logout: vi.fn(),
      hasPermission: () => false,
    })
  })

  it('loading 时显示恢复状态', () => {
    vi.mocked(useAuth).mockReturnValue({ bootstrap: null, loading: true, login: vi.fn(), logout: vi.fn(), hasPermission: () => false })
    renderLogin()
    expect(screen.getByText('正在恢复登录状态…')).toBeInTheDocument()
  })

  it('已登录时跳转目标地址', () => {
    vi.mocked(useAuth).mockReturnValue({
      bootstrap: { user: { id: 'u', username: 'admin', displayName: 'A', employeeId: 'E', avatarText: 'A', avatarUrl: null, roleName: 'R', organization: { id: 'o', name: 'O' } }, permissions: [], navigation: [] },
      loading: false,
      login: vi.fn(),
      logout: vi.fn(),
      hasPermission: () => false,
    })
    renderLogin()
    expect(screen.getByText('DASHBOARD_OK')).toBeInTheDocument()
  })

  it('提交成功后记住用户名并跳转', async () => {
    const login = vi.fn().mockResolvedValue(undefined)
    vi.mocked(useAuth).mockReturnValue({ bootstrap: null, loading: false, login, logout: vi.fn(), hasPermission: () => false })
    renderLogin()
    fireEvent.change(screen.getByLabelText('用户名'), { target: { value: 'admin' } })
    fireEvent.change(screen.getByLabelText('密码'), { target: { value: 'erp123' } })
    fireEvent.click(screen.getByLabelText('记住用户名'))
    fireEvent.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(login).toHaveBeenCalledWith({ userId: 'admin', password: 'erp123', rememberMe: true }))
    expect(localStorage.getItem('erp-remembered-user')).toBe('admin')
    await waitFor(() => expect(screen.getByText('DASHBOARD_OK')).toBeInTheDocument())
  })

  it('登录失败展示服务端消息', async () => {
    vi.mocked(useAuth).mockReturnValue({
      bootstrap: null,
      loading: false,
      login: vi.fn().mockRejectedValue(new ApiError(401, { code: 'INVALID_CREDENTIALS', message: '用户名或密码错误。' })),
      logout: vi.fn(),
      hasPermission: () => false,
    })
    renderLogin()
    fireEvent.change(screen.getByLabelText('用户名'), { target: { value: 'admin' } })
    fireEvent.change(screen.getByLabelText('密码'), { target: { value: 'bad' } })
    fireEvent.click(screen.getByRole('button', { name: '登录' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('用户名或密码错误。'))
  })

  it('显示密码切换 input 类型', () => {
    renderLogin()
    const password = screen.getByLabelText('密码') as HTMLInputElement
    expect(password.type).toBe('password')
    fireEvent.click(screen.getByRole('button', { name: '显示密码' }))
    expect(password.type).toBe('text')
  })
})
