/**
 * 日期/时间控件值与提交载荷的往返转换（ADR-006 决策 2.3）。
 *
 * 契约：
 * - 控件值：date → `yyyy-MM-dd`；datetime/smalldatetime → `yyyy-MM-ddTHH:mm:ss`（datetime-local，本地朴素串）
 * - 提交载荷：与控件值同构（无时区后缀），秒缺省补 `00`；格式化只发生在渲染层
 * - 服务端 record 返回的历史格式（`yyyy-MM-dd HH:mm:ss`、ISO `T` 分隔、带毫秒）都能解析回显，
 *   保证「读回→改存」不丢时间分量
 */

const pad = (value: number) => String(value).padStart(2, '0')

/** 解析库内/控件的历史日期串为 Date；无法解析返回 null */
function parseDateText(text: string): Date | null {
  const normalized = text.trim().replace(' ', 'T')
  const match = /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?$/.exec(normalized)
  if (!match) return null
  const [, y, m, d, hh = '00', mm = '00', ss = '00'] = match
  const date = new Date(Number(y), Number(m) - 1, Number(d), Number(hh), Number(mm), Number(ss))
  return Number.isNaN(date.getTime()) ? null : date
}

/** 日期/时间文本是否可解析（校验对拍用：与服务端 DateTime.TryParse 的受控子集一致） */
export function isParseableDateText(text: string): boolean {
  return parseDateText(text) !== null
}

/** 库内值 → 控件值（datetime-local/date 输入框可接受的格式）；空值/不可解析原样返回 */
export function toDateTimeControlValue(dataType: string, value: string): string {
  if (!value?.trim()) return ''
  const type = dataType.toLowerCase()
  const isDateTime = type.includes('datetime') || type.includes('time')
  if (!isDateTime && !type.includes('date')) return value
  const date = parseDateText(value)
  // 不可解析回退（契约：任何异常值不能让页面崩溃）：date 原样返回；datetime 控件无法承载 → 空
  if (!date) return isDateTime ? '' : value
  const base = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
  if (!isDateTime) return base
  return `${base}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

/** 控件值 → 提交载荷：datetime-local 秒缺省补 `00`，date 原样；空值返回空串 */
export function fromDateTimeControlValue(value: string): string {
  const text = value?.trim() ?? ''
  if (!text) return ''
  const match = /^(\d{4}-\d{2}-\d{2})(?:T(\d{2}:\d{2})(?::(\d{2}))?)?$/.exec(text)
  if (!match) return text
  const [, day, hm, ss] = match
  return hm ? `${day}T${hm}:${ss ?? '00'}` : day
}
