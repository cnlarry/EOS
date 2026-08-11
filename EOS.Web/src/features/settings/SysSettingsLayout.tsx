import type { CSSProperties } from 'react'
import { Button } from '../../components/ui/Button'
import {
  isBitType,
  isDateTimeType,
  isNumericType,
  isTruthy,
  type SettingsForm,
  type SysSettingsField,
} from './settingsTypes'

interface SysSettingsLayoutProps {
  fields: Record<string, SysSettingsField>
  form: SettingsForm
  onChange: (key: string, value: string) => void
  onSave: () => void
  saving: boolean
  saved: boolean
}

/**
 * 110111 系统参数设置（SYSSS）：按旧 MagSysSet.aspx 的七分区字段组织，
 * 视觉采用系统统一表单编辑网格（erp-form-card + erp-form-grid，12px 标准字号）。
 * 标签来自 FIELDS 元数据（未定义时沿用旧系统 "[未定义标签]" 提示）。
 */
export function SysSettingsLayout({ fields, form, onChange, onSave, saving, saved }: SysSettingsLayoutProps) {
  return (
    <div className="d-grid gap-2">
      {saved && <div role="alert" className="alert alert-success py-2 mb-0">设置成功！</div>}
      <section className="card erp-form-card">
        <div className="card-body">
          <div className="erp-form-toolbar">
            <div />
            <div className="d-flex gap-2 erp-form-toolbar-actions">
              <Button size="sm" variant="primary" accessKey="o" onClick={onSave} loading={saving}>确定(O)</Button>
            </div>
          </div>
          <div className="erp-form-grid erp-settings-form">
            <SectionRow title="一：未交易天数限制，超过以下设定天数，对应表单将不能批核。" />
            <FieldRow cols={3} keys={['CLIENT_DAYS', 'SUPPLIER_DAYS', 'PRODUCT_DAYS']} fields={fields} form={form} onChange={onChange} />
            <SectionRow title="二：MRPII计算的天、周、月参数" />
            <FieldRow cols={3} keys={['MRP_DAYS', 'MRP_WEEKS', 'MRP_MONTHS']} fields={fields} form={form} onChange={onChange} />
            <SectionRow title="三：产品管理基本参数" />
            <FieldRow cols={3} keys={['PRO_MRP', 'PRO_EDITION_TAG', null]} fields={fields} form={form} onChange={onChange} />
            <SectionRow title="四：业务流程参数" />
            <FieldRow cols={3} keys={['FITOUT_TAG', 'SEND_TAG', 'COP_RETURN_DEPOT_TAG']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['FITOUT_ORDER_TAG', 'FITOUT_PRODUCE_TAG', 'FITOUT_PRODUCE_TRANSFER_TAG']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['SEND_ORDER_TAG', 'SEND_PRODUCE_TAG', 'SEND_PRODUCE_TRANSFER_TAG']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['SEND_ORDER_FITOUT_TAG', 'SEND_PRODUCE_FITOUT_TAG', 'SEND_FITOUT_TAG']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['RETURN_SEND_TAG', 'RETURN_PRODUCE_SEND_TAG', 'RETURN_ORDER_SEND_TAG']} fields={fields} form={form} onChange={onChange} />
            <SectionRow title="五：采购流程参数" />
            <FieldRow cols={3} keys={['PUR_APPLY_TAG', 'PUR_PRODUCE_APPLY_TAG', 'PUR_APPLY_PLAN_PUR_TAG']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['PUR_CANCEL_DEPOT_TAG', null, null]} fields={fields} form={form} onChange={onChange} />
            <SectionRow title="六：生产流程参数" />
            <FieldRow cols={3} keys={['PRODUCE_IN_ORDER_TAG', 'PRODUCE_ORDER_TAG', 'PRODUCE_PLAN_MOC_TAG']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['QTY_LINE1', 'QTY_LINE2', 'QTY_LINE3']} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={3} keys={['QTY_PS_WIDTH', null, null]} fields={fields} form={form} onChange={onChange} />
            <SectionRow title="七：系统查询参数" />
            <FieldRow cols={3} keys={['QUICK_SEARCH_ALL', null, null]} fields={fields} form={form} onChange={onChange} />
            <FieldRow cols={2} keys={['REMARK', 'CREATE_DATE']} fields={fields} form={form} onChange={onChange} multilineKeys={['REMARK']} />
          </div>
        </div>
      </section>
    </div>
  )
}

function SectionRow({ title }: { title: string }) {
  return (
    <div className="erp-form-row">
      <div className="erp-form-section">{title}</div>
    </div>
  )
}

interface FieldRowProps {
  cols: number
  keys: (string | null)[]
  fields: Record<string, SysSettingsField>
  form: SettingsForm
  onChange: (key: string, value: string) => void
  multilineKeys?: string[]
}

function FieldRow({ cols, keys, fields, form, onChange, multilineKeys = [] }: FieldRowProps) {
  const className = `erp-form-row${cols === 3 ? ' erp-form-row-3' : ''}`
  return (
    <div className={className} style={{ '--erp-form-cols': cols } as CSSProperties}>
      {keys.map((key, index) => {
        if (!key) return <div key={`empty-${index}`} className="erp-form-field" />
        const field = fields[key]
        if (!field) return <div key={key} className="erp-form-field" />
        return (
          <SettingField
            key={key}
            field={field}
            value={form[key] ?? ''}
            multiline={multilineKeys.includes(key)}
            onChange={(value) => onChange(key, value)}
          />
        )
      })}
    </div>
  )
}

interface SettingFieldProps {
  field: SysSettingsField
  value: string
  multiline?: boolean
  onChange: (value: string) => void
}

function SettingField({ field, value, multiline = false, onChange }: SettingFieldProps) {
  const label = field.label ?? '[未定义标签]'
  return (
    <div className="erp-form-field">
      <label className="erp-form-label">{label}</label>
      <div className="erp-form-control">
        <div className="d-flex gap-2">
          <SettingControl field={field} value={value} multiline={multiline} onChange={onChange} />
        </div>
      </div>
    </div>
  )
}

interface SettingControlProps {
  field: SysSettingsField
  value: string
  multiline?: boolean
  onChange: (value: string) => void
}

function SettingControl({ field, value, multiline = false, onChange }: SettingControlProps) {
  const label = field.label ?? field.key
  if (isBitType(field.type)) {
    return (
      <input
        aria-label={label}
        type="checkbox"
        className="form-check-input"
        checked={isTruthy(value)}
        onChange={(event) => onChange(event.target.checked ? 'true' : 'false')}
      />
    )
  }
  if (isDateTimeType(field.type)) {
    return (
      <input
        aria-label={label}
        type={field.type.toLowerCase() === 'date' ? 'date' : 'datetime-local'}
        className="form-control"
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }
  if (isNumericType(field.type)) {
    return (
      <input
        aria-label={label}
        type="number"
        step={field.type.toLowerCase().includes('int') ? '1' : 'any'}
        className="form-control"
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }
  if (multiline) {
    return (
      <textarea
        aria-label={label}
        className="form-control"
        rows={2}
        maxLength={field.maxLength ?? undefined}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
    )
  }
  return (
    <input
      aria-label={label}
      type="text"
      className="form-control"
      maxLength={field.maxLength ?? undefined}
      value={value}
      onChange={(event) => onChange(event.target.value)}
    />
  )
}
