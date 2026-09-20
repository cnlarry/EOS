/** 一个系统参数（服务端下发；键沿用原列名，页面不再依赖字段元数据）。 */
export interface SystemParameterItem {
  key: string
  value: string | null
  valueType: string
  defaultValue: string | null
  groupCode: string
  groupLabel: string
  description: string
  effectScope: string
  seqNo: number
  options: string | null
  effectiveValue: string | null
  usesDefault: boolean
}

/** 一个参数分组（页面上的一个选项卡）。 */
export interface SystemParameterGroup {
  groupCode: string
  groupLabel: string
  parameters: SystemParameterItem[]
}

/** 一个设置范围的参数清单（按分组下发）。 */
export interface SystemParameterList {
  ownerModule: number
  scope: string
  groups: SystemParameterGroup[]
}

/** 表单值统一按字符串承载，服务端按参数类型解释并拒存不合法取值。 */
export type SettingsForm = Record<string, string>

/** 历史表名/URL 片段 → 页面范围段，保证既有菜单与书签继续可用。 */
const SCOPE_ALIASES: Record<string, string> = {
  SYSSS: 'system',
  'HR-SETUP': 'hr-setup',
  HR_SETUP: 'hr-setup',
  'HRM-SETUP': 'hrm-setup',
  HRM_SETUP: 'hrm-setup',
}

export function normalizeScope(scope: string | undefined): string {
  if (!scope) return 'system'
  const upper = scope.toUpperCase()
  return SCOPE_ALIASES[upper] ?? scope.toLowerCase()
}

export function isTruthy(value: string): boolean {
  return value === 'true' || value === '1' || value === '是'
}

export function isNumericValueType(valueType: string): boolean {
  const normalized = valueType.toLowerCase()
  return normalized === 'int' || normalized === 'decimal'
}

export function isBitValueType(valueType: string): boolean {
  return valueType.toLowerCase() === 'bit'
}

/** 枚举参数的下拉选项："0=否;1=是" → [{value,label}]；空串表示按类型渲染控件。 */
export function parseOptions(raw: string | null): { value: string; label: string }[] {
  if (!raw) return []
  return raw
    .split(';')
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
    .map((entry) => {
      const separator = entry.indexOf('=')
      return separator < 0
        ? { value: entry, label: entry }
        : { value: entry.slice(0, separator).trim(), label: entry.slice(separator + 1).trim() }
    })
}

/** 表单初值：取生效值（已存值，否则声明的默认值），开关归一为 true/false。 */
export function initialSettingsForm(groups: SystemParameterGroup[]): SettingsForm {
  const form: SettingsForm = {}
  for (const group of groups) {
    for (const parameter of group.parameters) {
      form[parameter.key] = formatValueForInput(parameter, parameter.effectiveValue)
    }
  }
  return form
}

export function formatValueForInput(parameter: SystemParameterItem, raw: string | null): string {
  if (raw === null || raw === undefined) return ''
  if (isBitValueType(parameter.valueType)) return isTruthy(raw) ? 'true' : 'false'
  return raw
}
