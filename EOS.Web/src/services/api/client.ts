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

  postFile<TBody = unknown>(path: string, body?: TBody, options?: { query?: Record<string, string | number | undefined>; signal?: AbortSignal }) {
    return this.transport.request<Blob>({ method: 'POST', path, body, query: options?.query, signal: options?.signal, responseType: 'blob' })
  }

  put<TResponse, TBody = unknown>(path: string, body?: TBody, signal?: AbortSignal) {
    return this.transport.request<TResponse>({ method: 'PUT', path, body, signal })
  }

  delete<TResponse>(path: string, options?: { headers?: Record<string, string>; signal?: AbortSignal }) {
    return this.transport.request<TResponse>({ method: 'DELETE', path, headers: options?.headers, signal: options?.signal })
  }
}
