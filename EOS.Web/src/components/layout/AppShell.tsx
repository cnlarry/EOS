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
  IconMoon,
  IconPackage,
  IconPalette,
  IconReportMoney,
  IconSearch,
  IconSettings,
  IconShoppingCart,
  IconSun,
  IconTools,
  IconTruck,
  IconTruckDelivery,
  IconUsers,
  IconWorld,
  IconZoomScan,
} from '@tabler/icons-react'
import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../../features/auth/authContext'
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

const avatarPalette = ['#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6']

function avatarColor(username: string): string {
  let hash = 0
  for (let index = 0; index < username.length; index += 1) {
    hash = (hash * 31 + username.charCodeAt(index)) >>> 0
  }
  return avatarPalette[hash % avatarPalette.length]
}

const pageTitles: Record<string, { section: string; title: string }> = {
  '/dashboard': { section: '首页', title: '工作台' },
  '/procurement/purchase-orders': { section: '采购管理', title: '采购订单' },
  '/admin/tables': { section: '系统管理', title: '数据表维护' },
  '/admin/menus': { section: '系统管理', title: '菜单管理' },
  '/admin/users': { section: '系统管理', title: '用户管理' },
  '/settings/profile': { section: '系统设置', title: '个人设置' },
}

function getInitialTheme(): Theme {
  const savedTheme = localStorage.getItem('erp-theme')
  if (savedTheme === 'light' || savedTheme === 'dark') return savedTheme
  return window.matchMedia('(prefers-color-scheme: dark)').matches
    ? 'dark'
    : 'light'
}

/** 深度展开导航树，返回全部叶子（用于页面标题与菜单搜索）。 */
function flattenLeaves(items: NavigationItem[]): NavigationItem[] {
  return items.flatMap((item) => (item.children?.length ? flattenLeaves(item.children) : [item]))
}

/** 查找命中路由的叶子节点祖先 id 链（不含叶子自身），用于自动展开当前分支。 */
function findAncestors(items: NavigationItem[], path: string): string[] {
  for (const item of items) {
    if (!item.children?.length) continue
    if (item.children.some((child) => child.route === path)) return [item.id]
    const nested = findAncestors(item.children, path)
    if (nested.length) return [item.id, ...nested]
  }
  return []
}

/** 判断子树内是否有叶子命中当前路由（分组节点高亮）。 */
function isSubtreeActive(item: NavigationItem, path: string): boolean {
  if (item.route === path) return true
  return item.children?.some((child) => isSubtreeActive(child, path)) ?? false
}

