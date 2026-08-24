export interface ApiRequest {
  method: 'GET' | 'POST' | 'PUT' | 'DELETE'
  path: string
  query?: Record<string, string | number | undefined>
  body?: unknown
  /** 追加请求头（如 DELETE 的 X-Idempotency-Key，ADR-006 决策 2.1） */
  headers?: Record<string, string>
  signal?: AbortSignal
  responseType?: 'json' | 'blob'
}

export interface ApiTransport {
  request<TResponse>(request: ApiRequest): Promise<TResponse>
}
