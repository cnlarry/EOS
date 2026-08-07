import {
  IconBarcode,
  IconBriefcase,
  IconBuildingFactory,
  IconCar,
  IconChartBar,
  IconChevronDown,
  IconChevronLeft,
  IconChevronRight,
  IconClipboardCheck,
  IconCoins,
  IconDashboard,
  IconDatabase,
  IconFileText,
  IconFolder,
  IconGitBranch,
  IconHome,
  IconMenu2,
  IconPackage,
  IconPalette,
  IconReportMoney,
  IconShoppingCart,
  IconTruckDelivery,
  IconUsers,
  IconMoon,
  IconSettings,
  IconSun,
  IconTools,
  IconTruck,
  IconWorld,
  IconZoomScan,
} from '@tabler/icons-react'
import { Fragment, useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../../features/auth/AuthProvider'
import type { NavigationItem } from '../../features/auth/types'

type Theme = 'light' | 'dark'

const fallbackNavigation = [
  { id: 'dashboard', label: '工作台', route: '/dashboard', icon: 'dashboard' },
  {
    id: 'procurement',
    label: '采购管理',
    icon: 'procurement',
    children: [
      { id: 'po', label: '采购订单', route: '/procurement/purchase-orders', icon: 'procurement' },
      { id: 'req', label: '请购单', route: '/procurement/requisitions', icon: 'procurement' },
      { id: 'recv', label: '采购收货', route: '/procurement/receipts', icon: 'procurement' },
    ],
  },
  {
    id: 'sales',
    label: '销售管理',
    icon: 'sales',
    children: [
      { id: 'so', label: '销售订单', route: '/sales/orders', icon: 'sales' },
      { id: 'quot', label: '报价单', route: '/sales/quotations', icon: 'sales' },
      { id: 'del', label: '销售发货', route: '/sales/deliveries', icon: 'sales' },
    ],
  },
  {
    id: 'inventory',
    label: '库存管理',
    icon: 'inventory',
    children: [
      { id: 'stock', label: '库存查询', route: '/inventory/stock', icon: 'inventory' },
      { id: 'transfer', label: '调拨单', route: '/inventory/transfers', icon: 'inventory' },
    ],
  },
  { id: 'settings', label: '个人设置', route: '/settings/profile', icon: 'settings' },
]

const navigationIcons: Record<string, typeof IconDashboard> = {
  dashboard: IconDashboard,
  procurement: IconShoppingCart,
  sales: IconReportMoney,
  inventory: IconTruckDelivery,
  settings: IconSettings,
  folder: IconFolder,
  production: IconBuildingFactory,
  hr: IconUsers,
  finance: IconCoins,
  quality: IconClipboardCheck,
  report: IconChartBar,
  base: IconDatabase,
  equipment: IconTools,
  customer: IconBriefcase,
  supplier: IconTruck,
  document: IconFileText,
  product: IconPackage,
  vehicle: IconCar,
  barcode: IconBarcode,
  workflow: IconGitBranch,
  sample: IconPalette,
  query: IconZoomScan,
  outsource: IconTruck,
  customs: IconWorld,
}

const pageTitles: Record<string, { section: string; title: string }> = {
  '/dashboard': { section: '首页', title: '工作台' },
  '/procurement/purchase-orders': { section: '采购管理', title: '采购订单' },
  '/admin/tables': { section: '系统管理', title: '数据表维护' },
  '/settings/profile': { section: '系统设置', title: '个人设置' },
}

function getInitialTheme(): Theme {
  const savedTheme = localStorage.getItem('erp-theme')
  if (savedTheme === 'light' || savedTheme === 'dark') return savedTheme
  return window.matchMedia('(prefers-color-scheme: dark)').matches
    ? 'dark'
    : 'light'
}

export function AppShell() {
  const [theme, setTheme] = useState<Theme>(getInitialTheme)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => localStorage.getItem('erp-sidebar-collapsed') === 'true')
  const [expandedGroup, setExpandedGroup] = useState<string | null>('采购管理')
  const [currentDate, setCurrentDate] = useState(() => new Date())
  const { bootstrap, logout } = useAuth()
  const navigate = useNavigate()
  const navigation = bootstrap?.navigation ?? fallbackNavigation as unknown as NavigationItem[]
  const location = useLocation()
  const isFormEditor = /\/(new|edit)$/.test(location.pathname)
  const basePath = location.pathname.replace(/\/(new|edit)$/, '')
  const activeMenu = navigation.flatMap((item) => item.children ?? [item]).find((item) => item.route === basePath)
  const activeGroup = navigation.find((item) => item.children?.some((child) => child.route === basePath))
  const page: { section: string; module?: string; title: string } = isFormEditor
    ? {
        section: activeGroup?.label ?? 'ERP',
        module: activeMenu?.label,
        title: `${location.pathname.endsWith('/new') ? '新建' : '编辑'}${activeMenu?.label ?? ''}`,
      }
    : pageTitles[location.pathname] ?? {
        section: activeGroup?.label ?? 'ERP',
        title: activeMenu?.label ?? '页面',
      }

  useEffect(() => {
    document.documentElement.setAttribute('data-bs-theme', theme)
    localStorage.setItem('erp-theme', theme)
  }, [theme])

  useEffect(() => {
    document.documentElement.classList.toggle('erp-sidebar-collapsed', sidebarCollapsed)
    localStorage.setItem('erp-sidebar-collapsed', String(sidebarCollapsed))
  }, [sidebarCollapsed])

  useEffect(() => setSidebarOpen(false), [location.pathname])

  useEffect(() => {
    const updateDate = () => setCurrentDate(new Date())
    const now = new Date()
    const millisecondsUntilTomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1).getTime() - now.getTime()
    let dailyTimer: number | undefined
    const midnightTimer = window.setTimeout(() => {
      updateDate()
      dailyTimer = window.setInterval(updateDate, 24 * 60 * 60 * 1000)
    }, millisecondsUntilTomorrow)

    return () => {
      window.clearTimeout(midnightTimer)
      if (dailyTimer !== undefined) window.clearInterval(dailyTimer)
    }
  }, [])

  const dateLabel = new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(currentDate)
  const weekdayLabel = new Intl.DateTimeFormat('zh-CN', { weekday: 'long' }).format(currentDate)

  return (
    <div className="page erp-shell">
      <aside
        className={`navbar navbar-vertical navbar-expand-lg erp-sidebar ${sidebarOpen ? 'show' : ''}`}
        aria-label="主导航"
        title={sidebarCollapsed ? '展开导航' : undefined}
      >
        <div className="container-fluid">
          <div className="navbar-brand navbar-brand-autodark">
            <span className="erp-brand-mark" aria-hidden="true">
              E
            </span>
            <span>
              <strong>EOS</strong>
              <small>企业操作系统</small>
            </span>
          </div>
          <div className="navbar-nav pt-lg-3">
            {navigation.map((item) => {
              const Icon = navigationIcons[item.icon] ?? IconFolder
              if (item.children) {
                const isExpanded = expandedGroup === item.label
                const isGroupActive = item.children.some((child) => location.pathname.startsWith(child.route!))
                return (
                  <Fragment key={item.label}>
                    <div className="erp-nav-group">
                      <button
                        className={`nav-link erp-nav-group-toggle ${isGroupActive ? 'group-active' : ''}`}
                        type="button"
                        aria-expanded={isExpanded}
                        title={sidebarCollapsed ? item.label : undefined}
                        onClick={() => {
                          if (sidebarCollapsed) setSidebarCollapsed(false)
                          setExpandedGroup((current) => current === item.label ? null : item.label)
                        }}
                      >
                        <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>
                        <span className="nav-link-title">{item.label}</span>
                        <IconChevronDown className="erp-nav-chevron" size={16} />
                      </button>
                    </div>
                    {isExpanded && !sidebarCollapsed && (
                      <div className="erp-nav-children">
                        {item.children.map((child) => (
                          <NavLink className={({ isActive }) => `nav-link erp-nav-child ${isActive ? 'active' : ''}`} key={child.route} to={child.route!}>
                            <span className="erp-nav-child-marker" aria-hidden="true" />
                            <span className="nav-link-title">{child.label}</span>
                          </NavLink>
                        ))}
                      </div>
                    )}
                  </Fragment>
                )
              }
              return (
                <NavLink className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`} key={item.route} to={item.route!} title={sidebarCollapsed ? item.label : undefined}>
                  <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>
                  <span className="nav-link-title">{item.label}</span>
                </NavLink>
              )
            })}
          </div>
          <button
            className="btn btn-icon btn-ghost-secondary erp-sidebar-toggle d-none d-lg-inline-flex"
            type="button"
            aria-label={sidebarCollapsed ? '展开导航' : '折叠导航'}
            title={sidebarCollapsed ? '展开导航' : '折叠导航'}
            onClick={() => setSidebarCollapsed((collapsed) => !collapsed)}
          >
            {sidebarCollapsed ? <IconChevronRight size={18} /> : <IconChevronLeft size={18} />}
          </button>
        </div>
      </aside>

      {sidebarOpen && (
        <button
          className="erp-sidebar-backdrop"
          aria-label="关闭导航"
          onClick={() => setSidebarOpen(false)}
          type="button"
        />
      )}

      <div className="page-wrapper">
        <div className="erp-context-bar d-print-none">
          <div className="container-fluid px-3 px-lg-4">
            <nav aria-label="当前位置">
              <IconHome className="erp-context-home" size={18} stroke={2} aria-hidden="true" />
              <span className="erp-context-section">{page.section}</span>
              {page.module ? (
                <>
                  <IconChevronRight className="erp-context-separator" size={16} stroke={2} aria-hidden="true" />
                  <span className="erp-context-section">{page.module}</span>
                </>
              ) : null}
              <IconChevronRight className="erp-context-separator" size={16} stroke={2} aria-hidden="true" />
              <h1>{page.title}</h1>
            </nav>
            <div className="erp-context-user ms-auto">
              <time className="erp-context-date d-none d-md-flex" dateTime={currentDate.toISOString().slice(0, 10)} title="今天">
                <span>{dateLabel}</span>
                <strong>{weekdayLabel}</strong>
              </time>
              <button
                className="btn btn-icon btn-ghost-secondary"
                type="button"
                aria-label={theme === 'light' ? '切换到深色主题' : '切换到浅色主题'}
                onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}
              >
                {theme === 'light' ? <IconMoon size={20} /> : <IconSun size={20} />}
              </button>
              <button className="btn erp-user" type="button" aria-label="退出登录" title="点击退出登录" onClick={() => void logout().then(() => navigate('/login', { replace: true }))}>
                <span className="avatar avatar-sm">{bootstrap?.user.avatarText}</span>
                <span className="d-none d-sm-block text-start">
                  <span className="d-block fw-semibold">{bootstrap?.user.displayName}</span>
                  <small className="text-secondary">{bootstrap?.user.roleName}</small>
                </span>
                <IconChevronDown size={16} className="text-secondary" />
              </button>
            </div>
          </div>
        </div>

        <button
          className="btn btn-icon btn-ghost-secondary d-lg-none erp-mobile-menu-trigger"
          type="button"
          aria-label="打开导航"
          onClick={() => setSidebarOpen(true)}
        >
          <IconMenu2 size={22} />
        </button>

        <main className="page-body">
          <div className="container-fluid px-3 px-lg-4">
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  )
}
