import { apiClient } from '../../services/api'
import type { AssistantSessionPage } from '../assistant/types'

/** 列会话的三态视图，语义与个人侧一致（服务端过滤，前端不自己筛）。 */
export type AdminSessionView = 'active' | 'archived' | 'all'

export interface AdminSessionQuery {
  offset?: number
  limit?: number
  state?: AdminSessionView
  /** 只搜标题（会话没有别的人类可读字段）。 */
  keyword?: string
  /** 按归属用户筛；空表示不限（管理侧的关键差别：这里能跨用户看）。 */
  owner?: string
  /** 排序列。**排序由服务端做**：列表是服务端分页的，在前端排只会排到当前这一页。 */
  sortBy?: 'lastActive' | 'created' | 'title' | 'messages' | 'tokens'
  sortDir?: 'asc' | 'desc'
}

/**
 * 管理侧：跨用户分页列会话（**只有元数据**，见 ADR-030 §2）。
 *
 * 与个人侧的 `listSessions` 是两个端点：那一侧服务端强制按 `USER_ID` 隔离，这一侧面向管理员。
 */
export function listAllSessions(params: AdminSessionQuery = {}) {
  const {
    offset = 0, limit = 50, state = 'active', keyword = '', owner = '',
    sortBy = 'lastActive', sortDir = 'desc',
  } = params
  return apiClient.get<AssistantSessionPage>('/admin/assistant/sessions', {
    query: {
      offset, limit, state,
      keyword: keyword.trim() || undefined,
      owner: owner || undefined,
      sortBy, sortDir,
    },
  })
}

/** 出现过会话的归属用户（"按用户筛选"下拉）。 */
export function listSessionOwners() {
  return apiClient.get<string[]>('/admin/assistant/sessions/owners')
}

/** 归档 / 取消归档任意用户的会话（幂等）。 */
export function archiveAnySession(sessionId: string, archived = true) {
  return apiClient.put<void>(`/admin/assistant/sessions/${sessionId}/archive`, { archived })
}

/**
 * 永久删除任意用户的会话（连消息，不可恢复）。
 *
 * **只对已归档会话生效**：服务端强制，在列的会话会返回 409 `NOT_DELETABLE`
 * （界面上该按钮只在已归档行出现，这里只是最后一道兜底）。
 */
export function deleteAnySession(sessionId: string) {
  return apiClient.delete<void>(`/admin/assistant/sessions/${sessionId}`)
}

/** 助手当前挂着的一个工具。 */
export interface AssistantToolInfo {
  name: string
  /** 风险分级（读取级 / 写入级…），由服务端枚举转字符串下发。 */
  risk: string
  description: string
  parametersJson: string
}

/** 助手可代理的一个动作（含幂等键与审计动作——这两样是"能不能安全代理"的关键）。 */
export interface AssistantActionInfo {
  name: string
  actorSubject: string
  target: string
  parameters: string
  idempotencyKey: string
  auditAction: string
  endpoint: string
  implementation: string
}

/** 能力面边界：写在服务端，让"助手不能做什么"与"能做什么"同样可见。 */
export interface AssistantBoundaryInfo {
  title: string
  detail: string
}

export interface AssistantMechanism {
  tools: AssistantToolInfo[]
  actions: AssistantActionInfo[]
  boundaries: AssistantBoundaryInfo[]
}

/** 机制与工具总览（只读）。数据**现算**：这份清单的意义就是"代码里现在到底是什么"。 */
export function getMechanism() {
  return apiClient.get<AssistantMechanism>('/admin/assistant/mechanism')
}

/** 知识库集合。 */
export interface KbCollectionInfo {
  collectionId: string
  title: string
  embeddingModel: string
  dimension: number
  defaultVisibility: string
}

/** 知识库文档（`status = 'deleted'` 是软删墓碑：向量已移除，行还在）。 */
export interface KbDocumentInfo {
  docId: string
  collectionId: string
  title: string
  sourceUri: string | null
  visibility: string
  status: string
  version: number
}

export function listKbCollections() {
  return apiClient.get<KbCollectionInfo[]>('/admin/assistant/kb/collections')
}

