/**
 * 工作区标签模型与状态机（非组件模块，避免与组件文件混导出）。
 *
 * 不变式：活动标签的地址恒等于浏览器地址（由外壳的同步效应保证）；标签自身的地址是各标签
 * 渲染与去重的依据。多个标签并存时组件常驻不卸载，仅以隐藏属性收起。
 */
import { WORKSPACE_TABS_ENABLED_KEY } from '../../lib/storageKeys'
import type { FormBreadcrumb } from './FormBreadcrumbContext'
import type { PageBreadcrumb } from './PageBreadcrumbContext'

/** 标签各自持有的面包屑上下文值：隐藏标签写入不覆盖活动标签。 */
export interface TabCrumb {
  form: FormBreadcrumb | null
  page: PageBreadcrumb | null
}

export type TabCrumbPatch = Partial<TabCrumb>

export const EMPTY_TAB_CRUMB: TabCrumb = { form: null, page: null }

export interface WorkspaceTab {
  /** 标签稳定标识（跨渲染不变，用于 key 与关闭定位） */
  id: string
  /** 标签自身地址：完整路径 + 查询串 */
  url: string
  /** 标签栏显示文字 */
  label: string
  /** 来源模块（跨模块关联浏览的 `from`），参与去重键并附在标题上 */
  fromModuleId?: string
  /** 组件是否已挂载过：刷新恢复的标签首次激活才挂载，避免开页面即并发取数 */
  mounted: boolean
}

/** 持久化形态：只存标签身份与地址，不存组件状态 */
export interface PersistedTab {
  id: string
  url: string
  label: string
  fromModuleId?: string
}

export interface WorkspaceState {
  tabs: WorkspaceTab[]
  /** 活动标签标识；标签列表非空时必有值 */
  activeId: string
  /** 一次性提示文案（撞顶、接近上限）；由外壳转成轻提示后清空 */
  hint: string | null
}

export type WorkspaceAction =
  /** 外壳入口打开目标地址：已开则聚焦，未开则新建（撞顶时只提示不新建） */
  | { type: 'open'; id: string; url: string; label: string; fromModuleId?: string }
  | { type: 'activate'; id: string }
  | { type: 'close'; id: string }
  /** 关闭其他：只保留指定标签（调用方已把脏标签与要保留的标签计入保留集） */
  | { type: 'closeOthers'; keepIds: string[]; activeId: string }
  | { type: 'closeAll' }
  /** 地址事件（标签内导航 / 前进后退）：命中其它标签地址即激活该标签，否则改写活动标签地址 */
  | { type: 'sync'; url: string; label: string; fromModuleId?: string }
  /** 隐藏标签内部发起的导航：只改写该标签自身地址，不动浏览器地址与历史 */
  | { type: 'tabUrl'; id: string; url: string }
  /** 标签列表清空后的兜底重建 */
  | { type: 'reset'; id: string; url: string; label: string; fromModuleId?: string }
  | { type: 'hint'; hint: string | null }

/** 标签栏高度（px）——同时作为表格区高度上限的扣除量，见 app.css 的 --erp-tabbar-height。 */
export const TAB_BAR_HEIGHT = 34

/** 标签数量上限，超出后拒绝新建并提示。 */
export const MAX_TABS = 12

/** 首页标签：常驻列表首位，且不参与任何关闭动作（关闭当前/其它/全部都绕开它）。 */
export const HOME_URL = '/dashboard'
export const HOME_LABEL = '首页'

/** 站点根路径。它本身不是一个页面，只是首页的重定向入口。 */
export const INDEX_URL = '/'

/**
 * 把地址归一到"能承载页面的标签地址"：根路径归一为首页地址。
 *
 * 根路径上没有页面（工作区路由表里只有一条重定向），若给它开标签，该标签只会渲染一个
 * 空面板——重定向组件本身不产出内容；重定向发生后它还会被改写成首页地址，与常驻的
 * 首页标签形成两个同地址标签（一个空白、一个正常），并且"去重键 = 地址"从此失效。
 */
