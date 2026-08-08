import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuth } from '../../features/auth/authContext'
import type { AppBootstrap } from '../../features/auth/types'
import { AppShell } from './AppShell'

vi.mock('../../features/auth/authContext', () => ({ useAuth: vi.fn() }))

const bootstrap: AppBootstrap = {
  user: {
    id: 'u1', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW',
    avatarUrl: null, roleName: '系统管理员', organization: { id: 'east', name: '华东运营中心' },
  },
  permissions: [],
  navigation: [
    { id: 'dashboard', label: '工作台', route: '/dashboard', icon: 'dashboard' },
    { id: 'procurement', label: '采购管理', icon: 'procurement', children: [{ id: 'po', label: '采购订单', route: '/procurement/purchase-orders', icon: 'procurement' }] },
    { id: 'settings', label: '个人设置', route: '/settings/profile', icon: 'settings' },
  ],
}

function renderShell(initialEntry: string, auth: Partial<ReturnType<typeof useAuth>> = {}) {
  const logout = vi.fn().mockResolvedValue(undefined)
  vi.mocked(useAuth).mockReturnValue({
    bootstrap: bootstrap,
    loading: false,
    login: vi.fn(),
    logout,
    hasPermission: () => false,
    ...auth,
  })
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route element={<AppShell />}>
          <Route path="/dashboard" element={<div>DASH</div>} />
          <Route path="/settings/profile" element={<div>PROFILE</div>} />
          <Route path="/document-workbench/:moduleId" element={<div>WB</div>} />
          <Route path="/document-workbench/:moduleId/new" element={<div>NEW_FORM</div>} />
          <Route path="/login" element={<div>LOGIN_PAGE</div>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  )
}

describe('AppShell', () => {
  beforeEach(() => {
    localStorage.clear()
    window.matchMedia = vi.fn().mockReturnValue({
      matches: false,
      media: '',
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      onchange: null,
      dispatchEvent: vi.fn(),
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
    document.documentElement.removeAttribute('data-bs-theme')
    document.documentElement.classList.remove('erp-sidebar-collapsed')
  })

  it('渲染品牌、导航与用户信息', () => {
    renderShell('/dashboard')
    expect(screen.getByText('EOS')).toBeInTheDocument()
    expect(screen.getAllByText('工作台').length).toBeGreaterThan(0)
    expect(screen.getByText('采购管理')).toBeInTheDocument()
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getByText('admin')).toBeInTheDocument()
    expect(screen.getByText('LW')).toBeInTheDocument()
  })

  it('无 bootstrap 时使用兜底导航', () => {
    renderShell('/dashboard', { bootstrap: null })
    expect(screen.getByText('采购管理')).toBeInTheDocument()
    expect(screen.getAllByText('采购订单').length).toBeGreaterThan(0)
  })

  it('导航分组可展开收起', () => {
    renderShell('/dashboard')
    const toggle = screen.getByRole('button', { name: '采购管理' })
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
  })

  it('主题切换写入 data-bs-theme 与 localStorage', () => {
    renderShell('/dashboard')
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('light')
    fireEvent.click(screen.getByRole('button', { name: '切换到深色主题' }))
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('dark')
    expect(localStorage.getItem('erp-theme')).toBe('dark')
  })

  it('侧边栏折叠切换', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    expect(document.documentElement.classList.contains('erp-sidebar-collapsed')).toBe(true)
    expect(localStorage.getItem('erp-sidebar-collapsed')).toBe('true')
    fireEvent.click(screen.getByRole('button', { name: '展开导航' }))
    expect(document.documentElement.classList.contains('erp-sidebar-collapsed')).toBe(false)
  })

  it('移动端菜单打开与遮罩关闭', () => {
    renderShell('/dashboard')
    expect(screen.queryByLabelText('关闭导航')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '打开导航' }))
    const backdrop = screen.getByRole('button', { name: '关闭导航' })
    expect(backdrop).toBeInTheDocument()
    fireEvent.click(backdrop)
    expect(screen.queryByRole('button', { name: '关闭导航' })).not.toBeInTheDocument()
  })

  it('用户菜单退出登录跳转 /login', async () => {
    const logout = vi.fn().mockResolvedValue(undefined)
    renderShell('/dashboard', { logout })
    fireEvent.click(screen.getByRole('button', { name: '用户菜单' }))
    fireEvent.click(screen.getByRole('menuitem', { name: '退出登录' }))
    expect(logout).toHaveBeenCalled()
    await waitFor(() => expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument())
  })

  it('用户菜单个人设置跳转', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '用户菜单' }))
    fireEvent.click(screen.getByRole('menuitem', { name: '个人设置' }))
    expect(screen.getByText('PROFILE')).toBeInTheDocument()
  })

  it('页面标题随路由变化', () => {
    renderShell('/dashboard')
    expect(screen.getByRole('heading', { name: '工作台' })).toBeInTheDocument()
    renderShell('/settings/profile')
    expect(screen.getByRole('heading', { name: '个人设置' })).toBeInTheDocument()
    renderShell('/document-workbench/1209/new')
    expect(screen.getByRole('heading', { name: '新建' })).toBeInTheDocument()
  })

  it('渲染当前日期时间元素', () => {
    renderShell('/dashboard')
    const time = document.querySelector('time')
    expect(time).toBeInTheDocument()
    expect(time!.getAttribute('dateTime')).toMatch(/^\d{4}-\d{2}-\d{2}$/)
  })
})