export function listKbDocuments(collectionId: string, includeDeleted = false) {
  return apiClient.get<KbDocumentInfo[]>('/admin/assistant/kb/documents', {
    query: { collectionId, includeDeleted: includeDeleted ? 'true' : 'false' },
  })
}

/** 删除知识库文档（软删墓碑 + 物理移除向量）。**管理面不开放入库**，入库仍走 2302 那条链路。 */
export function deleteKbDocument(docId: string) {
  return apiClient.delete<void>(`/admin/assistant/kb/documents/${docId}`)
}

// ---------------------------------------------------------------------------
// 3102 模型与用量（见 ADR-030 §3）：密钥只写不读，库里只有环境变量名。
// ---------------------------------------------------------------------------

/** 预设目录里的一个模型：系统"知道"的参考参数（价格留空 = 用全局兜底价）。 */
export interface AssistantModelPreset {
  modelCode: string
  displayName: string
  contextWindow: number | null
  maxOutputTokens: number | null
  supportsTools: boolean
  inputPerMillionYuan: number | null
  outputPerMillionYuan: number | null
  defaultTemperature: number | null
  remark: string | null
}

/** 预设目录里的一个供应商（接入点）及其可用模型。 */
export interface AssistantProviderPreset {
  code: string
  displayName: string
  baseUrl: string
  suggestedApiKeyEnvVar: string
  timeoutSeconds: number
  remark: string | null
  models: AssistantModelPreset[]
}

/**
 * 一条模型（**模型级**）：真正发给厂商的标识、上下文窗口、单价、工具能力。
 *
 * <p>端点与密钥不在这里——它们在供应商上（一个供应商一个端点、一把密钥，通吃它名下所有模型）。</p>
 */
export interface AssistantModelItem {
  modelId: number
  providerId: number
  modelCode: string
  displayName: string
  contextWindow: number | null
  maxOutputTokens: number | null
  defaultTemperature: number | null
  /** 超时覆盖：null = 用供应商的默认超时（推理模型往往要单独调大）。 */
  timeoutSeconds: number | null
  inputPerMillionYuan: number | null
  outputPerMillionYuan: number | null
  supportsTools: boolean
  isActive: boolean
  enabled: boolean
  sortIdx: number
  remark: string | null
}

/** 一个供应商（**接入点**）：端点 + 密钥环境变量名 + 默认超时 + 它名下的模型。 */
export interface AssistantProviderItem {
  providerId: number
  code: string
  displayName: string
  baseUrl: string
  apiKeyEnvVar: string
  apiKeyConfigured: boolean
  /** 形如 `****abcd`；未配置时为 null。密钥本体永远不会下发。 */
  apiKeyMaskedTail: string | null
  timeoutSeconds: number
  enabled: boolean
  sortIdx: number
  remark: string | null
  models: AssistantModelItem[]
}

/** "现在到底在用哪个模型"。**null 就是尚未配置**——此时助手不可用，界面要直说。 */
export interface AssistantModelCurrent {
  modelId: number
  displayName: string
  modelCode: string
  providerId: number
  providerCode: string
  providerDisplayName: string
  contextWindow: number | null
  timeoutSeconds: number
  supportsTools: boolean
  apiKeyConfigured: boolean
}

export interface AssistantProviderList {
  providers: AssistantProviderItem[]
  current: AssistantModelCurrent | null
}

/** 新增 / 修改模型的入参（**不含密钥**：密钥挂在供应商上，走 setProviderKey 那条单独的路）。 */
export interface AssistantModelWriteInput {
  providerId?: number
  modelCode: string
  displayName: string
  contextWindow: number | null
  maxOutputTokens: number | null
  defaultTemperature: number | null
  timeoutSeconds: number | null
  inputPerMillionYuan: number | null
  outputPerMillionYuan: number | null
  supportsTools: boolean
  enabled: boolean
  sortIdx: number
  remark: string | null
}

/**
 * 新增 / 修改供应商的入参。
 *
 * <p>`models` 是"顺带添加"：界面上"选供应商 → 勾选可用模型"是**一次**提交，
 * 先建供应商再发 N 个建模型请求的话，中间失败会留下半个供应商。</p>
 */
