import { ApiError, type ApiErrorBody } from '../../types/api'
import type { ApiRequest, ApiTransport } from './transport'

export class HttpTransport implements ApiTransport {
  private readonly baseUrl: string
  constructor(baseUrl = '/api') { this.baseUrl = baseUrl }

  async request<TResponse>(request: ApiRequest): Promise<TResponse> {
    const url = new URL(`${this.baseUrl}${request.path}`, window.location.origin)
    Object.entries(request.query ?? {}).forEach(([key, value]) => { if (value !== undefined) url.searchParams.set(key, String(value)) })
    const response = await fetch(url, {
      method: request.method,
      credentials: 'include',
      headers: request.body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      signal: request.signal,
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => ({})) as { title?: string; detail?: string; message?: string; code?: string; traceId?: string }
      const message = problem.message ?? problem.detail ?? problem.title ?? '请求失败。'
      const body: ApiErrorBody = { code: problem.code ?? `HTTP_${response.status}`, message, requestId: problem.traceId }
      throw new ApiError(response.status, body)
    }
    if (request.responseType === 'blob') return response.blob() as Promise<TResponse>
    if (response.status === 204) return undefined as TResponse
    return response.json() as Promise<TResponse>
  }
}
