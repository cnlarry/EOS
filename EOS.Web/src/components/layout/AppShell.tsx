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
import { Fragment, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState, type CSSProperties } from 'react'
import { NavLink, UNSAFE_DataRouterContext, parsePath, useLocation, useNavigate, type RouteObject } from 'react-router-dom'
import { useAuth } from '../../features/auth/authContext'
import type { NavigationItem } from '../../features/auth/types'
import { AssistantDock } from '../../features/assistant/AssistantDock'
import { navigationIcons } from './navigationIcons'
import { childPad, dotLeft, groupPad, lineSidebar } from './menuDepth'
import { workbenchAction, workbenchList, workbenchModuleId } from '../../features/document-workbench/workbenchPath'
import { RECENT_MODULES_KEY, SIDEBAR_COLLAPSED_KEY, SIDEBAR_WIDTH_KEY, THEME_KEY, workspaceTabsKey } from '../../lib/storageKeys'
import { PAGE_META } from '../../app/routeMeta'
import { WORKSPACE_ROUTES } from '../../app/workspaceRoutes'
import { WorkspaceNavContext, tabLinkHandler } from './WorkspaceNavContext'
import { DirtyConfirmDialog } from './DirtyConfirmDialog'
import { WorkspacePanel } from './WorkspacePanel'
import { WorkspaceNavGuard } from './WorkspaceNavGuard'
import { WorkspaceTabBar } from './WorkspaceTabBar'
import { WorkspaceDirtyContext, type TabDirtyHandlers } from './workspaceDirty'
import {
  EMPTY_TAB_CRUMB,
  HINT_TAB_LIMIT,
  MAX_TABS,
  TAB_BAR_HEIGHT,
  createWorkspaceState,
  fromModuleIdOf,
  moduleIdOfUrl,
  neighborAfterClose,
  parsePersistedTabs,
  restoreWorkspaceState,
  serializeTabs,
  tabUrlOf,
  workspaceReducer,
  workspaceTabsEnabled,
  type TabCrumb,
  type TabCrumbPatch,
} from './workspaceTabs'

type Theme = 'light' | 'dark'

const SIDEBAR_WIDTH_MIN = 160
const SIDEBAR_WIDTH_MAX = 480
const DEFAULT_SIDEBAR_WIDTH = 220

/** 待确认的关闭/离开动作 */
type PendingConfirm = { kind: 'close'; tabId: string } | { kind: 'all' } | { kind: 'logout' } | null

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
  const savedTheme = localStorage.getItem(THEME_KEY)
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

interface AppShellProps {
  /** 工作区路由表；默认取应用工作区路由表，测试可注入夹具路由 */
  routes?: RouteObject[]
}

