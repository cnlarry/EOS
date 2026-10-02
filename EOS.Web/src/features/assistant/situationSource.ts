/**
 * 工作区级界面状态总线：把"用户此刻在做什么"结构化采集，供 chat 请求与"打开即见"使用。
 *
 * 四条纪律：
 * 1. 采集到的内容一律是**数据**——服务端会再截断与做白名单校验，且不作为权限依据；
 * 2. 页面只上报，不在这里做权限判断（界面上的可见性由各页自己的权限元数据决定）；
 * 3. 表单脏字段只上报字段名与值，敏感字段的剔除由服务端按模块权限二次执行；
 * 4. **用户能看见、也能逐项关掉**（`included` 开关）：处境是隐式发出的，看不见的隐式外发
 *    会让"助手怎么知道我在改这个"变成一个无法回答的问题。
 */
import { extractPageContext, type AssistantConfigTarget, type AssistantPageContext } from './pageContext'

/** 列表上的一条结构化筛选条件。 */
export interface SituationFilter {
  field: string
  operator: string
  value: string
}

/** 表单上已改未保存的字段。 */
export interface SituationDirtyField {
  field: string
  old: string
  new: string
}

/** 最近一次服务端拒绝：错误码 + 摘要。 */
export interface SituationNotice {
  code: string
  summary: string
}

/** 随 chat 请求上报的完整处境。 */
export interface ChatSituation {
  moduleId?: number
  moduleTitle?: string
  pageType?: string
  docNo?: string
  filters?: SituationFilter[]
  selection?: string[]
  formDirty?: SituationDirtyField[]
  lastNotice?: SituationNotice
  configTarget?: AssistantConfigTarget
}

/**
 * 处境里**可被用户单独关掉**的项。路由类（模块/页面/单号）不在这里：
 * 助手要知道"你在哪个页面"才有意义，关掉它等于关掉助手本身；
 * 关得掉的是"页面里比路由更细的状态"（筛选、选中、未保存的改动、最近拒绝、配置目标）。
 */
export const SITUATION_TOGGLE_KEYS = ['filters', 'selection', 'formDirty', 'lastNotice', 'configTarget'] as const

export type SituationToggleKey = (typeof SITUATION_TOGGLE_KEYS)[number]

/** 开关面板上的固定标题（即便该项此刻没有内容也要能列出来，否则用户找不到关掉它的入口）。 */
export const SITUATION_TOGGLE_LABELS: Record<SituationToggleKey, string> = {
  filters: '列表当前的筛选条件',
  selection: '列表里选中的行',
  formDirty: '表单里改了还没保存的值',
  lastNotice: '最近一次服务端拒绝',
  configTarget: '正在配置的对象',
}

/** 一条处境项在界面上的呈现（同时用于"将要发出"的预览与"已经发出"的回看）。 */
export interface SituationItem {
  key: SituationToggleKey
  /** 人话标题（"列表筛选""表单未保存字段"…）。 */
  label: string
  /** 具体内容摘要。 */
  detail: string
}

/** 前端侧上限（与服务端同一组数量级；服务端仍会独立截断，不信任这里）。 */
export const SITUATION_LIMITS = {
  filters: 10,
  selection: 20,
  formDirty: 20,
  value: 120,
} as const

const EXCLUDED_KEY = 'erp-assistant-situation-excluded'

interface SourceState {
  filters: SituationFilter[]
  selection: string[]
  formDirty: SituationDirtyField[]
  lastNotice?: SituationNotice
  configTarget?: AssistantConfigTarget
}

const EMPTY_STATE: SourceState = { filters: [], selection: [], formDirty: [] }

let state: SourceState = { ...EMPTY_STATE }

/** 被用户关掉的项。持久化：这是"别把我改的东西发出去"这类偏好，不该每次开抽屉重设一遍。 */
function readExcluded(): Set<SituationToggleKey> {
  try {
    const raw = localStorage.getItem(EXCLUDED_KEY)
    if (!raw) return new Set()
    const parsed = JSON.parse(raw) as unknown
    if (!Array.isArray(parsed)) return new Set()
    return new Set(parsed.filter((item): item is SituationToggleKey =>
      typeof item === 'string' && (SITUATION_TOGGLE_KEYS as readonly string[]).includes(item)))
  } catch {
    return new Set()
  }
}

let excluded: Set<SituationToggleKey> = typeof localStorage === 'undefined' ? new Set() : readExcluded()

/** 这一项当前会不会随请求发出。 */
export function isSituationIncluded(key: SituationToggleKey): boolean {
  return !excluded.has(key)
}

/** 打开 / 关掉某一项（关掉 = 不发出；与"服务端剔除"是两件事，这里是用户自己的选择）。 */
export function setSituationIncluded(key: SituationToggleKey, included: boolean): void {
  const next = new Set(excluded)
  if (included) next.delete(key)
  else next.add(key)
  excluded = next
  try {
    localStorage.setItem(EXCLUDED_KEY, JSON.stringify([...next]))
  } catch {
    // 存不下就算了：本次会话内仍然按内存里的选择走，不因为写不了 localStorage 而报错
  }
}

/** 列表当前筛选条件（由列表页上报）。 */
export function reportListFilters(filters: readonly SituationFilter[]): void {
  state = { ...state, filters: filters.slice(0, SITUATION_LIMITS.filters) }
}

