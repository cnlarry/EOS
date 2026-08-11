import type { FormFieldDefinition } from './formDefinition'

export function inputKind(field: FormFieldDefinition): 'checkbox' | 'select' | 'date' | 'text' {
  const type = field.dataType.toLowerCase()
  if (type.includes('bit')) return 'checkbox'
  if ((field.options?.length ?? 0) > 0) return 'select'
  if (type.includes('date') || type.includes('time')) return 'date'
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
