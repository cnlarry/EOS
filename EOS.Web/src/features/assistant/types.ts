/** 工作助手前端类型。ID 一律字符串（架构约定）。 */

export interface AssistantSession {
  id: string
  userId: string
  title: string
  createdAt: string
  lastActiveAt: string
}

export type AssistantRole = 1 | 2 | 3 // 1=USER 2=ASSISTANT 3=SYSTEM

/** 跨会话显式记忆（本人可见；业务数据只存引用，不存快照）。 */
export interface AssistantMemory {
  id: string
  type: string
  key: string
  value: string
  source: string
  updatedAt: string
}

export interface AssistantMemoryList {
  preferences: string | null
  memories: AssistantMemory[]
}

/** 元数据变更集确认卡（admin-write）：试算 diff + 结构化确认执行。 */
export interface AssistantAdminDraftTable {
  table: string
  action: string
  status: string
  wouldCreate: string[]
  wouldSkip: string[]
  errors: string[]
}

export interface AssistantAdminDraft {
  kind: 'admin-changeset'
  goal: string
  blocked: boolean
  tables: AssistantAdminDraftTable[]
  errors: string[]
  /** 原始变更集（确认执行时原样回传，服务端重跑试算）。 */
  changeset: unknown
}

export interface AssistantApplyResult {
  goal: string
  tablesRegistered: number
  fieldsCreated: number
  fieldsSkipped: number
  notes: string[]
}

/** 知识库来源文档（引用链接落点；不可见文档服务端统一 404）。 */
export interface KbDocChunk {
  serialNo: number
  content: string
}

export interface KbDocument {
  docId: string
  title: string
  sourceUri: string | null
  chunks: KbDocChunk[]
}

/** 处境快照：打开抽屉即见的结构化内容（服务端规则引擎产出，零模型调用）。 */
export interface SituationIdentity {
  userId: string
  employeeName: string
  employeeId: string
  departmentId: string
  departmentName: string
  companyId: string
  defaultGroupId: string
  directLeader: string | null
  departmentLeader: string | null
  workGroups: string[]
}

/** 摘要条目：kind = overdue（滞留未批核，此刻状态）/ blocked-now（此刻办不下去）/ rejected（最近被拒，历史事件）。 */
export interface SituationDigestItem {
  kind: string
  moduleId: number
  moduleTitle: string
  key: string
  reason: string
  occurredAt: string | null
  ageDays: number
}

export interface SituationDigest {
  items: SituationDigestItem[]
  sources: string[]
  caveats: string[]
}

export interface SituationWhere {
  moduleId: number | null
  moduleTitle: string | null
  pageType: string | null
  docNo: string | null
  dropped: string[]
}

export interface SituationRecentFailure {
  occurredAt: string
  action: string
  moduleId: number | null
  summary: string
  errorCode: string | null
}

export interface SituationSnapshot {
  identity: SituationIdentity
  where: SituationWhere
  pending: { myApproval: number; startedInFlight: number }
  recent: SituationRecentFailure[]
  digest: SituationDigest
  budget: { residentTokens: number; residentTokenLimit: number }
}

export interface AssistantMessage {
  id: string
  sessionId: string
  role: AssistantRole
  content: string
  modelName: string | null
  promptTokens: number | null
  completionTokens: number | null
  elapsedMs: number | null
  correlationId: string | null
  createdAt: string
}
