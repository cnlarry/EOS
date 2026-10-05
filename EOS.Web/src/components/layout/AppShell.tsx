import {
  IconChevronLeft,
  IconChevronRight,
  IconChevronUp,
  IconFolder,
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
import { AssistantProvider } from '../../features/assistant/AssistantProvider'
import { useToast } from '../ui/toastContext'
import { navigationIcons } from './navigationIcons'
import { childPad, dotLeft, groupPad, lineSidebar } from './menuDepth'
import { searchMenu } from './menuSearch'
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
  HOME_URL,
  INDEX_URL,
  MAX_TABS,
  TAB_BAR_HEIGHT,
  canonicalTabUrl,
  createWorkspaceState,
  fromModuleIdOf,
  isHomeTab,
  isHomeUrl,
  moduleIdOfUrl,
  neighborAfterClose,
  parsePersistedTabs,
  restoreWorkspaceState,
  serializeTabs,
  tabUrlOf,
  UNRESOLVED_TAB_LABEL,
  workspaceReducer,
  workspaceTabsEnabled,
  type TabCrumb,
  type TabCrumbPatch,
} from './workspaceTabs'

type Theme = 'light' | 'dark'

const SIDEBAR_WIDTH_MIN = 160
const SIDEBAR_WIDTH_MAX = 480
const DEFAULT_SIDEBAR_WIDTH = 220

/** 个人设置改由侧栏底部的用户菜单进入，主导航不再保留该入口。 */
const PROFILE_ROUTE = '/settings/profile'

/** 去掉指向指定路由的菜单叶子；分组因此变空时一并去掉分组。 */
function filterNavRoute(items: NavigationItem[], route: string): NavigationItem[] {
  return items.flatMap((item) => {
    if (item.children?.length) {
      const children = filterNavRoute(item.children, route)
      return children.length > 0 ? [{ ...item, children }] : []
    }
    return item.route === route ? [] : [item]
  })
}

/** 待确认的关闭/离开动作 */
type PendingConfirm = { kind: 'close'; tabId: string } | { kind: 'all' } | { kind: 'logout' } | null

/** 折叠态菜单浮层的一层：第 0 层是根分组，之后每层挂在其父项的右侧（N 级菜单） */
interface NavFlyoutLevel {
  id: string
  label: string
  items: NavigationItem[]
  top: number
  left: number
  maxHeight: number
}

/** 浮层宽度（11rem），用于判断是否该向左弹出 */
const FLYOUT_WIDTH = 176
/** 浮层单行高度、标题与内边距占位、以及最多直接显示的行数（超出改为内部滚动） */
const FLYOUT_ITEM_HEIGHT = 30
const FLYOUT_CHROME_HEIGHT = 46
const FLYOUT_MAX_VISIBLE = 10
const FLYOUT_MIN_HEIGHT = 120

/** 按触发项位置与子项数量算出浮层的坐标与最大高度：整块始终落在可视区内，行数超过上限则内部滚动。 */
function flyoutPlacement(rect: DOMRect, itemCount: number, offsetX: number) {
  const wanted = FLYOUT_CHROME_HEIGHT + Math.min(itemCount, FLYOUT_MAX_VISIBLE) * FLYOUT_ITEM_HEIGHT
  const maxHeight = Math.max(FLYOUT_MIN_HEIGHT, Math.min(wanted, window.innerHeight - 16))
  // 默认与触发项顶部对齐；下方放不下就整体上移，仍放不下则由 maxHeight 截断并可滚动
  const top = Math.max(8, Math.min(rect.top - 6, window.innerHeight - 8 - maxHeight))
  const left = rect.right + offsetX + FLYOUT_WIDTH > window.innerWidth
    ? Math.max(8, rect.left - FLYOUT_WIDTH - offsetX)
    : rect.right + offsetX
  return { top, left, maxHeight }
}

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
]

const avatarPalette = ['#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899', '#14b8a6']

