import type { FormFieldDefinition } from './formDefinition'

export type FieldErrors = Record<string, string>

/** 与服务端一致的同步校验规则（服务端仍为最终权威） */
export function validateField(field: FormFieldDefinition, value: string): string | null {
  if (field.isReadonly || field.serverFilled) return null
  const text = value ?? ''
  const isBoolean = field.dataType.toLowerCase().includes('bit')
  // 勾选/布尔字段永远有值（0/1），不适用"必填"语义
  if (!isBoolean && field.isRequired && text.trim() === '') return '该字段不能为空。'
  if (field.maxLength != null && text.length > field.maxLength) return `内容长度超出限制（最多 ${field.maxLength} 字符）。`
  if (field.regex && text.trim() !== '') {
    try {
      if (!new RegExp(field.regex).test(text)) return '内容不符合格式要求。'
    } catch {
      // 忽略非法正则（服务端仍会校验），避免前端崩溃
    }
  }
  return null
}

export function validateMasterFields(fields: FormFieldDefinition[], values: Record<string, string>): FieldErrors {
  const errors: FieldErrors = {}
  for (const field of fields) {
    if (!field.isVisible) continue
    const message = validateField(field, values[field.key] ?? '')
    if (message) errors[field.key] = message
  }
  return errors
}

export function validateDetailRows(fields: FormFieldDefinition[], rows: Record<string, string>[]): FieldErrors[] {
  return rows.map(row => {
    const errors: FieldErrors = {}
    for (const field of fields) {
      if (!field.isVisible) continue
      const message = validateField(field, row[field.key] ?? '')
      if (message) errors[field.key] = message
    }
    return errors
  })
}
