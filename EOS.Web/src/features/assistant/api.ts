import { apiClient } from '../../services/api'
import type { AssistantMessage, AssistantSession } from './types'

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
