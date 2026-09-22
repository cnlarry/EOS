import { ApiError } from '../../types/api'
import { fieldVariant } from './formFieldKind'
import type { FormDefinition, FormFieldDefinition } from './formDefinition'
import { createId } from '../../lib/uuid'

/** 统一表单保存载荷（主表 values + 明细 details，edit 时带 original 并发快照； 强制幂等键） */
export interface SaveRecordRequest {
  values: Record<string, string>
  details: Record<string, string>[]
  original?: Record<string, string>
  idempotencyKey?: string
  /** 本次提交里用户在界面上选过的来源（字段键 → 来源序号）；未重选的字段不下发 */
  chooserSources?: Record<string, number>
  /** 明细行同上，按行下标对齐 submitted details；无来源的行给 null */
  detailChooserSources?: (Record<string, number> | null)[]
  /**
   * 明细各行**原有的项次**（按行下标对齐 submitted details；新行给 null）。
   * 项次是明细行的身份（下游单据按"单号 + 项次"引用明细），回传既有项次后，
   * 删行/调序不会让其余行被服务端重编号。
   */
  detailSerials?: (string | null)[]
}

/**
 * 明细行内携带「本行选过的来源」的隐藏键：只驻留前端行对象，保存时汇总成
 * detailChooserSources 单独下发；提交载荷按字段定义逐键取值，因此不会进 details。
 */
export const DETAIL_CHOOSER_SOURCE_KEY = '__chooserSources'

/** 读出明细行的「字段 → 来源序号」；无记录或非法时返回 null。 */
export function readDetailChooserSources(row: Record<string, string>): Record<string, number> | null {
  const raw = row[DETAIL_CHOOSER_SOURCE_KEY]
  if (!raw) return null
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null
    const entries = Object.entries(parsed as Record<string, unknown>)
      .filter(([, value]) => typeof value === 'number' && Number.isFinite(value)) as [string, number][]
    return entries.length > 0 ? Object.fromEntries(entries) : null
  } catch {
    return null
  }
}

/** 记录明细行某字段选用的来源（同一行重复选择时后者覆盖）。 */
export function withDetailChooserSource(
  row: Record<string, string>,
  fieldKey: string,
  serialNo: number | null | undefined,
): Record<string, string> {
  if (serialNo == null) return row
  const merged = { ...(readDetailChooserSources(row) ?? {}), [fieldKey]: serialNo }
  return { ...row, [DETAIL_CHOOSER_SOURCE_KEY]: JSON.stringify(merged) }
}

/** 保存/批核/结案响应 */
export interface RecordSaveResponse {
  key: string[]
  flowStarted?: boolean
  warnings?: { code: string; message: string }[]
}

/** 幂等键生成：一次用户操作意图一个键，成功后换新键 */
export function newIdempotencyKey(): string {
  return createId()
}

export interface RecordBundle {
  master: Record<string, unknown>
  details: Record<string, unknown>[]
  /** 在途流程状态（服务端 FlowState 投影）：None/InProgress/Completed/Withdrawn */
  flowState?: 'None' | 'InProgress' | 'Completed' | 'Withdrawn'
}

/** 明细网格行：__id 为表格行键，__index 映射 detailRows 原始行号 */
export interface DetailGridRow {
  __id: string
  __index: number
  [key: string]: unknown
}

export function buildKey(form: FormDefinition, values: Record<string, string>): string[] {
  return form.masterPkOrder.map(column => values[column] ?? '')
}

/** 常见单别/类别类主键列：这些列是「单据类别」而非单据编号，面包屑单号应跳过。 */
const DOC_NO_SKIP_PATTERN = /TYPE|KIND|CLASS|GRADE|_ID$|_IDX|_NO_FIELDS|SERIAL_NO/i

/**
 * 从主键值中提取单据编号用于面包屑：
 * 跳过单别/类别类列（如 单别/单据类别），取剩余主键值（通常即单号）；
 * 若主键全为类别列则回退为主键值组合；无任何主键值返回 null（如新增未生成单号）。
 */
export function extractDocNo(form: FormDefinition, values: Record<string, string>): string | null {
  const nonCategory = form.masterPkOrder
    .map(column => ({ column, value: values[column] ?? '' }))
    .filter(item => item.value && !DOC_NO_SKIP_PATTERN.test(item.column))
  if (nonCategory.length > 0) return nonCategory.map(item => item.value).join('-')
  const any = form.masterPkOrder.map(column => values[column] ?? '').filter(Boolean)
  return any.length > 0 ? any.join('-') : null
}

export function emptyValue(field: FormFieldDefinition): string {
  if (field.defaultValue != null) return field.defaultValue
  if (field.dataType.toLowerCase().includes('bit')) return '0'
  return ''
}

/**
 * 明细编辑控件的可用最小列宽。DISPLAY_LENGTH 是只读列表展示宽度（往往只有几十像素），
 * 编辑态直接套用会把输入控件压到无法操作；此处按控件变体给列宽下限，
 * 并配合 `minWidthFloor` 让拖拽/历史宽度也不能低于该下限。
 */
export function detailControlMinWidth(field: FormFieldDefinition): number {
  const variant = fieldVariant(field)
  if (variant === 'checkbox') return 56
  if (variant === 'select') return 104
  if (variant === 'date' || variant === 'datetime') return 132
  const hasChooser = field.choosers.some(source => source.active && source.table)
  return hasChooser ? 168 : 110
}

/**
 * 数值输入规范化：
 * 全角数字/句点转半角、去除千分位逗号，可解析时输出不变文化的普通数字串；
 * 不可解析（含货币符号等）原样返回，交由校验报错。
 */
export function canonicalizeDecimalValue(raw: string): string {
  let text = (raw ?? '').trim()
  if (!text) return ''
  text = text
    .replace(/[０-９]/g, character => String.fromCharCode(character.charCodeAt(0) - 0xFEE0))
    .replace('．', '.')
    .replace(/，/g, ',')
  if (/^%/.test(text) || text.endsWith('%')) return text
  const numeric = Number(text.replace(/,/g, ''))
  return Number.isFinite(numeric) ? String(numeric) : text
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

/** 回填映射条目。 */
export interface ChooserReturnItem {
  target: string
  column: string
}

/** 解析 RETURN_ITEMS JSON；空/非法返回空数组（不抛错）。 */
export function parseReturnItems(raw: string | null | undefined): ChooserReturnItem[] {
  if (!raw) return []
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((item): item is ChooserReturnItem =>
      Boolean(item) && typeof (item as ChooserReturnItem).target === 'string'
      && typeof (item as ChooserReturnItem).column === 'string')
  } catch {
    return []
  }
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
