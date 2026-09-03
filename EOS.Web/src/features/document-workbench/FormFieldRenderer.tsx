import type { ReactElement } from 'react'
import { Button } from '../../components/ui/Button'
import { IconListDetails } from '@tabler/icons-react'
import type { FormFieldDefinition } from './formDefinition'
import { fieldVariant, isFullWidthField, type FieldVariant } from './formFieldKind'
import { formatFieldValue } from './fieldFormat'
import { canonicalizeDecimalValue } from './formEditorUtils'
import { fromDateTimeControlValue, toDateTimeControlValue } from './dateTimeValue'

interface FormFieldRendererProps {
  field: FormFieldDefinition
  value: string
  error?: string
  onChange: (value: string) => void
  onChoose?: (field: FormFieldDefinition) => void
  /** 标签右键进入字段设置（仅 canSetup 时传入，传入即启用右键菜单）；x/y 为右键落点坐标 */
  onFieldSetup?: (field: FormFieldDefinition, x: number, y: number) => void
  /** 复合单元格内联模式：不渲染标签与格线，只渲染控件（供复合格 [主][选择][从] 使用） */
  bare?: boolean
  /** 浏览态：全部字段走只读文本渲染 */
  viewing?: boolean
}

interface ControlProps {
  field: FormFieldDefinition
  value: string
  disabled: boolean
  onChange: (value: string) => void
}

const controlClassName = (error?: string) => `form-control${error ? ' is-invalid' : ''}`

// ===== 控件变体注册表：新控件类型只加条目，不改页面编排 =====

function TextControl({ field, value, disabled, onChange, error }: ControlProps & { error?: string }) {
  return (
    <input
      type="text"
      className={controlClassName(error)}
      value={value}
      maxLength={field.maxLength ?? undefined}
      disabled={disabled}
      onChange={event => onChange(event.target.value)}
    />
  )
}

/** decimal 变体：文本框 + inputmode，失焦按 DISPLAY_FORMAT 展示格式化；保存前经 canonicalizeDecimalValue 规范化 */
function DecimalControl({ field, value, disabled, onChange, error }: ControlProps & { error?: string }) {
  const handleBlur = () => {
    if (disabled) return
    const canonical = canonicalizeDecimalValue(value)
    // 不可解析（含货币符号等）保留原值交由校验报错；可解析则套 DISPLAY_FORMAT 展示
    if (!canonical || !Number.isFinite(Number(canonical))) return
    const formatted = formatFieldValue(Number(canonical), field.dataType, field.displayFormat)
    const next = formatted === '' ? canonical : formatted
    if (next !== value) onChange(next)
  }
  return (
    <input
      type="text"
      inputMode="decimal"
      className={controlClassName(error)}
      value={value}
      maxLength={field.maxLength ?? undefined}
      disabled={disabled}
      onChange={event => onChange(event.target.value)}
      onBlur={handleBlur}
    />
  )
}

function DateControl({ field, value, disabled, onChange, error }: ControlProps & { error?: string }) {
  return (
    <input
      type="date"
      className={controlClassName(error)}
      value={toDateTimeControlValue(field.dataType, value)}
      disabled={disabled}
      onChange={event => onChange(fromDateTimeControlValue(event.target.value))}
    />
  )
}

/** datetime 变体：datetime-local + 秒分量；提交 yyyy-MM-ddTHH:mm:ss 本地朴素串 */
function DateTimeControl({ field, value, disabled, onChange, error }: ControlProps & { error?: string }) {
  return (
    <input
      type="datetime-local"
      step={1}
      className={controlClassName(error)}
      value={toDateTimeControlValue(field.dataType, value)}
      disabled={disabled}
      onChange={event => onChange(fromDateTimeControlValue(event.target.value))}
    />
  )
}

function TextareaControl({ field, value, disabled, onChange, error }: ControlProps & { error?: string }) {
  return (
    <textarea
      className={controlClassName(error)}
      rows={3}
      value={value}
      maxLength={field.maxLength ?? undefined}
      disabled={disabled}
      onChange={event => onChange(event.target.value)}
    />
  )
}

