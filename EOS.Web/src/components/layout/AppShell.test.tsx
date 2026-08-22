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
    {
      id: 'sales',
      label: '销售管理',
      icon: 'sales',
      children: [
        { id: 'so', label: '销售订单', route: '/sales/orders', icon: 'sales' },
        {
          id: 'sales-sub',
          label: '销售子组',
          icon: 'sales',
          children: [
            { id: 'so-sub', label: '销售子页', route: '/sales/sub-page', icon: 'sales' },
            {
              id: 'grouped',
              label: '分组模块',
              route: '/document-workbench/1209',
              icon: 'sales',
              moduleId: 1209,
              groups: [{ index: 1, description: '结案' }],
            },
          ],
        },
      ],
    },
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
          <Route path="/admin/tables/:tableId/fields" element={<div>FIELDS</div>} />
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
    expect(screen.getByText('销售管理')).toBeInTheDocument()
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getByText('admin')).toBeInTheDocument()
    expect(screen.getByText('LW')).toBeInTheDocument()
  })

  it('无 bootstrap 时使用兜底导航', () => {
    renderShell('/dashboard', { bootstrap: null })
    expect(screen.getByText('销售管理')).toBeInTheDocument()
  })

  it('导航分组默认闭合，点击展开收起', () => {
    renderShell('/dashboard')
    const toggle = screen.getByRole('button', { name: '销售管理' })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
  })

  it('三级菜单可逐级展开', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))
    const subGroup = screen.getByRole('button', { name: '销售子组' })
    expect(subGroup).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(subGroup)
    expect(subGroup).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('link', { name: '销售子页' })).toBeInTheDocument()
  })

  it('菜单搜索命中后显示面包屑并可直达', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))
    fireEvent.click(screen.getByRole('button', { name: '销售子组' }))
    fireEvent.input(screen.getByRole('searchbox', { name: '搜索菜单' }), { target: { value: '分组模块' } })
    const result = screen.getByRole('button', { name: '销售管理 / 销售子组 / 分组模块' })
    fireEvent.click(result)
    expect(screen.getByText('WB')).toBeInTheDocument()
  })

  it('菜单搜索无命中时提示', () => {
    renderShell('/dashboard')
    fireEvent.input(screen.getByRole('searchbox', { name: '搜索菜单' }), { target: { value: '不存在的菜单' } })
    expect(screen.getByText('没有匹配的菜单')).toBeInTheDocument()
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
    expect(screen.getByRole('heading', { name: '新建分组模块' })).toBeInTheDocument()
  })

  it('渲染当前日期时间元素', () => {
    renderShell('/dashboard')
    const time = document.querySelector('time')
    expect(time).toBeInTheDocument()
    expect(time!.getAttribute('dateTime')).toMatch(/^\d{4}-\d{2}-\d{2}$/)
  })

  it('拖拽手柄可调整侧栏宽度并持久化', () => {
    renderShell('/dashboard')
    const resizer = screen.getByRole('separator', { name: '调整菜单宽度' })
    const shell = resizer.closest('.erp-shell') as HTMLElement
    expect(shell.style.getPropertyValue('--erp-sidebar-width')).toBe('220px')

    fireEvent.pointerDown(resizer, { clientX: 220, pointerId: 1 })
    fireEvent.pointerMove(resizer, { clientX: 300, pointerId: 1 })
    fireEvent.pointerUp(resizer, { pointerId: 1 })

    expect(shell.style.getPropertyValue('--erp-sidebar-width')).toBe('300px')
    expect(localStorage.getItem('erp-sidebar-width')).toBe('300')
    expect(document.body.style.userSelect).toBe('')
  })

  it('侧栏宽度限制在最小/最大范围并恢复持久化值', () => {
    localStorage.setItem('erp-sidebar-width', '999')
    renderShell('/dashboard')
    const resizer = screen.getByRole('separator', { name: '调整菜单宽度' })
    const shell = resizer.closest('.erp-shell') as HTMLElement
    // 非法持久化值回退默认 220px
    expect(shell.style.getPropertyValue('--erp-sidebar-width')).toBe('220px')

    fireEvent.pointerDown(resizer, { clientX: 220, pointerId: 1 })
    fireEvent.pointerMove(resizer, { clientX: 5000, pointerId: 1 })
    expect(shell.style.getPropertyValue('--erp-sidebar-width')).toBe('480px')
    fireEvent.pointerMove(resizer, { clientX: -5000, pointerId: 1 })
    expect(shell.style.getPropertyValue('--erp-sidebar-width')).toBe('160px')
    fireEvent.pointerUp(resizer, { pointerId: 1 })
    expect(localStorage.getItem('erp-sidebar-width')).toBe('160')
  })

  it('字段维护子页面包屑固定为 系统管理 > 数据表维护 > 数据表维护 > 表名 > 字段', () => {
    renderShell('/admin/tables/PRODUCT_EDITION/fields')
    const nav = screen.getByRole('navigation', { name: '当前位置' })
    const text = nav.textContent ?? ''
    const order = ['系统管理', '数据表维护', '数据表维护', 'PRODUCT_EDITION', '字段']
    let cursor = -1
    for (const segment of order) {
      const index = text.indexOf(segment, cursor + 1)
      expect(index).toBeGreaterThan(cursor)
      cursor = index
    }
  })
})
