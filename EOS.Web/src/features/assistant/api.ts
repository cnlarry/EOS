import { apiClient } from '../../services/api'
import type { FormDefinition } from '../document-workbench/formDefinition'
import type {
  ApprovalRequestAction,
  ApprovalRequestPayload,
  AssistantApplyResult,
  AssistantApprovalRequestPreview,
  AssistantConfigApplyResult,
  AssistantConfigDiff,
  AssistantMemoryList,
  AssistantMessage,
  AssistantRecordActionPayload,
  AssistantRecordActionPreview,
  AssistantRecordActionResult,
  AssistantSession,
  AssistantSessionPage,
  KbDocument,
  SituationSnapshot,
} from './types'

/**
 * 分页列会话。默认只给在列的；`archived=true` 时把已归档的一并带出来
 * （界面上"归档"代替了删除：会话从列表里收起来，历史一行不动）。
 *
 * `keyword` 由服务端按标题过滤（不是拿回全量在前端搜——那样分页与搜索会互相打架）。
 */
export function listSessions(params: {
  offset?: number
  limit?: number
  /** 视图：在列 / 只看已归档 / 全部。由服务端过滤——前端自己筛会让分页与总数都不准。 */
  state?: 'active' | 'archived' | 'all'
  keyword?: string
} = {}) {
  const { offset = 0, limit = 50, state = 'active', keyword = '' } = params
  return apiClient.get<AssistantSessionPage | AssistantSession[]>('/assistant/sessions', {
    query: { offset, limit, state, keyword: keyword.trim() || undefined },
  })
    // 归一化：接口改成 {items,total} 之前返回的是裸数组。前后端滚动更新期间（前端已发布、后端还没重启）
    // 会读到旧形状——不兜住的话解构出来是 undefined，`items[0]` 会把整个助手连工作区一起弄白屏。
    .then(data => (Array.isArray(data) ? { items: data, total: data.length } : data))
}

/**
 * 永久删除会话（连消息一并删，不可恢复）。
 *
 * **只对已归档会话有效**：服务端强制，未归档会返回 400 `SESSION_NOT_ARCHIVED`。
 * 界面上"先归档、再删除"是两步，为的就是不让手滑删掉还在用的会话。
 */
export function deleteSession(sessionId: string) {
  return apiClient.delete<void>(`/assistant/sessions/${sessionId}`)
}

export function createSession() {
  return apiClient.post<AssistantSession>('/assistant/sessions')
}

export function listMessages(sessionId: string) {
  return apiClient.get<AssistantMessage[]>(`/assistant/sessions/${sessionId}/messages`)
}

export function renameSession(sessionId: string, title: string) {
  return apiClient.put<void>(`/assistant/sessions/${sessionId}/rename`, { title })
}

/** 归档 / 取消归档。归档不是删除：默认列表看不到，历史完整保留，可随时取消。 */
export function archiveSession(sessionId: string, archived = true) {
  return apiClient.put<void>(`/assistant/sessions/${sessionId}/archive`, { archived })
}

/** 打开即见的处境快照（零模型调用）：身份 / 在哪 / 待办 / 最近被拒 / 结构化摘要。 */
export function getSituation(params: { moduleId?: number; pageType?: string; docNo?: string } = {}) {
  return apiClient.get<SituationSnapshot>('/assistant/situation', { query: params })
}

export function listMemory() {
  return apiClient.get<AssistantMemoryList>('/assistant/memory')
}

export function saveMemory(payload: { memoryType: string; memoryKey: string; memoryValue: string; confirmedRisk?: boolean }) {
  return apiClient.post<{ id: string }>('/assistant/memory', payload)
}

export function deleteMemory(memoryId: string) {
  return apiClient.delete<void>(`/assistant/memory/${memoryId}`)
}

export interface PendingMemory {
  id: string
  type: string
  key: string
  value: string
  confidence: number | null
}

export function listPendingMemory() {
  return apiClient.get<{ memories: PendingMemory[] }>('/assistant/memory/pending')
}

