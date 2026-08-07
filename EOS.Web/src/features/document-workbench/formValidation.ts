import { z } from 'zod'
import type { FormFieldDefinition } from './formDefinition'

export type FieldErrors = Record<string, string>

function buildFieldSchema(field: FormFieldDefinition): z.ZodType<string> {
  let schema: z.ZodString = z.string()
  if (field.maxLength != null) {
    schema = schema.max(field.maxLength, { message: `内容长度超出限制（最多 ${field.maxLength} 字符）。` })
  }
  let validated: z.ZodType<string> = schema
  const isBoolean = field.dataType.toLowerCase().includes('bit')
  // 勾选/布尔字段永远有值（0/1），不适用"必填"语义
  if (field.isRequired && !isBoolean) {
    validated = validated.refine(value => value.trim() !== '', { message: '该字段不能为空。' })
  }
  if (field.regex) {
    try {
      const pattern = new RegExp(field.regex)
      validated = validated.refine(value => value.trim() === '' || pattern.test(value), { message: '内容不符合格式要求。' })
    } catch {
      // 忽略非法正则（服务端仍会校验），避免前端崩溃
    }
  }
  return validated
}

function validateFields(fields: FormFieldDefinition[], values: Record<string, string>): FieldErrors {
  const entries = fields
    .filter(field => field.isVisible && !field.isReadonly && !field.serverFilled)
    .map(field => [field.key, buildFieldSchema(field)] as const)
  if (entries.length === 0) return {}
  const schema = z.object(Object.fromEntries(entries) as Record<string, z.ZodTypeAny>)
  const result = schema.safeParse(values)
  if (result.success) return {}
  const errors: FieldErrors = {}
  for (const issue of result.error.issues) {
    const key = String(issue.path[0] ?? '')
    if (key && !errors[key]) errors[key] = issue.message
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
