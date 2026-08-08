import type { FormFieldDefinition } from './formDefinition'

export function inputKind(field: FormFieldDefinition): 'checkbox' | 'date' | 'text' {
  const type = field.dataType.toLowerCase()
  if (type.includes('bit')) return 'checkbox'
  if (type.includes('date') || type.includes('time')) return 'date'
  return 'text'
}