function SelectControl({ field, value, disabled, onChange, error }: ControlProps & { error?: string }) {
  return (
    <select
      className={`form-select${error ? ' is-invalid' : ''}`}
      value={value}
      disabled={disabled}
      onChange={event => onChange(event.target.value)}
    >
      {value === '' ? <option value="">请选择</option> : null}
      {field.options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
    </select>
  )
}

function CheckboxControl({ value, disabled, onChange, error }: ControlProps & { error?: string }) {
  return (
    <input
      type="checkbox"
      className={`form-check-input${error ? ' is-invalid' : ''}`}
      checked={value === '1' || value === 'true'}
      disabled={disabled}
      onChange={event => onChange(event.target.checked ? '1' : '0')}
    />
  )
}

const CONTROL_RENDERERS: Record<FieldVariant, (props: ControlProps & { error?: string }) => ReactElement> = {
  text: TextControl,
  decimal: DecimalControl,
  date: DateControl,
  datetime: DateTimeControl,
  textarea: TextareaControl,
  select: SelectControl,
  checkbox: CheckboxControl,
}

export function FormFieldRenderer({ field, value, error, onChange, onChoose, onFieldSetup, bare = false, viewing = false }: FormFieldRendererProps) {
  const variant = fieldVariant(field)
  const hasChooser = variant !== 'select' && Boolean(onChoose) && field.choosers.some(source => source.active && source.table)
  // 只读文本触发条件：浏览态全量；编辑/新增态仅 serverFilled 且无选择器的字段
  // 渲染只读文本（审计列/批核/结案字段不渲染输入框）。isReadonly/displayOnly 从字段渲染只读控件
  // （disabled 输入框）以保持组合字段左右宽度协调；带选择器的联动字段（CURR_ID/TAX_ID）
  // 保留只读框+可用选择按钮。
  const readOnlyStatic = viewing || (field.serverFilled && !hasChooser)
  const disabled = !readOnlyStatic && (field.isReadonly || field.serverFilled)
  // 只读联动字段的选择按钮仍可用；serverFilled 无选择器时按钮无意义
  const chooserDisabled = field.serverFilled && !hasChooser

  let control: ReactElement
  if (readOnlyStatic && variant === 'checkbox') {
    // 浏览态复选框：渲染只读复选框（勾选反映状态），不渲染「是/否」文本徽标
    control = CONTROL_RENDERERS.checkbox({ field, value, disabled: true, onChange: () => {}, error })
  } else if (readOnlyStatic) {
    // 只读文本（非复选框字段）
    const display = formatFieldValue(value, field.dataType, field.displayFormat)
    control = (
      <span className="erp-form-static">
        {display || '—'}
      </span>
    )
  } else {
    control = CONTROL_RENDERERS[variant]({ field, value, disabled, onChange, error })
  }

  const container = (
    <div className="erp-form-control" data-field-key={field.key}>
      <div className="d-flex gap-2">
        {control}
        {hasChooser && !readOnlyStatic ? (
          <Button size="sm" variant="secondary" className="erp-chooser-btn" aria-label="选择" title={`选择${field.label}`} disabled={chooserDisabled} onClick={() => onChoose?.(field)}>
            <IconListDetails size={14} />
          </Button>
        ) : null}
      </div>
      {error ? <div className="invalid-feedback d-block">{error}</div> : null}
      {field.regex ? <div className="form-hint">格式校验：{field.regex}</div> : null}
    </div>
  )
  if (bare) return container
  const fullWidth = isFullWidthField(field)
  const className = [
    'erp-form-field',
    fullWidth ? 'is-full' : '',
    readOnlyStatic ? 'is-static' : '',
    !readOnlyStatic && disabled ? 'is-readonly' : '',
    error ? 'has-error' : '',
  ].filter(Boolean).join(' ')
  return (
    <div className={className} data-field-key={field.key}>
      <label
        className="erp-form-label"
        title={field.label}
        onContextMenu={onFieldSetup ? event => { event.preventDefault(); onFieldSetup(field, event.clientX, event.clientY) } : undefined}
      >
        {field.label}{!readOnlyStatic && variant !== 'checkbox' && !disabled && field.isRequired ? ' *' : ''}
      </label>
      {container}
    </div>
  )
}