/** 列表当前选中行主键（由列表页上报）。 */
export function reportSelection(keys: readonly string[]): void {
  state = { ...state, selection: keys.slice(0, SITUATION_LIMITS.selection) }
}

/** 表单已改未保存的字段（由表单页上报）。 */
export function reportDirtyFields(fields: readonly SituationDirtyField[]): void {
  state = { ...state, formDirty: fields.slice(0, SITUATION_LIMITS.formDirty) }
}

/** 最近一次服务端拒绝（由处理拒绝的页面上报；服务端校验错误码白名单）。 */
export function reportServerNotice(code: string, summary: string): void {
  state = { ...state, lastNotice: { code, summary: summary.slice(0, SITUATION_LIMITS.value) } }
}

/** 清除最近拒绝（例如保存已成功，避免旧拒绝一直被当作"当前状态"）。 */
export function clearServerNotice(): void {
  if (!state.lastNotice) return
  state = { ...state, lastNotice: undefined }
}

/** 配置页正在配置的对象（由配置页上报；缺省由路由推导）。 */
export function setConfigTarget(target: AssistantConfigTarget | null): void {
  state = { ...state, configTarget: target ?? undefined }
}

/** 清空全部采集（离开页面或测试复位）。 */
export function resetSituationSource(): void {
  state = { ...EMPTY_STATE }
}

/** 当前采集到的原始状态（供测试与调试读取）。 */
export function currentSituation(): Readonly<SourceState> {
  return state
}

/**
 * 组装一次请求要携带的处境：路由解析 + 总线采集。
 * 路由给不出模块时（如首页、配置页）仍会带上总线里的筛选/选中/脏值/拒绝。
 * **被用户关掉的项不进来**（见 `setSituationIncluded`）。
 */
export function buildChatSituation(pathname: string): ChatSituation {
  const route: AssistantPageContext | null = extractPageContext(pathname)
  const situation: ChatSituation = {}
  if (route) {
    if (route.moduleId !== undefined) situation.moduleId = route.moduleId
    if (route.moduleTitle) situation.moduleTitle = route.moduleTitle
    if (route.pageType) situation.pageType = route.pageType
    if (route.docNo) situation.docNo = route.docNo
    if (route.configTarget) situation.configTarget = route.configTarget
  }

  if (isSituationIncluded('filters') && state.filters.length > 0) situation.filters = state.filters
  if (isSituationIncluded('selection') && state.selection.length > 0) situation.selection = state.selection
  if (isSituationIncluded('formDirty') && state.formDirty.length > 0) situation.formDirty = state.formDirty
  if (isSituationIncluded('lastNotice') && state.lastNotice) situation.lastNotice = state.lastNotice
  // 页面显式上报的配置目标优先于路由推导（按钮/效果面只能由页面自己知道）
  if (isSituationIncluded('configTarget') && state.configTarget) situation.configTarget = state.configTarget
  return situation
}

/**
 * 把一份处境翻译成界面上的逐项列表：**同一份函数**服务两个场景——
 * 发送前告诉用户"这次会带上什么"（可逐项关掉），发送后告诉用户"这次带了什么"（回看）。
 * 两处用同一份翻译，界面上的说法不会互相漂移。
 */
export function describeSituation(situation: ChatSituation): SituationItem[] {
  const items: SituationItem[] = []
  if (situation.filters && situation.filters.length > 0) {
    items.push({
      key: 'filters',
      label: '列表筛选',
      detail: situation.filters
        .slice(0, 3)
        .map(filter => `${filter.field} ${filter.operator} ${filter.value}`)
        .join('；') + (situation.filters.length > 3 ? ` 等 ${situation.filters.length} 条` : ''),
    })
  }

  if (situation.selection && situation.selection.length > 0) {
    items.push({
      key: 'selection',
      label: '选中行',
      detail: situation.selection.slice(0, 3).join('、')
        + (situation.selection.length > 3 ? ` 等 ${situation.selection.length} 行` : ''),
    })
  }

  if (situation.formDirty && situation.formDirty.length > 0) {
    items.push({
      key: 'formDirty',
      label: '未保存的改动',
      detail: situation.formDirty
        .slice(0, 3)
        .map(field => `${field.field}「${field.old}」→「${field.new}」`)
        .join('；') + (situation.formDirty.length > 3 ? ` 等 ${situation.formDirty.length} 个字段` : ''),
    })
  }

  if (situation.lastNotice) {
    items.push({
      key: 'lastNotice',
      label: '最近一次拒绝',
      detail: `${situation.lastNotice.code} ${situation.lastNotice.summary}`,
    })
  }

  if (situation.configTarget) {
    const target = situation.configTarget
    const parts = [
      target.tableId ? `表 ${target.tableId}` : '',
      target.fieldId ? `字段 ${target.fieldId}` : '',
      target.actionId ? `动作 ${target.actionId}` : '',
      target.effectKey ? `效果键 ${target.effectKey}` : '',
    ].filter(Boolean)
    items.push({
      key: 'configTarget',
      label: '正在配置的对象',
      detail: `${target.surface}${parts.length > 0 ? ` · ${parts.join(' · ')}` : ''}`,
    })
  }

  return items
}

/** 当前处境里"会被发出"的项（发送前预览用）——把开关状态也算进去。 */
export function situationPreview(pathname: string): SituationItem[] {
  return describeSituation(buildChatSituation(pathname))
}
