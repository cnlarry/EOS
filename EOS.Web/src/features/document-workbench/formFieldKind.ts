import type { FormFieldDefinition } from './formDefinition'

/** 控件变体 */
export type FieldVariant = 'checkbox' | 'select' | 'date' | 'datetime' | 'decimal' | 'textarea' | 'text'

const DECIMAL_TYPES = /decimal|numeric|money|float|real|int/
const DATE_TYPES = /date|time/

/**
 * 变体判定（按 F_TYPE/dataType + 版式行高）：
 * - bit → checkbox；FORM_OPTIONS 非空 → select；
 * - date → date；datetime/smalldatetime/time → datetime；
 * - decimal/numeric/money/float/int 系 → decimal（文本框 + inputmode，失焦按 DISPLAY_FORMAT 格式化）；
 * - REMARK 类/text(ntext) 长文本 → textarea；**版式把行高定成 1 行时改用单行文本框**——
 *   行高是设计者对形态的显式要求，长文本"默认多行"的启发式不能压过它；其余 → text。
 */
export function fieldVariant(field: FormFieldDefinition): FieldVariant {
  const type = field.dataType.toLowerCase()
  if (type.includes('bit')) return 'checkbox'
  if ((field.options?.length ?? 0) > 0) return 'select'
  if (DATE_TYPES.test(type)) return type === 'date' ? 'date' : 'datetime'
  if (DECIMAL_TYPES.test(type)) return 'decimal'
  if (isLongTextField(field)) return field.rowSpan === 1 ? 'text' : 'textarea'
  return 'text'
}

/**
 * 长文本字段（备注类）：字段名以 REMARK 结尾（FIELDS.DISPLAY_LENGTH 是显示宽度不可靠，
 * 如 TEL=220 但并非长文本），或物理类型是 text/ntext。
 */
export function isLongTextField(field: Pick<FormFieldDefinition, 'key' | 'dataType'>): boolean {
  const type = (field.dataType ?? '').toLowerCase()
  if (type === 'text' || type === 'ntext') return true
  return /REMARK$/i.test(field.key)
}

/**
 * 长文本字段整行独占并渲染为多行文本框（系统编辑页 REMARK 控件的整行布局）。
 *
 * **只在版式没说话时兜底**：`rowSpan` 有值说明版式里显式排过这个字段（定制过的表），
 * 那时列宽与行高以版式为准——否则设计者在画板上把备注摆成 1 段宽、1 行高，
 * 运行态却被"整行独占 + 多行文本"顶掉，画板上的调整等于没生效。
 */
export function isFullWidthField(field: FormFieldDefinition): boolean {
  if (field.rowSpan != null) return false
  return isLongTextField(field)
}
