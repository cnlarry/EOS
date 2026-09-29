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
  KbDocument,
  SituationSnapshot,
} from './types'

export function listSessions(limit = 50) {
  return apiClient.get<AssistantSession[]>('/assistant/sessions', { query: { limit } })
}

export function createSession() {
  return apiClient.post<AssistantSession>('/assistant/sessions')
}

export function listMessages(sessionId: string) {
  return apiClient.get<AssistantMessage[]>(`/assistant/sessions/${sessionId}/messages`)
}

export function deleteSession(sessionId: string) {
  return apiClient.delete<void>(`/assistant/sessions/${sessionId}`)
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