function avatarColor(username: string): string {
  let hash = 0
  for (let index = 0; index < username.length; index += 1) {
    hash = (hash * 31 + username.charCodeAt(index)) >>> 0
  }
  return avatarPalette[hash % avatarPalette.length]
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

/** 子树里是否有展开中的分组：多级展开时让祖先也保持展开态高亮，看得出当前分支挂在谁下面。 */
function hasExpandedDescendant(item: NavigationItem, expanded: Set<string>): boolean {
  return (item.children ?? []).some((child) =>
    child.children?.length ? expanded.has(child.id) || hasExpandedDescendant(child, expanded) : false,
  )
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
  // 折叠态下的多级菜单浮层：点一级图标只展开第一层，逐级悬停/点击再展开下一层
  const [navFlyout, setNavFlyout] = useState<NavFlyoutLevel[]>([])
  const [menuQuery, setMenuQuery] = useState('')
  const searchInputRef = useRef<HTMLInputElement | null>(null)
  const { bootstrap, logout } = useAuth()
  const { notify } = useToast()
  // 侧栏底部用户卡：姓名/角色缺失时退回用户名，避免卡片出现空行
  const displayName = bootstrap?.user.displayName ?? bootstrap?.user.username ?? '未登录'
  const displayRole = bootstrap?.user.roleName ?? bootstrap?.user.username ?? ''
  // 折叠态只放一个切换按钮，图标与提示均指向"切换后"的模式
  const nextTheme: Theme = theme === 'light' ? 'dark' : 'light'
  const themeToggleLabel = nextTheme === 'dark' ? '切换到深色模式' : '切换到浅色模式'
  // 数据路由下才支持导航拦截；其他路由形态（如单测的 MemoryRouter）退化为无拦截
  const dataRouter = useContext(UNSAFE_DataRouterContext)
  const navigate = useNavigate()
  const navigation = useMemo(
    () => filterNavRoute((bootstrap?.navigation ?? fallbackNavigation) as unknown as NavigationItem[], PROFILE_ROUTE),
    [bootstrap?.navigation],
  )
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
    const url = canonicalTabUrl(tabUrlOf(location))
    if (!tabsEnabled || !bootstrap?.user?.id) return createWorkspaceState(url, 't1')
    const saved = parsePersistedTabs(localStorage.getItem(tabsStorageKey))
    if (saved.length === 0) return createWorkspaceState(url, 't1', '', true)
    // 恢复的标签 id 从上一会话继承，序号接着最大值往后发，避免新建标签撞 id
    tabSeq.current = saved.reduce((max, tab) => Math.max(max, Number(/^t(\d+)$/.exec(tab.id)?.[1] ?? 0)), 0)
    return restoreWorkspaceState(saved, url, () => `t${++tabSeq.current}`, true)
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
  /**
   * 模块域路由（报表/明细查询/打印/版式设计/旧模块等）在导航树里没有同名叶子，
   * 但它们的模块 ID 指向的模块通常有；据此兜底出可读标题，避免退化成占位文案。
   */
  const moduleLabelOfPath = useCallback((pathname: string): string | null => {
    // 报表身份地址（/report/{reportId}）路径里只有报表编号、没有模块号，解析不出模块名；
    // 给一个稳定的兜底标题，免得标签退化成占位文案（页面自身展示报表名与模块名）。
    if (/^\/report\/[^/]+$/.test(pathname)) return '报表'
    const moduleId = moduleIdOfUrl(pathname)
    if (!moduleId) return null
    return allLeaves.find((item) => item.route === workbenchList(moduleId))?.label ?? null
  }, [allLeaves])
  // 字段维护子页（2302 /admin/tables/:tableId/fields）：
  // 面包屑固定为 系统管理 > 数据表维护 > 数据表维护 > {表名} > 字段
  const fieldAdminFields = location.pathname.match(/^\/admin\/tables\/([^/]+)\/fields$/)
  const fieldAdminCrumb = fieldAdminFields
    ? { leads: ['系统管理', '数据表维护', '数据表维护', decodeURIComponent(fieldAdminFields[1])], title: '字段' }
    : null
  const groupAdminPage = location.pathname.match(/^\/admin\/groups\/[^/]+\/(rights|button-rights|members)$/)
  const page: { section: string; title: string } = fieldAdminCrumb
    ? { section: '系统管理', title: fieldAdminCrumb.title }
    : pageCrumb
      ? { section: '系统管理', title: pageCrumb.title }
    : groupAdminPage
      ? {
          section: '系统管理',
          title: groupAdminPage[1] === 'rights'
            ? '用户组权限'
            : groupAdminPage[1] === 'button-rights'
              ? '用户组按钮权限'
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
          title: activeMenu?.label ?? moduleLabelOfPath(location.pathname) ?? UNRESOLVED_TAB_LABEL,
        }
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
    const leaf = allLeaves.find((item) => item.route === pathname)
    if (leaf) return leaf.label
    // 都命中不了时用路径末段顶着（如 /settings/PRODUCT → PRODUCT），页面挂载后会被真实标题刷新
    return decodeURIComponent(pathname.split('/').filter(Boolean).pop() ?? '') || UNRESOLVED_TAB_LABEL
  }, [allLeaves])

  /** 打开标签：已开则聚焦，未开则新建；撞顶只提示，既不新建也不跳转 */
  const openTab = useCallback((rawUrl: string) => {
    const url = canonicalTabUrl(rawUrl)
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

  /** 关闭全部可关闭的标签：首页常驻，其余关掉并把地址切回首页 */
  const applyCloseAll = useCallback(() => {
    const { tabs } = workspaceRef.current
    const removed = tabs.filter((tab) => !isHomeTab(tab))
    const home = tabs.find(isHomeTab)
    if (removed.length === 0) return
    dispatch({ type: 'closeAll' })
    setCrumbs((prev) => {
      const copy = { ...prev }
      for (const tab of removed) delete copy[tab.id]
      return copy
    })
    for (const tab of removed) dropDirty(tab.id)
    if (home && tabUrlOf(locationRef.current) !== home.url) {
      pendingNavRef.current = home.url
      navigateRef.current(home.url)
    }
  }, [dropDirty])

  const logoutNow = useCallback(() => {
    pendingNavRef.current = '/login'
    void logout().then(() => navigateRef.current('/login', { replace: true }))
  }, [logout])

  const closeTab = useCallback((id: string) => {
    const target = workspaceRef.current.tabs.find((tab) => tab.id === id)
    // 首页标签常驻，任何关闭动作都不作用于它
    if (!target || isHomeTab(target)) return
    if (dirtyFlagsRef.current[id]) {
      setPendingConfirm({ kind: 'close', tabId: id })
      return
    }
    applyClose(id)
  }, [applyClose])

  /** 关闭其他：保留被右键的标签（并激活它）、首页与所有脏标签，其余直接关闭（不涉及丢弃，无需确认） */
  const closeOthers = useCallback((keepId: string) => {
    const { tabs, activeId } = workspaceRef.current
    if (tabs.length <= 1) return
    const keep = tabs.filter((tab) => tab.id === keepId || isHomeTab(tab) || dirtyFlagsRef.current[tab.id])
    const removed = tabs.filter((tab) => !keep.some((item) => item.id === tab.id)).map((tab) => tab.id)
    if (removed.length === 0) return
    dispatch({ type: 'closeOthers', keepIds: keep.map((tab) => tab.id), activeId: keepId })
    setCrumbs((prev) => {
      const copy = { ...prev }
      for (const id of removed) delete copy[id]
      return copy
    })
    for (const id of removed) dropDirty(id)
    if (activeId !== keepId) activateTab(keepId)
  }, [dropDirty, activateTab])

  /** 关闭全部：只关可关闭的标签（首页常驻）；涉及脏标签时先走确认 */
  const closeAll = useCallback(() => {
    const closable = workspaceRef.current.tabs.filter((tab) => !isHomeTab(tab))
    if (closable.length === 0) return
    if (closable.some((tab) => dirtyFlagsRef.current[tab.id])) {
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

  /** 关闭当前活动标签：助手全屏形态"回到半屏"时用（首页常驻标签由 closeTab 内部挡掉） */
  const closeActiveTab = useCallback(() => closeTab(workspaceRef.current.activeId), [closeTab])

  const workspaceNav = useMemo(() => ({ openTab, closeActiveTab }), [openTab, closeActiveTab])

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
    // 关闭全部不涉及首页，首页上的改动不该被要求处置；退出登录则会丢掉全部标签
    if (pending.kind === 'all') {
      return workspaceRef.current.tabs.filter((tab) => !isHomeTab(tab) && dirtyFlagsRef.current[tab.id]).map((tab) => tab.id)
    }
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

  const activeUrl = canonicalTabUrl(tabUrlOf(location))
  const fromLabel = fromParam ? allLeaves.find((item) => item.route === workbenchList(fromParam))?.label : undefined
  /**
   * 标题尚未解析出来时下发空串，让标签保留原标题——页面标题依赖"页面自己上抛"或"导航树命中"，
   * 二者都可能短暂缺位（页面刚挂载还没上抛、后台刷新期间上下文换了），
   * 此时若把占位文案写进标签，标签就会被永久改成「页面」。
   */
  const resolvedTitle = page.title === UNRESOLVED_TAB_LABEL ? '' : page.title
  // 带来源的标签标题附上来源，否则同一模块因来源不同开出两个标签时无法区分
  const tabLabel = resolvedTitle ? (fromLabel ? `${resolvedTitle} ← ${fromLabel}` : resolvedTitle) : ''

  // 标题解析不出来时在开发态留一条线索：这个地址既不在导航树里、页面也没上抛标题
  const unresolvedWarnedRef = useRef<Set<string>>(new Set())
  useEffect(() => {
    if (!import.meta.env.DEV || resolvedTitle) return
    if (unresolvedWarnedRef.current.has(location.pathname)) return
    unresolvedWarnedRef.current.add(location.pathname)
    console.warn('[workspace] 标签标题未解析出来，暂用标签原值顶着', {
      pathname: location.pathname,
      hasFocus: document.hasFocus(),
      navLeaves: allLeaves.length,
    })
  }, [resolvedTitle, location.pathname, allLeaves.length])

  // 根路径归一到首页：入口落在站点根路径时，标签已经按首页建好，这里把地址栏一并归位，
  // 否则"地址栏 = 活动标签地址"不成立，且根路径会被后续地址同步当成新的打开目标。
  // 必须用被动 effect：路由在挂载期的 layout effect 里才订阅 history，此时导航会被静默丢弃。
  useEffect(() => {
    if (location.pathname === INDEX_URL) navigateRef.current(HOME_URL, { replace: true })
  }, [location.pathname])

  // 地址同步：命中其它标签的地址即激活该标签，否则改写活动标签地址（标签内导航）
  useLayoutEffect(() => {
    pendingNavRef.current = null
    const { tabs, activeId } = workspaceRef.current
    const active = tabs.find((tab) => tab.id === activeId)
    // 首页标签常驻且地址固定：它内部若发生导航（如助手跳转），另开一个标签承载，不改写首页
    if (active && isHomeTab(active) && !isHomeUrl(activeUrl)) {
      dispatch({ type: 'open', id: nextTabId(), url: activeUrl, label: tabLabel || labelForUrl(activeUrl), fromModuleId: fromParam ?? undefined })
      return
    }
    dispatch({ type: 'sync', url: activeUrl, label: tabLabel, fromModuleId: fromParam ?? undefined })
  }, [activeUrl, tabLabel, fromParam, nextTabId, labelForUrl])

  // 标签列表清空后兜底重建（正常路径由关闭动作保证非空）
  useLayoutEffect(() => {
    if (workspace.tabs.length > 0) return
    dispatch({ type: 'reset', id: nextTabId(), url: activeUrl, label: tabLabel, fromModuleId: fromParam ?? undefined })
  }, [workspace.tabs.length, activeUrl, tabLabel, fromParam, nextTabId])

  // 标签相关的一次性提示（撞顶、接近上限）走全局轻提示：标签栏是 34px 的横向滚动单行，
  // 提示塞进去只会和标签抢同一条窄带，而这两种提示恰恰只在标签开满时出现
  useEffect(() => {
    if (!workspace.hint) return
    notify({ message: workspace.hint, variant: 'warning' })
    dispatch({ type: 'hint', hint: null })
  }, [notify, workspace.hint])

  // 标签栏高度同步到根元素：轻提示浮层挂在外壳之外，拿不到 .page-body 上的内联变量
  useEffect(() => {
    const root = document.documentElement
    root.style.setProperty('--erp-tabbar-height', tabsEnabled ? `${TAB_BAR_HEIGHT}px` : '0px')
    return () => { root.style.removeProperty('--erp-tabbar-height') }
  }, [tabsEnabled])

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
    if (!location.pathname.match(/^\/admin\/(groups\/[^/]+\/(rights|button-rights|members)|users\/[^/]+\/(rights|button-rights|groups)|fields\/[^/]+\/[^/]+)$/)) {
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

  // 菜单浮层：点击别处、按 Esc、滚动或调整窗口都收起（滚动会让按钮位置失效）
  const navFlyoutOpen = navFlyout.length > 0
  useEffect(() => {
    if (!navFlyoutOpen) return
    const close = () => setNavFlyout([])
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as HTMLElement
      if (target.closest('.erp-nav-flyout') || target.closest('.erp-nav-group-toggle')) return
      close()
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') close()
    }
    // 只有侧栏导航列滚动才会让触发项位置失效（弹层是 fixed，跟随触发项）；
    // 浮层自身滚动条滚动、页面内容滚动都不影响对齐，不收起
    const navScroller = document.querySelector('.erp-sidebar .navbar-nav')
    document.addEventListener('mousedown', onPointerDown)
    document.addEventListener('keydown', onKeyDown)
    navScroller?.addEventListener('scroll', close)
    window.addEventListener('resize', close)
    return () => {
      document.removeEventListener('mousedown', onPointerDown)
      document.removeEventListener('keydown', onKeyDown)
      navScroller?.removeEventListener('scroll', close)
      window.removeEventListener('resize', close)
    }
  }, [navFlyoutOpen])

  // 侧栏展开后回到常规树形展开，浮层不再需要
  useEffect(() => {
    if (!sidebarCollapsed) setNavFlyout([])
  }, [sidebarCollapsed])

  const toggleExpanded = (id: string) => {
    setExpandedIds((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const trimmedQuery = menuQuery.trim().toLowerCase()
  const searchResults = useMemo(() => searchMenu(navigation, trimmedQuery), [navigation, trimmedQuery])

  const renderChildren = (items: NavigationItem[], depth: number) =>
    items.map((item) => {
      if (item.children?.length) {
        const Icon = navigationIcons[item.icon] ?? IconFolder
        const isExpanded = expandedIds.has(item.id)
        const isGroupActive = item.children.some((child) => isSubtreeActive(child, basePath))
        // 自己展开、或子树里还有展开中的分组，都算"当前展开分支"：标出展开菜单的归属
        const isBranchOpen = !sidebarCollapsed && (isExpanded || hasExpandedDescendant(item, expandedIds))
        const groupStyle =
          depth > 1 ? ({ '--menu-gpad': `${groupPad(depth)}px` } as React.CSSProperties) : undefined
        return (
          <Fragment key={item.id}>
            <div className={`erp-nav-group erp-nav-group-depth-${depth}`} style={groupStyle}>
              <button
                className={`nav-link erp-nav-group-toggle ${isGroupActive ? 'group-active' : ''}${isBranchOpen ? ' group-open' : ''}`}
                type="button"
                aria-expanded={sidebarCollapsed ? navFlyout[0]?.id === item.id : isExpanded}
                title={sidebarCollapsed ? item.label : undefined}
                onClick={(event) => {
                  // 折叠态不展开侧栏，改为在按钮右侧弹出第一层菜单，更深的层级再逐级展开
                  if (sidebarCollapsed) {
                    const children = item.children
                    if (!children) return
                    const rect = event.currentTarget.getBoundingClientRect()
                    setNavFlyout((current) => current[0]?.id === item.id
                      ? []
                      : [{ id: item.id, label: item.label, items: children, ...flyoutPlacement(rect, children.length, 8) }])
                    return
                  }
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

  /** 展开某一级的下一层浮层：同级换目标会丢掉旧的下级，位置按触发项与视口实时计算 */
  const openFlyoutLevel = useCallback((index: number, item: NavigationItem, rect: DOMRect) => {
    const children = item.children
    if (!children?.length) return
    setNavFlyout((current) => {
      if (current[index + 1]?.id === item.id) return current
      return [
        ...current.slice(0, index + 1),
        { id: item.id, label: item.label, items: children, ...flyoutPlacement(rect, children.length, 4) },
      ]
    })
  }, [])

  // 助手的状态容器包住整棵树：半屏抽屉与全屏标签页共用同一份会话（切换形态不打断对话）。
  // 缩进不改动，保持这次改动的 diff 只落在首尾两行。
  return (
    <AssistantProvider>
    <div
      className={`page erp-shell${sidebarResizing ? ' erp-sidebar-resizing' : ''}`}
      style={{ '--erp-sidebar-width': `${sidebarWidth}px` } as React.CSSProperties}
    >
      <aside
        className={`navbar navbar-vertical navbar-expand-lg erp-sidebar ${sidebarOpen ? 'show' : ''}${userMenuOpen ? ' erp-menu-open' : ''}`}
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
                  // 回车打开第一项：输入的就是某个模块的完整编号时，该项已被提到首位（见 searchMenu），即"编号直达"
                  if (event.key === 'Enter' && searchResults.length > 0) {
                    event.preventDefault()
                    openTab(searchResults[0].item.route!)
                    setMenuQuery('')
                  }
                }}
                placeholder="搜索菜单…"
                aria-label="搜索菜单"
              />
              {!menuQuery && <span className="erp-nav-kbd">Ctrl K</span>}
              {menuQuery && searchResults.length > 0 && <span className="erp-nav-kbd">Enter</span>}
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
          {navFlyout.map((level, index) => (
            <div
              key={level.id}
              className="erp-nav-flyout"
              role="menu"
              aria-label={level.label}
              style={{ top: level.top, left: level.left, maxHeight: level.maxHeight }}
            >
              <div className="erp-nav-flyout-title">{level.label}</div>
              {level.items.map((item) => item.children?.length ? (
                <button
                  key={item.id}
                  className={`erp-nav-flyout-item erp-nav-flyout-parent${navFlyout[index + 1]?.id === item.id ? ' active' : ''}`}
                  type="button"
                  role="menuitem"
                  aria-haspopup="menu"
                  aria-expanded={navFlyout[index + 1]?.id === item.id}
                  onMouseEnter={(event) => openFlyoutLevel(index, item, event.currentTarget.getBoundingClientRect())}
                  onClick={(event) => openFlyoutLevel(index, item, event.currentTarget.getBoundingClientRect())}
                >
                  <span className="erp-nav-flyout-label">{item.label}</span>
                  <IconChevronRight size={13} aria-hidden="true" />
                </button>
              ) : (
                <button
                  key={item.id}
                  className={`erp-nav-flyout-item${item.route === basePath ? ' active' : ''}`}
                  type="button"
                  role="menuitem"
                  onMouseEnter={() => setNavFlyout((current) => (current.length > index + 1 ? current.slice(0, index + 1) : current))}
                  onClick={() => {
                    setNavFlyout([])
                    openTab(item.route!)
                  }}
                >
                  <span className="erp-nav-flyout-label">{item.label}</span>
                </button>
              ))}
            </div>
          ))}
          <div className="erp-sidebar-footer">
            <div className="erp-user-row">
              <div className={`dropdown dropup erp-user-menu${userMenuOpen ? ' is-open' : ''}`}>
                <button
                  className="btn erp-user"
                  type="button"
                  aria-label="用户菜单"
                  aria-haspopup="menu"
                  aria-expanded={userMenuOpen}
                  title={sidebarCollapsed ? `${displayName}（${displayRole}）` : undefined}
                  onClick={() => setUserMenuOpen((open) => !open)}
                >
                  <span className="erp-user-avatar">
                    {bootstrap?.user.avatarUrl ? (
                      <span className="avatar avatar-sm"><img src={bootstrap.user.avatarUrl} alt="" /></span>
                    ) : (
                      <span className="avatar avatar-sm" style={{ '--erp-avatar-color': avatarColor(bootstrap?.user.username ?? 'user') } as CSSProperties}>{bootstrap?.user.avatarText}</span>
                    )}
                  </span>
                  <span className="erp-user-meta">
                    <span className="erp-user-name">{displayName}</span>
                    <small className="erp-user-role">{displayRole}</small>
                  </span>
                  <IconChevronUp className="erp-user-chevron" size={14} aria-hidden="true" />
                </button>
                {userMenuOpen && (
                  <div className="dropdown-menu show" role="menu">
                    <button
                      className="dropdown-item"
                      type="button"
                      role="menuitem"
                      onClick={() => {
                        setUserMenuOpen(false)
                        openTab('/settings/profile')
                      }}
                    >
                      个人设置
                    </button>
                    <div className="dropdown-divider" />
                    <button className="dropdown-item text-danger" type="button" role="menuitem" onClick={handleLogout}>退出登录</button>
                  </div>
                )}
              </div>
              <button
                className="btn erp-theme-toggle"
                type="button"
                aria-label={themeToggleLabel}
                title={themeToggleLabel}
                onClick={() => setTheme(nextTheme)}
              >
                {nextTheme === 'dark' ? <IconMoon size={18} stroke={1.7} aria-hidden="true" /> : <IconSun size={18} stroke={1.7} aria-hidden="true" />}
              </button>
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
          </div>
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
    </AssistantProvider>
  )
}
