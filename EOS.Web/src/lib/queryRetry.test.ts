import { describe, expect, it } from 'vitest'
import { ApiError } from '../types/api'
import { shouldRetryQuery } from './queryRetry'

function apiError(status: number): ApiError {
  return new ApiError(status, { code: 'X', message: 'x' })
}

describe('shouldRetryQuery', () => {
  it.each([400, 401, 403, 404, 422])('4xx（%i）不重试——确定性失败，重试只会推迟报错', (status) => {
    expect(shouldRetryQuery(1, apiError(status))).toBe(false)
    expect(shouldRetryQuery(2, apiError(status))).toBe(false)
  })

  it('5xx 重试，且不超过 3 次', () => {
    expect(shouldRetryQuery(1, apiError(500))).toBe(true)
    expect(shouldRetryQuery(2, apiError(500))).toBe(true)
    expect(shouldRetryQuery(3, apiError(500))).toBe(false)
  })

  it('网络中断（不是 ApiError）按瞬时故障重试，次数同样封顶', () => {
    const networkError = new TypeError('Failed to fetch')
    expect(shouldRetryQuery(1, networkError)).toBe(true)
    expect(shouldRetryQuery(3, networkError)).toBe(false)
  })
})
