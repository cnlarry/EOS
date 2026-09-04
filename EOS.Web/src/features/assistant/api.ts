import { apiClient } from '../../services/api'
import type { AssistantApplyResult, AssistantMemoryList, AssistantMessage, AssistantSession, KbDocument } from './types'

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

export function listMemory() {
  return apiClient.get<AssistantMemoryList>('/assistant/memory')
}

export function saveMemory(payload: { memoryType: string; memoryKey: string; memoryValue: string; confirmedRisk?: boolean }) {
  return apiClient.post<{ id: string }>('/assistant/memory', payload)
}

export function deleteMemory(memoryId: string) {
  return apiClient.delete<void>(`/assistant/memory/${memoryId}`)
}

export function getKbDocument(docId: string) {
  return apiClient.get<KbDocument>(`/assistant/kb/documents/${docId}`)
}

/** 变更集确认执行（M8）：只接受结构化确认卡调用，自然语言确认无效。 */
export function applyChangeset(changeset: unknown) {
  return apiClient.post<AssistantApplyResult>('/assistant/apply-changeset', { changeset, confirmed: true })
}