export interface AssistantProviderWriteInput {
  code: string
  displayName: string
  baseUrl: string
  apiKeyEnvVar: string
  timeoutSeconds: number
  enabled: boolean
  sortIdx: number
  remark: string | null
  models?: AssistantModelWriteInput[]
}

export interface AssistantModelUsageRow {
  modelName: string
  requests: number
  promptTokens: number
  completionTokens: number
  estimatedCostYuan: number
  lastUsedAt: string
}

export interface AssistantUsageTrendRow {
  day: string
  requests: number
  promptTokens: number
  completionTokens: number
  estimatedCostYuan: number
}

export interface AssistantModelUsage {
  days: number
  since: string
  today: { requests: number; promptTokens: number; completionTokens: number; estimatedCostYuan: number }
  models: AssistantModelUsageRow[]
  trend: AssistantUsageTrendRow[]
  caps: { userDailyCapYuan: number; globalDailyCapYuan: number }
}

/** 预设目录（代码内置，不进数据库）：界面"选供应商 → 自动罗列可用模型"靠它。 */
export function listPresets() {
  return apiClient.get<AssistantProviderPreset[]>('/admin/assistant/presets')
}

/** 供应商 + 其下模型（一次给全，界面两级渲染）。`current` 为 null 表示**尚未配置**。 */
export function listProviders() {
  return apiClient.get<AssistantProviderList>('/admin/assistant/providers')
}

export function createProvider(input: AssistantProviderWriteInput) {
  return apiClient.post<{ providerId: number; modelsCreated: number }>('/admin/assistant/providers', input)
}

export function updateProvider(providerId: number, input: AssistantProviderWriteInput) {
  return apiClient.put<void>(`/admin/assistant/providers/${providerId}`, input)
}

/**
 * 写入**供应商**的密钥：服务端把它写进环境变量（进程级立即生效 + 用户级持久化），
 * **数据库里只留变量名**。返回值里的 `persisted` 为假表示只有本次进程生效
 * （受限账户 / 平台不支持用户级写入），界面要如实提示"重启后需重设"。
 */
export function setProviderKey(providerId: number, apiKey: string) {
  return apiClient.put<{
    envVar: string
    configured: boolean
    maskedTail: string | null
    processUpdated: boolean
    persisted: boolean
  }>(`/admin/assistant/providers/${providerId}/key`, { apiKey })
}

/** 删除供应商。**名下还有模型时服务端会拒绝**（级联会一次带走整家配置）。 */
export function deleteProvider(providerId: number) {
  return apiClient.delete<void>(`/admin/assistant/providers/${providerId}`)
}

export function createModel(input: AssistantModelWriteInput) {
  return apiClient.post<{ modelId: number }>('/admin/assistant/models', input)
}

export function updateModel(modelId: number, input: AssistantModelWriteInput) {
  return apiClient.put<void>(`/admin/assistant/models/${modelId}`, input)
}

/**
 * 设为当前模型。**要求该供应商的密钥已配置**，否则服务端拒绝——把没有密钥的模型设成当前，
 * 会让所有人的助手立刻不可用，而原因只写在服务端日志里。
 */
export function activateModel(modelId: number) {
  return apiClient.post<void>(`/admin/assistant/models/${modelId}/activate`)
}

/** 取消当前模型：助手回到**未配置**状态（供应商与模型都留着，只是没有"当前"）。 */
export function clearActiveModel() {
  return apiClient.post<void>('/admin/assistant/models/active/clear')
}

/** 删除模型。**当前模型删不掉**。 */
export function deleteModel(modelId: number) {
  return apiClient.delete<void>(`/admin/assistant/models/${modelId}`)
}

/** 近 N 天用量：按模型、按天，以及当日总览。 */
export function getModelUsage(days = 30) {
  return apiClient.get<AssistantModelUsage>('/admin/assistant/usage', { query: { days } })
}

