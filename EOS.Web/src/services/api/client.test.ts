import { describe, expect, it, vi } from 'vitest'
import { ApiClient } from './client'
import type { ApiTransport } from './transport'

function createClient() {
  const request = vi.fn<ApiTransport['request']>()
  const client = new ApiClient({ request } as ApiTransport)
  return { client, request }
}

describe('ApiClient', () => {
  it('get 传递 method/path/query/signal', async () => {
    const { client, request } = createClient()
    const signal = new AbortController().signal
    request.mockResolvedValue('ok')
    await client.get<string>('/records', { query: { page: 2, keyword: 'a b', empty: undefined }, signal })
    expect(request).toHaveBeenCalledWith({
      method: 'GET',
      path: '/records',
      query: { page: 2, keyword: 'a b', empty: undefined },
      signal,
    })
  })

  it('post 传递 body 与 signal', async () => {
    const { client, request } = createClient()
    request.mockResolvedValue({ id: '1' })
    const signal = new AbortController().signal
    const body = { name: 'x' }
    await client.post<{ id: string }, typeof body>('/records', body, signal)
    expect(request).toHaveBeenCalledWith({ method: 'POST', path: '/records', body, signal })
  })

  it('postFile 使用 blob responseType 并透传 query', async () => {
    const { client, request } = createClient()
    const blob = new Blob(['csv'])
    request.mockResolvedValue(blob)
    const result = await client.postFile('/export', { q: 1 }, { query: { page: 1 } })
    expect(result).toBe(blob)
    expect(request).toHaveBeenCalledWith({
      method: 'POST',
      path: '/export',
      body: { q: 1 },
      query: { page: 1 },
      signal: undefined,
      responseType: 'blob',
    })
  })

  it('put 与 delete 透传参数', async () => {
    const { client, request } = createClient()
    request.mockResolvedValue(undefined)
    await client.put('/records/1', { value: 1 })
    expect(request).toHaveBeenCalledWith({ method: 'PUT', path: '/records/1', body: { value: 1 }, signal: undefined })
    await client.delete('/records/1')
    expect(request).toHaveBeenCalledWith({ method: 'DELETE', path: '/records/1', signal: undefined })
  })
})
