import { beforeEach, describe, expect, it, vi } from 'vitest'
import { installGlobalErrorHandlers } from './errorReporting'

function mockBeacon(): ReturnType<typeof vi.fn> {
  const sendBeacon = vi.fn(() => true)
  Object.defineProperty(navigator, 'sendBeacon', {
    value: sendBeacon,
    configurable: true,
  })
  return sendBeacon
}

describe('installGlobalErrorHandlers', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('window error 事件经 sendBeacon 上报', () => {
    const sendBeacon = mockBeacon()
    installGlobalErrorHandlers()
    window.dispatchEvent(new ErrorEvent('error', { message: 'boom' }))
    expect(sendBeacon).toHaveBeenCalledOnce()
    const [url, blob] = sendBeacon.mock.calls[0] as [string, Blob]
    expect(url).toBe('/api/v1/client-errors')
    expect(blob.type).toBe('application/json')
  })

  it('unhandledrejection 被上报且上报永不抛异常', () => {
    const sendBeacon = mockBeacon()
    installGlobalErrorHandlers()
    expect(() =>
      window.dispatchEvent(new Event('unhandledrejection')),
    ).not.toThrow()
    expect(sendBeacon).toHaveBeenCalled()
  })
})
