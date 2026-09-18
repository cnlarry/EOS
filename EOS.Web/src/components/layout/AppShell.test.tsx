import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
import { Link, MemoryRouter, Route, Routes, createMemoryRouter, RouterProvider, useLocation, type RouteObject } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuth } from '../../features/auth/authContext'
import type { AppBootstrap } from '../../features/auth/types'
import { WORKSPACE_TABS_ENABLED_KEY, workspaceTabsKey } from '../../lib/storageKeys'
import { AppShell } from './AppShell'
import { useTabDirty } from './workspaceDirty'

vi.mock('../../features/auth/authContext', () => ({ useAuth: vi.fn() }))

/** 记录浏览器真实地址，用于断言标签切换后地址栏跟随 */
function LocationProbe() {
  const location = useLocation()
  return <span data-testid="browser-location">{location.pathname}{location.search}</span>
}

/** 带本地状态的页面：用于验证切换标签后组件常驻、状态不丢 */
function CounterPage() {
  const [count, setCount] = useState(0)
  return <button type="button" onClick={() => setCount((current) => current + 1)}>计数 {count}</button>
}

/** 会置脏的页面：用于验证脏点、关闭确认与离开拦截 */
function DirtyPage() {
  const [value, setValue] = useState('')
  useTabDirty(value !== '', {
    save: async () => { setValue('') },
    discard: () => setValue(''),
  })
  return (
    <div>
      <input aria-label="标题" value={value} onChange={(event) => setValue(event.target.value)} />
      <Link to="/counter">去计数器</Link>
    </div>
  )
}

const bootstrap: AppBootstrap = {
  user: {
    id: 'u1', username: 'admin', displayName: 'Demo User', employeeId: 'E001', avatarText: 'LW',
    avatarUrl: null, roleName: '系统管理员', organization: { id: 'east', name: '华东运营中心' },
  },
  permissions: [],
  navigation: [
    { id: 'dashboard', label: '首页', route: '/dashboard', icon: 'dashboard' },
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
              route: '/workbench/1209',
              icon: 'sales',
              moduleId: 1209,
              groups: [{ index: 1, description: '结案' }],
            },
          ],
        },
      ],
    },
    { id: 'settings', label: '个人设置', route: '/settings/profile', icon: 'settings' },
    { id: 'counter', label: '计数器', route: '/counter', icon: 'dashboard' },
    { id: 'dirty', label: '脏页演示', route: '/dirty', icon: 'dashboard' },
  ],
}

