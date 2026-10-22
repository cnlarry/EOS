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
  /**
   * 标签身份类型：首页 / 模块现场 / 普通页面。
   * 身份决定"什么算同一个标签"，与当前地址无关——地址只是它现在停在哪一页。
   */
  kind: TabKind
  /** 身份键（见 `tabIdentityOf`）：同键即同一个标签，查询串一律不参与 */
  key: string
  /** 来源模块（跨模块关联浏览的 `from`），仅用于标题展示 */
  fromModuleId?: string
  /** 对照窗口：从哪个标签开出来的（关闭时优先回到它） */
  openedFrom?: string
  /** 组件是否已挂载过：刷新恢复的标签首次激活才挂载，避免开页面即并发取数 */
  mounted: boolean
  /**
   * 该标签当前的导航 state（跨页上下文：浏览顺序、保存告警、窗体首帧提示）。
   * 标签面板会重造 location，若不把 state 一并带过去，依赖它的页面功能会静默失效
   * （如「上一条/下一条」判空后整段按钮不渲染）。属组件状态，不持久化。
   */
  state?: unknown
}

/**
 * 标签身份类型：
 * - `home`：首页，常驻首位。
 * - `module`：一个模块的工作现场（列表、单据浏览/编辑/新增/复制都在它里面）。
 * - `page`：独立页面（报表中心、系统管理各页、报表、打印、版式设计等）。
 * - `record`：**对照窗口**——固定承载一张单（身份 = 模块 + 主键），由显式"在新标签打开"产生，
 *   只用于同模块两单并排；它不承载列表，也不随导航换单（ADR-040 §2.6）。
 */
export type TabKind = 'home' | 'module' | 'page' | 'record'

/** 持久化形态：只存标签身份与地址，不存组件状态 */
export interface PersistedTab {
  id: string
  url: string
  label: string
  fromModuleId?: string
  /** 对照窗口的单据身份（其余标签的身份由地址重算，不落盘） */
  kind?: TabKind
  key?: string
  openedFrom?: string
}

export interface WorkspaceState {
  tabs: WorkspaceTab[]
  /** 活动标签标识；标签列表非空时必有值 */
  activeId: string
  /** 一次性提示文案（撞顶、接近上限）；由外壳转成轻提示后清空 */
  hint: string | null
}

export type WorkspaceAction =
  /**
   * 外壳入口打开目标地址：命中同一身份即聚焦；未开则新建（撞顶时只提示不新建）。
   * `locate` 为真时把已开标签带到目标地址（跨模块定位），否则只聚焦（菜单语义）。
   * `asRecord` 为真时按**单据身份**开一个对照窗口（同模块两单并排），`openedFrom` 记来源标签。
   */
  | { type: 'open'; id: string; url: string; label: string; fromModuleId?: string; state?: unknown; locate?: boolean; asRecord?: boolean; openedFrom?: string }
  | { type: 'activate'; id: string }
  | { type: 'close'; id: string }
  /** 关闭其他：只保留指定标签（调用方已把脏标签与要保留的标签计入保留集） */
  | { type: 'closeOthers'; keepIds: string[]; activeId: string }
  | { type: 'closeAll' }
  /** 地址事件（标签内导航 / 前进后退）：同身份改写活动标签；身份不同则切到/新建对应标签 */
  | { type: 'sync'; id: string; url: string; label: string; fromModuleId?: string; state?: unknown }
  /** 隐藏标签内部发起的导航：只改写该标签自身地址，不动浏览器地址与历史 */
  | { type: 'tabUrl'; id: string; url: string; state?: unknown }
  /** 标签列表清空后的兜底重建 */
  | { type: 'reset'; id: string; url: string; label: string; fromModuleId?: string; state?: unknown }
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

