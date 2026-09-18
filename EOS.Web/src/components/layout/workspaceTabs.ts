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
  /** 撞顶等一次性提示，短暂显示后清除 */
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

export const HINT_TAB_LIMIT = `标签已达上限 ${MAX_TABS} 个，请先关闭一个标签`

export const HINT_TAB_AT_LIMIT = `标签已达上限 ${MAX_TABS} 个，再打开新标签需先关闭一个`

/** 标签地址：路径 + 查询串（hash 一并保留）。 */
export function tabUrlOf(location: { pathname: string; search: string; hash: string }): string {
  return `${location.pathname}${location.search}${location.hash}`
}

/** 地址中的工作台模块 ID（非工作台地址返回 null）。 */
export function moduleIdOfUrl(url: string): string | null {
  const matched = /\/workbench\/(\d+)/.exec(url)
  return matched ? matched[1] : null
}

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

/** 只改标题/来源，内容相同时保持原对象引用（避免无谓重渲染）。 */
function relabel(tab: WorkspaceTab, label: string, fromModuleId?: string): WorkspaceTab {
  if (tab.label === label && tab.fromModuleId === fromModuleId) return tab
  return { ...tab, label, fromModuleId }
}

/** 标记已挂载（内容未变时保持原对象引用）。 */
function markMounted(tab: WorkspaceTab): WorkspaceTab {
  return tab.mounted ? tab : { ...tab, mounted: true }
}

/** 初始工作区：以当前地址开一个标签。 */
export function createWorkspaceState(url: string, id: string, label = ''): WorkspaceState {
  return { tabs: [newTab(id, url, label, undefined, true)], activeId: id, hint: null }
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
      const { id, url, label, fromModuleId } = item as Record<string, unknown>
      if (typeof id !== 'string' || !id || typeof url !== 'string' || !url.startsWith('/')) continue
      if (seen.has(url)) continue
      seen.add(url)
      tabs.push({
        id,
        url,
        label: typeof label === 'string' ? label : '',
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
 * 由持久化列表重建工作区：恢复的标签一律未挂载，只有当前地址所在的那个挂载；
 * 当前地址不在列表里时补一个（列表已满则挤掉最后一个，保证当前页一定有标签）。
 */
export function restoreWorkspaceState(saved: PersistedTab[], currentUrl: string, nextId: () => string): WorkspaceState {
  const tabs = saved.map((tab) => newTab(tab.id, tab.url, tab.label, tab.fromModuleId, false))
  const matched = tabs.find((tab) => tab.url === currentUrl)
  if (matched) {
    matched.mounted = true
    return { tabs, activeId: matched.id, hint: null }
  }
  if (tabs.length >= MAX_TABS) tabs.pop()
  const tab = newTab(nextId(), currentUrl, '', undefined, true)
  tabs.push(tab)
  return { tabs, activeId: tab.id, hint: null }
}

/** 关闭某标签后应接管的标签：优先右邻，其次左邻；列表为空返回 null。 */
export function neighborAfterClose(tabs: WorkspaceTab[], id: string): WorkspaceTab | null {
  const index = tabs.findIndex((tab) => tab.id === id)
  if (index < 0) return null
  const rest = tabs.filter((tab) => tab.id !== id)
  return rest[index] ?? rest[index - 1] ?? null
}

export function workspaceReducer(state: WorkspaceState, action: WorkspaceAction): WorkspaceState {
  switch (action.type) {
    case 'open': {
      const existing = state.tabs.find((tab) => tab.url === action.url)
      if (existing) return state.activeId === existing.id ? state : { ...state, activeId: existing.id, hint: null }
      if (state.tabs.length >= MAX_TABS) return { ...state, hint: HINT_TAB_LIMIT }
      const tab = newTab(action.id, action.url, action.label, action.fromModuleId, true)
      const tabs = [...state.tabs, tab]
      return { tabs, activeId: tab.id, hint: tabs.length >= MAX_TABS ? HINT_TAB_AT_LIMIT : null }
    }
    case 'activate': {
      const target = state.tabs.find((tab) => tab.id === action.id)
      if (!target) return state
      const tabs = state.tabs.map((tab) => (tab.id === action.id ? markMounted(tab) : tab))
      return { ...state, tabs, activeId: action.id }
    }
    case 'close': {
      const index = state.tabs.findIndex((tab) => tab.id === action.id)
      if (index < 0) return state
      const tabs = state.tabs.filter((tab) => tab.id !== action.id)
      if (state.activeId !== action.id) return { ...state, tabs }
      const next = neighborAfterClose(state.tabs, action.id)
      return { tabs, activeId: next ? next.id : '', hint: null }
    }
    case 'closeOthers': {
      const keep = new Set(action.keepIds)
      const tabs = state.tabs.filter((tab) => keep.has(tab.id))
      if (tabs.length === state.tabs.length) return state
      const activeId = keep.has(action.activeId) ? action.activeId : (tabs[0]?.id ?? '')
      return { tabs, activeId, hint: null }
    }
    case 'closeAll':
      return state.tabs.length === 0 ? state : { tabs: [], activeId: '', hint: null }
    case 'sync': {
      const active = state.tabs.find((tab) => tab.id === state.activeId)
      if (!active) return state
      // 前进/后退落在其它标签的地址上即视为"回到那个标签"，而不是把当前标签改写成它的地址
      const matched = state.tabs.find((tab) => tab.url === action.url && tab.id !== active.id)
      if (matched) {
        const tabs = state.tabs.map((tab) => (tab.id === matched.id ? markMounted(relabel(tab, action.label, action.fromModuleId)) : tab))
        return { ...state, tabs, activeId: matched.id }
      }
      const tabs = state.tabs.map((tab) => {
        if (tab.id !== active.id) return tab
        const relabeled = relabel(tab, action.label, action.fromModuleId)
        return relabeled.url === action.url ? relabeled : { ...relabeled, url: action.url }
      })
      const changed = tabs.some((tab, i) => tab !== state.tabs[i])
      return changed ? { ...state, tabs } : state
    }
    case 'tabUrl': {
      const tabs = state.tabs.map((tab) => (tab.id === action.id && tab.url !== action.url ? { ...tab, url: action.url } : tab))
      return tabs.some((tab, i) => tab !== state.tabs[i]) ? { ...state, tabs } : state
    }
    case 'reset':
      return { tabs: [newTab(action.id, action.url, action.label, action.fromModuleId, true)], activeId: action.id, hint: null }
    case 'hint':
      return state.hint === action.hint ? state : { ...state, hint: action.hint }
    default:
      return state
  }
}