/** 工作区路由夹具：AppShell 自己按标签地址求值渲染，因此必须注入路由表而不是用子路由 */
const fixtureRoutes: RouteObject[] = [
  { path: 'dashboard', element: <div>DASH</div> },
  { path: 'settings/profile', element: <div>PROFILE</div> },
  { path: 'counter', element: <CounterPage /> },
  { path: 'dirty', element: <DirtyPage /> },
  { path: 'admin/tables/:tableId/fields', element: <div>FIELDS</div> },
  { path: 'workbench/:moduleId', element: <div>WB</div> },
  { path: 'workbench/:moduleId/new', element: <div>NEW_FORM</div> },
  { path: 'workbench/:moduleId/view/*', element: <div>VIEW_FORM</div> },
]

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
      <LocationProbe />
      <Routes>
        <Route path="/*" element={<AppShell routes={fixtureRoutes} />} />
        <Route path="/login" element={<div>LOGIN_PAGE</div>} />
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
    expect(screen.getAllByText('首页').length).toBeGreaterThan(0)
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
    expect(screen.getByRole('heading', { name: '首页' })).toBeInTheDocument()
    renderShell('/settings/profile')
    expect(screen.getByRole('heading', { name: '个人设置' })).toBeInTheDocument()
    renderShell('/workbench/1209/new')
    expect(screen.getByRole('heading', { name: '新增分组模块' })).toBeInTheDocument()
  })

  it('表单页面包屑含完整层级，叶子可点击返回模块工作台', async () => {
    renderShell('/workbench/1209/new')
    const crumbs = within(screen.getByRole('navigation', { name: '当前位置' }))
    // 层级：销售管理 > 销售子组 > 分组模块（叶子可点击）> 新增分组模块
    expect(crumbs.getByText('销售管理')).toBeInTheDocument()
    expect(crumbs.getByText('销售子组')).toBeInTheDocument()
    const leaf = crumbs.getByText('分组模块')
    expect(leaf).toHaveAttribute('href', '/workbench/1209')
    fireEvent.click(leaf)
    expect(screen.getByText('WB')).toBeInTheDocument()
  })

  it('跨模块关联浏览（from 参数）面包屑按来源模块路径呈现', () => {
    renderShell('/workbench/1401/view/A?from=1209')
    const crumbs = within(screen.getByRole('navigation', { name: '当前位置' }))
    // 来源模块 1209（分组模块）的完整层级，叶子可点击返回来源工作台
    expect(crumbs.getByText('销售管理')).toBeInTheDocument()
    expect(crumbs.getByText('销售子组')).toBeInTheDocument()
    expect(crumbs.getByText('分组模块')).toHaveAttribute('href', '/workbench/1209')
  })

  it('常规浏览（无 from 参数）面包屑不显示来源模块路径', () => {
    renderShell('/workbench/1401/view/A')
    const crumbs = within(screen.getByRole('navigation', { name: '当前位置' }))
    expect(crumbs.queryByText('分组模块')).not.toBeInTheDocument()
    expect(crumbs.queryByText('销售子组')).not.toBeInTheDocument()
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

  it('点菜单开新标签，重复打开复用已有标签，切换标签时地址栏跟随', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByText('PROFILE')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: /首页/ }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    expect(screen.getByText('DASH')).toBeInTheDocument()

    // 再次从菜单打开同一地址：聚焦已有标签，不新增
    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')
  })

  it('切换标签不卸载页面：各自的本地状态保持', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '计数器' }))
    fireEvent.click(screen.getByRole('button', { name: '计数 0' }))
    expect(screen.getByRole('button', { name: '计数 1' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: /首页/ }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    fireEvent.click(screen.getByRole('tab', { name: /计数器/ }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/counter')
    // 组件未卸载，计数没有回到 0
    expect(screen.getByRole('button', { name: '计数 1' })).toBeInTheDocument()
  })

  it('脏标签显示脏点，关闭先确认；取消则保留，不保存则关闭', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '关闭标签 脏页演示' }))
    expect(screen.getByRole('dialog', { name: '未保存改动确认' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    expect(screen.queryByRole('dialog', { name: '未保存改动确认' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('tab')).toHaveLength(2)

    fireEvent.click(screen.getByRole('button', { name: '关闭标签 脏页演示' }))
    fireEvent.click(screen.getByRole('button', { name: '不保存并关闭' }))
    await waitFor(() => expect(screen.getAllByRole('tab')).toHaveLength(1))
  })

  it('脏标签的「保存并关闭」先保存再关闭', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '关闭标签 脏页演示' }))
    fireEvent.click(screen.getByRole('button', { name: '保存并关闭' }))
    await waitFor(() => expect(screen.getAllByRole('tab')).toHaveLength(1))
    expect(screen.getByText('DASH')).toBeInTheDocument()
  })

  it('「关闭其他」保留当前标签与脏标签', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())
    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    expect(screen.getAllByRole('tab')).toHaveLength(3)

    fireEvent.click(screen.getByRole('button', { name: '关闭其他' }))
    // 当前标签（个人设置）+ 脏标签（脏页演示）保留，首页被关掉
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByRole('tab', { name: /个人设置/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /脏页演示/ })).toBeInTheDocument()
  })

  it('「关闭全部」涉及脏标签时先确认，确认后落到首页', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '关闭全部' }))
    expect(screen.getByRole('dialog', { name: '未保存改动确认' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '不保存并关闭' }))
    await waitFor(() => expect(screen.getAllByRole('tab')).toHaveLength(1))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    expect(screen.getByText('DASH')).toBeInTheDocument()
  })

  it('存在脏标签时浏览器刷新/关闭被拦下', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
  })

  it('标签栏键盘可操作：左右方向键切换、Delete 关闭', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    fireEvent.click(screen.getByRole('link', { name: '计数器' }))
    expect(screen.getByRole('tablist', { name: '工作区标签' })).toBeInTheDocument()
    expect(screen.getAllByRole('tab')).toHaveLength(3)

    fireEvent.keyDown(screen.getAllByRole('tab')[2], { key: 'ArrowLeft' })
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')

    fireEvent.keyDown(screen.getAllByRole('tab')[1], { key: 'ArrowRight' })
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/counter')

    fireEvent.keyDown(screen.getAllByRole('tab')[2], { key: 'Delete' })
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')
  })

  it('撞顶：已达上限 12 时拒绝新建并提示', () => {
    const saved = Array.from({ length: 12 }, (_, index) => ({ id: `t${index + 1}`, url: `/p${index + 1}`, label: `页${index + 1}` }))
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify(saved))
    renderShell('/p1')
    expect(screen.getAllByRole('tab')).toHaveLength(12)

    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    expect(screen.getByText('标签已达上限 12 个，请先关闭一个标签')).toBeInTheDocument()
    expect(screen.getAllByRole('tab')).toHaveLength(12)
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/p1')
  })

  it('刷新恢复标签列表，未激活的标签不挂载（懒挂载）', () => {
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify([
      { id: 't1', url: '/dashboard', label: '首页' },
      { id: 't2', url: '/counter', label: '计数器' },
    ]))
    renderShell('/dashboard')
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByText('DASH')).toBeInTheDocument()
    // 未激活的恢复标签尚未挂载：其页面组件不在 DOM 中
    expect(screen.queryByRole('button', { name: '计数 0' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: /计数器/ }))
    expect(screen.getByRole('button', { name: '计数 0' })).toBeInTheDocument()
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/counter')
  })

  it('标签列表按用户持久化（last-write-wins）', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    const saved = JSON.parse(localStorage.getItem(workspaceTabsKey('u1')) ?? '[]') as { url: string }[]
    expect(saved.map((tab) => tab.url)).toEqual(['/dashboard', '/settings/profile'])
  })

  it('回退开关关闭：隐藏标签栏并原地导航（单标签行为）', () => {
    localStorage.setItem(WORKSPACE_TABS_ENABLED_KEY, 'off')
    renderShell('/dashboard')
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    expect(screen.getByText('PROFILE')).toBeInTheDocument()
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
    // 回退模式不落盘标签列表
    expect(localStorage.getItem(workspaceTabsKey('u1'))).toBeNull()
  })

  it('存在脏标签时退出登录先确认，取消则留在工作区', async () => {    const logout = vi.fn().mockResolvedValue(undefined)
    renderShell('/dashboard', { logout })
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('button', { name: '用户菜单' }))
    fireEvent.click(screen.getByRole('menuitem', { name: '退出登录' }))
    expect(screen.getByRole('dialog', { name: '未保存改动确认' })).toBeInTheDocument()
    expect(logout).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    expect(screen.queryByText('LOGIN_PAGE')).not.toBeInTheDocument()
    expect(screen.getByLabelText('标题')).toHaveValue('改动')
  })

  it('关闭标签后由相邻标签接管，地址栏不留停在已关闭标签上', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '个人设置' }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')

    fireEvent.click(screen.getByRole('button', { name: '关闭标签 个人设置' }))
    expect(screen.getAllByRole('tab')).toHaveLength(1)
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    expect(screen.getByText('DASH')).toBeInTheDocument()
  })
})

