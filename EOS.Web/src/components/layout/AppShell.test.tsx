import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
import { Link, MemoryRouter, Navigate, Route, Routes, createMemoryRouter, RouterProvider, useLocation, type RouteObject } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuth } from '../../features/auth/authContext'
import type { AppBootstrap } from '../../features/auth/types'
import { WORKSPACE_TABS_ENABLED_KEY, workspaceTabsKey } from '../../lib/storageKeys'
import { ToastProvider } from '../ui/Toast'
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
  // 与生产一致：根路径只做重定向，没有自己的页面
  { index: true, element: <Navigate to="/dashboard" replace /> },
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
    <ToastProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <LocationProbe />
        <Routes>
          <Route path="/*" element={<AppShell routes={fixtureRoutes} />} />
          <Route path="/login" element={<div>LOGIN_PAGE</div>} />
        </Routes>
      </MemoryRouter>
    </ToastProvider>,
  )
}

/** 打开侧栏底部的用户菜单 */
function openUserMenu() {
  fireEvent.click(screen.getByRole('button', { name: '用户菜单' }))
}

/** 个人设置改由用户菜单进入（主导航不再提供该入口） */
function openProfileTab() {
  openUserMenu()
  fireEvent.click(screen.getByRole('menuitem', { name: '个人设置' }))
}

