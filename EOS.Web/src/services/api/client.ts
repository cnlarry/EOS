import type { ApiTransport } from './transport'

export class ApiClient {
  private readonly transport: ApiTransport

  constructor(transport: ApiTransport) {
    this.transport = transport
  }

  get<TResponse>(path: string, options?: { query?: Record<string, string | number | undefined>; signal?: AbortSignal }) {
    return this.transport.request<TResponse>({ method: 'GET', path, ...options })
  }

  // query 与 get/postFile 对称：有些动作类端点用一个查询参数区分"对哪一类做这件事"
  // （如"取消当前模型"要说明是对话还是嵌入），而把这种标志塞进 body 会让 GET 与 POST 两种写法
  // 的服务端读法不一致——同一个参数在两处要用两种方式读，早晚有一处读漏
  post<TResponse, TBody = unknown>(path: string, body?: TBody, options?: { query?: Record<string, string | number | undefined>; headers?: Record<string, string>; signal?: AbortSignal }) {
    return this.transport.request<TResponse>({ method: 'POST', path, body, query: options?.query, headers: options?.headers, signal: options?.signal })
  }

  /**
   * multipart/form-data 上传：走统一传输层，因而与其余请求共用鉴权、X-Correlation-Id
   * 与错误体解码（`fetch` + `FormData` 手写一遍就会漏掉这三样）。
   */
  postForm<TResponse>(path: string, body: FormData, options?: { signal?: AbortSignal }) {
    return this.transport.request<TResponse>({ method: 'POST', path, body, signal: options?.signal })
  }

  postFile<TBody = unknown>(path: string, body?: TBody, options?: { query?: Record<string, string | number | undefined>; signal?: AbortSignal }) {
    return this.transport.request<Blob>({ method: 'POST', path, body, query: options?.query, signal: options?.signal, responseType: 'blob' })
  }

  /**
   * GET 下载：取二进制响应（如报表收件箱的 PDF）。
   * 与 postFile 对称——经统一传输层取 blob，鉴权头与错误体解码与其它请求一致，
   * 不走 window.open（那会绕过错误处理，失败时只剩一个浏览器原生错误页）。
   */
  getFile(path: string, options?: { query?: Record<string, string | number | undefined>; signal?: AbortSignal }) {
    return this.transport.request<Blob>({ method: 'GET', path, query: options?.query, signal: options?.signal, responseType: 'blob' })
  }

  put<TResponse, TBody = unknown>(path: string, body?: TBody, signal?: AbortSignal) {
    return this.transport.request<TResponse>({ method: 'PUT', path, body, signal })
  }

  delete<TResponse>(path: string, options?: { headers?: Record<string, string>; signal?: AbortSignal }) {
    return this.transport.request<TResponse>({ method: 'DELETE', path, headers: options?.headers, signal: options?.signal })
  }
}
