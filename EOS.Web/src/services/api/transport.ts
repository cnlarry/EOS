export interface ApiRequest {
  method: 'GET' | 'POST' | 'PUT' | 'DELETE'
  path: string
  query?: Record<string, string | number | undefined>
  body?: unknown
  signal?: AbortSignal
  responseType?: 'json' | 'blob'
}

export interface ApiTransport {
  request<TResponse>(request: ApiRequest): Promise<TResponse>
}
