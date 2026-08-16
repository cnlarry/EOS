import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../types/api'
import { MockTransport } from './mockTransport'

async function run(transport: MockTransport, request: Parameters<MockTransport['request']>[0]) {
  const promise = transport.request(request)
  promise.catch(() => undefined) // 提前挂空 catch，避免 unhandled rejection 干扰测试报告
  await vi.advanceTimersByTimeAsync(500)
  return promise
}

describe('MockTransport', () => {
  afterEach(() => {
    vi.useRealTimers()
    sessionStorage.clear()
  })

  it('登录成功写入会话并返回 bootstrap', async () => {
    vi.useFakeTimers()
    const transport = new MockTransport()
    const result = await run(transport, { method: 'POST', path: '/auth/login', body: { userId: 'admin', password: 'erp123', rememberMe: false } })
    expect((result as { user: { username: string } }).user.username).toBe('admin')
    expect(sessionStorage.getItem('erp-mock-session')).toBe('admin')
  })

  it('登录失败抛出 401', async () => {
    vi.useFakeTimers()
    const transport = new MockTransport()
    const error = await run(transport, { method: 'POST', path: '/auth/login', body: { userId: 'admin', password: 'wrong', rememberMe: false } }).catch((reason) => reason) as ApiError
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(401)
    expect(error.body.code).toBe('INVALID_CREDENTIALS')
  })

  it('注销清理会话', async () => {
    vi.useFakeTimers()
    const transport = new MockTransport()
    await run(transport, { method: 'POST', path: '/auth/login', body: { userId: 'purchaser', password: 'erp123', rememberMe: false } })
    await run(transport, { method: 'POST', path: '/auth/logout' })
    const error = await run(transport, { method: 'GET', path: '/app/bootstrap' }).catch((reason) => reason) as ApiError
    expect(error.body.code).toBe('UNAUTHENTICATED')
  })

  it('bootstrap 未登录返回 401', async () => {
    vi.useFakeTimers()
    const transport = new MockTransport()
    const error = await run(transport, { method: 'GET', path: '/app/bootstrap' }).catch((reason) => reason) as ApiError
    expect(error.status).toBe(401)
  })

  it('未知路由返回 404', async () => {
    vi.useFakeTimers()
    const transport = new MockTransport()
    const error = await run(transport, { method: 'GET', path: '/nope' }).catch((reason) => reason) as ApiError
    expect(error.status).toBe(404)
    expect(error.body.message).toContain('/nope')
  })

  it('请求中止时拒绝 AbortError', async () => {
    vi.useFakeTimers()
    const transport = new MockTransport()
    const controller = new AbortController()
    const promise = transport.request({ method: 'GET', path: '/nope', signal: controller.signal })
    promise.catch(() => undefined)
    controller.abort()
    await expect(promise).rejects.toMatchObject({ name: 'AbortError' })
  })
})
