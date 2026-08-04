export interface ApiRequest {
  method: 'GET' | 'POST' | 'PUT' | 'DELETE'
  path: string
  query?: Record<string, string | number | undefined>
  body?: unknown
  signal?: AbortSignal
}

export interface ApiTransport {
  request<TResponse>(request: ApiRequest): Promise<TResponse>
}
