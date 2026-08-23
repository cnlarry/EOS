import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { HttpTransport } from './httpTransport'

describe('HttpTransport', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('GET 拼接查询参数并携带 credentials', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ rows: [] }) })
    vi.stubGlobal('fetch', fetchMock)
    const transport = new HttpTransport()
    const result = await transport.request({ method: 'GET', path: '/records', query: { page: 2, keyword: 'a', empty: undefined } })
    expect(result).toEqual({ rows: [] })
    const [url, init] = fetchMock.mock.calls[0]
    expect(String(url)).toContain('/api/v1/records?page=2&keyword=a')
    expect(init.credentials).toBe('include')
    expect(init.headers['X-Client-Id']).toBe('eos.web')
    expect(init.headers['X-Correlation-Id']).toBeTruthy()
    expect(init.headers['Content-Type']).toBeUndefined()
  })

  it('POST 序列化 JSON body', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 204, json: async () => undefined })
    vi.stubGlobal('fetch', fetchMock)
    const transport = new HttpTransport()
    const result = await transport.request({ method: 'POST', path: '/records', body: { name: 'x' } })
    expect(result).toBeUndefined()
    const [, init] = fetchMock.mock.calls[0]
    expect(init.body).toBe(JSON.stringify({ name: 'x' }))
    expect(init.headers['Content-Type']).toBe('application/json')
    expect(init.headers['X-Client-Id']).toBe('eos.web')
    expect(init.headers['X-Correlation-Id']).toBeTruthy()
  })

  it('非 2xx 抛出 ApiError 并映射 message/code/traceId', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: false,
      status: 400,
      json: async () => ({ title: '标题', detail: '细节', code: 'BAD', traceId: 't-1' }),
    })
    vi.stubGlobal('fetch', fetchMock)
    const transport = new HttpTransport()
    const error = await transport.request({ method: 'GET', path: '/bad' }).catch((reason) => reason) as ApiError
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(400)
    expect(error.body).toEqual({ code: 'BAD', message: '细节', requestId: 't-1' })
  })

  it('错误响应体无法解析时使用默认消息与 HTTP_ 状态码', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: false, status: 500, json: async () => { throw new SyntaxError('bad json') } })
    vi.stubGlobal('fetch', fetchMock)
    const transport = new HttpTransport()
    const error = await transport.request({ method: 'GET', path: '/bad' }).catch((reason) => reason) as ApiError
    expect(error.body).toEqual({ code: 'HTTP_500', message: '请求失败。', requestId: undefined })
  })

  it('blob responseType 返回 Blob', async () => {
    const blob = new Blob(['a'])
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, blob: async () => blob })
    vi.stubGlobal('fetch', fetchMock)
    const transport = new HttpTransport()
    const result = await transport.request({ method: 'POST', path: '/export', responseType: 'blob' })
    expect(result).toBe(blob)
  })

  it('200 解析 JSON', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ total: 5 }) })
    vi.stubGlobal('fetch', fetchMock)
    const transport = new HttpTransport('https://example.test/api')
    const result = await transport.request({ method: 'GET', path: '/x' })
    expect(result).toEqual({ total: 5 })
  })
})
