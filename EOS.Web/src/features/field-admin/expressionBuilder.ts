/**
 * 字段设置高级表达式构建器的模型、文本生成与白名单数据类型。
 *
 * 分工：构建器只把选项拼成受控语法的文本，文本仍是唯一提交物——校验/预览/发布一律走服务端；
 * 已存表达式的结构回读用服务端解析结果（POST /admin/fields/expressions/parse），前端不另立一套语法。
 */
import type { ExpressionKind } from './FieldEditorForm'

/** 服务端表达式结构回读的形态取值（对齐 ExpressionStructureModes）。 */
export type ExpressionMode = 'reference' | 'arithmetic' | 'registry' | 'tableSql' | 'literalUnion' | 'raw'

/** 服务端结构回读结果（构建器初始化输入）。 */
export interface ExpressionStructure {
  kind: string
  mode: ExpressionMode
  table?: string | null
  column?: string | null
  function?: string | null
  dataSource?: ExpressionDataSourceStructure | null
}

/** 受限单表 SELECT 的结构回读（字符串常量已去引号）。 */
export interface ExpressionDataSourceStructure {
  table: string
  columns: string[]
  whereColumn: string | null
  whereValue: string | null
  whereValueIsString: boolean
  orderColumn: string | null
  orderDirection: string | null
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

/** 表列（物理列 + FIELDS 内受控虚拟列；表达式引用与回填来源只取物理列）。 */
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

/** 数据源 SQL 构建器模型（受限单表 SELECT：列清单 + 单条常量过滤 + 单列排序）。 */
export interface DataSourceModel {
  table: string
  columns: string[]
  whereColumn: string
  whereValue: string
  /** 字符串常量需加引号（列类型非数值时置 true）。 */
  whereValueIsString: boolean
  orderColumn: string
  orderDirection: 'ASC' | 'DESC'
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
  /** 数据源 SQL：结构化 SELECT。 */
  dataSource: DataSourceModel
}

/** 服务端 SELECT 列数上限（超限直接拒绝）。 */
export const DATASOURCE_MAX_COLUMNS = 20

const NUMERIC_TYPES = new Set([
  'int', 'bigint', 'smallint', 'tinyint', 'decimal', 'numeric', 'float', 'real',
  'money', 'smallmoney', 'integer', 'bit',
])

const NUMERIC_LITERAL = /^[+-]?\d+(\.\d+)?$/

/** 数值列：过滤常量必须是数字字面量（服务端同样只接受带引号字符串或数值）。 */
export function isNumericType(dataType: string): boolean {
  return NUMERIC_TYPES.has(dataType.trim().toLowerCase())
}

export function emptyDataSourceModel(): DataSourceModel {
  return { table: '', columns: [], whereColumn: '', whereValue: '', whereValueIsString: true, orderColumn: '', orderDirection: 'ASC' }
}

/** 空模型：编辑态值已被清空或新增字段时，构建器从空表单起步。 */
export function emptyModel(kind: ExpressionKind, currentTable: string): ExpressionModel {
  return {
    mode: kind === 'virtual_exp' ? 'reference' : kind === 'convert_function' ? 'registry' : 'tableSql',
    table: kind === 'virtual_exp' ? currentTable : '',
    column: '',
    function: '',
    dataSource: emptyDataSourceModel(),
  }
}

/** 服务端结构回读 → 构建器模型；构建器不覆盖的形态（算术/字面量 UNION/非法）返回 null。 */
export function modelFromStructure(kind: ExpressionKind, structure: ExpressionStructure, currentTable: string): ExpressionModel | null {
  const base = emptyModel(kind, currentTable)
  if (structure.mode === 'reference') {
    return { ...base, mode: 'reference', table: structure.table || currentTable, column: structure.column ?? '' }
  }
  if (structure.mode === 'registry') {
    return { ...base, mode: 'registry', function: structure.function ?? '' }
  }
  if (structure.mode === 'tableSql') {
    const dataSource = structure.dataSource
    if (!dataSource) return null
    return {
      ...base,
      mode: 'tableSql',
      dataSource: {
        table: dataSource.table ?? '',
        columns: [...(dataSource.columns ?? [])],
        whereColumn: dataSource.whereColumn ?? '',
        whereValue: dataSource.whereValue ?? '',
        whereValueIsString: dataSource.whereValueIsString,
        orderColumn: dataSource.orderColumn ?? '',
        orderDirection: dataSource.orderDirection?.toUpperCase() === 'DESC' ? 'DESC' : 'ASC',
      },
    }
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
  if (kind === 'convert_function') return model.function.trim() || null
  return buildDataSourceSql(model.dataSource)
}

function buildDataSourceSql(dataSource: DataSourceModel): string | null {
  const table = dataSource.table.trim()
  if (!table || dataSource.columns.length === 0) return null
  let sql = `SELECT ${dataSource.columns.join(',')} FROM ${table}`
  if (dataSource.whereColumn.trim()) {
    const literal = whereLiteral(dataSource)
    if (literal === null) return null
    sql += ` WHERE ${dataSource.whereColumn.trim()} = ${literal}`
  }
  if (dataSource.orderColumn.trim()) sql += ` ORDER BY ${dataSource.orderColumn.trim()} ${dataSource.orderDirection}`
  return sql
}

/** WHERE 常量：字符串加引号并转义单引号；数值列必须为数字，否则不生成（服务端也只接受这两种字面量）。 */
function whereLiteral(dataSource: DataSourceModel): string | null {
  if (dataSource.whereValueIsString) return `'${dataSource.whereValue.replace(/'/g, "''")}'`
  const value = dataSource.whereValue.trim()
  return NUMERIC_LITERAL.test(value) ? value : null
}

/** 模型缺项提示：非空表示构建器尚未拼出可提交文本（此时不覆盖表达式文本）。 */
export function modelIssue(kind: ExpressionKind, model: ExpressionModel): string | null {
  if (kind === 'virtual_exp') return model.column.trim() ? null : '请选择引用列。'
  if (kind === 'convert_function') return model.function.trim() ? null : '请选择注册表内的转换函数。'
  const dataSource = model.dataSource
  if (!dataSource.table.trim()) return '请选择来源表。'
  if (dataSource.columns.length === 0) return '请至少选择一列。'
  if (dataSource.columns.length > DATASOURCE_MAX_COLUMNS) return `最多选择 ${DATASOURCE_MAX_COLUMNS} 列。`
  if (!dataSource.whereColumn.trim()) return null
  if (dataSource.whereValueIsString) {
    return dataSource.whereValue === '' ? '过滤条件已选列，请填写过滤值（或点「清除」去掉该条件）。' : null
  }
  return NUMERIC_LITERAL.test(dataSource.whereValue.trim()) ? null : '数值列的过滤值必须是数字字面量。'
}

/** 引用来源对应的物理表：本表取表名，别名取 QUERY_RELATION 中的物理表名（取不到返回空，列清单不加载）。 */
export function resolvePhysicalTable(currentTable: string, relations: TableRelation[], source: string): string {
  if (!source.trim() || source.trim().toLowerCase() === currentTable.trim().toLowerCase()) return currentTable.trim()
  const join = relations.find(item => item.alias.toLowerCase() === source.trim().toLowerCase()
    || item.table.toLowerCase() === source.trim().toLowerCase())
  return join?.table.trim() ?? ''
}
