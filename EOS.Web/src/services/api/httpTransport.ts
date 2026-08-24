import { ApiError, type ApiErrorBody } from '../../types/api'
import type { ApiRequest, ApiTransport } from './transport'

export class HttpTransport implements ApiTransport {
  private readonly baseUrl: string
  constructor(baseUrl = '/api/v1') { this.baseUrl = baseUrl }

  async request<TResponse>(request: ApiRequest): Promise<TResponse> {
    const url = new URL(`${this.baseUrl}${request.path}`, window.location.origin)
    Object.entries(request.query ?? {}).forEach(([key, value]) => { if (value !== undefined) url.searchParams.set(key, String(value)) })
    // ADR-005 §5.1：调用方经 X-Correlation-Id 传入关联 ID（未携带时服务端生成），
    // X-Client-Id 标识调用方（eos.web），随日志、审计与错误响应透传。
    const headers: Record<string, string> = {
      'X-Client-Id': 'eos.web',
      'X-Correlation-Id': newCorrelationId(),
      ...request.headers,
    }
    if (request.body !== undefined) headers['Content-Type'] = 'application/json'
    const response = await fetch(url, {
      method: request.method,
      credentials: 'include',
      headers,
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      signal: request.signal,
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => ({})) as { title?: string; detail?: string; message?: string; code?: string; traceId?: string; correlationId?: string; definitionVersion?: string }
      const message = problem.message ?? problem.detail ?? problem.title ?? '请求失败。'
      const body: ApiErrorBody = { code: problem.code ?? `HTTP_${response.status}`, message, requestId: problem.traceId, correlationId: problem.correlationId, definitionVersion: problem.definitionVersion }
      throw new ApiError(response.status, body)
    }
    if (request.responseType === 'blob') return response.blob() as Promise<TResponse>
    if (response.status === 204) return undefined as TResponse
    return response.json() as Promise<TResponse>
  }
}

function newCorrelationId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `eos-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}