export function canonicalTabUrl(url: string): string {
  return url.split(/[?#]/)[0] === INDEX_URL ? HOME_URL : url
}

/** 地址是否为首页（只看路径，忽略查询串与 hash）；根路径按首页处理。 */
export function isHomeUrl(url: string): boolean {
  const path = url.split(/[?#]/)[0]
  return path === HOME_URL || path === INDEX_URL
}

export function isHomeTab(tab: WorkspaceTab): boolean {
  return isHomeUrl(tab.url)
}

/** 撞顶：满额后再打开被拒绝 */
export const HINT_TAB_LIMIT = `标签已达上限 ${MAX_TABS} 个，请先关闭一个标签`

/** 接近上限（只剩 1 个名额）：提前打招呼，而不是等用户撞顶才发现 */
export const HINT_TAB_NEAR_LIMIT = `标签已开 ${MAX_TABS - 1} 个，只剩 1 个名额`

/** 标签地址：路径 + 查询串（hash 一并保留）。 */
export function tabUrlOf(location: { pathname: string; search: string; hash: string }): string {
  return `${location.pathname}${location.search}${location.hash}`
}

/** 地址中的模块 ID：工作台/报表/明细查询/打印/版式设计/旧模块这几类模块域路由都算。 */
export function moduleIdOfUrl(url: string): string | null {
  const matched = /^\/(?:workbench|reports|detail-query|print|layout-designer|legacy\/modules)\/(\d+)/.exec(url.split(/[?#]/)[0])
  return matched ? matched[1] : null
}

/**
 * 标题暂不可解析时的占位文案（如页面尚未上抛标题、且该地址不在导航树里）。
 * 它只用于"从零开始"的场合，**不得用来覆盖标签上已有的标题**——否则一次上下文未就绪
 * 就会把标签永久改成占位文案。
 */
export const UNRESOLVED_TAB_LABEL = '页面'

/** 跨模块关联浏览的来源模块：`from` 须为纯数字模块 ID，且不等于目标模块自身。 */
export function fromModuleIdOf(url: string, moduleId: string | null): string | null {
  const query = url.split('?')[1]
  if (!query) return null
  const from = new URLSearchParams(query.split('#')[0]).get('from')
  if (!from || !/^\d+$/.test(from) || (moduleId && from === moduleId)) return null
  return from
}

function newTab(id: string, url: string, label: string, fromModuleId: string | undefined, mounted: boolean): WorkspaceTab {
  return { id, url, label, fromModuleId, mounted }
}

/**
 * 只改标题/来源，内容相同时保持原对象引用（避免无谓重渲染）。
 * 标题为空表示"本次没解析出标题"，保留原标题——标签只在解码出真实标题时才被改写。
 */
function relabel(tab: WorkspaceTab, label: string, fromModuleId?: string): WorkspaceTab {
  const nextLabel = label || tab.label
  if (nextLabel === tab.label && tab.fromModuleId === fromModuleId) return tab
  return { ...tab, label: nextLabel, fromModuleId }
}

/** 标记已挂载（内容未变时保持原对象引用）。 */
function markMounted(tab: WorkspaceTab): WorkspaceTab {
  return tab.mounted ? tab : { ...tab, mounted: true }
}

/**
 * 初始工作区：以当前地址开一个标签；多标签模式下先把首页标签放在首位（当前地址不是首页时）。
 * 首页标签不挂载——它是背景板，等用户切过去再挂载。
 */
export function createWorkspaceState(url: string, id: string, label = '', withHome = false): WorkspaceState {
  const target = canonicalTabUrl(url)
  const current = newTab(id, target, label || (target === HOME_URL ? HOME_LABEL : ''), undefined, true)
  if (!withHome || isHomeUrl(target)) return { tabs: [current], activeId: id, hint: null }
  return { tabs: [newTab('home', HOME_URL, HOME_LABEL, undefined, false), current], activeId: id, hint: null }
}

/** 多标签特性是否开启（默认开启；置为 `off` 回退单标签行为）。 */
export function workspaceTabsEnabled(): boolean {
  try {
    return localStorage.getItem(WORKSPACE_TABS_ENABLED_KEY) !== 'off'
  } catch {
    return true
  }
}

/**
 * 解析持久化的标签列表：容忍脏数据（非数组/字段类型不符/非法地址/重复地址），
 * 并按上限截断。解析失败一律当作没有可恢复的标签。
 */
export function parsePersistedTabs(raw: string | null): PersistedTab[] {
  if (!raw) return []
  try {
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return []
    const seen = new Set<string>()
    const tabs: PersistedTab[] = []
    for (const item of parsed) {
      if (!item || typeof item !== 'object') continue
      const { id, url: rawUrl, label, fromModuleId } = item as Record<string, unknown>
      if (typeof id !== 'string' || !id || typeof rawUrl !== 'string' || !rawUrl.startsWith('/')) continue
      const url = canonicalTabUrl(rawUrl)
      if (seen.has(url)) continue
      seen.add(url)
      tabs.push({
        id,
        url,
        label: typeof label === 'string' && label ? label : (url === HOME_URL ? HOME_LABEL : ''),
        fromModuleId: typeof fromModuleId === 'string' ? fromModuleId : undefined,
      })
      if (tabs.length >= MAX_TABS) break
    }
    return tabs
  } catch {
    return []
  }
}

/** 持久化形态（组件状态不落盘）。 */
export function serializeTabs(tabs: WorkspaceTab[]): PersistedTab[] {
  return tabs.map(({ id, url, label, fromModuleId }) => ({ id, url, label, fromModuleId }))
}

/**
 * 由持久化列表重建工作区：恢复的标签一律未挂载，只有当前地址所在的那个挂载。
 * 多标签模式下首页标签一定存在且位于首位；当前地址不在列表里时补一个
 * （列表已满则挤掉末位的普通标签，首页与当前页一定保留）。
 */
export function restoreWorkspaceState(saved: PersistedTab[], currentUrl: string, nextId: () => string, withHome = false): WorkspaceState {
  const target = canonicalTabUrl(currentUrl)
  const homeSaved = withHome ? saved.find((tab) => isHomeUrl(tab.url)) : undefined
  const tabs: WorkspaceTab[] = []
  if (withHome) {
    tabs.push(homeSaved
      ? newTab(homeSaved.id, HOME_URL, homeSaved.label || HOME_LABEL, homeSaved.fromModuleId, false)
      : newTab('home', HOME_URL, HOME_LABEL, undefined, false))
  }
  for (const tab of saved) {
    if (homeSaved && tab === homeSaved) continue
    if (tabs.length >= MAX_TABS) break
    const url = canonicalTabUrl(tab.url)
    // 归一后与首页同址的遗留条目（历史版本可能存下根路径标签）直接并进首页，不再单独成标签
    if (withHome && url === HOME_URL) continue
    tabs.push(newTab(tab.id, url, tab.label, tab.fromModuleId, false))
  }
  let active = tabs.find((tab) => tab.url === target)
  if (active) {
    active.mounted = true
  } else {
    if (tabs.length >= MAX_TABS) {
      const removable = tabs.filter((tab) => !isHomeTab(tab))
      const last = removable[removable.length - 1]
      if (last) tabs.splice(tabs.indexOf(last), 1)
    }
    active = newTab(nextId(), target, target === HOME_URL ? HOME_LABEL : '', undefined, true)
    tabs.push(active)
  }
  return { tabs, activeId: active.id, hint: null }
}

/** 关闭某标签后应接管的标签：优先右邻，其次左邻；列表为空返回 null。 */
export function neighborAfterClose(tabs: WorkspaceTab[], id: string): WorkspaceTab | null {
  const index = tabs.findIndex((tab) => tab.id === id)
  if (index < 0) return null
  const rest = tabs.filter((tab) => tab.id !== id)
  return rest[index] ?? rest[index - 1] ?? null
}

/**
 * 活动标签必须已挂载：接管活动位的那个标签可能是"刷新恢复但从未激活过"的标签
 * （关闭活动标签由右邻接管、关闭其它、关闭全部落到首页都会走到这里），
 * 未挂载就没有面板，工作区只剩空白。
 */
function ensureActiveMounted(state: WorkspaceState): WorkspaceState {
  const active = state.tabs.find((tab) => tab.id === state.activeId)
  if (!active || active.mounted) return state
  const tabs = state.tabs.map((tab) => (tab.id === active.id ? markMounted(tab) : tab))
  return { ...state, tabs }
}

export function workspaceReducer(state: WorkspaceState, action: WorkspaceAction): WorkspaceState {
  return ensureActiveMounted(applyWorkspaceAction(state, action))
}

function applyWorkspaceAction(state: WorkspaceState, action: WorkspaceAction): WorkspaceState {
  switch (action.type) {
    case 'open': {
      const url = canonicalTabUrl(action.url)
      const existing = state.tabs.find((tab) => tab.url === url)
      if (existing) return state.activeId === existing.id ? state : { ...state, activeId: existing.id, hint: null }
      if (state.tabs.length >= MAX_TABS) return { ...state, hint: HINT_TAB_LIMIT }
      const label = url === HOME_URL && url !== action.url ? HOME_LABEL : action.label
      const tab = newTab(action.id, url, label, action.fromModuleId, true)
      const tabs = [...state.tabs, tab]
      return { tabs, activeId: tab.id, hint: tabs.length === MAX_TABS - 1 ? HINT_TAB_NEAR_LIMIT : null }
    }
    case 'activate': {
      const target = state.tabs.find((tab) => tab.id === action.id)
      if (!target) return state
      const tabs = state.tabs.map((tab) => (tab.id === action.id ? markMounted(tab) : tab))
      return { ...state, tabs, activeId: action.id }
    }
    case 'close': {
      const target = state.tabs.find((tab) => tab.id === action.id)
      // 首页标签常驻：任何关闭动作都不作用于它
      if (!target || isHomeTab(target)) return state
      const tabs = state.tabs.filter((tab) => tab.id !== action.id)
      if (state.activeId !== action.id) return { ...state, tabs }
      const next = neighborAfterClose(state.tabs, action.id)
      return { tabs, activeId: next ? next.id : '', hint: null }
    }
    case 'closeOthers': {
      const keep = new Set(action.keepIds)
      const home = state.tabs.find(isHomeTab)
      if (home) keep.add(home.id)
      const tabs = state.tabs.filter((tab) => keep.has(tab.id))
      if (tabs.length === state.tabs.length) return state
      const activeId = keep.has(action.activeId) ? action.activeId : (home?.id ?? tabs[0]?.id ?? '')
      return { tabs, activeId, hint: null }
    }
    case 'closeAll': {
      const home = state.tabs.find(isHomeTab)
      // 没有首页（回退单标签模式）时按"清空"处理，由外壳兜底重建
      if (!home) return state.tabs.length === 0 ? state : { tabs: [], activeId: '', hint: null }
      if (state.tabs.length === 1 && state.activeId === home.id) return state
      return { tabs: [home], activeId: home.id, hint: null }
    }
    case 'sync': {
      const url = canonicalTabUrl(action.url)
      const active = state.tabs.find((tab) => tab.id === state.activeId)
      if (!active) return state
      // 前进/后退落在其它标签的地址上即视为"回到那个标签"，而不是把当前标签改写成它的地址
      const matched = state.tabs.find((tab) => tab.url === url && tab.id !== active.id)
      if (matched) {
        const tabs = state.tabs.map((tab) => (tab.id === matched.id ? markMounted(relabel(tab, action.label, action.fromModuleId)) : tab))
        return { ...state, tabs, activeId: matched.id }
      }
      const tabs = state.tabs.map((tab) => {
        if (tab.id !== active.id) return tab
        const relabeled = relabel(tab, action.label, action.fromModuleId)
        return relabeled.url === url ? relabeled : { ...relabeled, url }
      })
      const changed = tabs.some((tab, i) => tab !== state.tabs[i])
      return changed ? { ...state, tabs } : state
    }
    case 'tabUrl': {
      const url = canonicalTabUrl(action.url)
      // 去重键就是地址：另一标签已持有该地址时不再写入。隐藏标签内部的跳转（保存后回列表等）
      // 可能落到已被打开的地址上，照写会造出两个同地址标签，"已开则聚焦"从此失效。
      if (state.tabs.some((tab) => tab.id !== action.id && tab.url === url)) return state
      const tabs = state.tabs.map((tab) => (tab.id === action.id && tab.url !== url ? { ...tab, url } : tab))
      return tabs.some((tab, i) => tab !== state.tabs[i]) ? { ...state, tabs } : state
    }
    case 'reset':
      return { tabs: [newTab(action.id, canonicalTabUrl(action.url), action.label, action.fromModuleId, true)], activeId: action.id, hint: null }
    case 'hint':
      return state.hint === action.hint ? state : { ...state, hint: action.hint }
    default:
      return state
  }
}
