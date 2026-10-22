import type { QueryCondition } from '../../components/common/queryCondition'

/** 一次查询的完整语境：换回同一模块/表时可原样恢复。 */
export interface RecentSearch {
  moduleId: number
  table: string
  keyword: string
  conditions: QueryCondition[]
  at: number
}

const RECENT_KEY = 'eos.search-center.recent'
/** 最近查询保留条数：够用就好，太长会成为噪音。 */
const RECENT_LIMIT = 10

function readJson<T>(key: string): T | null {
  try {
    const raw = window.localStorage.getItem(key)
    return raw ? (JSON.parse(raw) as T) : null
  } catch {
    return null
  }
}

function writeJson(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // 隐私模式或配额满：最近查询/列选择是体验增强，写不进去不影响查询。
  }
}

/** 最近查询（新的在前，同模块+同表+同条件只留最近一次）。 */
export function readRecentSearches(): RecentSearch[] {
  const parsed = readJson<RecentSearch[]>(RECENT_KEY)
  if (!Array.isArray(parsed)) return []
  return parsed.filter(item => item && typeof item.moduleId === 'number' && typeof item.at === 'number')
}

export function pushRecentSearch(entry: Omit<RecentSearch, 'at'>): RecentSearch[] {
  const signature = JSON.stringify([entry.moduleId, entry.table, entry.keyword, entry.conditions])
  const next = [
    { ...entry, at: Date.now() },
    ...readRecentSearches().filter(item => JSON.stringify([item.moduleId, item.table, item.keyword, item.conditions]) !== signature),
  ].slice(0, RECENT_LIMIT)
  writeJson(RECENT_KEY, next)
  return next
}

export function clearRecentSearches(): RecentSearch[] {
  writeJson(RECENT_KEY, [])
  return []
}