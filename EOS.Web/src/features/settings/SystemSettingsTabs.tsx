import type { CSSProperties } from 'react'
import { TabbedPanel } from '../../components/common/TabbedPanel'
import { Button } from '../../components/ui/Button'
import {
  isBitValueType,
  isNumericValueType,
  isTruthy,
  parseOptions,
  type SettingsForm,
  type SystemParameterGroup,
  type SystemParameterItem,
} from './settingsTypes'

/** 每行参数个数：与统一表单的三列编辑网格一致。 */
const COLUMNS = 3

interface SystemSettingsTabsProps {
  groups: SystemParameterGroup[]
  form: SettingsForm
  activeGroupCode: string
  onSelectGroup: (groupCode: string) => void
  onChange: (key: string, value: string) => void
  onSave: () => void
  saving: boolean
  saved: boolean
  saveError: string | null
}

/**
 * 系统参数页：页签沿用系统级页签面板（TabbedPanel，与菜单管理右侧面板、统一表单同一套设计语言），
 * 每个分组一个页签；控件按参数类型渲染
 * （开关→复选框、整数/数值→数字输入、有枚举选项→下拉、其余→文本框）。
 * 生效范围为"需重启"的参数在标签旁标注，避免误以为改完立刻生效。
 */
export function SystemSettingsTabs({
  groups,
  form,
  activeGroupCode,
  onSelectGroup,
  onChange,
  onSave,
  saving,
  saved,
  saveError,
}: SystemSettingsTabsProps) {
  const active = groups.find((group) => group.groupCode === activeGroupCode) ?? groups[0]
  const parameters = active?.parameters ?? []
  const rows: SystemParameterItem[][] = []
  for (let index = 0; index < parameters.length; index += COLUMNS) {
    rows.push(parameters.slice(index, index + COLUMNS))
  }

  return (
    <div className="d-grid gap-2">
      {saved && <div role="alert" className="alert alert-success py-2 mb-0">设置成功！</div>}
      {saveError && <div role="alert" className="alert alert-danger py-2 mb-0">{saveError}</div>}
      <section className="card erp-form-card">
        <div className="card-body">
          <div className="erp-form-toolbar">
            <div />
            <div className="d-flex gap-2 erp-form-toolbar-actions">
              <Button size="sm" variant="primary" accessKey="o" onClick={onSave} loading={saving}>确定(O)</Button>
            </div>
          </div>
          {active && (
            <TabbedPanel
              label="参数分组"
              tabs={groups.map((group) => ({ key: group.groupCode, label: group.groupLabel }))}
              activeKey={active.groupCode}
              onActiveKeyChange={onSelectGroup}
              className="erp-settings-tabs"
            >
              <div className="erp-settings-form">
                {rows.map((row, rowIndex) => (
                  <div
                    // 行按位置呈现，无稳定业务键；序号即其身份
                    key={`row-${rowIndex}`}
                    className="erp-form-row erp-form-row-3"
                    style={{ '--erp-form-cols': COLUMNS } as CSSProperties}
                  >
                    {row.map((parameter) => (
                      <ParameterField
                        key={parameter.key}
                        parameter={parameter}
                        value={form[parameter.key] ?? ''}
                        onChange={onChange}
                      />
                    ))}
                    {Array.from({ length: COLUMNS - row.length }).map((_, fillerIndex) => (
                      <div key={`empty-${fillerIndex}`} className="erp-form-field" />
                    ))}
                  </div>
                ))}
              </div>
            </TabbedPanel>
          )}
        </div>
      </section>
    </div>
  )
}

interface ParameterFieldProps {
  parameter: SystemParameterItem
  value: string
  onChange: (key: string, value: string) => void
}

function ParameterField({ parameter, value, onChange }: ParameterFieldProps) {
  return (
    <div className="erp-form-field">
      <label className={`erp-form-label${parameter.isReferenced ? '' : ' text-secondary'}`}>
        {parameter.description}
        <span className="text-secondary ms-1" style={{ fontSize: 11 }}>{parameter.key}</span>
        {parameter.effectScope === 'restart' && <span className="badge bg-warning-lt ms-1">需重启</span>}
        {!parameter.isReferenced && (
          <span
            className="badge bg-secondary-lt ms-1"
            title="当前没有任何读取方引用这个参数（已发布配置与代码都没有），修改它暂时不产生效果"
          >
            当前无引用方
          </span>
        )}
      </label>
      <div className="erp-form-control">
        <ParameterControl parameter={parameter} value={value} onChange={onChange} />
      </div>
    </div>
  )
}

interface ParameterControlProps {
  parameter: SystemParameterItem
  value: string
  onChange: (key: string, value: string) => void
}

function ParameterControl({ parameter, value, onChange }: ParameterControlProps) {
  const label = parameter.description
  const options = parseOptions(parameter.options)
  if (options.length > 0) {
    return (
      <select
        aria-label={label}
        className="form-select"
        value={value}
        onChange={(event) => onChange(parameter.key, event.target.value)}
      >
        <option value="">（默认）</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
    )
  }
  if (isBitValueType(parameter.valueType)) {
    return (
      <input
        aria-label={label}
        type="checkbox"
        className="form-check-input"
        checked={isTruthy(value)}
        onChange={(event) => onChange(parameter.key, event.target.checked ? 'true' : 'false')}
      />
    )
  }
  if (isNumericValueType(parameter.valueType)) {
    return (
      <input
        aria-label={label}
        type="number"
        step={parameter.valueType.toLowerCase() === 'int' ? '1' : 'any'}
        className="form-control"
        value={value}
        onChange={(event) => onChange(parameter.key, event.target.value)}
      />
    )
  }
  return (
    <input
      aria-label={label}
      className="form-control"
      value={value}
      onChange={(event) => onChange(parameter.key, event.target.value)}
    />
  )
}
