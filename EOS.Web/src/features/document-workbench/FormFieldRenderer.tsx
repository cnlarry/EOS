import { Button } from '../../components/ui/Button'
import { IconSearch } from '@tabler/icons-react'
import type { FormFieldDefinition } from './formDefinition'
import { inputKind, isFullWidthField } from './formFieldKind'

interface FormFieldRendererProps {
  field: FormFieldDefinition
  value: string
  error?: string
  onChange: (value: string) => void
  onChoose?: (field: FormFieldDefinition) => void
  /** 复合单元格内联模式：不渲染标签与格线，只渲染控件（供复合格 [主][选择][从] 使用） */
  bare?: boolean
}

export function FormFieldRenderer({ field, value, error, onChange, onChoose, bare = false }: FormFieldRendererProps) {
  const kind = inputKind(field)
  const disabled = field.isReadonly || field.serverFilled
  // 只读联动字段（如 CURR_ID/TAX_ID）：输入框只读，但选择按钮仍可用（对齐旧系统只读框+选择器）
  const chooserDisabled = field.serverFilled
  const isBoolean = kind === 'checkbox'
  const numeric = /int|float|decimal|money|numeric/.test(field.dataType.toLowerCase())
  // 下拉（FORM_OPTIONS）已承担取值，不再叠加选择器按钮
  const hasChooser = kind !== 'select' && Boolean(onChoose) && field.choosers.some(source => source.active && source.table)
  const fullWidth = isFullWidthField(field)
  const control = (
    <div className="erp-form-control">
      <div className="d-flex gap-2">
        {kind === 'checkbox' ? (
          <input type="checkbox" className={`form-check-input${error ? ' is-invalid' : ''}`} checked={value === '1' || value === 'true'} disabled={disabled} onChange={event => onChange(event.target.checked ? '1' : '0')} />
        ) : kind === 'select' ? (
          <select className={`form-select${error ? ' is-invalid' : ''}`} value={value} disabled={disabled} onChange={event => onChange(event.target.value)}>
            {value === '' ? <option value="">请选择</option> : null}
            {field.options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
          </select>
        ) : fullWidth && kind === 'text' ? (
          <textarea className={`form-control${error ? ' is-invalid' : ''}`} rows={3} value={value} maxLength={field.maxLength ?? undefined} disabled={disabled} onChange={event => onChange(event.target.value)} />
        ) : (
          <input type={kind === 'date' ? 'date' : numeric ? 'number' : 'text'} className={`form-control${error ? ' is-invalid' : ''}`} value={value} maxLength={field.maxLength ?? undefined} disabled={disabled} onChange={event => onChange(event.target.value)} />
        )}
        {hasChooser ? (
          <Button size="sm" variant="secondary" className="erp-chooser-btn" aria-label="选择" title={`选择${field.label}`} disabled={chooserDisabled} onClick={() => onChoose?.(field)}>
            <IconSearch size={14} />
          </Button>
        ) : null}
      </div>
      {error ? <div className="invalid-feedback d-block">{error}</div> : null}
      {field.regex ? <div className="form-hint">格式校验：{field.regex}</div> : null}
    </div>
  )
  if (bare) return control
  const className = [
    'erp-form-field',
    fullWidth ? 'is-full' : '',
    disabled ? 'is-readonly' : '',
    error ? 'has-error' : '',
  ].filter(Boolean).join(' ')
  return (
    <div className={className}>
      <label className="erp-form-label">{field.label}{!isBoolean && !disabled && field.isRequired ? ' *' : ''}</label>
      {control}
    </div>
  )
}
