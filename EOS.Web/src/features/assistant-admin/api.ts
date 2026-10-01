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

/** 一条模型配置。**没有密钥字段**——只有"用哪个环境变量 + 是否已配置 + 掩码末四位"。 */
export interface AssistantModelItem {
  modelId: number
  displayName: string
  provider: string
  modelName: string
  baseUrl: string
  apiKeyEnvVar: string
  apiKeyConfigured: boolean
  /** 形如 `****abcd`；未配置时为 null。 */
  apiKeyMaskedTail: string | null
  timeoutSeconds: number
  temperature: number | null
  maxTokens: number | null
  isActive: boolean
  enabled: boolean
  sortIdx: number
  remark: string | null
  createdAt: string
  updatedAt: string
}

/** "现在到底在用哪个模型"：表里没有启用的当前模型时，助手用配置文件那套。 */
export interface AssistantModelCurrent {
  source: 'appsettings' | 'database'
  modelId?: number | null
  displayName?: string | null
  model: string
  baseUrl: string
  timeoutSeconds: number
  temperature: number | null
  maxTokens: number | null
  apiKeyConfigured: boolean
}

export interface AssistantModelList {
  items: AssistantModelItem[]
  current: AssistantModelCurrent
}

/** 新增 / 修改模型的入参（**不含密钥**：密钥走 setModelKey 那条单独的路）。 */
export interface AssistantModelWriteInput {
  displayName: string
  provider: string
  modelName: string
  baseUrl: string
  apiKeyEnvVar: string
  timeoutSeconds: number
  temperature: number | null
  maxTokens: number | null
  enabled: boolean
  sortIdx: number
  remark: string | null
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

export function listModels() {
  return apiClient.get<AssistantModelList>('/admin/assistant/models')
}

export function createModel(input: AssistantModelWriteInput) {
  return apiClient.post<{ modelId: number }>('/admin/assistant/models', input)
}

export function updateModel(modelId: number, input: AssistantModelWriteInput) {
  return apiClient.put<void>(`/admin/assistant/models/${modelId}`, input)
}

/**
 * 写入密钥：服务端把它写进环境变量（进程级立即生效 + 用户级持久化），**数据库里只留变量名**。
 * 返回值里的 `persisted` 为假表示只有本次进程生效（受限账户 / 平台不支持用户级写入）。
 */
export function setModelKey(modelId: number, apiKey: string) {
  return apiClient.put<{
    envVar: string
    configured: boolean
    maskedTail: string | null
    processUpdated: boolean
    persisted: boolean
  }>(`/admin/assistant/models/${modelId}/key`, { apiKey })
}

/** 设为当前模型。**要求密钥已配置**，否则服务端拒绝（否则整个助手的模型调用会立刻失败）。 */
export function activateModel(modelId: number) {
  return apiClient.post<void>(`/admin/assistant/models/${modelId}/activate`)
}

/** 取消当前模型：助手回到用配置文件里的那套。 */
export function clearActiveModel() {
  return apiClient.post<void>('/admin/assistant/models/active/clear')
}

export function deleteModel(modelId: number) {
  return apiClient.delete<void>(`/admin/assistant/models/${modelId}`)
}

/** 近 N 天用量：按模型、按天，以及当日总览。 */
export function getModelUsage(days = 30) {
  return apiClient.get<AssistantModelUsage>('/admin/assistant/usage', { query: { days } })
}

// ---------------------------------------------------------------------------
// 3105 助手设置（见 ADR-030 §8）：全局策略参数。**缺行 = 代码默认值**。
// ---------------------------------------------------------------------------

export interface AssistantSettingItem {
  key: string
  displayName: string
  valueType: 'string' | 'bool' | 'int' | 'decimal' | 'long'
  unit: string | null
  description: string
  /** 界面上显示的"默认值"取自后端代码，不会与真实行为漂移。 */
  defaultValue: string
  /** null = 没覆盖过，生效的就是 defaultValue。 */
  value: string | null
  isOverridden: boolean
  updatedAt: string | null
  updatedBy: string | null
}

export interface AssistantSettingList {
  items: AssistantSettingItem[]
  /**
   * 库里解析不了的值（有人手改过库、或升级后格式变了）。
   * 界面必须**当场**指出来——否则显示着一个其实没生效的值，谁也不知道为什么。
   */
  problems: string[]
}

export function listSettings() {
  return apiClient.get<AssistantSettingList>('/admin/assistant/settings')
}

/** 写回一个设置项。**空值 = 恢复默认**（服务端会删掉覆盖行，而不是存空串）。 */
export function updateSetting(key: string, value: string) {
  return apiClient.put<void>(`/admin/assistant/settings/${encodeURIComponent(key)}`, { value })
}

/** 恢复默认：删掉覆盖行（缺行 = 用代码默认值）。 */
export function resetSetting(key: string) {
  return apiClient.delete<void>(`/admin/assistant/settings/${encodeURIComponent(key)}`)
}
