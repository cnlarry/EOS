/**
 * 自定义按钮（EVENT_CODE='MANUAL'）的配置辅助：事件码常量与入参声明的编解码。
 *
 * 事件码与服务端 `BusinessActionCatalog.Events` 同源；这里的编解码与服务端
 * `DocumentActionParams` 的闭式结构一一对应，产出即服务端认得的结构。
 */
export const MANUAL_EVENT = 'MANUAL'

/** 一个入参声明项：点击按钮时让用户填什么。 */
export interface DocumentActionParamField {
  key: string
  label: string
  type: string
  required: boolean
  maxLength: number | null
}

const PARAM_TYPES = ['string', 'number', 'date', 'bool'] as const

/** 解析声明文本；非法 JSON 或非声明结构一律视为"还没有参数"。 */
export function parseDocumentActionParams(json: string | null | undefined): DocumentActionParamField[] {
  if (!json || json.trim() === '') return []
  try {
    const parsed = JSON.parse(json) as { fields?: unknown }
    if (!parsed || typeof parsed !== 'object' || !Array.isArray(parsed.fields)) return []
    return parsed.fields.flatMap((item) => {
      if (!item || typeof item !== 'object') return []
      const record = item as Record<string, unknown>
      const key = typeof record.key === 'string' ? record.key : ''
      if (key.trim() === '') return []
      const type = typeof record.type === 'string' && (PARAM_TYPES as readonly string[]).includes(record.type)
        ? record.type
        : 'string'
      return [{
        key,
        label: typeof record.label === 'string' ? record.label : '',
        type,
        required: record.required === true,
        maxLength: typeof record.maxLength === 'number' ? record.maxLength : null,
      }]
    })
  } catch {
    return []
  }
}

/** 序列化回声明文本；空清单返回 null（"不需要参数"就是没有声明）。 */
export function serializeDocumentActionParams(fields: DocumentActionParamField[]): string | null {
  if (fields.length === 0) return null
  return JSON.stringify({
    fields: fields.map((field) => {
      const item: Record<string, unknown> = { key: field.key.trim(), label: field.label, type: field.type }
      if (field.required) item.required = true
      if (field.maxLength != null && field.type === 'string') item.maxLength = field.maxLength
      return item
    }),
  })
}