/** 地址中的模块 ID：工作台/报表/打印/版式设计/兜底模块这几类模块域路由都算。 */
export function moduleIdOfUrl(url: string): string | null {
  const matched = /^\/(?:workbench|reports|print|layout-designer|fallback\/modules)\/(\d+)/.exec(url.split(/[?#]/)[0])
  return matched ? matched[1] : null
}

/**
 * 页面族：同一"工作现场"下的一组父子页共用一个标签（菜单点进来只聚焦、父页与子页之间原地切换）。
 * 只登记**确实是同一现场**的：BOM 的列表/树/编辑/对比、系统管理里列表与其下钻页。
 * 判据是"它们之间有返回关系、用户不会想同时开两个"——不是按 URL 前缀粗略归并：
 * 归并错了会让点菜单落到另一个页面上（那比多开一个标签更糟）。
 */
const PAGE_FAMILIES: { key: string; pattern: RegExp }[] = [
  { key: 'bom', pattern: /^\/bom(?:\/|$)/ },
  { key: 'admin:tables', pattern: /^\/admin\/(?:tables|fields)(?:\/|$)/ },
  { key: 'admin:users', pattern: /^\/admin\/users(?:\/|$)/ },
  { key: 'admin:groups', pattern: /^\/admin\/groups(?:\/|$)/ },
  { key: 'report-center', pattern: /^\/report-center(?:\/|$)/ },
  // 查询中心：路径里的模块号是"这次要检索哪个模块"，不是页面身份——
  // 不归并的话，切一次模块就多一个标签（旧页签停在上一个模块，新页签标题还解析不出来）。
  { key: 'search-center', pattern: /^\/search-center(?:\/|$)/ },
  { key: 'settings', pattern: /^\/settings(?:\/|$)/ },
]

/** 首页标签的身份键 */
export const HOME_TAB_KEY = 'home'

/**
 * 由地址求标签身份。**查询串一律不参与**：筛选、排序、页码不同仍是同一个标签
 * （否则点菜单会因为"筛过的列表"与"菜单上的列表"地址不同而另开一个）。
 *
 * 模块域路由（工作台 / 报表承载 / 模块兜底页）按其模块号算——列表与单据、浏览与编辑
 * 因此天然是同一个标签；打印与版式设计器是独立工具页，各自成键。
 *
 * 工作台的**单据地址**另给一个 `recordKey`：它只在"对照窗口"（显式新标签打开一张单）时
 * 充当标签身份，普通导航仍按模块归并——否则每次双击行都会多一个标签。
 */
export interface TabIdentity {
  kind: TabKind
  key: string
  /** 工作台单据地址（`view`/`edit`）的单据身份；其余地址为 undefined */
  recordKey?: string
}

export function tabIdentityOf(rawUrl: string): TabIdentity {
  const path = rawUrl.split(/[?#]/)[0] || '/'
  if (path === INDEX_URL || path === HOME_URL) return { kind: 'home', key: HOME_TAB_KEY }
  const moduleMatch = /^\/(?:workbench|reports|fallback\/modules)\/(\d+)/.exec(path)
  if (moduleMatch) {
    const moduleId = moduleMatch[1]
    const recordMatch = /^\/workbench\/\d+\/(?:view|edit)\/(.+)$/.exec(path)
    return {
      kind: 'module',
      key: `m:${moduleId}`,
      recordKey: recordMatch ? `r:${moduleId}:${recordMatch[1]}` : undefined,
    }
  }
  const reportMatch = /^\/report\/([^/]+)\/?$/.exec(path)
  if (reportMatch) return { kind: 'page', key: `report:${reportMatch[1]}` }
  const toolMatch = /^\/(print|layout-designer)\/(\d+)/.exec(path)
  if (toolMatch) return { kind: 'page', key: `${toolMatch[1]}:${toolMatch[2]}` }
  const family = PAGE_FAMILIES.find((item) => item.pattern.test(path))
  if (family) return { kind: 'page', key: family.key }
  return { kind: 'page', key: `p:${path}` }
}

/**
 * 该地址是否属于这个标签。
 *
 * 普通标签按模块/页面身份判定；**对照窗口按单据身份判定**——所以同一模块的列表导航
 * 不会落进对照窗口，而"浏览↔编辑同一张单"仍算同一标签内的移动。
 */
export function tabMatchesUrl(tab: WorkspaceTab, url: string): boolean {
  const identity = tabIdentityOf(url)
  return tab.kind === 'record' ? identity.recordKey === tab.key : identity.key === tab.key
}

/**
 * 地址同步效应是否应当接手。
 *
 * 外壳自己发起的切换（开/切/关标签）会先后改两处状态：标签、浏览器地址。两者落进两次渲染时，
 * 中间那一帧是"地址还是旧的、活动标签已经是新的"——此时按身份推断会把刚切过去的标签判成
 * "用户离开了它"，进而把它的地址改写成旧地址（同一模块两个标签时表现为一次点击开出两份、
 * 或每次切标签都莫名重挂载一次）。
 *
 * 判据：还有待落地的地址、且它既不是当前地址、当前地址也与活动标签不一致 ⇒ 先等。
 * 导航被拦下时两边都没动（当前地址 = 活动标签地址），这里会放行，不会卡住同步。
 */
export function shouldApplyAddressSync(pending: string | null, activeUrl: string, activeTabUrl: string | null): boolean {
  if (pending === null) return true
  return pending === activeUrl || activeTabUrl === activeUrl
}

/**
 * 标题暂不可解析时的占位文案（如页面尚未上抛标题、且该地址不在导航树里）。
 * 它只用于"从零开始"的场合，**不得用来覆盖标签上已有的标题**——否则一次上下文未就绪
 * 就会把标签永久改成占位文案。
 */
export const UNRESOLVED_TAB_LABEL = '页面'

/**
 * 跨模块关联浏览的来源模块：`from` 须为纯数字模块 ID，且不等于目标模块自身。
 * 只用于标题/面包屑的"← 来源"提示，**不参与身份、不参与返回**（见 ADR-040 §2.4）。
 */
export function fromModuleIdOf(url: string, moduleId: string | null): string | null {
  const query = url.split('?')[1]
  if (!query) return null
  const from = new URLSearchParams(query.split('#')[0]).get('from')
  if (!from || !/^\d+$/.test(from) || (moduleId && from === moduleId)) return null
  return from
}

function newTab(
  id: string,
  url: string,
  label: string,
  fromModuleId: string | undefined,
  mounted: boolean,
  state?: unknown,
  identity?: { kind: TabKind; key: string; openedFrom?: string },
): WorkspaceTab {
  const base = identity ?? tabIdentityOf(url)
  return { id, url, label, kind: base.kind, key: base.key, fromModuleId, openedFrom: identity?.openedFrom, mounted, state }
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

/** 写入导航 state；引用相同时保持原对象（state 逐次导航都会换成新对象，故按引用比较足够）。 */
function withState(tab: WorkspaceTab, state: unknown): WorkspaceTab {
  return tab.state === state ? tab : { ...tab, state }
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
      const { id, url: rawUrl, label, fromModuleId, kind, key, openedFrom } = item as Record<string, unknown>
      if (typeof id !== 'string' || !id || typeof rawUrl !== 'string' || !rawUrl.startsWith('/')) continue
      const url = canonicalTabUrl(rawUrl)
      // 对照窗口的身份由单据键表达（地址与模块标签同形，光看地址分不出来），其余按地址重算
      const isRecord = kind === 'record' && typeof key === 'string' && key.startsWith('r:')
      const identityKey = isRecord ? key : tabIdentityOf(url).key
      // 按身份去重：历史数据里可能存在"同一模块的列表与单据各存了一个"（旧口径按完整地址去重），
      // 恢复时只留第一个，否则会立刻出现两个同身份标签
      if (seen.has(identityKey)) continue
      seen.add(identityKey)
      tabs.push({
        id,
        url,
        label: typeof label === 'string' && label ? label : (url === HOME_URL ? HOME_LABEL : ''),
        fromModuleId: typeof fromModuleId === 'string' ? fromModuleId : undefined,
        ...(isRecord
          ? { kind: 'record' as const, key: key as string, openedFrom: typeof openedFrom === 'string' ? openedFrom : undefined }
          : {}),
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
  return tabs.map(({ id, url, label, fromModuleId, kind, key, openedFrom }) => (
    kind === 'record'
      ? { id, url, label, fromModuleId, kind, key, openedFrom }
      : { id, url, label, fromModuleId }
  ))
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
    const next = tab.kind === 'record' && tab.key
      ? newTab(tab.id, url, tab.label, tab.fromModuleId, false, undefined, { kind: 'record', key: tab.key, openedFrom: tab.openedFrom })
      : newTab(tab.id, url, tab.label, tab.fromModuleId, false)
    if (tabs.some((existing) => existing.key === next.key)) continue
    tabs.push(next)
  }
  // 当前地址可能同时属于某个模块标签与某个对照窗口：优先认定对照窗口（刷新时正看着它）
  let active = tabs.find((tab) => tab.kind === 'record' && tabMatchesUrl(tab, target))
    ?? tabs.find((tab) => tabMatchesUrl(tab, target))
  if (active) {
    // 地址栏是权威：恢复出的标签若停在别的地址（同一身份下的另一页），以当前地址为准
    active.url = target
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
      const identity = tabIdentityOf(url)
      // 对照窗口：按单据身份去重，与模块标签互不影响（同一张单不会开出两个对照窗口）
      if (action.asRecord && identity.recordKey) {
        const recordKey = identity.recordKey
        const existingRecord = state.tabs.find((tab) => tab.kind === 'record' && tab.key === recordKey)
        if (existingRecord) {
          if (existingRecord.url === url) {
            return state.activeId === existingRecord.id ? state : { ...state, activeId: existingRecord.id, hint: null }
          }
          const moved = withState(
            { ...existingRecord, url, label: action.label || existingRecord.label },
            action.state,
          )
          const tabs = state.tabs.map((tab) => (tab.id === existingRecord.id ? moved : tab))
          return { tabs, activeId: existingRecord.id, hint: null }
        }
        if (state.tabs.length >= MAX_TABS) return { ...state, hint: HINT_TAB_LIMIT }
        const recordTab = newTab(action.id, url, action.label, action.fromModuleId, true, action.state, {
          kind: 'record',
          key: recordKey,
          openedFrom: action.openedFrom,
        })
        const tabs = [...state.tabs, recordTab]
        return { tabs, activeId: recordTab.id, hint: tabs.length === MAX_TABS - 1 ? HINT_TAB_NEAR_LIMIT : null }
      }
      const existing = state.tabs.find((tab) => tab.key === identity.key)
      if (existing) {
        // 菜单语义（默认）：只聚焦，不挪动它停在哪一页——用户可能正开着一半的单据。
        // 定位语义（跨模块跳转）：把该标签带到目标地址；同地址则等同于聚焦。
        if (!action.locate || existing.url === url) {
          return state.activeId === existing.id ? state : { ...state, activeId: existing.id, hint: null }
        }
        const moved = withState(
          { ...existing, url, label: action.label || existing.label, fromModuleId: action.fromModuleId },
          action.state,
        )
        const tabs = state.tabs.map((tab) => (tab.id === existing.id ? moved : tab))
        return { tabs, activeId: existing.id, hint: null }
      }
      if (state.tabs.length >= MAX_TABS) return { ...state, hint: HINT_TAB_LIMIT }
      const label = url === HOME_URL && url !== action.url ? HOME_LABEL : action.label
      const tab = newTab(action.id, url, label, action.fromModuleId, true, action.state)
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
      const { kind, key } = tabIdentityOf(url)
      const active = state.tabs.find((tab) => tab.id === state.activeId)
      if (!active) return state
      // 同一身份 ⇒ 标签内导航（列表↔单据、翻页、页面族内的父子页、对照窗口内的浏览↔编辑）
      if (tabMatchesUrl(active, url)) {
        const relabeled = relabel(active, action.label, action.fromModuleId)
        const next = withState(relabeled.url === url ? relabeled : { ...relabeled, url }, action.state)
        if (next === active) return state
        return { ...state, tabs: state.tabs.map((tab) => (tab.id === active.id ? next : tab)) }
      }
      // 身份变了（前进/后退落到别的模块，或页面里直接导航到别处）：
      // 已经有那张标签就切过去，没有才新建——绝不把当前标签改成另一个模块。
      const owner = state.tabs.find((tab) => tab.key === key && tab.id !== active.id)
      if (owner) {
        const moved = withState({ ...relabel(owner, action.label, action.fromModuleId), url }, action.state)
        const tabs = state.tabs.map((tab) => (tab.id === owner.id ? moved : tab))
        return { ...state, tabs, activeId: owner.id }
      }
      if (state.tabs.length >= MAX_TABS) {
        // 撞顶：宁可让当前标签跟随地址，也不出现"地址栏变了、界面没变"的假象
        //（对照窗口撞顶时退化成模块标签，身份随之下放）
        const tabs = state.tabs.map((tab) => (tab.id === active.id
          ? withState({ ...relabel(tab, action.label, action.fromModuleId), url, kind, key, openedFrom: undefined }, action.state)
          : tab))
        return { ...state, tabs }
      }
      const tab = newTab(action.id, url, action.label, action.fromModuleId, true, action.state)
      return { tabs: [...state.tabs, tab], activeId: tab.id, hint: null }
    }
    case 'tabUrl': {
      const url = canonicalTabUrl(action.url)
      const self = state.tabs.find((tab) => tab.id === action.id)
      if (!self) return state
      // 隐藏标签不得把身份报到别处去（那会造出两个同身份标签，菜单的"已开则聚焦"随之失效）。
      // 跨模块跳转必须在入口处走 openTab（切/开目标标签），不能靠页面里的 navigate 顶掉自己。
      if (!tabMatchesUrl(self, url)) {
        if (import.meta.env.DEV) {
          console.warn('[workspace] 隐藏标签的导航越过自身身份，已忽略', { tabId: action.id, url, kind: self.kind })
        }
        return state
      }
      const tabs = state.tabs.map((tab) => {
        if (tab.id !== action.id) return tab
        const next = tab.url === url ? tab : { ...tab, url }
        return withState(next, action.state)
      })
      return tabs.some((tab, i) => tab !== state.tabs[i]) ? { ...state, tabs } : state
    }
    case 'reset':
      return { tabs: [newTab(action.id, canonicalTabUrl(action.url), action.label, action.fromModuleId, true, action.state)], activeId: action.id, hint: null }
    case 'hint':
      return state.hint === action.hint ? state : { ...state, hint: action.hint }
    default:
      return state
  }
}