describe('AppShell 脏页导航拦截（数据路由）', () => {
  function renderDataRouterShell(initialEntry: string) {
    vi.mocked(useAuth).mockReturnValue({
      bootstrap,
      loading: false,
      login: vi.fn(),
      logout: vi.fn().mockResolvedValue(undefined),
      hasPermission: () => false,
    })
    const router = createMemoryRouter(
      [
        { path: '/*', element: <AppShell routes={fixtureRoutes} /> },
        { path: '/login', element: <div>LOGIN_PAGE</div> },
      ],
      { initialEntries: [initialEntry] },
    )
    return render(<RouterProvider router={router} />)
  }

  beforeEach(() => {
    localStorage.clear()
  })

  it('标签内导航离开脏页被拦下，取消后停留原处', async () => {
    renderDataRouterShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('link', { name: '去计数器' }))
    await waitFor(() => expect(screen.getByRole('dialog', { name: '未保存改动确认' })).toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: '取消' }))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: '未保存改动确认' })).not.toBeInTheDocument())
    expect(screen.getByLabelText('标题')).toBeInTheDocument()
  })

  it('切换标签不算离开脏页，不弹确认', async () => {
    renderDataRouterShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    fireEvent.click(screen.getByRole('tab', { name: /首页/ }))
    await waitFor(() => expect(screen.getByText('DASH')).toBeInTheDocument())
    expect(screen.queryByRole('dialog', { name: '未保存改动确认' })).not.toBeInTheDocument()
    // 草稿随标签常驻，未被丢弃
    expect(screen.getByLabelText('标题')).toHaveValue('改动')
  })
})

