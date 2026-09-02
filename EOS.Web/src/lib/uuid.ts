/**
 * 幂等/关联 ID 生成（代码质量批 3 C8 收编）：formEditorUtils.newIdempotencyKey 与
 * httpTransport.newCorrelationId 原本逐字相同，统一到本函数。
 */
export function createId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID()
  return `eos-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`
}
