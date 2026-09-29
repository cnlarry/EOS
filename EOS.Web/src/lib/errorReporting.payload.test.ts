import { afterEach, describe, expect, it, vi } from 'vitest'
import { reportClientError } from './errorReporting'

function stubBeacon(): ReturnType<typeof vi.fn> {
  const sendBeacon = vi.fn(() => true)
  vi.stubGlobal('navigator', { ...navigator, sendBeacon })
  return sendBeacon
}

/** 取最近一次上报的 JSON 载荷（sendBeacon 的第二个参数是 Blob，需按文本读取）。 */
async function lastPayload(sendBeacon: ReturnType<typeof vi.fn>): Promise<Record<string, unknown>> {
  const calls = sendBeacon.mock.calls
  const blob = calls[calls.length - 1][1] as Blob
  return JSON.parse(await blob.text()) as Record<string, unknown>
}

describe('reportClientError', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('上报载荷带关联键与产品版本（前后端错误可在同一报障编号下合流）', async () => {
    const sendBeacon = stubBeacon()
    reportClientError({
      kind: 'render',
      message: 'TypeError: boom',
      correlationId: 'corr-123',
      appVersion: '0.2.0',
      url: '/workbench/1406',
    })
    const payload = await lastPayload(sendBeacon)
    expect(payload.kind).toBe('render')
    expect(payload.correlationId).toBe('corr-123')
    expect(payload.appVersion).toBe('0.2.0')
    expect(payload.url).toBe('/workbench/1406')
  })

  it('未显式给版本时回落到构建注入的版本（测试环境未注入则为 unknown）', async () => {
    const sendBeacon = stubBeacon()
    reportClientError({ kind: 'error', message: 'boom' })
    const payload = await lastPayload(sendBeacon)
    expect(typeof payload.appVersion).toBe('string')
    expect(payload.appVersion).not.toBe('')
  })

  it('上报永不抛异常（视窗缺少 sendBeacon 时走 fetch 分支）', () => {
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('offline'))))
    expect(() => reportClientError({ kind: 'error', message: 'boom' })).not.toThrow()
  })
})
