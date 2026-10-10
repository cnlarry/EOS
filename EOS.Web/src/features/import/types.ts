/** 可导入的目标模块（服务端 ImportTarget）。权限、字段限制与业务逻辑都挂在模块上。 */
export interface ImportTarget {
  moduleId: number
  title: string
  masterTable: string
}

/** 导入定义里的一列（服务端 ImportFieldInfo）。 */
export interface ImportFieldInfo {
  key: string
  label: string
  dataType: string
  format: string | null
  isRequired: boolean
  isPrimaryKey: boolean
  isAutoIncrement: boolean
  maxLength: number | null
}

/** 模块的导入定义：可填列 + 必须映射的主键列。 */
export interface ImportDefinitionInfo {
  moduleId: number
  title: string
  masterTable: string
  fields: ImportFieldInfo[]
  requiredKeys: string[]
}

/** 解析后的表格数据：列名与全部数据行；`truncated` 表示超过服务端行数上限。 */
export interface ImportFileData {
  columns: string[]
  rows: string[][]
  totalRows: number
  truncated: boolean
  sourceName: string
}

export interface ImportRowIssue {
  field: string
  message: string
  code: string | null
}

/** 逐行判定结果。`code` 与保存路径的错误码同源，可直接照着排查。 */
export interface ImportRowOutcome {
  rowNumber: number
  ok: boolean
  code: string | null
  message: string | null
  fieldErrors: ImportRowIssue[] | null
}

export interface ImportRunResult {
  dryRun: boolean
  total: number
  succeeded: number
  failed: number
  rows: ImportRowOutcome[]
}

/** 预演/执行请求：mapping 与 columns 按下标对齐，null 表示该列不导入。 */
export interface ImportRunRequest {
  columns: string[]
  mapping: (string | null)[]
  rows: string[][]
  sourceName?: string | null
}

/**
 * 一条列映射：源列名 → 目标字段键。按**列名**存而不是列下标记——
 * 补导的文件常由 Excel 重新另存，列顺序会变而列名不变。
 */
export interface ImportMappingEntry {
  column: string
  field: string | null
}

/** 服务端记住的映射（按 用户 + 模块）。经办人/日期列可空：NULL = 还没发生过。 */
export interface ImportMappingSnapshot {
  moduleId: number
  sourceName: string
  entries: ImportMappingEntry[]
  updatedAt: string | null
  updatedBy: string
}

/** 前置资料未就绪：必填字段引用的主档还是空表。 */
export interface ImportReadinessItem {
  field: string
  fieldLabel: string
  sourceTable: string
  sourceLabel: string
  rows: number
}
