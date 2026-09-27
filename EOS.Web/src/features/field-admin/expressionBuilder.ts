/**
 * 字段设置高级表达式构建器的模型、文本生成与白名单数据类型。
 *
 * 分工：构建器只把选项拼成受控语法的文本，文本仍是唯一提交物——保存字段时由服务端做受控解析校验；
 * 已存表达式的结构回读用服务端解析结果（POST /admin/fields/expressions/parse），前端不另立一套语法。
 */
import type { ExpressionKind } from './FieldEditorForm'

/** 服务端表达式结构回读的形态取值（对齐 ExpressionStructureModes）。 */
export type ExpressionMode = 'reference' | 'arithmetic' | 'registry' | 'raw'

/** 服务端结构回读结果（构建器初始化输入）。 */
export interface ExpressionStructure {
  kind: string
  mode: ExpressionMode
  table?: string | null
  column?: string | null
  function?: string | null
}

/** 受控表达式注册表（白名单版本 + 转换函数名）。 */
export interface ExpressionRegistryItem {
  name: string
  description: string
}

export interface ExpressionRegistry {
  whiteListVersion: number
  convertFunctions: ExpressionRegistryItem[]
}

/** 表列（物理列 + FIELDS 内受控虚拟列；表达式引用只取物理列）。 */
export interface TableColumn {
  name: string
  dataType: string
  description: string
  isVirtual?: boolean
}

/** QUERY_RELATION 白名单中的一条关联（别名可不同于物理表名）。 */
export interface TableRelation {
  table: string
  alias: string
  conditions: string[]
}

export interface TableRelations {
  tableId: string
  ok: boolean
  error: string | null
  items: TableRelation[]
}

/** 构建器模型：一个字段只用一种形态，按 kind 取对应分支。 */
export interface ExpressionModel {
  mode: ExpressionMode
  /** 虚拟表达式：引用来源（本表名或 QUERY_RELATION 别名）。 */
  table: string
  /** 虚拟表达式：引用列。 */
  column: string
  /** 转换函数：注册表函数名。 */
  function: string
}

/** 空模型：编辑态值已被清空或新增字段时，构建器从空表单起步。 */
export function emptyModel(kind: ExpressionKind, currentTable: string): ExpressionModel {
  return {
    mode: kind === 'virtual_exp' ? 'reference' : 'registry',
    table: kind === 'virtual_exp' ? currentTable : '',
    column: '',
    function: '',
  }
}

/** 服务端结构回读 → 构建器模型；构建器不覆盖的形态（算术/非法）返回 null。 */
export function modelFromStructure(kind: ExpressionKind, structure: ExpressionStructure, currentTable: string): ExpressionModel | null {
  const base = emptyModel(kind, currentTable)
  if (structure.mode === 'reference') {
    return { ...base, mode: 'reference', table: structure.table || currentTable, column: structure.column ?? '' }
  }
  if (structure.mode === 'registry') {
    return { ...base, mode: 'registry', function: structure.function ?? '' }
  }
  return null
}

/**
 * 模型 → 表达式文本；不完整时返回 null（调用方保留既有文本，只提示缺项，避免半成品清空表达式）。
 */
export function buildExpression(kind: ExpressionKind, model: ExpressionModel): string | null {
  if (kind === 'virtual_exp') {
    const table = model.table.trim()
    const column = model.column.trim()
    return table && column ? `${table}.${column}` : null
  }
  return model.function.trim() || null
}

/** 模型缺项提示：非空表示构建器尚未拼出可提交文本（此时不覆盖表达式文本）。 */
export function modelIssue(kind: ExpressionKind, model: ExpressionModel): string | null {
  if (kind === 'virtual_exp') return model.column.trim() ? null : '请选择引用列。'
  return model.function.trim() ? null : '请选择注册表内的转换函数。'
}

/** 引用来源对应的物理表：本表取表名，别名取 QUERY_RELATION 中的物理表名（取不到返回空，列清单不加载）。 */
export function resolvePhysicalTable(currentTable: string, relations: TableRelation[], source: string): string {
  if (!source.trim() || source.trim().toLowerCase() === currentTable.trim().toLowerCase()) return currentTable.trim()
  const join = relations.find(item => item.alias.toLowerCase() === source.trim().toLowerCase()
    || item.table.toLowerCase() === source.trim().toLowerCase())
  return join?.table.trim() ?? ''
}
