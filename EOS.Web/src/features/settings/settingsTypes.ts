export interface SysSettingsField {
  key: string
  label: string | null
  type: string
  maxLength: number | null
  nullable: boolean
  order: number
}

export interface SysSettingsPayload {
  values: Record<string, string | number | boolean | null>
  fields: SysSettingsField[]
}

/** 页面表单值：全部按字符串保存，服务端按列类型归一化。 */
export type SettingsForm = Record<string, string>

export function isTruthy(value: string): boolean {
  return value === 'true' || value === '1' || value === '是'
}

export function isBitType(type: string): boolean {
  return type.toLowerCase() === 'bit'
}

export function isDateTimeType(type: string): boolean {
  const normalized = type.toLowerCase()
  return normalized.includes('datetime') || normalized === 'date'
}

export function isNumericType(type: string): boolean {
  const normalized = type.toLowerCase()
  return normalized.includes('int')
    || normalized.includes('decimal')
    || normalized.includes('numeric')
    || normalized.includes('float')
    || normalized.includes('real')
    || normalized.includes('money')
}

/** 把服务端返回值转换为表单字符串（bit→true/false，日期→datetime-local/date 可编辑格式）。 */
export function formatValueForInput(field: SysSettingsField, raw: string | number | boolean | null | undefined): string {
  if (raw === null || raw === undefined) return ''
  if (isBitType(field.type)) return raw === true ? 'true' : 'false'
  if (isDateTimeType(field.type) && typeof raw === 'string') {
    const normalized = field.type.toLowerCase() === 'date' ? raw.slice(0, 10) : raw.slice(0, 16)
    return normalized
  }
  return String(raw)
}

export function initialSettingsForm(fields: SysSettingsField[], values: SysSettingsPayload['values']): SettingsForm {
  const form: SettingsForm = {}
  for (const field of fields) form[field.key] = formatValueForInput(field, values[field.key])
  return form
}
