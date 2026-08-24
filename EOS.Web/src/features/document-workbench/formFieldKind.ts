import type { FormFieldDefinition } from './formDefinition'

/** 控件变体（ADR-006 决策 1：变体注册表派发键；新控件类型只加变体，不改页面编排） */
export type FieldVariant = 'checkbox' | 'select' | 'date' | 'datetime' | 'decimal' | 'textarea' | 'text'

const DECIMAL_TYPES = /decimal|numeric|money|float|real|int/
const DATE_TYPES = /date|time/

/**
 * 变体判定（按 F_TYPE/dataType）：
 * - bit → checkbox；FORM_OPTIONS 非空 → select；
 * - date → date；datetime/smalldatetime/time → datetime；
 * - decimal/numeric/money/float/int 系 → decimal（文本框 + inputmode，失焦按 DISPLAY_FORMAT 格式化）；
 * - REMARK 类/text(ntext) 长文本 → textarea；其余 → text。
 */
export function fieldVariant(field: FormFieldDefinition): FieldVariant {
  const type = field.dataType.toLowerCase()
  if (type.includes('bit')) return 'checkbox'
  if ((field.options?.length ?? 0) > 0) return 'select'
  if (DATE_TYPES.test(type)) return type === 'date' ? 'date' : 'datetime'
  if (DECIMAL_TYPES.test(type)) return 'decimal'
  if (type === 'text' || type === 'ntext') return 'textarea'
  if (/REMARK$/i.test(field.key)) return 'textarea'
  return 'text'
}

/**
 * 长文本字段（备注类）整行独占并渲染为多行文本框，对齐旧系统编辑页
 * REMARK 控件的整行布局（旧库 321 个页面把 REMARK/*_REMARK 渲染为 MultiLine）。
 * 判定依据：字段名以 REMARK 结尾（FIELDS.DISPLAY_LENGTH 是显示宽度不可靠，
 * 如 TEL=220 但并非长文本），text/ntext 物理类型按长文本处理。
 */
export function isFullWidthField(field: FormFieldDefinition): boolean {
  const type = field.dataType.toLowerCase()
  if (type === 'text' || type === 'ntext') return true
  return /REMARK$/i.test(field.key)
}
