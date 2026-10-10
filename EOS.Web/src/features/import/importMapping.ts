import type { ImportDefinitionInfo, ImportMappingSnapshot } from './types'

/** 列名归一：忽略大小写与空白差异，只用于"能不能自动对上"。 */
export function normalizeHeader(value: string) {
  return value.replace(/\s+/g, '').toLowerCase()
}

/**
 * 按列名自动匹配字段：先匹配字段标签（下载的模板用的就是标签），再匹配字段代号。
 * 匹配不上留空由人工选——猜错列比留空更贵。
 *
 * 返回「列下标 → 字段键」，与列的下标对齐（不是与列名对齐：重名列各自独立映射）。
 */
export function matchColumns(columns: string[], definition: ImportDefinitionInfo): Record<number, string> {
  const byLabel = new Map<string, string>()
  const byKey = new Map<string, string>()
  for (const field of definition.fields) {
    const label = normalizeHeader(field.label)
    if (label.length > 0 && !byLabel.has(label)) byLabel.set(label, field.key)
    byKey.set(normalizeHeader(field.key), field.key)
  }
  const result: Record<number, string> = {}
  columns.forEach((column, index) => {
    const text = normalizeHeader(column)
    const key = byLabel.get(text) ?? byKey.get(text)
    if (key) result[index] = key
  })
  return result
}

/** 还没映射的必须映射键：非空即禁止预演（缺主键必然撞库）。 */
export function missingRequiredKeys(definition: ImportDefinitionInfo, mapping: Record<number, string>): string[] {
  const mapped = new Set(Object.values(mapping))
  return definition.requiredKeys.filter((key) => !mapped.has(key))
}

/**
 * 服务端记住的映射优先、自动匹配兜底：按**列名**套用，因此补导的文件换列序也照样对得上。
 *
 * 三条取舍：
 * ① 记住的是"不导入"就尊重它（不要被自动匹配又填回去）；
 * ② 记住的字段已不在当前定义里（改名/退役）就忽略这一条；
 * ③ 记住的列名在新文件里没有同名列时不动它——留空由人工选。
 */
export function applySavedMapping(
  columns: string[],
  saved: ImportMappingSnapshot | null,
  definition: ImportDefinitionInfo,
): Record<number, string> {
  const result = matchColumns(columns, definition)
  if (saved == null || saved.entries.length === 0) return result

  const byColumn = new Map<string, string | null>()
  for (const entry of saved.entries) {
    const key = normalizeHeader(entry.column)
    if (key.length > 0 && !byColumn.has(key)) byColumn.set(key, entry.field)
  }
  const knownFields = new Set(definition.fields.map((field) => field.key.toLowerCase()))

  columns.forEach((column, index) => {
    const key = normalizeHeader(column)
    if (!byColumn.has(key)) return
    const field = byColumn.get(key) ?? null
    if (field == null) {
      delete result[index]
      return
    }
    if (knownFields.has(field.toLowerCase())) result[index] = field
  })
  return result
}

/** 把当前映射导出成可给团队复用的形状（按列名，与文件列序无关）。 */
export function toMappingEntries(columns: string[], mapping: Record<number, string>) {
  return columns.map((column, index) => ({ column, field: mapping[index] ?? null }))
}
