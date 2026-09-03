/** 工作助手前端类型。ID 一律字符串（架构约定）。 */

export interface AssistantSession {
  id: string
  userId: string
  title: string
  createdAt: string
  lastActiveAt: string
}

export type AssistantRole = 1 | 2 | 3 // 1=USER 2=ASSISTANT 3=SYSTEM

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
