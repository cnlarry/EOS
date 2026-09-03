import type { FormFieldDefinition } from './formDefinition'
import { canonicalizeDecimalValue } from './formEditorUtils'
import { fieldVariant } from './formFieldKind'
import { isParseableDateText } from './dateTimeValue'

export type FieldErrors = Record<string, string>

/**
 * 字段校验判定码：
 * 与服务端 RecordPayloadValidator 的结论一一对应，两端由同一份
 * `EOS.API.Tests/form-validation-parity.json` 一致性比对锁定——任一端修改规则必须先改 fixture。
 */
export type FieldVerdictCode =
  | 'ok'
  | 'VALUE_TOO_LONG'
  | 'REQUIRED_FIELD_MISSING'
  | 'REGEX_MISMATCH'
  | 'INVALID_VALUE'
  | 'SCALE_EXCEEDED'
  | 'PRECISION_EXCEEDED'

/** 判定码 → 展示文案（与服务端 FieldError.Message 文本保持一致） */
export const VERDICT_MESSAGES: Record<Exclude<FieldVerdictCode, 'ok'>, string> = {
  VALUE_TOO_LONG: '内容长度超出限制（最多 {max} 字符）。',
  REQUIRED_FIELD_MISSING: '该字段不能为空。',
  REGEX_MISMATCH: '内容不符合格式要求。',
  INVALID_VALUE: '数值格式不正确。',
  SCALE_EXCEEDED: '小数位超出精度。',
  PRECISION_EXCEEDED: '数值超出精度范围。',
}

export interface FieldVerdict {
  code: FieldVerdictCode
  /** 规范化后的提交值（trim 契约 / 数值规范化）；错误时为原输入 */
  value: string
  messageArgs?: Record<string, number>
}

const DECIMAL_VARIANT_TYPES = /decimal|numeric/

/**
 * 单字段判定（纯函数）：镜像服务端 ValidateSubmitted + CheckRequiredAndRegex 管线。
 * - trim 契约：校验与产出值均基于 trim 后文本；
 * - 空串等价 null：跳过长度/正则，必填报 REQUIRED_FIELD_MISSING；
 * - decimal 变体：canonicalize 后必须可解析，且受 precision/scale 拒绝式约束；
 * - date/datetime 变体：必须可解析为 yyyy-MM-dd[THH:mm[:ss]]；
 * - bit：任意值合法（true/1/是 → '1'，否则 '0'），无必填语义。
 */
export function evaluateField(field: FormFieldDefinition, rawInput: string | undefined): FieldVerdict {
  const raw = rawInput ?? ''
  const trimmed = raw.trim()
  const isBoolean = fieldVariant(field) === 'checkbox'
  if (isBoolean) {
    return { code: 'ok', value: trimmed === '1' || trimmed.toLowerCase() === 'true' || trimmed === '是' ? '1' : '0' }
  }
  if (trimmed === '') {
    // 空串等价 null：长度/正则不适用；必填在此拦截（与服务端转换后 null 一致）
    if (field.isRequired) return { code: 'REQUIRED_FIELD_MISSING', value: trimmed }
    return { code: 'ok', value: '' }
  }
  if (field.maxLength != null && trimmed.length > field.maxLength) {
    return { code: 'VALUE_TOO_LONG', value: trimmed, messageArgs: { max: field.maxLength } }
  }
  const variant = fieldVariant(field)
  let value = trimmed
  if (variant === 'decimal') {
    value = canonicalizeDecimalValue(trimmed)
    if (!Number.isFinite(Number(value))) return { code: 'INVALID_VALUE', value: trimmed }
    // int 字段：canonicalize 后必须仍是纯整数（服务端 int.Parse 拒绝小数/超界）
    if (/int/.test(field.dataType.toLowerCase())) {
      if (!/^-?\d+$/.test(value) || !Number.isSafeInteger(Number(value))) {
        return { code: 'INVALID_VALUE', value: trimmed }
      }
    }
    // precision/scale 仅 decimal/numeric 启用（float/money 不适用），拒绝式不做静默舍入
    if (DECIMAL_VARIANT_TYPES.test(field.dataType.toLowerCase()) && field.scale != null) {
      const [, fraction = ''] = value.split('.')
      if (fraction.length > field.scale) return { code: 'SCALE_EXCEEDED', value: trimmed, messageArgs: { scale: field.scale } }
      if (field.precision != null) {
        const integerLimit = Math.pow(10, field.precision - field.scale)
        if (Math.abs(Number(value)) >= integerLimit) {
          return { code: 'PRECISION_EXCEEDED', value: trimmed, messageArgs: { integerDigits: Math.max(0, field.precision - field.scale) } }
        }
      }
    }
  } else if (variant === 'date' || variant === 'datetime') {
    if (!isParseableDateText(trimmed)) return { code: 'INVALID_VALUE', value: trimmed }
  }
  if (field.regex) {
    try {
      if (!new RegExp(field.regex).test(value)) return { code: 'REGEX_MISMATCH', value }
    } catch {
      // 忽略非法正则（服务端仍会校验），避免前端崩溃
    }
  }
  return { code: 'ok', value }
}

function verdictMessage(verdict: FieldVerdict): string {
  if (verdict.code === 'ok') return ''
  const template = VERDICT_MESSAGES[verdict.code]
  if (verdict.code === 'VALUE_TOO_LONG') return template.replace('{max}', String(verdict.messageArgs?.max ?? ''))
  if (verdict.code === 'SCALE_EXCEEDED') return template.replace('{scale}', String(verdict.messageArgs?.scale ?? ''))
  if (verdict.code === 'PRECISION_EXCEEDED') return template.replace('{integerDigits}', String(verdict.messageArgs?.integerDigits ?? ''))
  return template
}

function buildFieldSchema(field: FormFieldDefinition) {
  return (rawInput: string | undefined) => {
    const verdict = evaluateField(field, rawInput)
    return verdict.code === 'ok' ? null : verdictMessage(verdict)
  }
}

function validateFields(fields: FormFieldDefinition[], values: Record<string, string>): FieldErrors {
  const errors: FieldErrors = {}
  for (const field of fields) {
    if (!field.isVisible || field.isReadonly || field.serverFilled) continue
    const error = buildFieldSchema(field)(values[field.key])
    if (error) errors[field.key] = error
  }
  return errors
}

export function validateField(field: FormFieldDefinition, value: string): string | null {
  if (field.isReadonly || field.serverFilled) return null
  return validateFields([field], { [field.key]: value })[field.key] ?? null
}

export function validateMasterFields(fields: FormFieldDefinition[], values: Record<string, string>): FieldErrors {
  return validateFields(fields, values)
}

export function validateDetailRows(fields: FormFieldDefinition[], rows: Record<string, string>[], dfVerify = ''): FieldErrors[] {
  const result = rows.map(row => validateFields(fields, row))
  if (dfVerify) {
    const keys = dfVerify.split(';').map(key => key.trim()).filter(Boolean)
    if (keys.length > 0) {
      const seen = new Map<string, number>()
      rows.forEach((row, index) => {
        const groupKey = keys.map(key => row[key] ?? '').join('\u0001')
        const first = seen.get(groupKey)
        if (first === undefined) seen.set(groupKey, index)
        else if (!result[index][keys[0]]) result[index][keys[0]] = '明细表资料重复。'
      })
    }
  }
  return result
}