/** 在某个标签上打开右键操作菜单 */
function openTabMenu(name: string | RegExp) {
  fireEvent.contextMenu(screen.getByRole('tab', { name }))
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

  it('渲染品牌、导航与侧栏底部用户信息', () => {
    renderShell('/dashboard')
    expect(screen.getByText('EOS')).toBeInTheDocument()
    expect(screen.getAllByText('首页').length).toBeGreaterThan(0)
    expect(screen.getByText('销售管理')).toBeInTheDocument()
    // 用户信息移到侧栏底部：显示姓名与角色
    expect(screen.getByText('Demo User')).toBeInTheDocument()
    expect(screen.getByText('系统管理员')).toBeInTheDocument()
    expect(screen.getByText('LW')).toBeInTheDocument()
    // 顶部信息条已移除，工作区从标签栏开始
    expect(document.querySelector('.erp-context-bar')).toBeNull()
    expect(document.querySelector('.page-body > .erp-tabbar')).not.toBeNull()
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

  it('菜单搜索支持按模块编号直达', () => {
    renderShell('/dashboard')
    fireEvent.input(screen.getByRole('searchbox', { name: '搜索菜单' }), { target: { value: '1209' } })
    fireEvent.click(screen.getByRole('button', { name: '销售管理 / 销售子组 / 分组模块' }))
    expect(screen.getByText('WB')).toBeInTheDocument()
  })

  it('菜单搜索无命中时提示', () => {
    renderShell('/dashboard')
    fireEvent.input(screen.getByRole('searchbox', { name: '搜索菜单' }), { target: { value: '不存在的菜单' } })
    expect(screen.getByText('没有匹配的菜单')).toBeInTheDocument()
  })

  it('显示模式切换以单图标按钮显示在侧栏底部，点击写入 data-bs-theme 与 localStorage', () => {
    renderShell('/dashboard')
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('light')
    // 按钮始终展示"切换后"的模式：当前浅色 → 显示切换到深色
    fireEvent.click(screen.getByRole('button', { name: '切换到深色模式' }))
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('dark')
    expect(localStorage.getItem('erp-theme')).toBe('dark')

    fireEvent.click(screen.getByRole('button', { name: '切换到浅色模式' }))
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('light')
    expect(localStorage.getItem('erp-theme')).toBe('light')
  })

  it('用户菜单只保留个人设置与退出登录（模式切换已移出）', () => {
    renderShell('/dashboard')
    openUserMenu()
    expect(screen.getByRole('menuitem', { name: '个人设置' })).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: '退出登录' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: '深色模式' })).not.toBeInTheDocument()
  })

  it('折叠态下模式切换按钮保留在同一位置，仍可切换主题', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))

    fireEvent.click(screen.getByRole('button', { name: '切换到深色模式' }))
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('dark')
    expect(localStorage.getItem('erp-theme')).toBe('dark')
    // 切换后按钮文案随之翻转
    fireEvent.click(screen.getByRole('button', { name: '切换到浅色模式' }))
    expect(document.documentElement.getAttribute('data-bs-theme')).toBe('light')
  })

  it('折叠态打开用户菜单：放开侧栏裁剪，菜单项可用', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    openUserMenu()
    // 气泡向右弹出，需要侧栏放开 overflow，否则菜单会被 72px 宽度裁掉
    expect(document.querySelector('.erp-sidebar.erp-menu-open')).not.toBeNull()
    expect(screen.getByRole('menuitem', { name: '个人设置' })).toBeInTheDocument()
  })

  it('展开中的分组及其祖先保持激活态，收起后恢复', () => {
    renderShell('/dashboard')
    const group = screen.getByRole('button', { name: '销售管理' })
    expect(group.className).not.toContain('group-open')

    fireEvent.click(group)
    expect(group.className).toContain('group-open')

    fireEvent.click(screen.getByRole('button', { name: '销售子组' }))
    // 子级展开后祖先仍高亮，看得出当前展开分支挂在谁下面
    expect(group.className).toContain('group-open')
    expect(screen.getByRole('button', { name: '销售子组' }).className).toContain('group-open')

    fireEvent.click(screen.getByRole('button', { name: '销售子组' }))
    expect(screen.getByRole('button', { name: '销售子组' }).className).not.toContain('group-open')
    expect(group.className).toContain('group-open')
  })

  it('折叠态浮层按触发项位置落在可视区内，行数超上限改为内部滚动', () => {
    const longNav: AppBootstrap = {
      ...bootstrap,
      navigation: [
        { id: 'dashboard', label: '首页', route: '/dashboard', icon: 'dashboard' },
        {
          id: 'big',
          label: '大数据',
          icon: 'sales',
          children: Array.from({ length: 12 }, (_, index) => ({
            id: `m${index}`,
            label: `模块${index + 1}`,
            route: `/big/${index}`,
            icon: 'sales',
          })),
        },
      ],
    }
    renderShell('/dashboard', { bootstrap: longNav })
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))

    // 触发项贴近视口下沿：浮层整体上移，不越过屏幕底部
    const spy = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      top: 740, bottom: 772, left: 16, right: 56, width: 40, height: 32, x: 16, y: 740,
      toJSON: () => ({}),
    } as DOMRect)
    fireEvent.click(screen.getByRole('button', { name: '大数据' }))

    const flyout = screen.getByRole('menu', { name: '大数据' })
    const top = Number.parseFloat(flyout.style.top)
    const maxHeight = Number.parseFloat(flyout.style.maxHeight)
    expect(top).toBeGreaterThanOrEqual(8)
    expect(top + maxHeight).toBeLessThanOrEqual(window.innerHeight - 8)
    // 12 个子项不整块铺开：最多直接显示 10 行（行高 30px），其余走内部滚动
    expect(maxHeight).toBeLessThan(12 * 30)
    spy.mockRestore()
  })

  it('折叠态点一级菜单图标只弹出第一层，侧栏保持折叠', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    expect(document.documentElement.classList.contains('erp-sidebar-collapsed')).toBe(true)

    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))
    // 关键：不再自动展开侧栏
    expect(document.documentElement.classList.contains('erp-sidebar-collapsed')).toBe(true)
    const flyout = screen.getByRole('menu', { name: '销售管理' })
    expect(within(flyout).getByRole('menuitem', { name: '销售订单' })).toBeInTheDocument()
    // 只列当前一层的分组，不把整棵子树一次铺开
    expect(within(flyout).getByRole('menuitem', { name: '销售子组' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: '分组模块' })).not.toBeInTheDocument()
  })

  it('悬停分组项逐级展开下一层（N 级菜单）', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))

    fireEvent.mouseEnter(within(screen.getByRole('menu', { name: '销售管理' })).getByRole('menuitem', { name: '销售子组' }))
    const subMenu = screen.getByRole('menu', { name: '销售子组' })
    expect(within(subMenu).getByRole('menuitem', { name: '销售子页' })).toBeInTheDocument()
    expect(within(subMenu).getByRole('menuitem', { name: '分组模块' })).toBeInTheDocument()
    // 父层保留在屏幕上，形成级联
    expect(screen.getByRole('menu', { name: '销售管理' })).toBeInTheDocument()
  })

  it('点浮层最深一层的菜单项直达页面并收起全部浮层，侧栏仍折叠', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))
    fireEvent.mouseEnter(screen.getByRole('menuitem', { name: '销售子组' }))

    fireEvent.click(screen.getByRole('menuitem', { name: '分组模块' }))
    expect(screen.getByText('WB')).toBeInTheDocument()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(document.documentElement.classList.contains('erp-sidebar-collapsed')).toBe(true)
  })

  it('浮层再点图标、按 Esc、点外部均可收起', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    const groupToggle = screen.getByRole('button', { name: '销售管理' })

    fireEvent.click(groupToggle)
    expect(screen.getByRole('menu', { name: '销售管理' })).toBeInTheDocument()
    fireEvent.click(groupToggle)
    expect(screen.queryByRole('menu', { name: '销售管理' })).not.toBeInTheDocument()

    fireEvent.click(groupToggle)
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('menu', { name: '销售管理' })).not.toBeInTheDocument()

    fireEvent.click(groupToggle)
    fireEvent.mouseDown(document.body)
    expect(screen.queryByRole('menu', { name: '销售管理' })).not.toBeInTheDocument()
  })

  it('浮层内滚动保持打开，浮层外的滚动才收起', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))
    const flyout = screen.getByRole('menu', { name: '销售管理' })

    // 菜单项多时需要在浮层内部滚动查看，滚动条一动不该把菜单关掉
    fireEvent.scroll(flyout)
    expect(screen.getByRole('menu', { name: '销售管理' })).toBeInTheDocument()
    fireEvent.scroll(flyout.querySelector('.erp-nav-flyout-item')!)
    expect(screen.getByRole('menu', { name: '销售管理' })).toBeInTheDocument()

    // 侧栏导航滚动会让触发项位置失效，这时才收起
    fireEvent.scroll(document.querySelector('.navbar-nav')!)
    expect(screen.queryByRole('menu', { name: '销售管理' })).not.toBeInTheDocument()
  })

  it('侧栏展开后浮层收起，恢复树形展开行为', () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('button', { name: '折叠导航' }))
    fireEvent.click(screen.getByRole('button', { name: '销售管理' }))
    expect(screen.getByRole('menu', { name: '销售管理' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '展开导航' }))
    expect(screen.queryByRole('menu', { name: '销售管理' })).not.toBeInTheDocument()
    expect(document.documentElement.classList.contains('erp-sidebar-collapsed')).toBe(false)
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

  it('页面标题随路由变化（由标签标题承担）', () => {
    renderShell('/dashboard')
    expect(screen.getByRole('tab', { name: /首页/ })).toBeInTheDocument()
    renderShell('/settings/profile')
    expect(screen.getByRole('tab', { name: /个人设置/ })).toBeInTheDocument()
    renderShell('/workbench/1209/new')
    expect(screen.getByRole('tab', { name: /新增分组模块/ })).toBeInTheDocument()
  })

  it('主导航不再提供个人设置入口，该页仍可由用户菜单打开', () => {
    renderShell('/dashboard')
    expect(screen.queryByRole('link', { name: '个人设置' })).not.toBeInTheDocument()
    openProfileTab()
    expect(screen.getByText('PROFILE')).toBeInTheDocument()
  })

  it('跨模块关联浏览（from 参数）时标签标题附来源模块', () => {
    renderShell('/workbench/1401/view/A?from=1209')
    expect(screen.getByRole('tab', { name: /← 分组模块/ })).toBeInTheDocument()
  })

  it('常规浏览（无 from 参数）标签标题不带来源', () => {
    renderShell('/workbench/1401/view/A')
    // 首页标签常驻首位，当前页是最后一个标签
    const tabs = screen.getAllByRole('tab')
    expect(tabs[tabs.length - 1].textContent ?? '').not.toContain('←')
  })

  it('字段维护子页标题固定为「字段」', () => {
    renderShell('/admin/tables/PRODUCT_EDITION/fields')
    expect(screen.getByRole('tab', { name: /字段/ })).toBeInTheDocument()
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

  it('点菜单开新标签，重复打开复用已有标签，切换标签时地址栏跟随', () => {
    renderShell('/dashboard')
    openProfileTab()
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByText('PROFILE')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: /首页/ }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    expect(screen.getByText('DASH')).toBeInTheDocument()

    // 再次从菜单打开同一地址：聚焦已有标签，不新增
    openProfileTab()
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

  it('右键「关闭其它」保留被右键的标签与脏标签', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())
    openProfileTab()
    expect(screen.getAllByRole('tab')).toHaveLength(3)

    openTabMenu(/个人设置/)
    fireEvent.click(screen.getByRole('menuitem', { name: '关闭其它' }))
    // 被右键的标签（个人设置）+ 脏标签（脏页演示）+ 常驻首页保留，且被右键者成为活动标签
    expect(screen.getAllByRole('tab')).toHaveLength(3)
    expect(screen.getByRole('tab', { name: /首页/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /个人设置/ })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: /脏页演示/ })).toBeInTheDocument()
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/settings/profile')
  })

  it('右键「关闭全部」涉及脏标签时先确认，确认后落到首页', async () => {
    renderShell('/dashboard')
    fireEvent.click(screen.getByRole('link', { name: '脏页演示' }))
    fireEvent.change(screen.getByLabelText('标题'), { target: { value: '改动' } })
    await waitFor(() => expect(screen.getByLabelText('有未保存的改动')).toBeInTheDocument())

    openTabMenu(/脏页演示/)
    fireEvent.click(screen.getByRole('menuitem', { name: '关闭全部' }))
    expect(screen.getByRole('dialog', { name: '未保存改动确认' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '不保存并关闭' }))
    await waitFor(() => expect(screen.getAllByRole('tab')).toHaveLength(1))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    expect(screen.getByText('DASH')).toBeInTheDocument()
  })

  it('标签操作菜单：只剩一个标签时三项均禁用', () => {
    renderShell('/dashboard')
    openTabMenu(/首页/)
    expect(screen.getByRole('menuitem', { name: '关闭当前' })).toBeDisabled()
    expect(screen.getByRole('menuitem', { name: '关闭其它' })).toBeDisabled()
    expect(screen.getByRole('menuitem', { name: '关闭全部' })).toBeDisabled()
  })

  it('右键「关闭当前」关闭被右键的标签', () => {
    renderShell('/dashboard')
    openProfileTab()
    openTabMenu(/个人设置/)
    fireEvent.click(screen.getByRole('menuitem', { name: '关闭当前' }))
    expect(screen.getAllByRole('tab')).toHaveLength(1)
    expect(screen.getByRole('tab', { name: /首页/ })).toBeInTheDocument()
  })

  it('首页标签常驻首位、无关闭按钮，右键时「关闭当前」禁用', () => {
    renderShell('/counter')
    const tabs = screen.getAllByRole('tab')
    expect(tabs).toHaveLength(2)
    // 首位是首页，即使当前打开的是别的地址
    expect(tabs[0]).toHaveTextContent('首页')
    expect(screen.getByRole('tab', { name: /首页/ })).toBeInTheDocument()
    // 首页不提供关闭入口
    expect(screen.queryByRole('button', { name: '关闭标签 首页' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: '关闭标签 计数器' })).toBeInTheDocument()

    openTabMenu(/首页/)
    expect(screen.getByRole('menuitem', { name: '关闭当前' })).toBeDisabled()
  })

  it('首页标签不可关闭：Delete 与关闭按钮都不生效', () => {
    renderShell('/counter')
    const home = screen.getByRole('tab', { name: /首页/ })
    fireEvent.keyDown(home, { key: 'Delete' })
    expect(screen.getAllByRole('tab')).toHaveLength(2)
  })

  it('刷新恢复时列表里没有首页也会补上并置首', () => {
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify([
      { id: 't1', url: '/counter', label: '计数器' },
      { id: 't2', url: '/settings/profile', label: '个人设置' },
    ]))
    renderShell('/counter')
    const tabs = screen.getAllByRole('tab')
    expect(tabs).toHaveLength(3)
    expect(tabs[0]).toHaveTextContent('首页')
    expect(tabs[1]).toHaveTextContent('计数器')
  })

  it('刷新恢复的标签若标题解析不出来，保留原标题而不是退化成「页面」', () => {
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify([
      { id: 't1', url: '/p1', label: '采购订单' },
    ]))
    renderShell('/p1')
    expect(screen.getByRole('tab', { name: /采购订单/ })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: /页面/ })).not.toBeInTheDocument()
  })

  it('导航树里没有的地址用模块标题或路径末段顶着，不写占位文案', () => {
    // 报表路由在导航树里没有同名叶子，但模块 1209 有（分组模块）
    renderShell('/reports/1209')
    expect(screen.getByRole('tab', { name: /分组模块/ })).toBeInTheDocument()
    expect(screen.queryByRole('tab', { name: /^页面$/ })).not.toBeInTheDocument()
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
    openProfileTab()
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

  it('撞顶：已达上限 12 时拒绝新建，提示走轻提示而不是标签栏内', () => {
    const saved = Array.from({ length: 12 }, (_, index) => ({ id: `t${index + 1}`, url: `/p${index + 1}`, label: `页${index + 1}` }))
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify(saved))
    renderShell('/p1')
    expect(screen.getAllByRole('tab')).toHaveLength(12)

    openProfileTab()
    const toast = screen.getByRole('alert')
    expect(toast).toHaveTextContent('标签已达上限 12 个，请先关闭一个标签')
    expect(document.querySelector('.erp-toast-container')).toContainElement(toast)
    // 标签栏本身不再承载提示文案
    expect(screen.queryByText('标签已达上限 12 个，请先关闭一个标签', { selector: '.erp-tab-hint' })).toBeNull()
    expect(screen.getAllByRole('tab')).toHaveLength(12)
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/p1')
  })

  it('剩 1 个名额时提前提示，且提示可手动关闭', async () => {
    // 恢复时首页会补进来，故存 9 条 → 10 个标签，再开一个正好剩 1 个名额
    const saved = Array.from({ length: 9 }, (_, index) => ({ id: `t${index + 1}`, url: `/p${index + 1}`, label: `页${index + 1}` }))
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify(saved))
    renderShell('/p1')
    expect(screen.getAllByRole('tab')).toHaveLength(10)

    openProfileTab()
    expect(screen.getAllByRole('tab')).toHaveLength(11)
    expect(screen.getByRole('alert')).toHaveTextContent('标签已开 11 个，只剩 1 个名额')

    // 关闭后先淡出，动画结束才从 DOM 摘掉
    fireEvent.click(screen.getByRole('button', { name: '关闭提示' }))
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument())
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

  it('入口落在站点根路径：归一为首页，不为重定向地址留空标签', async () => {
    renderShell('/')
    await waitFor(() => expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard'))
    const tabs = screen.getAllByRole('tab')
    // 根路径没有页面，只为它开一个标签会渲染成空白面板并与首页形成两个同址标签
    expect(tabs).toHaveLength(1)
    expect(tabs[0]).toHaveTextContent('首页')
    expect(screen.queryByRole('tab', { name: /^页面$/ })).not.toBeInTheDocument()
    expect(screen.getByText('DASH')).toBeInTheDocument()

    const saved = JSON.parse(localStorage.getItem(workspaceTabsKey('u1')) ?? '[]') as { url: string }[]
    expect(saved.map((tab) => tab.url)).toEqual(['/dashboard'])
  })

  it('恢复时把历史遗留的根路径标签并进首页，不遗留空白标签', () => {
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify([
      { id: 'home', url: '/dashboard', label: '首页' },
      { id: 't1', url: '/', label: '' },
      { id: 't2', url: '/counter', label: '计数器' },
    ]))
    renderShell('/dashboard')
    expect(screen.getAllByRole('tab')).toHaveLength(2)
    expect(screen.getByRole('tab', { name: /首页/ })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('tab', { name: /计数器/ }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/counter')
    expect(screen.getByRole('button', { name: '计数 0' })).toBeInTheDocument()
  })

  it('关闭活动标签后接管的未挂载标签会渲染内容，不停在空白页', () => {
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify([
      { id: 'home', url: '/dashboard', label: '首页' },
      { id: 't2', url: '/counter', label: '计数器' },
    ]))
    renderShell('/counter')
    // 首页是恢复出来的、尚未激活，此时关闭活动标签会由它接管
    expect(screen.queryByText('DASH')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '关闭标签 计数器' }))
    expect(screen.getByTestId('browser-location')).toHaveTextContent('/dashboard')
    expect(screen.getByText('DASH')).toBeInTheDocument()
  })

  it('多标签逐一切换：被切到的标签一定有内容（不出现空白工作区）', () => {
    localStorage.setItem(workspaceTabsKey('u1'), JSON.stringify([
      { id: 't1', url: '/dashboard', label: '首页' },
      { id: 't2', url: '/counter', label: '计数器' },
      { id: 't3', url: '/settings/profile', label: '个人设置' },
      { id: 't4', url: '/workbench/1209', label: '分组模块' },
    ]))
    renderShell('/dashboard')

    const visibleText = () =>
      [...document.querySelectorAll('.erp-tab-panel')].find((panel) => !(panel as HTMLElement).hasAttribute('hidden'))?.textContent ?? ''

    expect(visibleText()).toContain('DASH')
    for (const [name, text] of [['计数器', '计数 0'], ['个人设置', 'PROFILE'], ['分组模块', 'WB'], ['首页', 'DASH']] as const) {
      fireEvent.click(screen.getByRole('tab', { name: new RegExp(name) }))
      expect(visibleText()).toContain(text)
    }
  })

  it('标签列表按用户持久化（last-write-wins）', () => {
    renderShell('/dashboard')
    openProfileTab()
    const saved = JSON.parse(localStorage.getItem(workspaceTabsKey('u1')) ?? '[]') as { url: string }[]
    expect(saved.map((tab) => tab.url)).toEqual(['/dashboard', '/settings/profile'])
  })

  it('回退开关关闭：隐藏标签栏并原地导航（单标签行为）', () => {
    localStorage.setItem(WORKSPACE_TABS_ENABLED_KEY, 'off')
    renderShell('/dashboard')
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()

    openProfileTab()
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
    openProfileTab()
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
    return render(<ToastProvider><RouterProvider router={router} /></ToastProvider>)
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

