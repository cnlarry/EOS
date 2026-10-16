import { ApiError } from '../types/api'

/**
 * 查询失败是否重试：**4xx 一律不重试**，5xx 与网络中断才重试（最多 3 次，与 react-query 默认同量级）。
 *
 * 为什么 4xx 不该重试：403（权限不足）、404（按防探测口径隐藏）、400/422（参数不对）都是
 * **确定性结果**——同一个请求再打一遍只会得到同一个失败。默认"一律重试 3 次"的代价在两处可见：
 * 界面上错误要等 1+2+4 秒退避才显示；服务端日志里同一次失败被刷成 4 条
 * （实测 `GET /api/v1/import/targets -> 403` 连打 5 次的记录就是这么来的）。
 *
 * 5xx 与网络错误是瞬时故障，重试有意义，保持重试。
 */
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false
  return failureCount < 3
}
