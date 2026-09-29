import { ApiError, type ApiErrorBody } from '../../types/api'
import type { ApiRequest, ApiTransport } from './transport'
import { createId } from '../../lib/uuid'

export class HttpTransport implements ApiTransport {
  private readonly baseUrl: string
  constructor(baseUrl = '/api/v1') { this.baseUrl = baseUrl }

  async request<TResponse>(request: ApiRequest): Promise<TResponse> {
    const url = new URL(`${this.baseUrl}${request.path}`, window.location.origin)
    Object.entries(request.query ?? {}).forEach(([key, value]) => { if (value !== undefined) url.searchParams.set(key, String(value)) })
    // X-Correlation-Id: 每次请求默认新生成一个；同一用户操作触发多个请求时，
    // 调用方应显式传入同一个值（request.headers 展开在最后，可覆盖默认值），
    // 这样服务端日志里能把"这一次点击"的全部请求串起来。
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
  return createId()
}
