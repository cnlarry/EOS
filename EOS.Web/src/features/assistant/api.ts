import { apiClient } from '../../services/api'
import type { AssistantMemoryList, AssistantMessage, AssistantSession } from './types'

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
