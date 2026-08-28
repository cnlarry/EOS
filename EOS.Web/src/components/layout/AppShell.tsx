import {
  IconChevronDown,
  IconChevronLeft,
  IconChevronRight,
  IconFolder,
  IconHome,
  IconMenu2,
  IconMoon,
  IconSearch,
  IconSun,
} from '@tabler/icons-react'
import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from '../../features/auth/authContext'
import type { NavigationItem } from '../../features/auth/types'
import { AssistantDock } from '../../features/assistant/AssistantDock'
import { navigationIcons } from './navigationIcons'
import { childPad, dotLeft, groupPad, lineSidebar } from './menuDepth'
import { FormBreadcrumbContext, type FormBreadcrumb } from './FormBreadcrumbContext'
import { PageBreadcrumbContext, type PageBreadcrumb } from './PageBreadcrumbContext'
import { workbenchAction, workbenchList, workbenchModuleId } from '../../features/document-workbench/workbenchPath'

type Theme = 'light' | 'dark'

const SIDEBAR_WIDTH_KEY = 'erp-sidebar-width'
const SIDEBAR_WIDTH_MIN = 160
const SIDEBAR_WIDTH_MAX = 480
const DEFAULT_SIDEBAR_WIDTH = 220

const RECENT_MODULES_KEY = 'erp-dashboard-recent'
const RECENT_MODULES_MAX = 8

