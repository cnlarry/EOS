import type { SortingState } from '@tanstack/react-table'
import type { QueryCondition } from '../../components/common/ErpQueryBuilder'

export const pageSizeOptions = [10, 16, 25, 50]

export interface WorkbenchListState {
  page: number
  pageSize: number | null
  keyword: string
  sort: SortingState
  conditions: QueryCondition[]
  columnFilters: Record<string, QueryCondition>
}

function positiveInt(value: string | null, fallback: number): number {
  const parsed = Number(value)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback
}

function parseSort(fields: string | null, directions: string | null): SortingState {
  if (!fields) return []
  const names = fields.split(',').map((item) => item.trim()).filter(Boolean)
  const dirs = (directions ?? '').split(',').map((item) => item.trim().toLowerCase())
  return names.map((id, index) => ({ id, desc: dirs[index] === 'desc' }))
}

function parseJson<T>(raw: string | null): T | null {
  if (!raw) return null
  try {
    return JSON.parse(raw) as T
  } catch {
    return null
  }
}

/** 从 URL 恢复工作台列表状态（非法值回退默认） */
export function readListState(search: URLSearchParams): WorkbenchListState {
  const parsedConditions = parseJson<QueryCondition[]>(search.get('q'))
  const parsedFilters = parseJson<Record<string, QueryCondition>>(search.get('cf'))
  const pageSizeRaw = positiveInt(search.get('pageSize'), 0)
  return {
    page: positiveInt(search.get('page'), 1),
    pageSize: pageSizeOptions.includes(pageSizeRaw) ? pageSizeRaw : null,
    keyword: search.get('keyword') ?? '',
    sort: parseSort(search.get('sortFields'), search.get('sortDirections')),
    conditions: Array.isArray(parsedConditions) ? parsedConditions : [],
    columnFilters: parsedFilters && typeof parsedFilters === 'object' ? parsedFilters : {},
  }
}

/** 把工作台列表状态写入 URL（仅包含非默认值，便于分享与刷新恢复） */
export function writeListState(state: WorkbenchListState): URLSearchParams {
  const params = new URLSearchParams()
  if (state.page > 1) params.set('page', String(state.page))
  if (state.pageSize) params.set('pageSize', String(state.pageSize))
  if (state.keyword) params.set('keyword', state.keyword)
  if (state.sort.length) {
    params.set('sortFields', state.sort.map((item) => item.id).join(','))
    params.set('sortDirections', state.sort.map((item) => (item.desc ? 'desc' : 'asc')).join(','))
  }
  if (state.conditions.length) params.set('q', JSON.stringify(state.conditions))
  if (Object.keys(state.columnFilters).length) params.set('cf', JSON.stringify(state.columnFilters))
  return params
}