export function AppShell() {
  const [theme, setTheme] = useState<Theme>(getInitialTheme)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => localStorage.getItem('erp-sidebar-collapsed') === 'true')
  const [userMenuOpen, setUserMenuOpen] = useState(false)
  const [currentDate, setCurrentDate] = useState(() => new Date())
  const [menuQuery, setMenuQuery] = useState('')
  const searchInputRef = useRef<HTMLInputElement | null>(null)
  const { bootstrap, logout } = useAuth()
  const navigate = useNavigate()
  const navigation = bootstrap?.navigation ?? fallbackNavigation as unknown as NavigationItem[]
  const location = useLocation()
  const firstGroup = navigation.find((item) => item.children?.length)
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => new Set(firstGroup ? [firstGroup.id] : []))
  const isFormEditor = /\/(new|edit|view)$/.test(location.pathname)
  const basePath = location.pathname.replace(/\/(new|edit|view)$/, '')
  const allLeaves = useMemo(() => flattenLeaves(navigation), [navigation])
  const activeMenu = allLeaves.find((item) => item.route === basePath)
  const activeGroup = navigation.find((item) => item.children?.some((child) => isSubtreeActive(child, basePath)))
  const page: { section: string; module?: string; title: string } = isFormEditor
    ? {
        section: activeGroup?.label ?? 'ERP',
        module: activeMenu?.label,
        title: `${location.pathname.endsWith('/new') ? '新建' : location.pathname.endsWith('/view') ? '查看' : '编辑'}${activeMenu?.label ?? ''}`,
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

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        searchInputRef.current?.focus()
      } else if (event.key === '/' && document.activeElement?.tagName !== 'INPUT') {
        event.preventDefault()
        searchInputRef.current?.focus()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [])

  useEffect(() => {
    setSidebarOpen(false)
    setUserMenuOpen(false)
  }, [location.pathname])

  // 进入页面时自动展开当前模块所在的分支（含从菜单搜索直达的场景）
  useEffect(() => {
    const ancestors = findAncestors(navigation, basePath)
    if (ancestors.length === 0) return
    setExpandedIds((current) => {
      const next = new Set(current)
      ancestors.forEach((id) => next.add(id))
      return next.size === current.size ? current : next
    })
  }, [basePath, navigation])

  useEffect(() => {
    if (!userMenuOpen) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!(event.target as HTMLElement).closest('.erp-user-menu')) setUserMenuOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setUserMenuOpen(false)
    }
    document.addEventListener('click', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('click', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [userMenuOpen])

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

  const toggleExpanded = (id: string) => {
    setExpandedIds((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const trimmedQuery = menuQuery.trim().toLowerCase()
  const searchResults = useMemo(() => {
    if (!trimmedQuery) return []
    const hits: { item: NavigationItem; breadcrumb: string }[] = []
    const walk = (items: NavigationItem[], trail: string[]) => {
      for (const item of items) {
        if (item.children?.length) {
          walk(item.children, [...trail, item.label])
        } else {
          const label = item.label.toLowerCase()
          const moduleId = item.moduleId != null ? String(item.moduleId) : ''
          const alias = (item.alias ?? '').toLowerCase()
          if (label.includes(trimmedQuery) || moduleId.includes(trimmedQuery) || alias.includes(trimmedQuery)) {
            hits.push({ item, breadcrumb: [...trail, item.label].join(' / ') })
          }
        }
      }
    }
    walk(navigation, [])
    return hits.slice(0, 50)
  }, [navigation, trimmedQuery])

  const renderChildren = (items: NavigationItem[], depth: number) =>
    items.map((item) => {
      if (item.children?.length) {
        const Icon = navigationIcons[item.icon] ?? IconFolder
        const isExpanded = expandedIds.has(item.id)
        const isGroupActive = item.children.some((child) => isSubtreeActive(child, basePath))
        return (
          <Fragment key={item.id}>
            <div className={`erp-nav-group erp-nav-group-depth-${depth}`}>
              <button
                className={`nav-link erp-nav-group-toggle ${isGroupActive ? 'group-active' : ''}`}
                type="button"
                aria-expanded={isExpanded}
                title={sidebarCollapsed ? item.label : undefined}
                onClick={() => {
                  if (sidebarCollapsed) setSidebarCollapsed(false)
                  toggleExpanded(item.id)
                }}
              >
                {depth === 1 && (
                  <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>
                )}
                {depth > 1 && <IconChevronRight className="erp-nav-chevron" size={13} />}
                <span className="nav-link-title">{item.label}</span>
              </button>
            </div>
            {isExpanded && !sidebarCollapsed && (
              <div className={`erp-nav-children erp-nav-children-depth-${depth + 1}`}>{renderChildren(item.children, depth + 1)}</div>
            )}
          </Fragment>
        )
      }
      const Icon = navigationIcons[item.icon] ?? IconFolder
      return (
        <Fragment key={item.id}>
          <NavLink
            className={({ isActive }) => `nav-link ${depth > 1 ? `erp-nav-child erp-nav-child-depth-${depth}` : ''} ${isActive ? 'active' : ''}`}
            to={item.route!}
            title={sidebarCollapsed ? item.label : undefined}
          >
            {depth === 1 && <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>}
            <span className="nav-link-title">{item.label}</span>
          </NavLink>
        </Fragment>
      )
    })

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
          {!sidebarCollapsed && (
            <div className="erp-nav-search">
              <IconSearch size={16} aria-hidden="true" />
              <input
                type="search"
                ref={searchInputRef}
                value={menuQuery}
                onChange={(event) => setMenuQuery(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Escape') setMenuQuery('')
                }}
                placeholder="搜索菜单…"
                aria-label="搜索菜单"
              />
              {!menuQuery && <span className="erp-nav-kbd">Ctrl K</span>}
              {menuQuery && (
                <button
                  className="erp-nav-search-clear"
                  type="button"
                  aria-label="清除搜索"
                  onClick={() => setMenuQuery('')}
                >
                  ×
                </button>
              )}
            </div>
          )}
          <div className="navbar-nav">
            {trimmedQuery ? (
              <div className="erp-nav-search-results">
                {searchResults.length === 0 ? (
                  <div className="erp-nav-search-empty">没有匹配的菜单</div>
                ) : (
                  searchResults.map(({ item, breadcrumb }) => (
                    <button
                      key={item.id}
                      type="button"
                      className="erp-nav-search-result"
                      onClick={() => {
                        navigate(item.route!)
                        setMenuQuery('')
                      }}
                    >
                      <span className="erp-nav-search-crumb">{breadcrumb}</span>
                    </button>
                  ))
                )}
              </div>
            ) : (
              renderChildren(navigation, 1)
            )}
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
              <div className="dropdown erp-user-menu">
                <button
                  className="btn erp-user dropdown-toggle"
                  type="button"
                  aria-label="用户菜单"
                  aria-haspopup="menu"
                  aria-expanded={userMenuOpen}
                  onClick={() => setUserMenuOpen((open) => !open)}
                >
                  {bootstrap?.user.avatarUrl ? (
                    <span className="avatar avatar-sm"><img src={bootstrap.user.avatarUrl} alt="" /></span>
                  ) : (
                    <span className="avatar avatar-sm" style={{ backgroundColor: avatarColor(bootstrap?.user.username ?? 'user') }}>{bootstrap?.user.avatarText}</span>
                  )}
                  <span className="d-none d-sm-block text-start">
                    <span className="d-block fw-semibold">{bootstrap?.user.displayName}</span>
                    <small className="text-secondary">{bootstrap?.user.username}</small>
                  </span>
                  <IconChevronDown size={16} className="text-secondary" />
                </button>
                {userMenuOpen && (
                  <div className="dropdown-menu dropdown-menu-end show" role="menu">
                    <button className="dropdown-item" type="button" role="menuitem" onClick={() => navigate('/settings/profile')}>个人设置</button>
                    <div className="dropdown-divider" />
                    <button className="dropdown-item text-danger" type="button" role="menuitem" onClick={() => void logout().then(() => navigate('/login', { replace: true }))}>退出登录</button>
                  </div>
                )}
              </div>
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
