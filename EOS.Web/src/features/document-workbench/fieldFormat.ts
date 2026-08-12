/**
 * 元数据字段值显示格式化（复刻旧系统 FIELDS.DISPLAY_FORMAT / F_TYPE 规则）。
 *
 * 旧系统 DxGridView 把 DISPLAY_FORMAT 直接作为 .NET 格式串套进 DataFormatString（"{0:...}"），
 * 数据库实际值以自定义数字格式（0.##、0.00、#,##0.00、0.0000 等）与日期格式（yyyy-MM-dd）为主。
 * 本实现覆盖 .NET 常见自定义数字格式与标准数字格式（N/F/G/E/P/C/D/X），日期 token 与 .NET 对齐。
 *
 * 原则：任何异常值都不能让页面崩溃——解析失败时一律回退到原始字符串。
 */

const NUMERIC_TYPES = new Set([
  'int', 'integer', 'float', 'double', 'numeric', 'decimal', 'money', 'smallmoney',
  'bigint', 'smallint', 'tinyint', 'real',
])

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

/** .NET 标准数字格式（N/F/G/E/P/C/D/X + 精度），旧系统 DISPLAY_FORMAT 同样按 .NET 语义生效 */
function formatStandardNumber(num: number, code: string, digits: number | null): string {
  const d = digits ?? 2
  switch (code.toUpperCase()) {
    case 'N':
      return new Intl.NumberFormat('zh-CN', { minimumFractionDigits: d, maximumFractionDigits: d }).format(num)
    case 'F':
      return num.toFixed(d)
    case 'G':
      return digits == null ? String(num) : num.toPrecision(d)
    case 'E':
      return num.toExponential(digits ?? 6)
    case 'P':
      return `${(num * 100).toFixed(d)}%`
    case 'C':
      return new Intl.NumberFormat('zh-CN', { style: 'currency', currency: 'CNY' }).format(num)
    case 'D': {
      const sign = num < 0 ? '-' : ''
      return `${sign}${Math.trunc(Math.abs(num)).toString().padStart(d, '0')}`
    }
    case 'X':
      return Math.trunc(Math.abs(num)).toString(16).toUpperCase().padStart(d, '0')
    default:
      return String(num)
  }
}

/** 小数部分：'0' 位必填、'#' 位可省略；'#' 产生的尾零（含舍入补零）丢弃 */
function renderFraction(tokens: string[], value: number): string {
  if (tokens.length === 0) return ''
  const rounded = value.toFixed(tokens.length)
  const frac = rounded.includes('.') ? rounded.split('.')[1] : ''
  const out: string[] = []
  for (let i = 0; i < tokens.length; i++) {
    const digit = frac[i] ?? ''
    out.push(tokens[i] === '0' ? digit || '0' : digit)
  }
  let end = out.length
  for (let i = out.length - 1; i >= 0; i--) {
    if (tokens[i] === '#' && (out[i] === '0' || out[i] === '')) { end = i; continue }
    break
  }
  return out.slice(0, end).join('')
}

/**
 * 整数部分：'0' 位必填（含前导零补位）；'#' 位省略前导零（左起第一个非零数字以左的位
 * 不显示，含值为 0 时全部省略）；位不足时左侧溢出数字原样保留；含 ',' 时按千分位分组。
 */
function renderInteger(tokens: string[], intDigits: string, grouping: boolean): string {
  const significantStart = intDigits === '0' ? Number.POSITIVE_INFINITY : intDigits.search(/[1-9]/)
  const out: string[] = []
  for (let i = tokens.length - 1; i >= 0; i--) {
    const pos = intDigits.length - 1 - (tokens.length - 1 - i)
    const digit = pos >= 0 ? intDigits[pos] : ''
    if (tokens[i] === '0') {
      out.unshift(digit || '0')
    } else if (pos >= significantStart) {
      out.unshift(digit)
    } else {
      out.unshift('')
    }
  }
  const overflow = intDigits.length > tokens.length ? intDigits.slice(0, intDigits.length - tokens.length) : ''
  const result = overflow + out.join('')
  return grouping ? result.replace(/\B(?=(\d{3})+(?!\d))/g, ',') : result
}

/** .NET 自定义数字格式：0/# 占位符、小数点、千分位分组、%（百分比）、'-' 符号 */
function formatCustomNumber(num: number, formatText: string): string {
  const negative = num < 0
  const percent = formatText.includes('%')
  const value = Math.abs(num) * (percent ? 100 : 1)
  const [intSectionRaw = '', fracSectionRaw = ''] = formatText.split('.', 2)
  const intTokens = intSectionRaw.match(/[0#]/g) ?? []
  const fracTokens = fracSectionRaw.match(/[0#]/g) ?? []
  const fracOut = renderFraction(fracTokens, value)
  // 无小数占位符时按整数位四舍五入（对齐 .NET "0"/"#"）；有小数位时截断整数部分
  const intDigits = fracTokens.length === 0 ? String(Math.round(value)) : String(Math.trunc(value))
  const intOut = renderInteger(intTokens, intDigits, intSectionRaw.includes(','))
  const sign = negative ? '-' : ''
  return `${sign}${intOut}${fracOut ? `.${fracOut}` : ''}${percent ? '%' : ''}`
}

function formatNumberValue(value: unknown, format: string | null): string {
  const num = typeof value === 'number' ? value : Number(String(value).replace(/,/g, ''))
  if (!Number.isFinite(num)) return String(value)
  const formatText = format?.trim()
  if (!formatText) return String(num)
  const standard = /^([NnFfGgEePpCcDdXx])(\d{0,2})$/.exec(formatText)
  if (standard) return formatStandardNumber(num, standard[1], standard[2] ? Number(standard[2]) : null)
  if (!/[0#]/.test(formatText)) return String(num)
  return formatCustomNumber(num, formatText)
}

export function formatFieldValue(value: unknown, dataType: string, format: string | null): string {
  if (value == null) return ''
  const type = (dataType ?? '').toLowerCase()
  const formatText = format?.trim() ?? ''
  if (type === 'bit') return value ? '是' : '否'
  if (type.includes('date') || type.includes('time')) return formatDateValue(value, format)
  // 文本类型也可能配置数字/日期格式（库内 nvarchar 存在 '0.##' 与 'yyyy-MM-dd'），按格式特征判断
  const numericLike = NUMERIC_TYPES.has(type)
    || /^[NnFfGgEePpCcDdXx]\d{0,2}$/.test(formatText)
    || formatText.includes('0')
    || formatText.includes('#')
  if (numericLike) return formatNumberValue(value, format)
  if (/[yMdHhms]/i.test(formatText)) return formatDateValue(value, format)
  return String(value)
}

/**
 * 字段对齐方式（ITEM_ALIGN / HEADER_ALIGN：left/center/right）映射到 Tabler 文本对齐类。
 * 复刻旧系统 DxGridView 默认：ITEM_ALIGN 为空时 int/float/double 等数字字段右对齐，其余左对齐；
 * 显式 left/center/right 一律优先。
 */
export function alignClass(align?: string | null, dataType?: string | null): string {
  switch (align?.trim().toLowerCase()) {
    case 'center': return 'text-center'
    case 'right': return 'text-end'
    case 'left': return 'text-start'
    default: return NUMERIC_TYPES.has((dataType ?? '').toLowerCase()) ? 'text-end' : 'text-start'
  }
}
