import type { ApiTransport } from './transport'

export class ApiClient {
  private readonly transport: ApiTransport

  constructor(transport: ApiTransport) {
    this.transport = transport
  }

  get<TResponse>(path: string, options?: { query?: Record<string, string | number | undefined>; signal?: AbortSignal }) {
    return this.transport.request<TResponse>({ method: 'GET', path, ...options })
  }

  post<TResponse, TBody = unknown>(path: string, body?: TBody, signal?: AbortSignal) {
    return this.transport.request<TResponse>({ method: 'POST', path, body, signal })
  }

  put<TResponse, TBody = unknown>(path: string, body?: TBody, signal?: AbortSignal) {
    return this.transport.request<TResponse>({ method: 'PUT', path, body, signal })
  }

  delete<TResponse>(path: string, signal?: AbortSignal) {
    return this.transport.request<TResponse>({ method: 'DELETE', path, signal })
  }
}
