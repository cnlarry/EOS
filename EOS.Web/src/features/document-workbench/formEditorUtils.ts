import { ApiError } from '../../types/api'
import { inputKind } from './formFieldKind'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'

/** 统一表单保存载荷（主表 values + 明细 details，edit 时带 original 并发快照） */
export interface SaveRecordRequest {
  values: Record<string, string>
  details: Record<string, string>[]
  original?: Record<string, string>
}

export interface RecordBundle {
  master: Record<string, unknown>
  details: Record<string, unknown>[]
}

/** 明细网格行：__id 为表格行键，__index 映射 detailRows 原始行号，__filler 为占位空行 */
export interface DetailGridRow {
  __id: string
  __index: number
  __filler: boolean
  [key: string]: unknown
}

export function buildKey(form: FormDefinition, values: Record<string, string>): string[] {
  return form.masterPkOrder.map(column => values[column] ?? '')
}

export function emptyValue(field: FormFieldDefinition): string {
  if (field.defaultValue != null) return field.defaultValue
  if (field.dataType.toLowerCase().includes('bit')) return '0'
  return ''
}

/**
 * 明细编辑控件的可用最小列宽。DISPLAY_LENGTH 是只读列表展示宽度（往往只有几十像素），
 * 编辑态直接套用会把输入控件压到无法操作；此处按控件类型给列宽下限，
 * 并配合 `minWidthFloor` 让拖拽/历史宽度也不能低于该下限。
 */
export function detailControlMinWidth(field: FormFieldDefinition): number {
  const kind = inputKind(field)
  if (kind === 'checkbox') return 56
  if (kind === 'select') return 104
  if (kind === 'date') return 132
  const hasChooser = field.choosers.some(source => source.active && source.table)
  return hasChooser ? 168 : 110
}

/**
 * 只读可见字段随保存提交（与服务端规则一致）：ONLY_CHOOSE 带选择器字段（CURR_ID/TAX_ID）
 * 与只读必填联动字段（如 CURR_RATE，由币别带出）必须提交，否则服务端必填校验失败；
 * displayOnly/serverFilled/虚拟字段仍不提交（服务端维护）。
 */
export function writableFields(fields: FormFieldDefinition[]): FormFieldDefinition[] {
  return fields.filter(field => field.isVisible && !field.serverFilled && !field.isVirtual && !field.displayOnly
    && (!field.isReadonly || field.isRequired || field.choosers.some(source => source.active && source.table)))
}

/** 统一选择器标题：字段标签 + 数据源描述（多选后缀由 UnifiedChooser 内部追加） */
export function chooserTitle(field: FormFieldDefinition): string {
  const source = field.choosers.find(item => item.active && item.table)
  return `${field.label}${source?.description ? `（${source.description}）` : ''}`
}

export function describeError(error: unknown): string {
  if (error instanceof ApiError && error.status === 404) return '该模块未启用统一表单编辑（含存盘后业务逻辑的模块暂不开放，或不在白名单内）。'
  if (error instanceof ApiError) return error.body.message
  return '无法加载表单定义。'
}

export function summarizeFieldErrors(master: Record<string, string>, details: Record<string, string>[]): string {
  const messages = [...Object.values(master), ...details.flatMap(row => Object.values(row))]
  return messages.slice(0, 3).join('；')
}
