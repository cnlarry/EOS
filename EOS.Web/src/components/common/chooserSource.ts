import { apiClient } from '../../services/api'
import type { QueryCondition } from './queryCondition'

export interface UnifiedChooserColumn {
  key: string
  label: string
  dataType: string
  format?: string | null
  /** 兼容 document-workbench form-chooser 旧协议的显示格式字段 */
  displayFormat?: string | null
}

export interface UnifiedChooserRow { [key: string]: unknown }
export interface UnifiedChooserData {
  columns: UnifiedChooserColumn[]
  rows: UnifiedChooserRow[]
  total: number
  defaultKeys?: string[]
  /** 选择器实际取数的目标表（formField 数据源由服务端给出），用于筛选条件记忆 */
  targetTable?: string | null
}

export interface UnifiedChooserQuery {
  keyword?: string
  filterField?: string
  /** 高级查询结构化条件（服务端按数据源白名单 + 参数化执行） */
  conditions?: QueryCondition[]
  master?: Record<string, string>
  detail?: Record<string, string>
  sortField?: string | null
  sortDirection?: 'asc' | 'desc'
  page: number
  pageSize: number
}

/**
 * 统一选择器数据源（服务端描述）：
 * - formField：复用现有 CHOOSE_* 表单字段元数据（GET form-chooser 端点）；
 * - sourceKey：服务端注册数据源（POST /api/chooser/query，表/列/权限由服务端白名单解析）；
 * - loader：调用方自定义加载函数（过渡期，闭包内仍走各自授权 API）。
 */
export type UnifiedChooserSource =
  | { kind: 'formField'; moduleId: string; fieldKey: string; serialNo?: number | null }
  | { kind: 'sourceKey'; key: string; args?: Record<string, string> }
  | { kind: 'loader'; load: (query: UnifiedChooserQuery) => Promise<UnifiedChooserData> }

/**
 * 统一选择器取数入口（UnifiedChooser 与 ErpFieldChooser 共用）：
 * 同一套 sourceKey 白名单 / formField 元数据 / loader 管线，避免双份取数代码。
 */
export async function loadChooserSource(source: UnifiedChooserSource, query: UnifiedChooserQuery): Promise<UnifiedChooserData> {
  if (source.kind === 'loader') return source.load(query)
  if (source.kind === 'sourceKey') {
    return apiClient.post<UnifiedChooserData>('/chooser/query', {
      sourceKey: source.key,
      args: source.args,
      keyword: query.keyword,
      filterField: query.filterField,
      conditions: query.conditions?.length ? query.conditions : undefined,
      sortField: query.sortField,
      sortDirection: query.sortDirection,
      page: query.page,
      pageSize: query.pageSize,
    })
  }
  const result = await apiClient.get<UnifiedChooserData>(`/document-workbench/${source.moduleId}/form-chooser/${encodeURIComponent(source.fieldKey)}`, {
    query: {
      keyword: query.keyword,
      filterField: query.filterField,
      serialNo: source.serialNo != null ? String(source.serialNo) : undefined,
      conditions: query.conditions?.length ? JSON.stringify(query.conditions) : undefined,
      master: query.master ? JSON.stringify(query.master) : undefined,
      detail: query.detail ? JSON.stringify(query.detail) : undefined,
      sortField: query.sortField ?? undefined,
      sortDirection: query.sortDirection,
      page: String(query.page),
      pageSize: String(query.pageSize),
    },
  })
  return {
    columns: (result.columns ?? []).map(column => ({
      key: column.key,
      label: column.label,
      dataType: column.dataType,
      format: column.format ?? column.displayFormat ?? null,
    })),
    rows: result.rows ?? [],
    total: result.total ?? 0,
    targetTable: result.targetTable ?? null,
  }
}

/** 选择器记忆：某个用户对某张目标表上次使用的筛选字段与查询值。 */
export interface UnifiedChooserMemory {
  filterField: string | null
  keyword: string | null
}

/**
 * 读取选择器记忆（服务端按当前用户隔离，表名只作为查询参数）。
 * 记忆是录入加速手段，读不到时返回 null，选择器按空条件打开。
 */
export async function loadChooserMemory(tableId: string): Promise<UnifiedChooserMemory | null> {
  try {
    return await apiClient.get<UnifiedChooserMemory>('/chooser/memory', { query: { tableId } })
  } catch {
    return null
  }
}

/** 保存选择器记忆；失败不影响本次选择，因此吞掉异常。 */
export async function saveChooserMemory(tableId: string, filterField: string, keyword: string): Promise<void> {
  try {
    await apiClient.put<void>('/chooser/memory', {
      tableId,
      filterField: filterField || null,
      keyword: keyword || null,
    })
  } catch {
    // 记忆写入失败不打断选择流程
  }
}