// ---------------------------------------------------------------------------
// 3105 助手设置（见 ADR-030 §5）：助手参数目录的一页视图。
// 参数的**声明**在服务端代码（AssistantParameterCatalog），**取值**在 dbo.SYSSS 的 OWNER_MODULE = 3105。
// 界面按域分组呈现；目录与库按批同步生长，所以分组会随批次变多。
// ---------------------------------------------------------------------------

/** 一个参数域（页面上的一节）。 */
export interface AssistantSettingGroup {
  code: string
  label: string
  seq: number
}

export interface AssistantSettingItem {
  key: string
  displayName: string
  groupCode: string
  groupLabel: string | null
  seqNo: number
  valueType: 'string' | 'bit' | 'int' | 'decimal'
  unit: string | null
  description: string
  /** 取值范围提示（如"大于 0"），空串表示该类型没有额外约束。 */
  rangeHint: string
  /** 界面上显示的"默认值"取自服务端代码，不会与真实行为漂移。 */
  defaultValue: string
  /** 读取方符号：这个参数被谁消费。空 = 还没有读取方（界面要标出来）。 */
  consumers: string[]
  /** null = 没覆盖过，生效的就是 defaultValue。 */
  value: string | null
  isOverridden: boolean
  updatedAt: string | null
  updatedBy: string | null
}

export interface AssistantSettingList {
  groups: AssistantSettingGroup[]
  items: AssistantSettingItem[]
  /**
   * 库里解析不了的值（有人手改过库、或升级后格式变了），以及目录有、库里没有的行。
   * 界面必须**当场**指出来——否则显示着一个其实没生效的值，谁也不知道为什么。
   */
  problems: string[]
}

export function listSettings() {
  return apiClient.get<AssistantSettingList>('/admin/assistant/settings')
}

/** 写回一个设置项。**空值 = 恢复默认**（服务端清空取值，回到默认值，而不是存空串）。 */
export function updateSetting(key: string, value: string) {
  return apiClient.put<void>(`/admin/assistant/settings/${encodeURIComponent(key)}`, { value })
}

/** 恢复默认：清空取值，回到代码默认值（标量参数回到 DEFAULT_VALUE）。 */
export function resetSetting(key: string) {
  return apiClient.delete<void>(`/admin/assistant/settings/${encodeURIComponent(key)}`)
}

// ---------------------------------------------------------------------------
// 3105 → 作用域覆盖（ADR-030 §6.2）：把某一层（模块 / 用户）的值压到全局之上，优先级 用户 > 模块 > 全局。
//
// "哪条参数可被覆盖、允许出现在哪些层、能否往那个方向走"全部由服务端判定（参数目录 +
// 作用域规则），前端只负责显示服务端给出的可选项——界面不自己判断能不能覆盖。
// ---------------------------------------------------------------------------

export interface AssistantScopeOverride {
  scopeType: 'MODULE' | 'USER' | string
  scopeKey: string
  paramKey: string
  value: string | null
  updatedBy: string | null
  updatedAt: string | null
}

/** 允许被覆盖的参数（服务端给的可选项，含它声明的层与松紧方向）。 */
export interface AssistantScopableParameter {
  key: string
  displayName: string
  valueType: string
  unit: string | null
  /** 人话说明松紧方向（"只能收紧（关得掉、放不开）" / "可放宽"）。 */
  displayNameOfPolicy: string
  layers: string[]
  rangeHint: string
}

export interface AssistantScopeList {
  items: AssistantScopeOverride[]
  scopable: AssistantScopableParameter[]
}

export function listScopes() {
  return apiClient.get<AssistantScopeList>('/admin/assistant/settings/scopes')
}

/** 写入一层覆盖。**空值 = 清掉这一项在这一层的覆盖**（回到上层取值）。 */
export function upsertScope(payload: {
  scopeType: string
  scopeKey: string
  paramKey: string
  value: string
}) {
  return apiClient.put<void>('/admin/assistant/settings/scopes', payload)
}

/** 清掉某一层的全部覆盖。 */
export function deleteScopeLayer(scopeType: string, scopeKey: string) {
  return apiClient.delete<void>(
    `/admin/assistant/settings/scopes/${encodeURIComponent(scopeType)}/${encodeURIComponent(scopeKey)}`)
}
