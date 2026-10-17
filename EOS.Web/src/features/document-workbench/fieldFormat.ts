/**
 * 元数据字段值显示格式化。
 *
 * DISPLAY_FORMAT 直接作为 .NET 格式串生效（"{0:...}" 语义），
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

/** 显示 locale：显式传入优先，其次浏览器语言，最后中文。符号与分组跟随它，不再硬编码 zh-CN。 */
export function resolveLocale(locale?: string | null): string {
  if (locale?.trim()) return locale.trim()
  if (typeof navigator !== 'undefined' && navigator.language) return navigator.language
  return 'zh-CN'
}

/**
 * 当前 locale 的分组符与小数点符。
 * 印度是 2-2-3 分组、法语用不换行空格、德语小数点用逗号——这些都由 Intl 给出，不自己猜。
 */
function getSeparators(locale: string): { group: string; decimal: string } {
  const parts = new Intl.NumberFormat(locale, { useGrouping: true }).formatToParts(1234567.891)
  return {
    group: parts.find((part) => part.type === 'group')?.value ?? ',',
    decimal: parts.find((part) => part.type === 'decimal')?.value ?? '.',
  }
}

function formatDateValue(value: unknown, format: string | null, locale: string): string {
  const date = toDate(value)
  if (!date) return String(value)
  if (!format?.trim()) return date.toLocaleDateString(locale)
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

/** .NET 标准数字格式（N/F/G/E/P/C/D/X + 精度）， DISPLAY_FORMAT 同样按 .NET 语义生效 */
function formatStandardNumber(num: number, code: string, digits: number | null, locale: string, currency: string): string {
  const d = digits ?? 2
  switch (code.toUpperCase()) {
    case 'N':
      return new Intl.NumberFormat(locale, { minimumFractionDigits: d, maximumFractionDigits: d }).format(num)
    case 'F':
      return num.toFixed(d)
    case 'G':
      return digits == null ? String(num) : num.toPrecision(d)
    case 'E':
      return num.toExponential(digits ?? 6)
    case 'P':
      return `${(num * 100).toFixed(d)}%`
    case 'C':
      return new Intl.NumberFormat(locale, { style: 'currency', currency }).format(num)
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
function renderInteger(tokens: string[], intDigits: string, grouping: boolean, groupSeparator: string): string {
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
  // 注意：.NET 的 `#,##0` 是**固定 3 位分组**，不跟随 CLDR 的 2-2-3（印度）分组，
  // 所以这里保留 3 位分组算法，只把分隔符换成当前 locale 的符号。
  return grouping ? result.replace(/\B(?=(\d{3})+(?!\d))/g, groupSeparator) : result
}

/** .NET 自定义数字格式：0/# 占位符、小数点、千分位分组、%（百分比）、'-' 符号 */
function formatCustomNumber(num: number, formatText: string, locale: string): string {
  // .NET 自定义格式的分号分段：正;负;零。未写的段沿用第一段。
  const sections = formatText.split(';')
  const negativeSection = sections.length > 1 ? sections[1] : null
  const zeroSection = sections.length > 2 ? sections[2] : null
  const active = num === 0 && zeroSection !== null
    ? zeroSection
    : num < 0 && negativeSection !== null ? negativeSection : sections[0]

  // 会计口径：负号段写成 "(#,##0.00)" 时用括号代替负号
  const bracketed = num < 0 && active.startsWith('(') && active.endsWith(')')
  const core = bracketed ? active.slice(1, -1) : active

  const percent = core.includes('%')
  const magnitude = Math.abs(num) * (percent ? 100 : 1)
  const { group, decimal } = getSeparators(locale)
  const [intSectionRaw = '', fracSectionRaw = ''] = core.split('.', 2)
  const intTokens = intSectionRaw.match(/[0#]/g) ?? []
  const fracTokens = fracSectionRaw.match(/[0#]/g) ?? []
  const fracOut = renderFraction(fracTokens, magnitude)
  // 无小数占位符时按整数位四舍五入；有小数位时截断整数部分
  const intDigits = fracTokens.length === 0 ? String(Math.round(magnitude)) : String(Math.trunc(magnitude))
  const intOut = renderInteger(intTokens, intDigits, intSectionRaw.includes(','), group)
  const sign = num < 0 && !bracketed ? '-' : ''
  const body = `${sign}${intOut}${fracOut ? `${decimal}${fracOut}` : ''}${percent ? '%' : ''}`
  return bracketed ? `(${body})` : body
}

/**
 * 未配置 DISPLAY_FORMAT 时的类型默认：整数无小数，小数最多 6 位去尾零、带千分位。
 * 刻意**不**按金额压到 2 位——前端拿不到"这列是不是金额"（NumericRole 不下发），
 * 压错会把按千克计量的数量截断；等 numeric-context 端点上线后再细分。
 */
function formatDefaultNumber(num: number, dataType: string, locale: string): string {
  const isInteger = /^(int|bigint|smallint|tinyint)$/.test(dataType.toLowerCase())
  return new Intl.NumberFormat(locale, {
    useGrouping: true,
    minimumFractionDigits: 0,
    maximumFractionDigits: isInteger ? 0 : 6,
  }).format(num)
}

function formatNumberValue(
  value: unknown,
  format: string | null,
  dataType: string,
  locale: string,
  currency: string,
): string {
  const { group, decimal } = getSeparators(locale)
  // 按当前 locale 的分组符与小数点解析：德语区 "1.234,5" 与英语区 "1,234.5" 都要能读
  const rawText = String(value)
  const groupPattern = new RegExp(group.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'g')
  const normalized = typeof value === 'number'
    ? value
    : Number(rawText.replace(groupPattern, '').replace(decimal, '.'))
  const num = typeof value === 'number' ? value : normalized
  if (!Number.isFinite(num)) return String(value)
  const formatText = format?.trim()
  if (!formatText) return formatDefaultNumber(num, dataType, locale)
  const standard = /^([NnFfGgEePpCcDdXx])(\d{0,2})$/.exec(formatText)
  if (standard) return formatStandardNumber(num, standard[1], standard[2] ? Number(standard[2]) : null, locale, currency)
  if (!/[0#]/.test(formatText)) return String(num)
  return formatCustomNumber(num, formatText, locale)
}

export interface FormatOptions {
  /** 显示 locale；缺省跟随浏览器语言。 */
  locale?: string | null
  /** `C` 格式使用的 ISO 4217 币别，缺省取单据币别，再缺省为 CNY。 */
  currency?: string | null
}

export function formatFieldValue(
  value: unknown,
  dataType: string,
  format: string | null,
  options?: FormatOptions,
): string {
  if (value == null) return ''
  const locale = resolveLocale(options?.locale)
  const currency = options?.currency?.trim().toUpperCase() || 'CNY'
  const type = (dataType ?? '').toLowerCase()
  const formatText = format?.trim() ?? ''
  if (type === 'bit') return value ? '是' : '否'
  if (type.includes('date') || type.includes('time')) return formatDateValue(value, format, locale)
  // 文本类型也可能配置数字/日期格式（库内 nvarchar 存在 '0.##' 与 'yyyy-MM-dd'），按格式特征判断
  const numericLike = NUMERIC_TYPES.has(type)
    || /^[NnFfGgEePpCcDdXx]\d{0,2}$/.test(formatText)
    || formatText.includes('0')
    || formatText.includes('#')
  if (numericLike) return formatNumberValue(value, format, type, locale, currency)
  if (/[yMdHhms]/i.test(formatText)) return formatDateValue(value, format, locale)
  return String(value)
}

/**
 * 字段对齐方式（ITEM_ALIGN / HEADER_ALIGN：left/center/right）映射到 Tabler 文本对齐类。
 * 默认对齐：ITEM_ALIGN 为空时 int/float/double 等数字字段右对齐，其余左对齐；
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