export function resolvePendingMemory(memoryId: string, confirm: boolean) {
  return apiClient.post<{ resolved: string }>(`/assistant/memory/pending/${memoryId}/resolve`, { confirm })
}

export function forgetAllMemory() {
  return apiClient.delete<void>('/assistant/memory/all')
}

export function getKbDocument(docId: string) {
  return apiClient.get<KbDocument>(`/assistant/kb/documents/${docId}`)
}

/** 变更集确认执行：只接受结构化确认卡调用，自然语言确认无效。 */
export function applyChangeset(changeset: unknown) {
  return apiClient.post<AssistantApplyResult>('/assistant/apply-changeset', { changeset, confirmed: true })
}

/**
 * 操作卡：重算预演。与模型调用 `preview_record_action` 是同一段服务端代码、同一份参数契约，
 * 因此卡上"重算"出来的结论与助手先前说的是同一个东西。
 */
export function previewRecordAction(payload: AssistantRecordActionPayload) {
  return apiClient.post<AssistantRecordActionPreview>('/assistant/record-actions/preview', payload)
}

/**
 * 操作请求卡：重算逐行判定（只读）。与模型调用是同一段服务端代码、同一份参数契约，
 * 因此卡上"重算"出来的结论与助手先前说的是同一个东西。
 */
export function previewApprovalRequest(payload: ApprovalRequestPayload) {
  return apiClient.post<AssistantApprovalRequestPreview>('/assistant/approval-requests/preview', payload)
}

/**
 * 操作请求卡：记录"用户点了确认"这一件事（best-effort 审计）。
 * 它不执行任何处置——执行由下面的既有端点完成。
 */
export function confirmApprovalRequest(payload: ApprovalRequestPayload) {
  return apiClient.post<{ confirmed: number }>('/assistant/approval-requests/confirm', payload)
}

/**
 * 既有批核族端点：**由界面直接调用**，助手侧不持有这条通路。
 * 幂等键走请求体的 idempotencyKey（与统一工作台其余写端点同一约定）。
 */
export function executeApprovalAction(
  moduleId: number,
  action: ApprovalRequestAction,
  keys: string[],
  idempotencyKey: string,
) {
  return apiClient.post<unknown>(`/document-workbench/${moduleId}/${action}`, {
    key: JSON.stringify(keys),
    idempotencyKey,
  })
}

/**
 * 操作卡：用户点了确认执行。执行主体是这次点击，幂等键随本次确认给出——
 * 同一张卡重复点不会写两次（服务端按确认键去重），而两次点击各留一条确认审计。
 */
export function applyRecordAction(payload: AssistantRecordActionPayload, confirmKey: string) {
  return apiClient.post<AssistantRecordActionResult>('/assistant/record-actions/apply', payload, {
    headers: { 'X-Idempotency-Key': confirmKey },
  })
}

/**
 * 配置改动对照卡：重算计划（并跑一次预演/自检）。与模型调用是同一段服务端代码、同一份参数契约，
 * 因此卡上"重新检查"出来的结论与助手先前说的是同一个东西。
 */
export function previewConfigChange(request: unknown, items: string[]) {
  return apiClient.post<AssistantConfigDiff>('/assistant/config-changes/preview', {
    ...(request as Record<string, unknown>),
    items,
  })
}

/**
 * 配置改动对照卡：应用被勾选的项。执行主体是这次点击，幂等键随本次确认给出——
 * 同一张卡重复点不会写两次（服务端按确认键识别重放）。
 */
export function applyConfigChange(request: unknown, items: string[], confirmKey: string) {
  return apiClient.post<AssistantConfigApplyResult>(
    '/assistant/config-changes/apply',
    { ...(request as Record<string, unknown>), items },
    { headers: { 'X-Idempotency-Key': confirmKey } },
  )
}

/** 可编辑字段元数据：就地编辑面由它驱动（列/字段清单不写死在前端）。 */
export function getFormDefinition(moduleId: number, mode: 'new' | 'edit') {
  return apiClient.get<FormDefinition>(`/document-workbench/${moduleId}/form-definition`, { query: { mode } })
}
