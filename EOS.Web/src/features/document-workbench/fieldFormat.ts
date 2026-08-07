/**
 * 元数据字段值显示格式化（对应旧系统 FIELDS.DISPLAY_FORMAT / F_TYPE）。
 *
 * 原则：任何异常值都不能让页面崩溃——解析失败时一律回退到原始字符串。
 */

function toDate(value: unknown): Date | null {
  if (value instanceof Date) return Number.isNaN(value.getTime()) ? null : value
  try {
    const date = new Date(String(value))
    return Number.isNaN(date.getTime()) ? null : date
  } catch {
    return null
  }
}

const pad = (value: number) => String(value).padStart(2, '0')

function formatDateValue(value: unknown, format: string | null): string {
  const date = toDate(value)
  if (!date) return String(value)
  if (!format?.trim()) return date.toLocaleDateString('zh-CN')
  const tokens: Record<string, string> = {
    yyyy: String(date.getFullYear()),
    yy: String(date.getFullYear()).slice(-2),
    MM: pad(date.getMonth() + 1),
    M: String(date.getMonth() + 1),
    dd: pad(date.getDate()),
    d: String(date.getDate()),
    HH: pad(date.getHours()),
    H: String(date.getHours()),
    hh: pad(date.getHours() % 12 || 12),
    h: String(date.getHours() % 12 || 12),
    mm: pad(date.getMinutes()),
    ss: pad(date.getSeconds()),
  }
  // 长 token 优先，避免 yyyy 被 yy 抢先、MM 被 M 抢先
  return format.trim().replace(/yyyy|yy|MM|M|dd|d|HH|H|hh|h|mm|ss/g, (token) => tokens[token] ?? token)
}

function formatNumberValue(value: unknown, format: string | null): string {
  const num = typeof value === 'number' ? value : Number(String(value).replace(/,/g, ''))
  if (!Number.isFinite(num)) return String(value)
  if (!format?.trim()) return String(num)
  const formatText = format.trim()
  const decimalPart = formatText.includes('.') ? formatText.split('.')[1] : ''
  const decimals = (decimalPart.match(/0/g) ?? []).length
  return new Intl.NumberFormat('zh-CN', {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
    useGrouping: formatText.includes(','),
  }).format(num)
}

export function formatFieldValue(value: unknown, dataType: string, format: string | null): string {
  if (value == null) return ''
  const type = (dataType ?? '').toLowerCase()
  if (type === 'bit') return value ? '是' : '否'
  if (type.includes('date') || type.includes('time')) return formatDateValue(value, format)
  if (typeof value === 'number' || ['int', 'float', 'double', 'numeric', 'decimal', 'money'].includes(type)) {
    return formatNumberValue(value, format)
  }
  return String(value)
}

/**
 * 字段对齐方式（ITEM_ALIGN / HEADER_ALIGN：left/center/right）映射到 Tabler 文本对齐类。
 * Tabler 不提供 .text-left/.text-right，只有 .text-start/.text-center/.text-end。
 */
export function alignClass(align?: string | null): string {
  switch (align?.toLowerCase()) {
    case 'center': return 'text-center'
    case 'right': return 'text-end'
    default: return 'text-start'
  }
}
