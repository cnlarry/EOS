import { Button } from '../../components/ui/Button'
import type { FormFieldDefinition } from './formDefinition'
import { inputKind } from './formFieldKind'

interface FormFieldRendererProps {
  field: FormFieldDefinition
  value: string
  error?: string
  onChange: (value: string) => void
  onChoose?: (field: FormFieldDefinition) => void
}

export function FormFieldRenderer({ field, value, error, onChange, onChoose }: FormFieldRendererProps) {
  const kind = inputKind(field)
  const disabled = field.isReadonly || field.serverFilled
  const isBoolean = kind === 'checkbox'
  const numeric = /int|float|decimal|money|numeric/.test(field.dataType.toLowerCase())
  const hasChooser = Boolean(onChoose) && field.choosers.some(source => source.active && source.table)
  return (
    <div className="col-md-6">
      <label className="form-label">{field.label}{!isBoolean && !disabled && field.isRequired ? ' *' : ''}</label>
      <div className="d-flex gap-2">
        {kind === 'checkbox' ? (
          <input type="checkbox" className={`form-check-input mt-2${error ? ' is-invalid' : ''}`} checked={value === '1' || value === 'true'} disabled={disabled} onChange={event => onChange(event.target.checked ? '1' : '0')} />
        ) : (
          <input type={kind === 'date' ? 'date' : numeric ? 'number' : 'text'} className={`form-control${error ? ' is-invalid' : ''}`} value={value} maxLength={field.maxLength ?? undefined} disabled={disabled} onChange={event => onChange(event.target.value)} />
        )}
        {hasChooser ? (
          <Button size="sm" disabled={disabled} onClick={() => onChoose?.(field)}>选择</Button>
        ) : null}
      </div>
      {error ? <div className="invalid-feedback d-block">{error}</div> : null}
      {field.regex ? <div className="form-hint">格式校验：{field.regex}</div> : null}
      {field.serverFilled ? <div className="form-hint text-secondary">由系统维护</div> : null}
    </div>
  )
}
