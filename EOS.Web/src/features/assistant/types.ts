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

/** 助手可代理的记录动作：新增 / 修改 / 删除（批核族不在动作面内）。 */
export type AssistantRecordActionKind = 'insert' | 'update' | 'delete'

/** 删除的级联影响面：本模块声明的效果链会碰到的目标表.字段.算子。 */
export interface AssistantActionImpact {
  effectKey: string
  eventCode: string
  effectName: string | null
  targetTable: string
  targetField: string
  opCode: string
}

/** 预演里的一行：带上提交的值（界面据此可就地改），被拒时带原因码与文案。 */
export interface AssistantActionRowPreview {
  keys: string[]
  values: Record<string, string | null> | null
  allowed: boolean
  denialCode: string | null
  denialMessage: string | null
  impacts: AssistantActionImpact[] | null
}

/** 预演报告（模型经工具、界面经端点，拿到的是同一份形状）。 */
export interface AssistantRecordActionPreview {
  kind: 'record-action-preview'
  moduleId: number
  moduleTitle: string
  action: AssistantRecordActionKind
  blocked: boolean
  moduleDenialCode: string | null
  moduleDenialMessage: string | null
  rows: AssistantActionRowPreview[]
  notes: string[]
}

/** 执行结果里的一行：成功带业务生成的主键，失败带原因。 */
export interface AssistantActionRowOutcome {
  keys: string[]
  succeeded: boolean
  code: string | null
  message: string | null
  resultKeys: string[] | null
  idempotencyKey: string
}

/** 执行结果。 */
export interface AssistantRecordActionResult {
  kind: 'record-action-result'
  moduleId: number
  moduleTitle: string
  action: AssistantRecordActionKind
  moduleDenialCode: string | null
  moduleDenialMessage: string | null
  rows: AssistantActionRowOutcome[]
}

/**
 * 批核族处置动作：助手只准备请求卡，用户在卡片上确认后由界面**直接**调既有端点。
 * 这四个动作不在助手动作面内（助手侧没有它们的注册项与调用能力）。
 */
export type ApprovalRequestAction = 'approve' | 'deapprove' | 'endcase' | 'unendcase'

/** 请求卡里的一行：主键 + 当前状态 + 此刻可否执行及原因（原因来自服务端策略层）。 */
export interface ApprovalRequestRowPreview {
  keys: string[]
  status: string
  allowed: boolean
  denialCode: string | null
  denialMessage: string | null
}

/** 操作请求卡（模型经工具、界面经端点，拿到的是同一份形状）。 */
export interface AssistantApprovalRequestPreview {
  kind: 'approval-request-preview'
  moduleId: number
  moduleTitle: string
  action: ApprovalRequestAction
  blocked: boolean
  moduleDenialCode: string | null
  moduleDenialMessage: string | null
  rows: ApprovalRequestRowPreview[]
  notes: string[]
}

/** 请求卡载荷：只带"要处置哪些单据"，处置动作与模块由卡片自己给出。 */
export interface ApprovalRequestPayload {
  module_id: number
  action: ApprovalRequestAction
  rows: Array<{ keys: string[] }>
}

/** 配置改动对照卡里的一处值变更：旧值 → 新值。 */
export interface ConfigValueChange {
  field: string
  label: string
  oldValue: string | null
  newValue: string | null
}

/**
 * 对照卡里的一项。`previewable=false` 时 `previewNote` 必非空——
 * 不可预演的改动必须被单独标注，不能被混进"已校验"。
 */
export interface ConfigDiffItem {
  id: string
  surface: string
  target: string
  label: string
  changes: ConfigValueChange[]
  impacts: string[]
  previewable: boolean
  previewNote: string | null
  previewSummary: string | null
}

/** 配置改动对照卡（模型经工具、界面经端点，拿到的是同一份形状）。 */
export interface AssistantConfigDiff {
  kind: 'config-diff'
  surface: string
  sourceLabel: string
  targetLabel: string
  blocked: boolean
  blockedCode: string | null
  blockedMessage: string | null
  items: ConfigDiffItem[]
  notes: string[]
  /** 本次计划的意图参数（源/目标/点名对象），应用时原样回传；服务端据此重新规划。 */
  request: unknown
}

/** 应用结果里的一项。 */
export interface ConfigApplyItem {
  id: string
  target: string
  applied: boolean
  code: string | null
  message: string | null
}

/** 配置改动的应用结果。 */
export interface AssistantConfigApplyResult {
  kind: 'config-apply-result'
  surface: string
  targetLabel: string
  blockedCode: string | null
  blockedMessage: string | null
  items: ConfigApplyItem[]
  notes: string[]
}

/** 请求载荷：与模型工具的参数契约同形（新增只需 values，修改/删除需要 keys）。 */
export interface AssistantRecordActionPayload {
  module_id: number
  action: AssistantRecordActionKind
  rows: Array<{
    keys: string[]
    values: Record<string, string>
  }>
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
  /** 本次回复用过的工具摘要（与 done 事件同一形状）。历史消息也带，工具卡因此不会一刷新就消失。 */
  toolCalls?: Array<{ name: string; digest: string }> | null
}
