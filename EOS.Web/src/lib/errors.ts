import { ApiError } from '../types/api'

/**
 * 统一错误消息提取（代码质量批 3 C7 收编）：
 * 原 30+ 文件散落 `x instanceof ApiError ? x.body.message : fallback` 逐字拷贝，
 * 收敛到本函数；非 ApiError 或空消息回退 fallback。
 */
export function describeApiError(error: unknown, fallback: string): string {
  return error instanceof ApiError ? (error.body.message || fallback) : fallback
}