const fallbackNavigation = [
  { id: 'dashboard', label: '首页', route: '/dashboard', icon: 'dashboard' },
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

const avatarPalette = ['#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6']

function avatarColor(username: string): string {
  let hash = 0
  for (let index = 0; index < username.length; index += 1) {
    hash = (hash * 31 + username.charCodeAt(index)) >>> 0
  }
  return avatarPalette[hash % avatarPalette.length]
}

const pageTitles: Record<string, { section: string; title: string }> = {
  '/dashboard': { section: '首页', title: '首页' },
  '/admin/tables': { section: '系统管理', title: '数据表维护' },
  '/admin/menus': { section: '系统管理', title: '菜单管理' },
  '/admin/groups': { section: '系统管理', title: '用户组管理' },
  '/admin/users': { section: '系统管理', title: '用户管理' },
  '/settings/profile': { section: '系统设置', title: '个人设置' },
}

/** 查找命中路由的完整功能路径（含叶子自身），用于面包屑展示全部层级。 */
function findPath(items: NavigationItem[], path: string): NavigationItem[] {
  for (const item of items) {
    if (item.route === path) return [item]
    if (item.children?.length) {
      const nested = findPath(item.children, path)
      if (nested.length) return [item, ...nested]
    }
  }
  return []
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
  const [sidebarWidth, setSidebarWidth] = useState(() => {
    const saved = Number(localStorage.getItem(SIDEBAR_WIDTH_KEY))
    return Number.isFinite(saved) && saved >= SIDEBAR_WIDTH_MIN && saved <= SIDEBAR_WIDTH_MAX ? saved : DEFAULT_SIDEBAR_WIDTH
  })
  const [sidebarResizing, setSidebarResizing] = useState(false)
  const [userMenuOpen, setUserMenuOpen] = useState(false)
  const [currentDate, setCurrentDate] = useState(() => new Date())
  const [menuQuery, setMenuQuery] = useState('')
  const searchInputRef = useRef<HTMLInputElement | null>(null)
  // 统一表单上抛的单据面包屑（模块标题 + 单号），由 FormEditorPage 写入、面包屑渲染消费
  const [formBreadcrumb, setFormBreadcrumb] = useState<FormBreadcrumb | null>(null)
  // 定制页子页面包屑（如 用户组管理 > 采购 组权限），由子页写入、面包屑渲染消费
  const [pageCrumb, setPageCrumb] = useState<PageBreadcrumb | null>(null)
  const { bootstrap, logout } = useAuth()
  const navigate = useNavigate()
  const navigation = bootstrap?.navigation ?? fallbackNavigation as unknown as NavigationItem[]
  const location = useLocation()
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => new Set())
  const sidebarWidthRef = useRef(sidebarWidth)
  const sidebarResizeRef = useRef<{ startX: number; startWidth: number } | null>(null)
  const wbAction = workbenchAction(location.pathname)
  const isFormEditor = wbAction !== null
  const workbenchId = workbenchModuleId(location.pathname)
  const basePath = workbenchId ? workbenchList(workbenchId) : location.pathname.replace(/\/(new|edit|view)$/, '')
  // 跨模块关联字段浏览（FieldBrowseLink 带入 from）：面包屑/分区按来源工作台呈现，
  // 叶子（来源模块）可点击返回原工作台列表。仅当为合法模块 ID 且不同于当前模块时生效。
  const pathModuleId = workbenchId
  const fromParam = (() => {
    const raw = new URLSearchParams(location.search).get('from')
    if (!raw || !/^\d+$/.test(raw) || (pathModuleId && raw === pathModuleId)) return null
    return raw
  })()
  const crumbBasePath = fromParam ? workbenchList(fromParam) : basePath
  const allLeaves = useMemo(() => flattenLeaves(navigation), [navigation])
  const activeMenu = allLeaves.find((item) => item.route === crumbBasePath)
  const activeGroup = navigation.find((item) => item.children?.some((child) => isSubtreeActive(child, crumbBasePath)))
  // 字段维护子页（2302 /admin/tables/:tableId/fields）：
  // 面包屑固定为 系统管理 > 数据表维护 > 数据表维护 > {表名} > 字段
  const fieldAdminFields = location.pathname.match(/^\/admin\/tables\/([^/]+)\/fields$/)
  const fieldAdminCrumb = fieldAdminFields
    ? { leads: ['系统管理', '数据表维护', '数据表维护', decodeURIComponent(fieldAdminFields[1])], title: '字段' }
    : null
  const groupAdminPage = location.pathname.match(/^\/admin\/groups\/[^/]+\/(rights|report-rights|members)$/)
  const page: { section: string; title: string } = fieldAdminCrumb
    ? { section: '系统管理', title: fieldAdminCrumb.title }
    : pageCrumb
      ? { section: '系统管理', title: pageCrumb.title }
    : groupAdminPage
      ? {
          section: '系统管理',
          title: groupAdminPage[1] === 'rights'
            ? '用户组权限'
            : groupAdminPage[1] === 'report-rights'
              ? '用户组报表权限'
              : '用户组成员',
        }
    : isFormEditor
      ? (() => {
          const op = wbAction === 'new' ? '新增' : wbAction === 'copy' ? '复制' : wbAction === 'view' ? '查看' : '编辑'
          const moduleLabel = formBreadcrumb?.moduleTitle ?? activeMenu?.label ?? ''
          const docNo = op !== '新增' && formBreadcrumb?.docNo ? formBreadcrumb.docNo : null
          return {
            section: activeGroup?.label ?? 'ERP',
            title: `${op}${moduleLabel}${docNo ? `：${docNo}` : ''}`,
          }
        })()
      : pageTitles[location.pathname] ?? {
          section: activeGroup?.label ?? 'ERP',
          title: activeMenu?.label ?? '页面',
        }
  const breadcrumbPath = useMemo(() => findPath(navigation, crumbBasePath), [navigation, crumbBasePath])
  // 完整路径：命中导航树时展示全部祖先 + 叶子；未命中（如直达维护页）回退「分区 + 标题」。
  // 统一表单页（查看/编辑/新增）：叶子也作为面包屑项且可点击返回工作台列表；
  // 其余页面保持「祖先 + 叶子标题」两段式（叶子由 h1 承担）。
  const breadcrumbLeads: { label: string; to?: string }[] = fieldAdminCrumb
    ? fieldAdminCrumb.leads.map(label => ({ label }))
    : pageCrumb
      ? pageCrumb.leads
      : breadcrumbPath.length === 0
      ? [{ label: page.section }]
      : isFormEditor
        ? breadcrumbPath.map((item, index) => (index === breadcrumbPath.length - 1
            ? { label: item.label, to: item.route }
            : { label: item.label }))
        : breadcrumbPath.slice(0, -1).map(item => ({ label: item.label }))

  useEffect(() => {
    document.documentElement.setAttribute('data-bs-theme', theme)
    localStorage.setItem('erp-theme', theme)
  }, [theme])

  useEffect(() => {
    document.documentElement.classList.toggle('erp-sidebar-collapsed', sidebarCollapsed)
    localStorage.setItem('erp-sidebar-collapsed', String(sidebarCollapsed))
  }, [sidebarCollapsed])

  const clampSidebarWidth = (width: number) =>
    Math.min(SIDEBAR_WIDTH_MAX, Math.max(SIDEBAR_WIDTH_MIN, Math.round(width)))

  const startSidebarResize = (event: React.PointerEvent) => {
    if (window.innerWidth < 992 || sidebarCollapsed) return
    event.preventDefault()
    sidebarResizeRef.current = { startX: event.clientX, startWidth: sidebarWidthRef.current }
    setSidebarResizing(true)
    document.body.style.userSelect = 'none'
    try {
      event.currentTarget.setPointerCapture?.(event.pointerId)
    } catch {
      // jsdom/无活动指针时忽略，拖拽仍可继续
    }
  }

  const resizeSidebar = (event: React.PointerEvent) => {
    const state = sidebarResizeRef.current
    if (!state) return
    const next = clampSidebarWidth(state.startWidth + (event.clientX - state.startX))
    sidebarWidthRef.current = next
    setSidebarWidth(next)
  }

  const endSidebarResize = () => {
    if (!sidebarResizeRef.current) return
    sidebarResizeRef.current = null
    setSidebarResizing(false)
    document.body.style.userSelect = ''
    localStorage.setItem(SIDEBAR_WIDTH_KEY, String(sidebarWidthRef.current))
  }

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

  // 离开定制页子页时清空页面级面包屑（防止串到其它页面）
  useEffect(() => {
    if (!location.pathname.match(/^\/admin\/(groups\/[^/]+\/(rights|report-rights|members)|users\/[^/]+\/(rights|report-rights|groups)|fields\/[^/]+\/[^/]+)$/)) {
      setPageCrumb(null)
    }
  }, [location.pathname])

  // 记录最近访问的业务模块（工作台/报表等叶子），供 dashboard 快捷入口使用；本地持久化
  useEffect(() => {
    if (basePath === '/dashboard' || basePath === '/login') return
    const leaf = allLeaves.find((item) => item.route === basePath)
    if (!leaf) return
    let saved: string[]
    try {
      saved = JSON.parse(localStorage.getItem(RECENT_MODULES_KEY) ?? '[]') as string[]
      if (!Array.isArray(saved)) saved = []
    } catch {
      saved = []
    }
    const next = [leaf.route!, ...saved.filter((route) => route !== leaf.route)].slice(0, RECENT_MODULES_MAX)
    localStorage.setItem(RECENT_MODULES_KEY, JSON.stringify(next))
  }, [basePath, allLeaves])

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
        const groupStyle =
          depth > 1 ? ({ '--menu-gpad': `${groupPad(depth)}px` } as React.CSSProperties) : undefined
        return (
          <Fragment key={item.id}>
            <div className={`erp-nav-group erp-nav-group-depth-${depth}`} style={groupStyle}>
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
              <div
                className={`erp-nav-children erp-nav-children-depth-${depth + 1}`}
                style={{ '--menu-line': `${lineSidebar(depth + 1)}px` } as React.CSSProperties}
              >
                {renderChildren(item.children, depth + 1)}
              </div>
            )}
          </Fragment>
        )
      }
      const Icon = navigationIcons[item.icon] ?? IconFolder
      const childStyle =
        depth > 1
          ? ({ '--menu-cpad': `${childPad(depth)}px`, '--menu-dot': `${dotLeft(depth)}px` } as React.CSSProperties)
          : undefined
      return (
        <Fragment key={item.id}>
          <NavLink
            className={({ isActive }) => `nav-link ${depth > 1 ? `erp-nav-child erp-nav-child-depth-${depth}` : ''} ${isActive ? 'active' : ''}`}
            to={item.route!}
            title={sidebarCollapsed ? item.label : undefined}
            style={childStyle}
          >
            {depth === 1 && <span className="nav-link-icon"><Icon size={18} stroke={1.7} /></span>}
            <span className="nav-link-title">{item.label}</span>
          </NavLink>
        </Fragment>
      )
    })

  return (
    <div
      className={`page erp-shell${sidebarResizing ? ' erp-sidebar-resizing' : ''}`}
      style={{ '--erp-sidebar-width': `${sidebarWidth}px` } as React.CSSProperties}
    >
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

      <div
        className="erp-sidebar-resizer"
        role="separator"
        aria-orientation="vertical"
        aria-label="调整菜单宽度"
        title="拖动调整菜单宽度"
        onPointerDown={startSidebarResize}
        onPointerMove={resizeSidebar}
        onPointerUp={endSidebarResize}
        onPointerCancel={endSidebarResize}
      />

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
              {breadcrumbLeads.map((crumb, index) => (
                <Fragment key={`${crumb.label}-${index}`}>
                  {crumb.to ? (
                    <NavLink to={crumb.to} className="erp-context-section erp-context-link">{crumb.label}</NavLink>
                  ) : (
                    <span className="erp-context-section">{crumb.label}</span>
                  )}
                  <IconChevronRight className="erp-context-separator" size={16} stroke={2} aria-hidden="true" />
                </Fragment>
              ))}
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
                  className="btn erp-user"
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
            <FormBreadcrumbContext.Provider value={{ breadcrumb: formBreadcrumb, setBreadcrumb: setFormBreadcrumb }}>
              <PageBreadcrumbContext.Provider value={{ breadcrumb: pageCrumb, setBreadcrumb: setPageCrumb }}>
                <Outlet />
              </PageBreadcrumbContext.Provider>
            </FormBreadcrumbContext.Provider>
          </div>
        </main>

        <AssistantDock />
      </div>
    </div>
  )
}
