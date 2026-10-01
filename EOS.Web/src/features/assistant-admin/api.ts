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
  sortBy?: 'lastActive' | 'created' | 'title' | 'messages'
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
