/**
 * 服务端时间戳的显示格式化（操作审计 / 日志管理两张诊断列表共用的那一处）。
 *
 * 两处的时间串形状不同、语义相同，都是**服务端本地时间**的时刻：
 * - 审计来自 `CONVERT(varchar(23), OCCURRED_AT, 121)`：`2026-10-15 10:20:30.000`；
 * - 运行日志来自写盘时的 `yyyy-MM-ddTHH:mm:ss.fffzzz`：`2026-10-15T22:09:00.123+08:00`。
 *
 * 故只做字符串归一化取到秒，**不用 `new Date()` 重新解释**：那是把"墙上时钟"当成"带时区的时刻"，
 * 用户时区与服务器不一致时会把时间显示成另一个时刻——而这个时间是要拿去和日志文件、和同事对表的。
 */
export function formatTimestamp(value: string | null | undefined): string {
  const text = (value ?? '').trim()
  if (!text) return '—'
  const normalized = text.replace('T', ' ')
  return normalized.length > 19 ? normalized.slice(0, 19) : normalized
}