export function AppShell({ routes = WORKSPACE_ROUTES }: AppShellProps = {}) {
  const [theme, setTheme] = useState<Theme>(getInitialTheme)
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === 'true')
  const [sidebarWidth, setSidebarWidth] = useState(() => {
    const saved = Number(localStorage.getItem(SIDEBAR_WIDTH_KEY))
    return Number.isFinite(saved) && saved >= SIDEBAR_WIDTH_MIN && saved <= SIDEBAR_WIDTH_MAX ? saved : DEFAULT_SIDEBAR_WIDTH
  })
  const [sidebarResizing, setSidebarResizing] = useState(false)
  const [userMenuOpen, setUserMenuOpen] = useState(false)
  const [currentDate, setCurrentDate] = useState(() => new Date())
  const [menuQuery, setMenuQuery] = useState('')
  const searchInputRef = useRef<HTMLInputElement | null>(null)
  const { bootstrap, logout } = useAuth()
  // 数据路由下才支持导航拦截；其他路由形态（如单测的 MemoryRouter）退化为无拦截
  const dataRouter = useContext(UNSAFE_DataRouterContext)
  const navigate = useNavigate()
  const navigation = bootstrap?.navigation ?? fallbackNavigation as unknown as NavigationItem[]
  const location = useLocation()
  // 事件回调里需要"当前值"，用 ref 取值避免把回调身份绑到每次导航上
  const navigateRef = useRef(navigate)
  navigateRef.current = navigate
  const locationRef = useRef(location)
  locationRef.current = location

  const tabSeq = useRef(1)
  // 多标签开关在启动时读一次：关闭即回退单标签行为（标签栏隐藏、点菜单原地导航）
  const [tabsEnabled] = useState(workspaceTabsEnabled)
  const tabsStorageKey = workspaceTabsKey(bootstrap?.user?.id ?? '')
  const [workspace, dispatch] = useReducer(workspaceReducer, null, () => {
    const url = tabUrlOf(location)
    if (!tabsEnabled || !bootstrap?.user?.id) return createWorkspaceState(url, 't1')
    const saved = parsePersistedTabs(localStorage.getItem(tabsStorageKey))
    if (saved.length === 0) return createWorkspaceState(url, 't1')
    // 恢复的标签 id 从上一会话继承，序号接着最大值往后发，避免新建标签撞 id
    tabSeq.current = saved.reduce((max, tab) => Math.max(max, Number(/^t(\d+)$/.exec(tab.id)?.[1] ?? 0)), 0)
    return restoreWorkspaceState(saved, url, () => `t${++tabSeq.current}`)
  })
  const workspaceRef = useRef(workspace)
  workspaceRef.current = workspace
  const nextTabId = useCallback(() => `t${++tabSeq.current}`, [])

  // 面包屑按标签各持一份：隐藏标签的写入不会覆盖活动标签的展示
  const [crumbs, setCrumbs] = useState<Record<string, TabCrumb>>({})
  const patchCrumb = useCallback((tabId: string, patch: TabCrumbPatch) => {
    setCrumbs((prev) => {
      const current = prev[tabId] ?? EMPTY_TAB_CRUMB
      const next = { ...current, ...patch }
      if (next.form === current.form && next.page === current.page) return prev
      return { ...prev, [tabId]: next }
    })
  }, [])
  const activeCrumb = crumbs[workspace.activeId] ?? EMPTY_TAB_CRUMB
  const formBreadcrumb = activeCrumb.form
  const pageCrumb = activeCrumb.page

  // 脏页注册表：页面只上报"已改未保存"与处置动作，关闭/离开确认统一由外壳处理
  // （同一时刻只允许一个导航拦截器生效，页面各自 useBlocker 会互相覆盖）
  const [dirtyFlags, setDirtyFlags] = useState<Record<string, boolean>>({})
  const dirtyFlagsRef = useRef(dirtyFlags)
  dirtyFlagsRef.current = dirtyFlags
  const dirtyHandlersRef = useRef<Record<string, TabDirtyHandlers>>({})
  const pendingNavRef = useRef<string | null>(null)
  const [pendingConfirm, setPendingConfirm] = useState<PendingConfirm>(null)
  const [confirmBusy, setConfirmBusy] = useState(false)

  const registerDirty = useCallback((tabId: string, handlers: TabDirtyHandlers) => {
    dirtyHandlersRef.current[tabId] = handlers
    return () => {
      delete dirtyHandlersRef.current[tabId]
      setDirtyFlags((prev) => {
        if (!(tabId in prev)) return prev
        const next = { ...prev }
        delete next[tabId]
        return next
      })
    }
  }, [])
  const setTabDirty = useCallback((tabId: string, dirty: boolean) => {
    setDirtyFlags((prev) => {
      if (Boolean(prev[tabId]) === dirty) return prev
      const next = { ...prev }
      if (dirty) next[tabId] = true
      else delete next[tabId]
      return next
    })
  }, [])
  const dropDirty = useCallback((tabId: string) => {
    delete dirtyHandlersRef.current[tabId]
    setDirtyFlags((prev) => {
      if (!(tabId in prev)) return prev
      const next = { ...prev }
      delete next[tabId]
      return next
    })
  }, [])
  const dirtyRegistry = useMemo(() => ({ register: registerDirty, setDirty: setTabDirty }), [registerDirty, setTabDirty])
  const dirtyIds = useMemo(() => new Set(Object.keys(dirtyFlags)), [dirtyFlags])
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
      : PAGE_META[location.pathname] ?? {
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

  // ===== 标签工作区行为 =====
  /** 新标签的临时标题：激活后由标题同步效应刷新为页面真实标题 */
  const labelForUrl = useCallback((url: string) => {
    const pathname = parsePath(url).pathname || '/'
    const meta = PAGE_META[pathname]
    if (meta) return meta.title
    const moduleId = moduleIdOfUrl(url)
    if (moduleId) {
      const leaf = allLeaves.find((item) => item.route === workbenchList(moduleId))
      const action = workbenchAction(pathname)
      const prefix = action === 'new' ? '新增' : action === 'copy' ? '复制' : action === 'view' ? '查看' : action === 'edit' ? '编辑' : ''
      return `${prefix}${leaf?.label ?? moduleId}`
    }
    return allLeaves.find((item) => item.route === pathname)?.label ?? '页面'
  }, [allLeaves])

  /** 打开标签：已开则聚焦，未开则新建；撞顶只提示，既不新建也不跳转 */
  const openTab = useCallback((url: string) => {
    if (!tabsEnabled) {
      // 回退模式：不做标签，直接原地导航
      if (tabUrlOf(locationRef.current) !== url) navigateRef.current(url)
      return
    }
    const { tabs } = workspaceRef.current
    if (!tabs.some((tab) => tab.url === url) && tabs.length >= MAX_TABS) {
      dispatch({ type: 'hint', hint: HINT_TAB_LIMIT })
      return
    }
    dispatch({ type: 'open', id: nextTabId(), url, label: labelForUrl(url), fromModuleId: fromModuleIdOf(url, moduleIdOfUrl(url)) ?? undefined })
    if (tabUrlOf(locationRef.current) !== url) {
      // 开新标签不会丢弃任何标签的改动，登记后放行，避免被脏页拦截器误拦
      pendingNavRef.current = url
      navigateRef.current(url)
    }
  }, [labelForUrl, nextTabId, tabsEnabled])

  /** 菜单类入口：左键单击时开标签，修饰键与中键保留浏览器默认行为（新窗口打开） */
  const openTabFromLink = useCallback((url: string) => tabLinkHandler(openTab, url), [openTab])

  // 激活标签即把浏览器地址切到该标签（地址栏始终反映活动标签）
  const activateTab = useCallback((id: string) => {
    const tab = workspaceRef.current.tabs.find((item) => item.id === id)
    if (!tab) return
    dispatch({ type: 'activate', id })
    if (tabUrlOf(locationRef.current) !== tab.url) navigateRef.current(tab.url)
  }, [])
  const setTabUrl = useCallback((id: string, url: string) => dispatch({ type: 'tabUrl', id, url }), [])

  /** 真正执行关闭：清掉该标签的面包屑与脏位，关闭活动标签时地址跟着接管者走 */
  const applyClose = useCallback((id: string) => {
    const { tabs, activeId } = workspaceRef.current
    const next = activeId === id ? neighborAfterClose(tabs, id) : null
    dispatch({ type: 'close', id })
    setCrumbs((prev) => {
      if (!(id in prev)) return prev
      const copy = { ...prev }
      delete copy[id]
      return copy
    })
    dropDirty(id)
    if (next && tabUrlOf(locationRef.current) !== next.url) navigateRef.current(next.url)
  }, [dropDirty])

  /** 关闭全部标签并落到首页 */
  const applyCloseAll = useCallback(() => {
    dispatch({ type: 'closeAll' })
    setCrumbs({})
    setDirtyFlags({})
    dirtyHandlersRef.current = {}
    if (tabUrlOf(locationRef.current) !== '/dashboard') {
      pendingNavRef.current = '/dashboard'
      navigateRef.current('/dashboard')
    }
  }, [])

  const logoutNow = useCallback(() => {
    pendingNavRef.current = '/login'
    void logout().then(() => navigateRef.current('/login', { replace: true }))
  }, [logout])

  const closeTab = useCallback((id: string) => {
    const { tabs } = workspaceRef.current
    // 最后一个标签不关闭（用「关闭全部」退出工作区）
    if (tabs.length <= 1) return
    if (dirtyFlagsRef.current[id]) {
      setPendingConfirm({ kind: 'close', tabId: id })
      return
    }
    applyClose(id)
  }, [applyClose])

  /** 关闭其他：保留当前标签与所有脏标签，其余直接关闭（不涉及丢弃，无需确认） */
  const closeOthers = useCallback(() => {
    const { tabs, activeId } = workspaceRef.current
    if (tabs.length <= 1) return
    const keep = tabs.filter((tab) => tab.id === activeId || dirtyFlagsRef.current[tab.id])
    const removed = tabs.filter((tab) => !keep.some((item) => item.id === tab.id)).map((tab) => tab.id)
    if (removed.length === 0) return
    dispatch({ type: 'closeOthers', keepIds: keep.map((tab) => tab.id) })
    setCrumbs((prev) => {
      const copy = { ...prev }
      for (const id of removed) delete copy[id]
      return copy
    })
    for (const id of removed) dropDirty(id)
  }, [dropDirty])

  /** 关闭全部：落到首页；涉及脏标签时先走确认 */
  const closeAll = useCallback(() => {
    if (Object.keys(dirtyFlagsRef.current).length > 0) {
      setPendingConfirm({ kind: 'all' })
      return
    }
    applyCloseAll()
  }, [applyCloseAll])

  /** 退出登录：有未保存改动时先确认（登录态一旦清掉，草稿就再也拿不回来） */
  const handleLogout = useCallback(() => {
    if (Object.keys(dirtyFlagsRef.current).length > 0) {
      setPendingConfirm({ kind: 'logout' })
      return
    }
    logoutNow()
  }, [logoutNow])

  const workspaceNav = useMemo(() => ({ openTab }), [openTab])

  /** 该次导航是否会丢弃活动标签的未保存改动：切标签、开新标签都不算 */
  const shouldBlock = useCallback((nextUrl: string) => {
    if (pendingNavRef.current === nextUrl) return false
    const { tabs, activeId } = workspaceRef.current
    if (tabs.some((tab) => tab.url === nextUrl)) return false
    const active = tabs.find((tab) => tab.id === activeId)
    if (!active || active.url === nextUrl) return false
    return Boolean(dirtyFlagsRef.current[active.id])
  }, [])

  const dirtyTabIds = (pending: PendingConfirm): string[] => {
    if (!pending) return []
    if (pending.kind === 'close') return [pending.tabId]
    return Object.keys(dirtyFlagsRef.current)
  }
  const confirmLabels = (pending: PendingConfirm): string[] => dirtyTabIds(pending)
    .map((id) => workspaceRef.current.tabs.find((tab) => tab.id === id)?.label ?? '标签')
  const runPendingConfirm = (pending: PendingConfirm) => {
    if (!pending) return
    if (pending.kind === 'close') applyClose(pending.tabId)
    else if (pending.kind === 'all') applyCloseAll()
    else logoutNow()
  }
  const handleConfirmSave = () => {
    const pending = pendingConfirm
    if (!pending) return
    setConfirmBusy(true)
    void (async () => {
      for (const id of dirtyTabIds(pending)) await dirtyHandlersRef.current[id]?.save()
    })().then(
      () => { setConfirmBusy(false); setPendingConfirm(null); runPendingConfirm(pending) },
      // 保存失败：留在原处，页面自身会呈现错误
      () => setConfirmBusy(false),
    )
  }
  const handleConfirmDiscard = () => {
    const pending = pendingConfirm
    if (!pending) return
    for (const id of dirtyTabIds(pending)) dirtyHandlersRef.current[id]?.discard()
    setPendingConfirm(null)
    runPendingConfirm(pending)
  }

  const activeUrl = tabUrlOf(location)
  const fromLabel = fromParam ? allLeaves.find((item) => item.route === workbenchList(fromParam))?.label : undefined
  // 带来源的标签标题附上来源，否则同一模块因来源不同开出两个标签时无法区分
  const tabLabel = fromLabel ? `${page.title} ← ${fromLabel}` : page.title

  // 地址同步：命中其它标签的地址即激活该标签，否则改写活动标签地址（标签内导航）
  useLayoutEffect(() => {
    pendingNavRef.current = null
    dispatch({ type: 'sync', url: activeUrl, label: tabLabel, fromModuleId: fromParam ?? undefined })
  }, [activeUrl, tabLabel, fromParam])

  // 标签列表清空后兜底重建（正常路径由关闭动作保证非空）
  useLayoutEffect(() => {
    if (workspace.tabs.length > 0) return
    dispatch({ type: 'reset', id: nextTabId(), url: activeUrl, label: tabLabel, fromModuleId: fromParam ?? undefined })
  }, [workspace.tabs.length, activeUrl, tabLabel, fromParam, nextTabId])

  useEffect(() => {
    if (!workspace.hint) return
    const timer = window.setTimeout(() => dispatch({ type: 'hint', hint: null }), 4000)
    return () => window.clearTimeout(timer)
  }, [workspace.hint])

  // 标签列表按用户持久化：只存地址与标题，last-write-wins；不监听 storage 事件、不做跨窗口同步
  useEffect(() => {
    if (!tabsEnabled || !tabsStorageKey) return
    try {
      localStorage.setItem(tabsStorageKey, JSON.stringify(serializeTabs(workspace.tabs)))
    } catch {
      // 存储不可用（隐私模式/超出配额）时降级为不持久化
    }
  }, [workspace.tabs, tabsEnabled, tabsStorageKey])

  // 浏览器级刷新/关闭：只要有未保存改动就交给浏览器确认（站内导航由唯一的拦截器负责）
  useEffect(() => {
    if (dirtyIds.size === 0) return
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirtyIds])

  useEffect(() => {
    document.documentElement.setAttribute('data-bs-theme', theme)
    localStorage.setItem(THEME_KEY, theme)
  }, [theme])

  useEffect(() => {
    document.documentElement.classList.toggle('erp-sidebar-collapsed', sidebarCollapsed)
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(sidebarCollapsed))
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
      patchCrumb(workspaceRef.current.activeId, { page: null })
    }
  }, [location.pathname, patchCrumb])

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
            onClick={openTabFromLink(item.route!)}
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
                        openTab(item.route!)
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
                    <button className="dropdown-item" type="button" role="menuitem" onClick={() => openTab('/settings/profile')}>个人设置</button>
                    <div className="dropdown-divider" />
                    <button className="dropdown-item text-danger" type="button" role="menuitem" onClick={handleLogout}>退出登录</button>
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

        <main className="page-body" style={{ '--erp-tabbar-height': `${tabsEnabled ? TAB_BAR_HEIGHT : 0}px` } as CSSProperties}>
          {tabsEnabled && (
            <WorkspaceTabBar
              tabs={workspace.tabs}
              activeId={workspace.activeId}
              dirtyIds={dirtyIds}
              hint={workspace.hint}
              onActivate={activateTab}
              onClose={closeTab}
              onCloseOthers={closeOthers}
              onCloseAll={closeAll}
            />
          )}
          <WorkspaceDirtyContext.Provider value={dirtyRegistry}>
            <WorkspaceNavContext.Provider value={workspaceNav}>
              <div className="erp-workspace">
                {workspace.tabs.map((tab) => (tab.mounted ? (
                  <WorkspacePanel
                    key={tab.id}
                    id={tab.id}
                    url={tab.url}
                    routes={routes}
                    active={tab.id === workspace.activeId}
                    crumb={crumbs[tab.id] ?? EMPTY_TAB_CRUMB}
                    onCrumbChange={patchCrumb}
                    onTabUrl={setTabUrl}
                  />
                ) : null))}
              </div>
            </WorkspaceNavContext.Provider>
          </WorkspaceDirtyContext.Provider>
        </main>

        {dataRouter && (
          <WorkspaceNavGuard
            shouldBlock={shouldBlock}
            getSave={() => dirtyHandlersRef.current[workspaceRef.current.activeId]?.save ?? null}
            labels={confirmLabels({ kind: 'close', tabId: workspace.activeId })}
          />
        )}

        {pendingConfirm && (
          <DirtyConfirmDialog
            labels={confirmLabels(pendingConfirm)}
            mode={pendingConfirm.kind === 'logout' ? 'leave' : 'close'}
            busy={confirmBusy}
            onSave={handleConfirmSave}
            onDiscard={handleConfirmDiscard}
            onCancel={() => setPendingConfirm(null)}
          />
        )}

        <AssistantDock />
      </div>
    </div>
  )
}
