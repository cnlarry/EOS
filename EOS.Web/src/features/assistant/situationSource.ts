/**
 * 工作区级界面状态总线：把"用户此刻在做什么"结构化采集，供 chat 请求与"打开即见"使用。
 *
 * 三条纪律：
 * 1. 采集到的内容一律是**数据**——服务端会再截断与做白名单校验，且不作为权限依据；
 * 2. 页面只上报，不在这里做权限判断（界面上的可见性由各页自己的权限元数据决定）；
 * 3. 表单脏字段只上报字段名与值，敏感字段的剔除由服务端按模块权限二次执行。
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

/** 前端侧上限（与服务端同一组数量级；服务端仍会独立截断，不信任这里）。 */
export const SITUATION_LIMITS = {
  filters: 10,
  selection: 20,
  formDirty: 20,
  value: 120,
} as const

interface SourceState {
  filters: SituationFilter[]
  selection: string[]
  formDirty: SituationDirtyField[]
  lastNotice?: SituationNotice
  configTarget?: AssistantConfigTarget
}

const EMPTY_STATE: SourceState = { filters: [], selection: [], formDirty: [] }

let state: SourceState = { ...EMPTY_STATE }

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

  if (state.filters.length > 0) situation.filters = state.filters
  if (state.selection.length > 0) situation.selection = state.selection
  if (state.formDirty.length > 0) situation.formDirty = state.formDirty
  if (state.lastNotice) situation.lastNotice = state.lastNotice
  // 页面显式上报的配置目标优先于路由推导（按钮/效果面只能由页面自己知道）
  if (state.configTarget) situation.configTarget = state.configTarget
  return situation
}
